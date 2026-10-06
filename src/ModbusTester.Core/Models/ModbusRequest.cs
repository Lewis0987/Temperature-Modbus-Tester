namespace ModbusTester.Core.Models;

/// <summary>A read request as the user entered it. <see cref="PduAddress"/> is what goes on the wire.</summary>
public sealed record ModbusRequest(
    ModbusFunction Function,
    byte UnitId,
    int StartAddress,
    ushort Quantity,
    AddressBase AddressBase = AddressBase.OneBased)
{
    public ushort PduAddress => ModbusAddress.ToPdu(StartAddress, Quantity, AddressBase);
}
