namespace ModbusTester.Core.Services;

public enum DataType
{
    UInt16,
    Int16,
    UInt32,
    Int32,
    Float32,
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
/// Turns raw 16-bit registers into a numeric value. Phase 1 implements 16-bit types;
/// 32-bit types and byte orders are declared so callers already pass them, and are filled in by Phase 4.
/// </summary>
public static class RegisterDecoder
{
    public static int RegisterCount(DataType type) => type switch
    {
        DataType.UInt16 or DataType.Int16 => 1,
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
            DataType.UInt16 => first,
            DataType.Int16 => (short)first,
            _ => throw new NotSupportedException($"{type} decoding is planned for Phase 4."),
        };
    }
}
