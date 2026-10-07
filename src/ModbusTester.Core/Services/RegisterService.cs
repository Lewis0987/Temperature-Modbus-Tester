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
    public static IReadOnlyList<DecodedRegister> Decode(IEnumerable<ModbusResult> results, DeviceProfile profile, Func<int, double> manualScaleFor) =>
        results.SelectMany(r => Decode(r, profile, manualScaleFor)).OrderBy(r => r.PduAddress).ToArray();

    /// <param name="manualScaleFor">Scale for registers without a definition, by user-facing address.</param>
    public static IReadOnlyList<DecodedRegister> Decode(ModbusResult result, DeviceProfile profile, Func<int, double> manualScaleFor)
    {
        var rows = new List<DecodedRegister>(result.Registers.Count);
        ushort startPdu = result.Request.PduAddress;
        for (int i = 0; i < result.Registers.Count; i++)
        {
            int address = ModbusAddress.Display(result.Request.StartAddress, i);
            var pdu = (ushort)(startPdu + i);
            ushort raw = result.Registers[i];
            RegisterDefinition? definition = profile.Find(pdu);
            rows.Add(definition is not null
                ? RegisterDecoder.DecodeEngineering(address, pdu, raw, definition)
                : RegisterDecoder.DecodeGeneric(address, pdu, raw, manualScaleFor(address)));
        }
        return rows;
    }
}
