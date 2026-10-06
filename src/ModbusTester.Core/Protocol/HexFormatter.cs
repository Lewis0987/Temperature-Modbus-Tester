namespace ModbusTester.Core.Protocol;

public static class HexFormatter
{
    public static string Format(byte[]? bytes) =>
        bytes is null ? "" : string.Join(' ', bytes.Select(b => b.ToString("X2")));
}
