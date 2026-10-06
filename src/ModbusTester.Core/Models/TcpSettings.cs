namespace ModbusTester.Core.Models;

/// <param name="TimeoutMs">Response timeout per request.</param>
/// <param name="ConnectTimeoutMs">
/// TCP connect timeout. Kept separate and longer because Windows retries a refused SYN for ~1-2 s
/// before reporting "Connection Refused"; a short timeout would misreport it as Timeout.
/// </param>
public sealed record TcpSettings(string Host, int Port = 502, int TimeoutMs = 1000, int ConnectTimeoutMs = 3000);
