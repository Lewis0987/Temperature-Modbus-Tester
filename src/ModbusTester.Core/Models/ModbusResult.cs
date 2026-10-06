using ModbusTester.Core.Protocol;

namespace ModbusTester.Core.Models;

public sealed record ModbusResult
{
    public required ModbusRequest Request { get; init; }
    public required ResultStatus Status { get; init; }
    public IReadOnlyList<ushort> Registers { get; init; } = [];
    public byte[]? TxFrame { get; init; }
    public byte[]? RxFrame { get; init; }
    public double ElapsedMs { get; init; }
    public ModbusExceptionCode? ExceptionCode { get; init; }
    public string? ErrorMessage { get; init; }

    public bool IsSuccess => Status == ResultStatus.Pass;
}
