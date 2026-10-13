using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;

namespace ModbusTester.Core.Services;

/// <summary>Turns a read result into display rows. Shared by TCP and RTU: it only sees registers, never the transport.</summary>
public static class RegisterService
{
    /// <param name="scaleFor">Scale for a user-facing address; null means 1.</param>
    public static IReadOnlyList<RegisterRow> BuildRows(
        ModbusResult result,
        Func<int, double>? scaleFor = null,
        DataType dataType = DataType.UInt16)
    {
        var rows = new List<RegisterRow>(result.Registers.Count);
        ushort[] registers = [.. result.Registers];
        for (int i = 0; i < registers.Length; i++)
        {
            int address = ModbusAddress.Display(result.Request.StartAddress, i);
            double decoded = RegisterDecoder.Decode(registers.AsSpan(i, 1), dataType);
            rows.Add(new RegisterRow(address, registers[i], decoded, scaleFor?.Invoke(address) ?? 1));
        }
        return rows;
    }

    /// <summary>
    /// Decodes every register of a read with <paramref name="profile"/>. Registers the profile defines use the
    /// document (data type, scale, states, range); all others – and every register with the Generic profile –
    /// use the generic raw × manual scale.
    /// The definition is looked up by PDU address (request PDU address + offset), so the Address Base the user
    /// chose has already been applied exactly once, by <see cref="ModbusRequest.PduAddress"/>.
    /// </summary>
    /// <summary>Decodes several reads (e.g. the groups of a profile cumulative read) into one list, ascending by register.</summary>
    /// <remarks>
    /// All results form one read, so a register whose unit depends on another one (Reg 1 / 223 on System unit 401)
    /// is decoded with that register's value even when it came in another request.
    /// </remarks>
    public static IReadOnlyList<DecodedRegister> Decode(IEnumerable<ModbusResult> results, DeviceProfile profile, Func<int, double> manualScaleFor)
    {
        ModbusResult[] all = [.. results];
        IReadOnlyDictionary<ushort, ushort> read = RawByPdu(all);
        return all.SelectMany(r => Decode(r, profile, manualScaleFor, read)).OrderBy(r => r.PduAddress).ToArray();
    }

    /// <summary>
    /// Generic decoding (no profile) of whole reads as <paramref name="type"/>: each request's registers are taken
    /// <see cref="RegisterDecoder.RegisterCount"/> at a time from its start (Quantity = registers, never values), one row
    /// per value at the address of its first register. A trailing word that cannot complete a 32-bit value is an
    /// <see cref="RegisterQuality.Incomplete"/> row – never padded or dropped. UInt16 gives exactly the previous rows.
    /// </summary>
    public static IReadOnlyList<DecodedRegister> DecodeGeneric(IEnumerable<ModbusResult> results, DataType type, Register32Order order,
        Func<int, double> manualScaleFor)
    {
        int count = RegisterDecoder.RegisterCount(type);
        var rows = new List<DecodedRegister>();
        foreach (ModbusResult result in results)
        {
            ushort startPdu = result.Request.PduAddress;
            ushort[] words = [.. result.Registers];
            for (int i = 0; i < words.Length; i += count)
            {
                int address = ModbusAddress.Display(result.Request.StartAddress, i);
                var pdu = (ushort)(startPdu + i);
                rows.Add(type == DataType.UInt16
                    ? RegisterDecoder.DecodeGeneric(address, pdu, words[i], manualScaleFor(address))
                    : RegisterDecoder.DecodeGeneric(address, pdu, words.AsSpan(i, Math.Min(count, words.Length - i)), type, order, manualScaleFor(address)));
            }
        }
        return rows.OrderBy(r => r.PduAddress).ToArray();
    }

    /// <param name="manualScaleFor">Scale for registers without a definition, by user-facing address.</param>
    public static IReadOnlyList<DecodedRegister> Decode(ModbusResult result, DeviceProfile profile, Func<int, double> manualScaleFor) =>
        Decode(result, profile, manualScaleFor, RawByPdu([result]));

    private static IReadOnlyList<DecodedRegister> Decode(ModbusResult result, DeviceProfile profile, Func<int, double> manualScaleFor,
        IReadOnlyDictionary<ushort, ushort> read)
    {
        var rows = new List<DecodedRegister>(result.Registers.Count);
        ushort startPdu = result.Request.PduAddress;
        for (int i = 0; i < result.Registers.Count; i++)
        {
            int address = ModbusAddress.Display(result.Request.StartAddress, i);
            var pdu = (ushort)(startPdu + i);
            ushort raw = result.Registers[i];
            RegisterDefinition? covering = profile.FindCovering(pdu);

            if (covering is { RegisterCount: 2 })
            {
                RegisterDefinition definition = WithUnitContext(covering, profile, read);
                if (covering.PduAddress == pdu)
                {
                    // First word: both words go to one decoder → one row. The second word may come from another
                    // request of the same read; if it was not read at all the value is Incomplete (never guessed).
                    var second = (ushort)(pdu + 1);
                    if (read.TryGetValue(second, out ushort next))
                    {
                        rows.Add(RegisterDecoder.DecodeEngineering(address, pdu, [raw, next], definition));
                        if (i + 1 < result.Registers.Count)
                            i++;                                    // second word consumed here
                    }
                    else
                        rows.Add(RegisterDecoder.DecodeEngineering(address, pdu, [raw], definition));
                }
                else if (!read.ContainsKey(covering.PduAddress))
                    rows.Add(RegisterDecoder.DecodeEngineering(address, pdu, [raw], definition));   // second word only: Incomplete
                // else: second word, already part of the row of its first word (other request of this read)
                continue;
            }

            RegisterDefinition? single = profile.Find(pdu);
            rows.Add(single is not null
                ? RegisterDecoder.DecodeEngineering(address, pdu, raw, WithUnitContext(single, profile, read))
                : RegisterDecoder.DecodeGeneric(address, pdu, raw, manualScaleFor(address)));
        }
        return rows;
    }

    /// <summary>
    /// Unit and range of a profile register for the unit selector state in this read (e.g. Reg 1 with 401 = 1 → °F).
    /// If the selector was not part of the read, its document default is assumed (401 = 0 → °C, as before).
    /// Only labels and range change – the value is always raw × scale, never converted.
    /// </summary>
    private static RegisterDefinition WithUnitContext(RegisterDefinition definition, DeviceProfile profile, IReadOnlyDictionary<ushort, ushort> read)
    {
        if (definition.UnitSelectorRegister is not { } selectorNumber)
            return definition;
        var selectorPdu = (ushort)(selectorNumber - 1);
        ushort? state = read.TryGetValue(selectorPdu, out ushort raw) ? raw
            : profile.Find(selectorPdu)?.DefaultValue is { } d ? (ushort)d
            : null;
        return definition.ForUnitState(state);
    }

    /// <summary>Every register word of the read by PDU address (the context for dependent registers).</summary>
    private static IReadOnlyDictionary<ushort, ushort> RawByPdu(IEnumerable<ModbusResult> results)
    {
        var read = new Dictionary<ushort, ushort>();
        foreach (ModbusResult result in results)
        {
            ushort start = result.Request.PduAddress;
            for (int i = 0; i < result.Registers.Count; i++)
                read[(ushort)(start + i)] = result.Registers[i];
        }
        return read;
    }
}
