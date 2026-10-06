using ModbusTester.Core.Models;

namespace ModbusTester.Core.Protocol;

/// <summary>Any failure of a Modbus transaction, classified by <see cref="Status"/>.</summary>
public class ModbusCommException(ResultStatus status, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ResultStatus Status { get; } = status;

    /// <summary>Raw frames captured before the failure, if any (set by the transport).</summary>
    public byte[]? TxFrame { get; set; }
    public byte[]? RxFrame { get; set; }
}

/// <summary>The slave answered with a Modbus exception response (function code | 0x80).</summary>
public sealed class ModbusSlaveException(ModbusExceptionCode code)
    : ModbusCommException(ResultStatus.ModbusException, $"Modbus Exception 0x{(byte)code:X2}: {code.Describe()}")
{
    public ModbusExceptionCode Code { get; } = code;
}
