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

/// <summary>
/// 16-bit byte order (kept for compatibility): only <see cref="ByteSwap"/> / <see cref="LittleEndian"/> change a single
/// word (0x1234 → 0x3412). For 32-bit values use <see cref="Register32Order"/>; these names map to it as
/// BigEndian = ABCD, LittleEndian = DCBA, WordSwap = CDAB, ByteSwap = BADC.
/// </summary>
public enum ByteOrder
{
    BigEndian,      // ABCD
    LittleEndian,   // DCBA
    WordSwap,       // CDAB
    ByteSwap,       // BADC
}

/// <summary>
/// Byte arrangement of a 32-bit value over two registers. A is the most significant byte of the value; the letters
/// give the order in which the bytes appear on the wire (first register high byte, first register low byte,
/// second register high byte, second register low byte). E.g. value 0x12345678:
/// ABCD = 1234 5678, BADC = 3412 7856, CDAB = 5678 1234, DCBA = 7856 3412.
/// </summary>
public enum Register32Order
{
    ABCD,
    BADC,
    CDAB,
    DCBA,
}

/// <summary>
/// Turns raw 16-bit registers into values: UInt16 / Int16 / Status (1 register) and UInt32 / Int32 / Float32
/// (2 registers, IEEE-754 single for Float32). Too few registers is reported, never padded or guessed.
/// </summary>
public static class RegisterDecoder
{
    public static int RegisterCount(DataType type) => type switch
    {
        DataType.UInt16 or DataType.Int16 or DataType.Status => 1,
        DataType.UInt32 or DataType.Int32 or DataType.Float32 => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static bool Is32Bit(DataType type) => RegisterCount(type) == 2;

    /// <summary>Compatibility overload: 16-bit with <see cref="ByteOrder"/>; 32-bit types use the matching <see cref="Register32Order"/>.</summary>
    public static double Decode(ReadOnlySpan<ushort> registers, DataType type, ByteOrder order = ByteOrder.BigEndian)
    {
        if (Is32Bit(type))
            return Decode(registers, type, ToRegister32Order(order));
        RequireRegisters(registers, type);
        ushort first = order is ByteOrder.ByteSwap or ByteOrder.LittleEndian
            ? (ushort)((registers[0] << 8) | (registers[0] >> 8))
            : registers[0];
        return type == DataType.Int16 ? (short)first : first;
    }

    /// <summary>
    /// Decodes <paramref name="type"/> from the first <see cref="RegisterCount"/> registers. Float32 can return NaN or
    /// ±Infinity (they are values of the format, not errors). Throws <see cref="ArgumentException"/> if registers are missing.
    /// </summary>
    public static double Decode(ReadOnlySpan<ushort> registers, DataType type, Register32Order order)
    {
        RequireRegisters(registers, type);
        if (!Is32Bit(type))
            return type == DataType.Int16 ? (short)registers[0] : registers[0];
        uint bits = Combine32(registers[0], registers[1], order);
        // Every arm explicitly double: otherwise the switch's common type would be float and UInt32 / Int32 would lose
        // precision (0x12345678 = 305419896 would come out as 305419904).
        return type switch
        {
            DataType.UInt32 => (double)bits,
            DataType.Int32 => (double)unchecked((int)bits),
            _ => (double)BitConverter.UInt32BitsToSingle(bits),   // IEEE-754 single, exactly the bits on the wire
        };
    }

    /// <summary>The 32-bit pattern (A = most significant byte) of two registers as they came off the wire.</summary>
    public static uint Combine32(ushort first, ushort second, Register32Order order)
    {
        // Wire bytes w0..w3: first register high / low, second register high / low.
        byte w0 = (byte)(first >> 8), w1 = (byte)first, w2 = (byte)(second >> 8), w3 = (byte)second;
        (byte a, byte b, byte c, byte d) = order switch
        {
            Register32Order.ABCD => (w0, w1, w2, w3),
            Register32Order.BADC => (w1, w0, w3, w2),
            Register32Order.CDAB => (w2, w3, w0, w1),
            Register32Order.DCBA => (w3, w2, w1, w0),
            _ => throw new ArgumentOutOfRangeException(nameof(order), order, null),
        };
        return (uint)(a << 24 | b << 16 | c << 8 | d);
    }

    public static Register32Order ToRegister32Order(ByteOrder order) => order switch
    {
        ByteOrder.BigEndian => Register32Order.ABCD,
        ByteOrder.LittleEndian => Register32Order.DCBA,
        ByteOrder.WordSwap => Register32Order.CDAB,
        ByteOrder.ByteSwap => Register32Order.BADC,
        _ => throw new ArgumentOutOfRangeException(nameof(order), order, null),
    };

    private static void RequireRegisters(ReadOnlySpan<ushort> registers, DataType type)
    {
        if (registers.Length < RegisterCount(type))
            throw new ArgumentException($"{type} needs {RegisterCount(type)} register(s), got {registers.Length}.", nameof(registers));
    }

    /// <summary>
    /// Engineering value = decoded × scale. Integers: rounded to 6 decimals (removes scale noise, as before).
    /// Float32: the float's own shortest value first (1.1f → 1.1, not 1.100000023841858), then × scale with 15
    /// significant digits (1.5 × 0.1 → 0.15), so small floats such as 1E-07 are not rounded to 0.
    /// </summary>
    public static double ScaleValue(DataType type, double decoded, double scale)
    {
        if (type != DataType.Float32)
            return Math.Round(decoded * scale, 6);
        double exact = double.Parse(((float)decoded).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture);
        return scale == 1 ? exact
            : double.Parse((exact * scale).ToString("G15", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>"NaN", "+Infinity", "-Infinity" for non-finite Float32 values; null for a normal number.</summary>
    public static string? NonFiniteText(double value) =>
        double.IsNaN(value) ? "NaN" : double.IsPositiveInfinity(value) ? "+Infinity" : double.IsNegativeInfinity(value) ? "-Infinity" : null;

    /// <summary>
    /// Document-based decoding of one register:
    /// raw → data type (Int16 sign) → status lookup or × scale → range check → engineering value.
    /// Range and unknown states only set <see cref="DecodedRegister.Quality"/>; nothing throws.
    /// </summary>
    public static DecodedRegister DecodeEngineering(int address, ushort pduAddress, ushort raw, RegisterDefinition definition) =>
        DecodeEngineering(address, pduAddress, [raw], definition);

    /// <summary>
    /// Document-based decoding of one definition from its registers (1 for 16-bit / Status, 2 for 32-bit, in wire
    /// order). Too few registers → <see cref="RegisterQuality.Incomplete"/> (no value); a NaN / ±Infinity Float32 →
    /// <see cref="RegisterQuality.InvalidValue"/> (no engineering value, no range check). Nothing throws.
    /// </summary>
    public static DecodedRegister DecodeEngineering(int address, ushort pduAddress, ReadOnlySpan<ushort> words, RegisterDefinition definition)
    {
        int count = definition.RegisterCount;
        if (words.Length < count)
            return Incomplete(address, pduAddress, words, definition, definition.Scale);
        ushort raw = words[0];
        ushort[] used = words[..count].ToArray();

        if (count == 2)
        {
            uint bits = Combine32(used[0], used[1], definition.Order32);
            double decoded = Decode(used, definition.DataType, definition.Order32);
            long integer32 = definition.DataType == DataType.Int32 ? unchecked((int)bits) : bits;   // Float32: the bit pattern
            return Numeric(address, pduAddress, used, bits, integer32, decoded, definition);
        }

        long integer = (long)Decode(used, definition.DataType, Register32Order.ABCD);

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

        return Numeric(address, pduAddress, used, null, integer, integer, definition);
    }

    /// <summary>Numeric value × scale (null = ×1, never ×0) with the document range check; non-finite Float32 is invalid.</summary>
    private static DecodedRegister Numeric(int address, ushort pduAddress, ushort[] words, uint? bits, long integer, double decoded,
        RegisterDefinition definition)
    {
        if (NonFiniteText(decoded) is { } text)
            return new DecodedRegister
            {
                Address = address, PduAddress = pduAddress, Raw = words[0], SecondWord = words.Length > 1 ? words[1] : null, Bits32 = bits, Definition = definition,
                Integer = integer, Scale = definition.Scale, ValueText = text, Quality = RegisterQuality.InvalidValue,
            };

        // Document Scaling "-" (null) means "no scaling": computed as ×1 (never ×0), but Scale stays null so it shows as "-".
        double value = ScaleValue(definition.DataType, decoded, definition.Scale ?? 1);
        bool outOfRange = value < (definition.Min ?? double.NegativeInfinity) || value > (definition.Max ?? double.PositiveInfinity);
        return new DecodedRegister
        {
            Address = address, PduAddress = pduAddress, Raw = words[0], SecondWord = words.Length > 1 ? words[1] : null, Bits32 = bits, Definition = definition,
            Integer = integer, Scale = definition.Scale, Value = value, ValueText = RegisterFormat.Number(value),
            Quality = outOfRange ? RegisterQuality.OutOfRange : RegisterQuality.Ok,
        };
    }

    /// <summary>A value whose registers were not all read: the words that are there, no value, never a guess.</summary>
    private static DecodedRegister Incomplete(int address, ushort pduAddress, ReadOnlySpan<ushort> words, RegisterDefinition? definition, double? scale) =>
        new()
        {
            Address = address, PduAddress = pduAddress, Raw = words.Length > 0 ? words[0] : (ushort)0,
            Definition = definition, Integer = words.Length > 0 ? words[0] : 0, Scale = scale,
            ValueText = "Incomplete", Quality = RegisterQuality.Incomplete,
        };

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

    /// <summary>
    /// Generic decoding of one value of any data type from <paramref name="words"/> (no definition, no range): value × manual
    /// scale. Fewer words than the type needs → <see cref="RegisterQuality.Incomplete"/>; NaN / ±Infinity →
    /// <see cref="RegisterQuality.InvalidValue"/> shown as "NaN" / "+Infinity" / "-Infinity" (not a communication error).
    /// </summary>
    public static DecodedRegister DecodeGeneric(int address, ushort pduAddress, ReadOnlySpan<ushort> words, DataType type,
        Register32Order order, double scale)
    {
        if (type == DataType.Status)
            throw new ArgumentException("Status needs a definition (state table).", nameof(type));
        int count = RegisterCount(type);
        if (words.Length < count)
            return Incomplete(address, pduAddress, words, null, scale);
        ushort[] used = words[..count].ToArray();
        uint? bits = count == 2 ? Combine32(used[0], used[1], order) : null;
        double decoded = Decode(used, type, order);
        long integer = type == DataType.Float32 ? bits!.Value : (long)decoded;
        if (NonFiniteText(decoded) is { } text)
            return new DecodedRegister
            {
                Address = address, PduAddress = pduAddress, Raw = used[0], SecondWord = used.Length > 1 ? used[1] : null, Bits32 = bits, Integer = integer,
                Scale = scale, ValueText = text, Quality = RegisterQuality.InvalidValue,
            };
        double value = ScaleValue(type, decoded, scale);
        return new DecodedRegister
        {
            Address = address, PduAddress = pduAddress, Raw = used[0], SecondWord = used.Length > 1 ? used[1] : null, Bits32 = bits, Integer = integer,
            Scale = scale, Value = value, ValueText = RegisterFormat.Number(value),
        };
    }
}
