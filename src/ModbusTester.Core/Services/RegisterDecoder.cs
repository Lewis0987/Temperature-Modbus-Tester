using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;

namespace ModbusTester.Core.Services;

public enum DataType
{
    UInt16,
    Int16,
    UInt32,
    Int32,
    Float32,
    /// <summary>Enum-like register: the raw value is looked up in a state table, never scaled.</summary>
    Status,
}

/// <summary>Byte/word order for multi-register values (Phase 4). Letters are bytes, A = most significant.</summary>
public enum ByteOrder
{
    BigEndian,      // ABCD
    LittleEndian,   // DCBA
    WordSwap,       // CDAB
    ByteSwap,       // BADC
}

/// <summary>
/// Turns raw 16-bit registers into values. 16-bit types and status registers are implemented;
/// 32-bit types and byte orders are declared so callers already pass them, and are filled in by Phase 4.
/// </summary>
public static class RegisterDecoder
{
    public static int RegisterCount(DataType type) => type switch
    {
        DataType.UInt16 or DataType.Int16 or DataType.Status => 1,
        _ => 2,
    };

    public static double Decode(ReadOnlySpan<ushort> registers, DataType type, ByteOrder order = ByteOrder.BigEndian)
    {
        if (registers.Length < RegisterCount(type))
            throw new ArgumentException($"{type} needs {RegisterCount(type)} register(s).", nameof(registers));

        ushort first = order is ByteOrder.ByteSwap or ByteOrder.LittleEndian
            ? (ushort)((registers[0] << 8) | (registers[0] >> 8))
            : registers[0];

        return type switch
        {
            DataType.UInt16 or DataType.Status => first,
            DataType.Int16 => (short)first,
            _ => throw new NotSupportedException($"{type} decoding is planned for Phase 4."),
        };
    }

    /// <summary>
    /// Document-based decoding of one register:
    /// raw → data type (Int16 sign) → status lookup or × scale → range check → engineering value.
    /// Range and unknown states only set <see cref="DecodedRegister.Quality"/>; nothing throws.
    /// </summary>
    public static DecodedRegister DecodeEngineering(int address, ushort pduAddress, ushort raw, RegisterDefinition definition)
    {
        long integer = (long)Decode([raw], definition.DataType);

        if (definition.DataType == DataType.Status)
        {
            bool known = definition.States.TryGetValue(raw, out RegisterState? state);
            return new DecodedRegister
            {
                Address = address, PduAddress = pduAddress, Raw = raw, Definition = definition, Integer = integer,
                ValueText = known ? state!.Text : $"Unknown ({raw})",
                Quality = known ? RegisterQuality.Ok : RegisterQuality.UnknownState,
            };
        }

        // Two values in one word (document-defined layout): "Major.Minor", no number, no scale, no range.
        if (definition.ValueEncoding == RegisterValueEncoding.VersionMajorMinor)
            return new DecodedRegister
            {
                Address = address, PduAddress = pduAddress, Raw = raw, Definition = definition, Integer = integer,
                ValueText = SoftwareVersion.MajorMinorText(raw),
            };

        // Document Scaling "-" (null) means "no scaling": computed as ×1 (never ×0), but Scale stays null so it shows as "-".
        double value = Math.Round(integer * (definition.Scale ?? 1), 6);
        bool outOfRange = value < (definition.Min ?? double.NegativeInfinity) || value > (definition.Max ?? double.PositiveInfinity);
        return new DecodedRegister
        {
            Address = address, PduAddress = pduAddress, Raw = raw, Definition = definition, Integer = integer,
            Scale = definition.Scale, Value = value, ValueText = RegisterFormat.Number(value),
            Quality = outOfRange ? RegisterQuality.OutOfRange : RegisterQuality.Ok,
        };
    }

    /// <summary>Generic decoding (no definition): unsigned raw × manual scale, exactly as before profiles existed.</summary>
    public static DecodedRegister DecodeGeneric(int address, ushort pduAddress, ushort raw, double scale)
    {
        var row = new RegisterRow(address, raw, Decode([raw], DataType.UInt16), scale);
        return new DecodedRegister
        {
            Address = address, PduAddress = pduAddress, Raw = raw, Integer = raw,
            Scale = scale, Value = row.Value, ValueText = RegisterFormat.Number(row.Value),
        };
    }
}
