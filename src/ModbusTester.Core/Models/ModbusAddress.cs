namespace ModbusTester.Core.Models;

public static class ModbusAddress
{
    /// <summary>Converts a user address to the 0-based PDU address and validates that the whole range fits.</summary>
    public static ushort ToPdu(int address, int quantity, AddressBase addressBase)
    {
        int pdu = addressBase == AddressBase.OneBased ? address - 1 : address;
        if (pdu < 0 || pdu > ushort.MaxValue)
        {
            string range = addressBase == AddressBase.OneBased ? "1..65536" : "0..65535";
            throw new ArgumentOutOfRangeException(nameof(address), address, $"Address must be in {range}.");
        }
        if (quantity < 1 || pdu + quantity - 1 > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Address + Quantity exceeds 65536.");
        return (ushort)pdu;
    }

    /// <summary>The user-facing address of the register at <paramref name="offset"/> within a request.</summary>
    public static int Display(int startAddress, int offset) => startAddress + offset;
}
