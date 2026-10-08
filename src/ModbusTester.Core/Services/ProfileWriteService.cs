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
/// FC06 only; whether a device accepts FC16 is not assumed here.
/// </summary>
public sealed class ProfileWriteService(ModbusService modbus)
{
    public const ModbusFunction WriteFunction = ModbusFunction.WriteSingleRegister;

    /// <summary>Validates and encodes without sending. <paramref name="address"/> is in the user's Address Base.</summary>
    public static ProfileWriteResult Prepare(DeviceProfile profile, byte unitId, int address, AddressBase addressBase, string? input)
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
        CancellationToken cancellationToken = default)
    {
        ProfileWriteResult prepared = Prepare(profile, unitId, address, addressBase, input);
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
