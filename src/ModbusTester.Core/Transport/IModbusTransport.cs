using ModbusTester.Core.Models;

namespace ModbusTester.Core.Transport;

/// <summary>
/// The only part that differs between Modbus TCP and RTU: connection handling and framing.
/// It sends a PDU and returns the response PDU; function codes, decoding and logging live above it.
/// Failures are thrown as <see cref="Protocol.ModbusCommException"/>.
/// </summary>
public interface IModbusTransport : IAsyncDisposable
{
    ConnectionState State { get; }
    event EventHandler<ConnectionState>? StateChanged;

    /// <summary>Raised when stale bytes (e.g. a late reply to a timed-out request) are discarded.</summary>
    event EventHandler<byte[]>? StaleBytesDiscarded;

    /// <summary>Human-readable endpoint, e.g. "TCP 192.168.137.189:502" or "RTU COM3 9600 / 8E1".</summary>
    string Description { get; }

    /// <summary>What the protocol calls the device address: "Unit ID" (TCP) or "Slave ID" (RTU).</summary>
    string UnitIdLabel => "Unit ID";

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();

    /// <summary>Sends one request and waits for its response within the transport's timeout.</summary>
    Task<TransportReply> SendAsync(byte unitId, byte[] pdu, CancellationToken cancellationToken = default);
}

/// <param name="Pdu">Response PDU (function code + data), framing removed.</param>
/// <param name="TxFrame">Exact bytes written to the wire.</param>
/// <param name="RxFrame">Exact bytes read from the wire.</param>
/// <param name="Details">Transport check results to log, e.g. CRC received/calculated.</param>
public sealed record TransportReply(byte[] Pdu, byte[] TxFrame, byte[] RxFrame, IReadOnlyList<string>? Details = null);
