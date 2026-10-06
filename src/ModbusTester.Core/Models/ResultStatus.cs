namespace ModbusTester.Core.Models;

public enum ResultStatus
{
    Pass,
    NotConnected,
    Timeout,
    ConnectionRefused,
    ConnectionFailed,
    ConnectionLost,
    InvalidResponse,
    CrcError,
    ModbusException,
    InvalidRequest,
    Error,
}
