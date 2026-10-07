using System.Diagnostics;
using ModbusTester.Core.Models;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Transport;

namespace ModbusTester.Core.Services;

/// <summary>
/// Transport-independent Modbus operations: builds the PDU, sends it through the current
/// <see cref="IModbusTransport"/> (TCP or RTU), times it, classifies failures and writes the log.
/// Communication failures are returned as a <see cref="ModbusResult"/>, never thrown.
/// </summary>
public sealed class ModbusService(CommunicationLogger logger) : IAsyncDisposable
{
    private IModbusTransport? _transport;

    public CommunicationLogger Logger { get; } = logger;
    public ConnectionState State => _transport?.State ?? ConnectionState.Disconnected;
    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>Connects with a new transport, replacing (and closing) any previous one.</summary>
    public async Task<bool> ConnectAsync(IModbusTransport transport, CancellationToken cancellationToken = default)
    {
        await ReleaseTransportAsync();
        _transport = transport;
        _transport.StateChanged += OnTransportStateChanged;
        _transport.StaleBytesDiscarded += OnStaleBytesDiscarded;

        Logger.Info($"{transport.Description} Connect");
        try
        {
            await transport.ConnectAsync(cancellationToken);
            Logger.Info("Connected");
            return true;
        }
        catch (ModbusCommException ex)
        {
            Logger.Log(LogKind.Fail, $"Connect FAIL: {ex.Message}");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // e.g. host name that cannot be resolved
            Logger.Log(LogKind.Fail, $"Connect FAIL: {ex.Message}");
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_transport is null)
            return;
        await _transport.DisconnectAsync();
        Logger.Info($"{_transport.Description} Disconnected");
    }

    /// <summary>
    /// Drops the current transport (closing it if still open) and reports <see cref="ConnectionState.Disconnected"/>,
    /// so an Error/Timeout state of one connection type is not carried over to another. The state change is
    /// raised synchronously, before the old transport is disposed. The log is left untouched.
    /// </summary>
    public async Task ResetAsync()
    {
        IModbusTransport? old = _transport;
        if (old is null)
            return;
        _transport = null;
        old.StateChanged -= OnTransportStateChanged;
        old.StaleBytesDiscarded -= OnStaleBytesDiscarded;
        bool wasOpen = old.State is ConnectionState.Connected or ConnectionState.Connecting;
        StateChanged?.Invoke(this, ConnectionState.Disconnected);

        await old.DisposeAsync();
        if (wasOpen)
            Logger.Info($"{old.Description} Disconnected");
    }

    /// <summary>FC03 Read Holding Registers / FC04 Read Input Registers.</summary>
    public async Task<ModbusResult> ReadRegistersAsync(ModbusRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Function is not (ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters))
            throw new ArgumentException($"{request.Function.ToShortName()} is not a register read.", nameof(request));

        byte[] pdu;
        try
        {
            pdu = ModbusPdu.BuildReadRequest(request.Function, request.PduAddress, request.Quantity);
        }
        catch (ArgumentException ex)
        {
            Logger.Log(LogKind.Fail, $"Invalid request: {ex.Message}");
            return new ModbusResult { Request = request, Status = ResultStatus.InvalidRequest, ErrorMessage = ex.Message };
        }

        Logger.Log(LogKind.Tx,
            $"{request.Function.ToShortName()}  {_transport?.UnitIdLabel ?? "Unit ID"} = {request.UnitId}  Address = {request.StartAddress} " +
            $"(PDU 0x{request.PduAddress:X4}, {(request.AddressBase == AddressBase.OneBased ? "1-based" : "0-based")})  Quantity = {request.Quantity}");

        if (_transport is null)
            return Fail(request, new ModbusCommException(ResultStatus.NotConnected, "Not connected"), 0);

        var stopwatch = Stopwatch.StartNew();
        TransportReply? reply = null;
        try
        {
            reply = await _transport.SendAsync(request.UnitId, pdu, cancellationToken);
            stopwatch.Stop();
            Logger.Log(LogKind.Tx, $"[{HexFormatter.Format(reply.TxFrame)}]");
            Logger.Log(LogKind.Rx, $"[{HexFormatter.Format(reply.RxFrame)}]");
            LogDetails(reply.Details);

            ushort[] values = ModbusPdu.ParseReadRegistersResponse(request.Function, reply.Pdu, request.Quantity);
            Logger.Log(LogKind.Rx, string.Join(", ", values));
            Logger.Info($"Response Time: {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
            // A successful read ends the log at Response Time; only failures get a "Result:" line (see Fail).

            return new ModbusResult
            {
                Request = request,
                Status = ResultStatus.Pass,
                Registers = values,
                TxFrame = reply.TxFrame,
                RxFrame = reply.RxFrame,
                ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            };
        }
        catch (ModbusCommException ex)
        {
            stopwatch.Stop();
            if (reply is null)
            {
                // Failed inside the transport: its frames have not been logged yet.
                if (ex.TxFrame is not null)
                    Logger.Log(LogKind.Tx, $"[{HexFormatter.Format(ex.TxFrame)}]");
                if (ex.RxFrame is not null)
                    Logger.Log(LogKind.Rx, $"[{HexFormatter.Format(ex.RxFrame)}]");
                LogDetails(ex.Details);
            }
            ex.TxFrame ??= reply?.TxFrame;
            ex.RxFrame ??= reply?.RxFrame;
            return Fail(request, ex, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private ModbusResult Fail(ModbusRequest request, ModbusCommException ex, double elapsedMs)
    {
        if (elapsedMs > 0)
            Logger.Info($"Response Time: {elapsedMs:F0} ms");
        string label = ex.Status switch
        {
            ResultStatus.Timeout => "TIMEOUT",
            ResultStatus.CrcError => "CRC ERROR",
            _ => "FAIL",
        };
        Logger.Log(LogKind.Fail, $"Result: {label} [{ex.Status}] - {ex.Message}");

        return new ModbusResult
        {
            Request = request,
            Status = ex.Status,
            TxFrame = ex.TxFrame,
            RxFrame = ex.RxFrame,
            ElapsedMs = elapsedMs,
            ExceptionCode = (ex as ModbusSlaveException)?.Code,
            ErrorMessage = ex.Message,
        };
    }

    private void LogDetails(IReadOnlyList<string>? details)
    {
        foreach (string line in details ?? [])
            Logger.Info(line);
    }

    private void OnTransportStateChanged(object? sender, ConnectionState state) => StateChanged?.Invoke(this, state);

    private void OnStaleBytesDiscarded(object? sender, byte[] bytes) =>
        Logger.Info($"Discarded {bytes.Length} stale byte(s): [{HexFormatter.Format(bytes)}]");

    private async Task ReleaseTransportAsync()
    {
        if (_transport is null)
            return;
        _transport.StateChanged -= OnTransportStateChanged;
        _transport.StaleBytesDiscarded -= OnStaleBytesDiscarded;
        await _transport.DisposeAsync();
        _transport = null;
    }

    public ValueTask DisposeAsync() => new(ReleaseTransportAsync());
}
