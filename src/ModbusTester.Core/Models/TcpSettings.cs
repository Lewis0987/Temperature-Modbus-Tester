namespace ModbusTester.Core.Models;

/// <param name="TimeoutMs">Timeout for both the TCP connect and each Modbus request/response.</param>
/// <param name="ConnectTimeoutMs">
/// Optional separate connect timeout; null (the default) means the connect uses <paramref name="TimeoutMs"/> too.
/// Note: Windows retries a refused SYN for roughly 1-2 s before reporting "Connection Refused", so with a very
/// short timeout a refused port can be reported as a connect timeout instead.
/// </param>
public sealed record TcpSettings(string Host, int Port = 502, int TimeoutMs = 1000, int? ConnectTimeoutMs = null)
{
    /// <summary>The connect timeout actually applied (and logged).</summary>
    public int EffectiveConnectTimeoutMs => ConnectTimeoutMs ?? TimeoutMs;
}
