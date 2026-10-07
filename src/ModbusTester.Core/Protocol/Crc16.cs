namespace ModbusTester.Core.Protocol;

/// <summary>Modbus RTU CRC-16: reflected polynomial 0xA001, initial value 0xFFFF, sent low byte first.</summary>
public static class Crc16
{
    private static readonly ushort[] Table = BuildTable();

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
            crc = (ushort)((crc >> 8) ^ Table[(crc ^ b) & 0xFF]);
        return crc;
    }

    /// <summary>Returns <paramref name="data"/> followed by its CRC (low byte, high byte).</summary>
    public static byte[] Append(ReadOnlySpan<byte> data)
    {
        ushort crc = Compute(data);
        var frame = new byte[data.Length + 2];
        data.CopyTo(frame);
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    /// <summary>The CRC carried in the last two bytes of a frame (low byte first).</summary>
    public static ushort ReadFromFrame(ReadOnlySpan<byte> frame) => (ushort)(frame[^2] | (frame[^1] << 8));

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)i;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            table[i] = crc;
        }
        return table;
    }
}
