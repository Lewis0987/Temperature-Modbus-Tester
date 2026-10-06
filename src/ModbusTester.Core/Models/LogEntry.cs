namespace ModbusTester.Core.Models;

public enum LogKind
{
    Info,
    Tx,
    Rx,
    Pass,
    Fail,
    Error,
}

public sealed record LogEntry(DateTime Timestamp, LogKind Kind, string Message)
{
    public override string ToString() => $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff}  {Kind,-5} {Message}";
}
