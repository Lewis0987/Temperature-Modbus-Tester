namespace ModbusTester.Core.Models;

public sealed record RegisterRow(int Address, ushort Raw, double Decoded, double Scale)
{
    public string Hex => Raw.ToString("X4");
    public string Binary => Convert.ToString(Raw, 2).PadLeft(16, '0').Insert(12, " ").Insert(8, " ").Insert(4, " ");
    public double Value => Math.Round(Decoded * Scale, 6);
}
