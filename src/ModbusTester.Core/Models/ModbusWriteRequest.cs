namespace ModbusTester.Core.Models;

/// <summary>
/// A register write as the user entered it: raw 16-bit values only (no scaling, ranges or device rules – those
/// belong to a device profile on top). <see cref="PduAddress"/> is what goes on the wire; the Address Base is
/// applied there once, exactly as for reads.
/// </summary>
public sealed record ModbusWriteRequest(
    ModbusFunction Function,
    byte UnitId,
    int StartAddress,
    IReadOnlyList<ushort> Values,
    AddressBase AddressBase = AddressBase.OneBased)
{
    public ushort Quantity => (ushort)Values.Count;

    /// <summary>The 0-based address of the first register; also checks that the whole range fits in 65536 registers.</summary>
    public ushort PduAddress => ModbusAddress.ToPdu(StartAddress, Math.Max(1, Values.Count), AddressBase);

    /// <summary>FC06 Write Single Register.</summary>
    public static ModbusWriteRequest Single(byte unitId, int address, ushort value, AddressBase addressBase = AddressBase.OneBased) =>
        new(ModbusFunction.WriteSingleRegister, unitId, address, [value], addressBase);

    /// <summary>FC16 Write Multiple Registers.</summary>
    public static ModbusWriteRequest Multiple(byte unitId, int address, IReadOnlyList<ushort> values, AddressBase addressBase = AddressBase.OneBased) =>
        new(ModbusFunction.WriteMultipleRegisters, unitId, address, values, addressBase);
}

public sealed record ModbusWriteResult
{
    public required ModbusWriteRequest Request { get; init; }
    public required ResultStatus Status { get; init; }
    public byte[]? TxFrame { get; init; }
    public byte[]? RxFrame { get; init; }
    public double ElapsedMs { get; init; }
    public Protocol.ModbusExceptionCode? ExceptionCode { get; init; }
    public string? ErrorMessage { get; init; }

    public bool IsSuccess => Status == ResultStatus.Pass;
}
