using System.Diagnostics;
using ModbusTester.Core.Models;
using ModbusTester.Core.Protocol;

namespace ModbusTester.Core.Transport;

/// <summary>
/// Modbus RTU over a serial line: [Slave ID][PDU][CRC]. Responsible only for the serial port, framing,
/// CRC and response-frame validation; address base, decoding and scale stay in the shared upper layers.
/// </summary>
public sealed class ModbusRtuTransport : IModbusTransport
{
    /// <summary>Silence that ends a frame whose length cannot be derived (unexpected function code).</summary>
    private static readonly TimeSpan UnknownFrameSilence = TimeSpan.FromMilliseconds(50);

    private readonly Func<RtuSettings, ISerialChannel> _channelFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISerialChannel? _channel;
    private CancellationTokenSource? _connectionCts;
    private ConnectionState _state = ConnectionState.Disconnected;
    private long _lastActivityTimestamp;

    public ModbusRtuTransport(RtuSettings settings, Func<RtuSettings, ISerialChannel>? channelFactory = null)
    {
        Settings = settings;
        _channelFactory = channelFactory ?? (s => new SerialPortChannel(s));
    }

    public RtuSettings Settings { get; }
    public string Description => $"RTU {Settings.PortName} {Settings.FrameFormat}";
    public string UnitIdLabel => "Slave ID";
    public ConnectionState State => _state;
    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<byte[]>? StaleBytesDiscarded;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await DisconnectAsync();
        SetState(ConnectionState.Connecting);

        ISerialChannel channel = _channelFactory(Settings);
        try
        {
            if (!channel.IsPresent)
                throw new ModbusCommException(ResultStatus.SerialPortUnavailable, $"{Settings.PortName} is not available (not found on this PC)");
            // SerialPort.Open is synchronous and can take a while on some USB drivers.
            await Task.Run(channel.Open, cancellationToken);
        }
        catch (Exception ex)
        {
            channel.Dispose();
            SetState(ex is OperationCanceledException ? ConnectionState.Disconnected : ConnectionState.Error);
            throw ex switch
            {
                ModbusCommException or OperationCanceledException => ex,
                UnauthorizedAccessException => new ModbusCommException(ResultStatus.UnauthorizedAccess,
                    $"{Settings.PortName} access denied (in use by another program?)", ex),
                ArgumentException => new ModbusCommException(ResultStatus.SerialPortUnavailable,
                    $"{Settings.PortName} is not a valid serial port: {ex.Message}", ex),
                IOException => new ModbusCommException(ResultStatus.IOException, $"{Settings.PortName} open failed: {ex.Message}", ex),
                _ => new ModbusCommException(ResultStatus.UnknownError, $"{Settings.PortName} open failed: {ex.GetType().Name}: {ex.Message}", ex),
            };
        }

        _channel = channel;
        _connectionCts = new CancellationTokenSource();
        _lastActivityTimestamp = Stopwatch.GetTimestamp();
        SetState(ConnectionState.Connected);
    }

    public async Task DisconnectAsync()
    {
        // Abort an in-flight request first so the gate is released promptly.
        try
        {
            _connectionCts?.Cancel();
        }
        catch (ObjectDisposedException) { }
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
            if (_channel is null || _connectionCts is null || _state != ConnectionState.Connected)
                throw new ModbusCommException(ResultStatus.NotConnected, "Not connected");

            ISerialChannel channel = _channel;
            CancellationTokenSource connectionCts = _connectionCts;
            var function = (ModbusFunction)pdu[0];
            byte[] tx = ModbusRtuFrame.Build(unitId, pdu);
            var rx = new RxBuffer();
            var details = new List<string>();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionCts.Token);
            try
            {
                await WaitInterFrameDelayAsync(linked.Token);
                byte[] stale = channel.DrainInput();
                if (stale.Length > 0)
                    StaleBytesDiscarded?.Invoke(this, stale);

                await channel.WriteAsync(tx, linked.Token);

                // Response timeout starts after the request has been written.
                linked.CancelAfter(Settings.TimeoutMs);
                await ReadFrameAsync(channel, function, rx, linked.Token);
                _lastActivityTimestamp = Stopwatch.GetTimestamp();

                byte[] frame = rx.Received!;
                byte[] responsePdu = ModbusRtuFrame.Validate(frame, unitId, details);
                return new TransportReply(responsePdu, tx, frame, details);
            }
            catch (ModbusCommException ex)
            {
                ex.TxFrame ??= tx;
                ex.RxFrame ??= rx.Received;
                ex.Details = details;
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (connectionCts.IsCancellationRequested)
            {
                throw new ModbusCommException(ResultStatus.NotConnected, "Request aborted: disconnected") { TxFrame = tx };
            }
            catch (OperationCanceledException)
            {
                _lastActivityTimestamp = Stopwatch.GetTimestamp();
                if (rx.Count > 0)
                {
                    throw new ModbusCommException(ResultStatus.InvalidResponse,
                        $"Invalid Response: truncated frame, received {rx.Count} byte(s), then no more data within {Settings.TimeoutMs} ms")
                    { TxFrame = tx, RxFrame = rx.Received };
                }
                if (!channel.IsPresent)
                {
                    Close(ConnectionState.Error);
                    throw new ModbusCommException(ResultStatus.SerialPortDisconnected, $"{Settings.PortName} disconnected (no longer present)") { TxFrame = tx };
                }
                throw new ModbusCommException(ResultStatus.Timeout, $"Timeout after {Settings.TimeoutMs} ms (no response)") { TxFrame = tx };
            }
            catch (TimeoutException ex)
            {
                // SerialPort.Write timed out (e.g. flow control stuck).
                throw new ModbusCommException(ResultStatus.Timeout, $"Write timeout: {ex.Message}", ex) { TxFrame = tx };
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException)
            {
                bool present = channel.IsPresent;
                Close(ConnectionState.Error);
                ResultStatus status = !present ? ResultStatus.SerialPortDisconnected
                    : ex is UnauthorizedAccessException ? ResultStatus.UnauthorizedAccess
                    : ResultStatus.IOException;
                throw new ModbusCommException(status, $"{Settings.PortName} {(present ? "I/O error" : "disconnected")}: {ex.Message}", ex)
                { TxFrame = tx, RxFrame = rx.Received };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads until the frame length derived from its header has arrived. Serial reads return arbitrary
    /// fragments, so the length is re-evaluated after every chunk: 2 bytes (slave, function) decide
    /// exception vs normal, then the byte count (FC01-04) gives the rest. Progress is kept in
    /// <paramref name="rx"/> so a timeout can tell "nothing" (Timeout) from "partial" (truncated frame).
    /// </summary>
    private static async Task ReadFrameAsync(ISerialChannel channel, ModbusFunction function, RxBuffer rx, CancellationToken ct)
    {
        while (true)
        {
            int expected = ModbusRtuFrame.ExpectedResponseLength(function, rx.Bytes.AsSpan(0, rx.Count));
            if (expected == ModbusRtuFrame.Unknown)
            {
                await ReadUntilSilenceAsync(channel, rx, ct);
                return;
            }
            if (expected > rx.Bytes.Length)
                throw new ModbusCommException(ResultStatus.InvalidResponse,
                    $"Invalid Response: declared frame length {expected} exceeds {rx.Bytes.Length}");
            if (expected != ModbusRtuFrame.NeedMoreBytes && rx.Count >= expected)
                return; // anything after the frame stays in the channel and is drained before the next request

            int target = expected == ModbusRtuFrame.NeedMoreBytes ? rx.Count + 1 : expected;
            rx.Count += await channel.ReadAsync(rx.Bytes.AsMemory(rx.Count, target - rx.Count), ct);
        }
    }

    /// <summary>For a frame whose length is unknown: collect bytes until the line is quiet.</summary>
    private static async Task ReadUntilSilenceAsync(ISerialChannel channel, RxBuffer rx, CancellationToken ct)
    {
        while (rx.Count < rx.Bytes.Length)
        {
            using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
            silence.CancelAfter(UnknownFrameSilence);
            try
            {
                rx.Count += await channel.ReadAsync(rx.Bytes.AsMemory(rx.Count), silence.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private sealed class RxBuffer
    {
        public byte[] Bytes { get; } = new byte[ModbusRtuFrame.MaxFrameLength];
        public int Count { get; set; }
        public byte[]? Received => Count > 0 ? Bytes[..Count] : null;
    }

    private async Task WaitInterFrameDelayAsync(CancellationToken ct)
    {
        TimeSpan remaining = Settings.InterFrameDelay - Stopwatch.GetElapsedTime(_lastActivityTimestamp);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, ct);
    }

    private void Close(ConnectionState newState)
    {
        _channel?.Dispose();
        _channel = null;
        _connectionCts?.Dispose();
        _connectionCts = null;
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
