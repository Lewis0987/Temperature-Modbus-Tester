using System.Globalization;
using ModbusTester.Core.Profiles;

namespace ModbusTester.Core.Services;

/// <summary>Why a profile write was refused. Each kind is a different problem and is reported separately.</summary>
public enum ProfileWriteError
{
    None,
    /// <summary>The address is not a register of the selected profile (or not a valid address at all).</summary>
    RegisterNotDefined,
    /// <summary>The register is read-only (Access = Read).</summary>
    ReadOnly,
    /// <summary>The input is not a number (empty, text, NaN, ∞).</summary>
    InvalidEngineeringValue,
    /// <summary>The engineering value is outside the definition's Min…Max.</summary>
    OutOfRange,
    /// <summary>The engineering value is not a whole multiple of Scale (e.g. 1.55 with Scale 0.1); never rounded silently.</summary>
    NotRepresentableByScale,
    /// <summary>The raw integer does not fit the data type (Int16 -32768…32767, UInt16 0…65535).</summary>
    DataTypeOverflow,
    /// <summary>A Status register was given a name that is not one of its States.</summary>
    UnknownStatus,
    /// <summary>The value is in range but listed in the definition's ExcludedValues.</summary>
    ExcludedValue,
    /// <summary>The data type cannot be written as one 16-bit register (UInt32 / Int32 / Float32).</summary>
    UnsupportedDataType,
    /// <summary>The value is valid, but writes to this register are held back (definition's WriteHoldReason).</summary>
    WriteOnHold,
    /// <summary>The register's unit / range depends on a selector (e.g. System unit 401) whose state on this device is not known.</summary>
    UnitContextUnknown,
    /// <summary>The value was sent, but the Modbus exchange failed (timeout, exception response, ...).</summary>
    CommunicationFailed,
}

/// <summary>Outcome of encoding one engineering value / status name into the raw register word.</summary>
public sealed record RegisterWriteEncoding
{
    public required RegisterDefinition Definition { get; init; }
    public required ProfileWriteError Error { get; init; }
    /// <summary>The 16-bit word to write; set only when <see cref="IsValid"/>.</summary>
    public ushort? Raw { get; init; }
    /// <summary>The engineering value the raw word stands for (status: the raw state value).</summary>
    public double? EngineeringValue { get; init; }
    public string Message { get; init; } = "";

    public bool IsValid => Error == ProfileWriteError.None;
}

/// <summary>
/// Engineering value / status name → validated raw register word, using only the register's metadata
/// (Access, DataType, Scale, Min, Max, States, ExcludedValues). Nothing is sent from here.
/// Numbers: finite → Min…Max (inclusive) → ExcludedValues → value / (Scale ?? 1) must be a whole number
/// (tolerance 1e-9, never rounded) → fits Int16 / UInt16 → two's complement for negatives.
/// Status: the state name (as listed in States) → its raw value; numbers or unknown names are refused.
/// </summary>
public static class RegisterWriteEncoder
{
    /// <summary>Allowed distance of value / Scale from a whole number (relative for large values), for floating-point noise only.</summary>
    public const double ScaleTolerance = 1e-9;

    /// <summary>Encodes text input: a state name for Status registers, an invariant-culture number otherwise.</summary>
    public static RegisterWriteEncoding Encode(RegisterDefinition definition, string? input)
    {
        if (Precheck(definition) is { } refused)
            return refused;
        string text = (input ?? "").Trim();
        if (definition.DataType == DataType.Status)
            return EncodeStatus(definition, text);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return Fail(definition, ProfileWriteError.InvalidEngineeringValue, $"\"{text}\" is not a number.");
        return Encode(definition, value);
    }

    /// <summary>Encodes a numeric engineering value (not for Status registers – use the state name).</summary>
    public static RegisterWriteEncoding Encode(RegisterDefinition definition, double value)
    {
        if (Precheck(definition) is { } refused)
            return refused;
        if (definition.DataType == DataType.Status)
            return Fail(definition, ProfileWriteError.UnknownStatus, $"Register {definition.RegisterNumber} is a status register: choose one of {StateList(definition)}.");
        if (!double.IsFinite(value))
            return Fail(definition, ProfileWriteError.InvalidEngineeringValue, "The value must be a finite number.");

        string shown = Format(value);
        if (definition.Min is { } min && value < min || definition.Max is { } max && value > max)
            return Fail(definition, ProfileWriteError.OutOfRange, $"{shown} is outside {RangeText(definition)}.");
        foreach ((double excluded, string reason) in definition.ExcludedValues)
            if (Math.Abs(value - excluded) <= ScaleTolerance)
                return Fail(definition, ProfileWriteError.ExcludedValue, $"{shown} cannot be written: {reason}.");

        double scale = definition.Scale ?? 1;   // null = no scaling (×1), never ×0
        double exact = value / scale;
        double whole = Math.Round(exact);
        if (Math.Abs(exact - whole) > ScaleTolerance * Math.Max(1, Math.Abs(whole)))
            return Fail(definition, ProfileWriteError.NotRepresentableByScale, $"{shown} is not a multiple of {Format(scale)}.");

        (double low, double high) = definition.DataType == DataType.Int16
            ? ((double)short.MinValue, (double)short.MaxValue)
            : ((double)ushort.MinValue, (double)ushort.MaxValue);
        if (whole < low || whole > high)
            return Fail(definition, ProfileWriteError.DataTypeOverflow, $"Raw {Format(whole)} does not fit {definition.DataType} ({low}..{high}).");

        ushort raw = definition.DataType == DataType.Int16 ? unchecked((ushort)(short)whole) : (ushort)whole;
        return new RegisterWriteEncoding { Definition = definition, Error = ProfileWriteError.None, Raw = raw, EngineeringValue = value };
    }

    /// <summary>Encodes the definition's DefaultValue (status: the default raw state). Only encodes – never writes.</summary>
    public static RegisterWriteEncoding EncodeDefault(RegisterDefinition definition)
    {
        if (Precheck(definition) is { } refused)
            return refused;
        if (definition.DefaultValue is not { } value)
            return Fail(definition, ProfileWriteError.InvalidEngineeringValue, $"Register {definition.RegisterNumber} has no default value.");
        if (definition.DataType != DataType.Status)
            return Encode(definition, value);
        return value is >= 0 and <= ushort.MaxValue && value == Math.Floor(value) && definition.States.TryGetValue((ushort)value, out RegisterState? state)
            ? Encode(definition, state.Text)
            : Fail(definition, ProfileWriteError.UnknownStatus, $"Default {Format(value)} is not one of {StateList(definition)}.");
    }

    private static RegisterWriteEncoding EncodeStatus(RegisterDefinition definition, string name)
    {
        foreach ((ushort raw, RegisterState state) in definition.States)
            if (string.Equals(state.Text, name, StringComparison.OrdinalIgnoreCase) || state.ChineseText.Length > 0 && state.ChineseText == name)
                return new RegisterWriteEncoding { Definition = definition, Error = ProfileWriteError.None, Raw = raw, EngineeringValue = raw };
        return Fail(definition, ProfileWriteError.UnknownStatus, $"\"{name}\" is not one of {StateList(definition)}.");
    }

    private static RegisterWriteEncoding? Precheck(RegisterDefinition definition)
    {
        if (!definition.IsWritable)
            return Fail(definition, ProfileWriteError.ReadOnly, $"Register {definition.RegisterNumber} ({definition.Description}) is read-only.");
        if (definition.DataType is not (DataType.Int16 or DataType.UInt16 or DataType.Status))
            return Fail(definition, ProfileWriteError.UnsupportedDataType, $"{definition.DataType} cannot be written as one register.");
        return null;
    }

    private static RegisterWriteEncoding Fail(RegisterDefinition definition, ProfileWriteError error, string message) =>
        new() { Definition = definition, Error = error, Message = message };

    private static string RangeText(RegisterDefinition d) =>
        $"{(d.Min is { } min ? Format(min) : "…")}..{(d.Max is { } max ? Format(max) : "…")}";

    private static string StateList(RegisterDefinition d) => string.Join(", ", d.States.OrderBy(s => s.Key).Select(s => s.Value.Text));

    private static string Format(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
}
