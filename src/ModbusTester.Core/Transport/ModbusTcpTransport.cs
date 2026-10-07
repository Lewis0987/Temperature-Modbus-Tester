using System.Buffers.Binary;
using System.Net.Sockets;
using ModbusTester.Core.Models;
using ModbusTester.Core.Protocol;

namespace ModbusTester.Core.Transport;

/// <summary>Modbus TCP: MBAP header (transaction id, protocol id 0, length, unit id) + PDU over a TCP socket.</summary>
public sealed class ModbusTcpTransport(TcpSettings settings) : IModbusTransport
{
    private const int MbapLength = 7;
    private const int MaxAduLength = 260;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private ushort _transactionId;
    private ConnectionState _state = ConnectionState.Disconnected;

    public TcpSettings Settings { get; } = settings;
    public string Description => $"TCP {Settings.Host}:{Settings.Port}";
    public ConnectionState State => _state;
    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<byte[]>? StaleBytesDiscarded;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await DisconnectAsync();
        SetState(ConnectionState.Connecting);

        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Settings.EffectiveConnectTimeoutMs);
        try
        {
            await client.ConnectAsync(Settings.Host, Settings.Port, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            SetState(ConnectionState.Timeout);
            throw new ModbusCommException(ResultStatus.Timeout, $"Connect timeout ({Settings.EffectiveConnectTimeoutMs} ms)");
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            SetState(ConnectionState.Disconnected);
            throw;
        }
        catch (SocketException ex)
        {
            client.Dispose();
            SetState(ConnectionState.Error);
            throw ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => new ModbusCommException(ResultStatus.ConnectionRefused, "Connection Refused", ex),
                SocketError.TimedOut => new ModbusCommException(ResultStatus.Timeout,
                    $"Connect timeout (the OS gave up before {Settings.EffectiveConnectTimeoutMs} ms)", ex),
                _ => new ModbusCommException(ResultStatus.ConnectionFailed, $"Connect failed: {ex.SocketErrorCode} ({ex.Message})", ex),
            };
        }

        _client = client;
        _stream = client.GetStream();
        SetState(ConnectionState.Connected);
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Close(ConnectionState.Disconnected);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TransportReply> SendAsync(byte unitId, byte[] pdu, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_stream is null || _state != ConnectionState.Connected)
                throw new ModbusCommException(ResultStatus.NotConnected, "Not connected");

            ushort tid = ++_transactionId;
            byte[] tx = BuildAdu(tid, unitId, pdu);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Settings.TimeoutMs);
            try
            {
                DiscardStaleBytes(_stream);
                await _stream.WriteAsync(tx, timeout.Token);
                byte[] rx = await ReadMatchingAduAsync(_stream, tid, timeout.Token);

                if (rx[6] != unitId)
                    throw new ModbusCommException(ResultStatus.InvalidResponse, $"Invalid Response: Unit ID {rx[6]}, expected {unitId}") { RxFrame = rx };

                return new TransportReply(rx[MbapLength..], tx, rx);
            }
            catch (ModbusCommException ex)
            {
                ex.TxFrame ??= tx;
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModbusCommException(ResultStatus.Timeout, $"Timeout ({Settings.TimeoutMs} ms)") { TxFrame = tx };
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                Close(ConnectionState.Error);
                throw new ModbusCommException(ResultStatus.ConnectionLost, $"Connection lost: {ex.Message}", ex) { TxFrame = tx };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static byte[] BuildAdu(ushort transactionId, byte unitId, byte[] pdu)
    {
        var adu = new byte[MbapLength + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(0), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(2), 0); // protocol id
        BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(4), (ushort)(pdu.Length + 1));
        adu[6] = unitId;
        pdu.CopyTo(adu, MbapLength);
        return adu;
    }

    /// <summary>Reads ADUs until one carries <paramref name="tid"/>; older ones are late replies and are dropped.</summary>
    private async Task<byte[]> ReadMatchingAduAsync(NetworkStream stream, ushort tid, CancellationToken ct)
    {
        while (true)
        {
            var header = new byte[MbapLength];
            await ReadExactAsync(stream, header, ct);

            ushort rxTid = BinaryPrimitives.ReadUInt16BigEndian(header);
            ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));

            if (protocolId != 0 || length < 2 || MbapLength - 1 + length > MaxAduLength)
            {
                // The stream is out of sync; nothing after this can be trusted.
                Close(ConnectionState.Error);
                throw new ModbusCommException(ResultStatus.InvalidResponse,
                    $"Invalid Response: bad MBAP header (protocol id {protocolId}, length {length}), connection closed") { RxFrame = header };
            }

            var adu = new byte[MbapLength - 1 + length];
            header.CopyTo(adu, 0);
            await ReadExactAsync(stream, adu.AsMemory(MbapLength), ct);

            if (rxTid == tid)
                return adu;
            StaleBytesDiscarded?.Invoke(this, adu);
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        while (buffer.Length > 0)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                throw new IOException("Remote host closed the connection");
            buffer = buffer[read..];
        }
    }

    private void DiscardStaleBytes(NetworkStream stream)
    {
        if (!stream.DataAvailable)
            return;
        var stale = new List<byte>();
        var buffer = new byte[MaxAduLength];
        while (stream.DataAvailable)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            stale.AddRange(buffer.AsSpan(0, read));
        }
        StaleBytesDiscarded?.Invoke(this, stale.ToArray());
    }

    private void Close(ConnectionState newState)
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        SetState(newState);
    }

    private void SetState(ConnectionState state)
    {
        if (_state == state)
            return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }
}
