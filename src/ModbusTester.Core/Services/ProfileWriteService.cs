using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;

namespace ModbusTester.Core.Services;

/// <summary>Outcome of a profile write: the refusal reason, or the request that was sent and its Modbus result.</summary>
public sealed record ProfileWriteResult
{
    public required ProfileWriteError Error { get; init; }
    public RegisterDefinition? Definition { get; init; }
    public RegisterWriteEncoding? Encoding { get; init; }
    /// <summary>The FC06 request built from the encoding (also set for WriteOnHold, which is never sent).</summary>
    public ModbusWriteRequest? Request { get; init; }
    /// <summary>Set only when the request actually went to the transport.</summary>
    public ModbusWriteResult? Transport { get; init; }
    public string Message { get; init; } = "";

    public bool IsSent => Transport is not null;
    public bool IsSuccess => Error == ProfileWriteError.None && Transport?.IsSuccess == true;
}

/// <summary>
/// Writes one engineering value / status name to a register of a device profile:
/// address → definition → writable? → <see cref="RegisterWriteEncoder"/> → hold check → FC06 request → <see cref="ModbusService"/>.
/// Every refusal happens before the transport, so nothing reaches the device. Profile writes are single-register
/// FC06 only; whether a device accepts FC16 is not assumed here. (A6V12045847 mentions that a "multiple-writing
/// command" with invalid values is rejected with an error and leaves the registers unchanged, but names no function
/// code – to be verified on the real sensor before any profile FC16.)
/// </summary>
public sealed class ProfileWriteService(ModbusService modbus)
{
    public const ModbusFunction WriteFunction = ModbusFunction.WriteSingleRegister;

    /// <summary>
    /// The definition a write must use: for a register whose unit follows a selector (Reg 223 ← System unit 401) the
    /// unit and range of the selector state that is known for this device (<paramref name="unitContext"/>, see
    /// <see cref="ProfileUnitContext"/>). Unlike a read, a write never falls back to the document default: if the
    /// device's selector state is not known (not read yet / unknown state), the write is refused.
    /// Only unit and range change – the value is still encoded as value / scale, never converted.
    /// </summary>
    public static (RegisterDefinition? Definition, string? Error) ResolveForWrite(DeviceProfile profile, RegisterDefinition definition,
        IReadOnlyDictionary<int, ushort>? unitContext)
    {
        if (definition.UnitSelectorRegister is not { } selector)
            return (definition, null);
        string selectorText = $"Register {selector} ({profile.Find((ushort)(selector - 1))?.Description ?? "unit selector"})";
        if (unitContext is null || !unitContext.TryGetValue(selector, out ushort state))
            return (null, $"Register {definition.RegisterNumber} ({definition.Description}) depends on {selectorText}, which has not been " +
                          $"read from this device yet: read Register {selector} first (e.g. profile read up to Address {selector}).");
        if (!definition.UnitVariants.ContainsKey(state))
            return (null, $"{selectorText} = {state} is not a known state: Register {definition.RegisterNumber} cannot be written.");
        return (definition.ForUnitState(state), null);
    }

    /// <summary>Validates and encodes without sending. <paramref name="address"/> is in the user's Address Base.</summary>
    /// <param name="unitContext">Known selector states of this device (register number → raw), e.g. 401 → 1.</param>
    public static ProfileWriteResult Prepare(DeviceProfile profile, byte unitId, int address, AddressBase addressBase, string? input,
        IReadOnlyDictionary<int, ushort>? unitContext = null)
    {
        ushort pduAddress;
        try
        {
            pduAddress = ModbusAddress.ToPdu(address, 1, addressBase);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return new ProfileWriteResult { Error = ProfileWriteError.RegisterNotDefined, Message = ex.Message };
        }
        if (profile.Find(pduAddress) is not { } definition)
            return new ProfileWriteResult
            {
                Error = ProfileWriteError.RegisterNotDefined,
                Message = $"Address {address} ({BaseText(addressBase)}) is not a register of {profile.Name}.",
            };

        if (definition.IsWritable)
        {
            (RegisterDefinition? resolved, string? contextError) = ResolveForWrite(profile, definition, unitContext);
            if (resolved is null)
                return new ProfileWriteResult { Error = ProfileWriteError.UnitContextUnknown, Definition = definition, Message = contextError! };
            definition = resolved;
        }

        RegisterWriteEncoding encoding = RegisterWriteEncoder.Encode(definition, input);
        if (!encoding.IsValid)
            return new ProfileWriteResult { Error = encoding.Error, Definition = definition, Encoding = encoding, Message = encoding.Message };

        var request = ModbusWriteRequest.Single(unitId, address, encoding.Raw!.Value, addressBase);
        // Final check: exactly one FC06 word, aimed at the register that was validated.
        if (request.Function != WriteFunction || request.Values.Count != 1 || request.PduAddress != definition.PduAddress)
            throw new InvalidOperationException("Profile write request does not match the validated register.");

        if (definition.WriteHoldReason is { } hold)
            return new ProfileWriteResult
            {
                Error = ProfileWriteError.WriteOnHold, Definition = definition, Encoding = encoding, Request = request,
                Message = $"Register {definition.RegisterNumber} ({definition.Description}) not sent: {hold}.",
            };

        return new ProfileWriteResult { Error = ProfileWriteError.None, Definition = definition, Encoding = encoding, Request = request };
    }

    /// <summary>Validates, encodes and – only if everything passed – sends one FC06 write.</summary>
    public async Task<ProfileWriteResult> WriteAsync(DeviceProfile profile, byte unitId, int address, AddressBase addressBase, string? input,
        IReadOnlyDictionary<int, ushort>? unitContext = null, CancellationToken cancellationToken = default)
    {
        ProfileWriteResult prepared = Prepare(profile, unitId, address, addressBase, input, unitContext);
        if (prepared.Error != ProfileWriteError.None)
            return prepared;

        ModbusWriteResult sent = await modbus.WriteRegistersAsync(prepared.Request!, cancellationToken);
        return prepared with
        {
            Transport = sent,
            Error = sent.IsSuccess ? ProfileWriteError.None : ProfileWriteError.CommunicationFailed,
            Message = sent.IsSuccess ? "" : $"{sent.Status}: {sent.ErrorMessage}",
        };
    }

    private static string BaseText(AddressBase addressBase) => addressBase == AddressBase.OneBased ? "1-based" : "0-based";
}
