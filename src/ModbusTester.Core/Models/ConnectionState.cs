namespace ModbusTester.Core.Models;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Timeout,
    Error,
}
