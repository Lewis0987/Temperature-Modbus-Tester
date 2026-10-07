namespace ModbusTester.Core.Models;

public sealed record RegisterRow(int Address, ushort Raw, double Decoded, double Scale)
{
    public string Hex => RegisterFormat.Hex(Raw);
    public string Binary => RegisterFormat.Binary(Raw);
    public double Value => Math.Round(Decoded * Scale, 6);
}
