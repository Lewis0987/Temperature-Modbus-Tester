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
            $"(PDU 0x{request.PduAddress:X4}, {BaseText(request.AddressBase)})  Quantity = {request.Quantity}");

        Exchange<ushort[]> x = await ExchangeAsync(request.UnitId, pdu,
            response => ModbusPdu.ParseReadRegistersResponse(request.Function, response, request.Quantity),
            values => Logger.Log(LogKind.Rx, string.Join(", ", values)),
            cancellationToken);

        return new ModbusResult
        {
            Request = request,
            Status = x.Error?.Status ?? ResultStatus.Pass,
            Registers = x.Value ?? [],
            TxFrame = x.TxFrame,
            RxFrame = x.RxFrame,
            ElapsedMs = x.ElapsedMs,
            ExceptionCode = (x.Error as ModbusSlaveException)?.Code,
            ErrorMessage = x.Error?.Message,
        };
    }

    /// <summary>FC06 Write Single Register (raw value; no scaling or device rules here).</summary>
    public Task<ModbusWriteResult> WriteSingleRegisterAsync(byte unitId, int address, ushort value,
        AddressBase addressBase = AddressBase.OneBased, CancellationToken cancellationToken = default) =>
        WriteRegistersAsync(ModbusWriteRequest.Single(unitId, address, value, addressBase), cancellationToken);

    /// <summary>FC16 Write Multiple Registers (raw values; no scaling or device rules here).</summary>
    public Task<ModbusWriteResult> WriteMultipleRegistersAsync(byte unitId, int address, IReadOnlyList<ushort> values,
        AddressBase addressBase = AddressBase.OneBased, CancellationToken cancellationToken = default) =>
        WriteRegistersAsync(ModbusWriteRequest.Multiple(unitId, address, values, addressBase), cancellationToken);

    /// <summary>
    /// FC06 / FC16. Same path as a read: build PDU → transport (MBAP or RTU+CRC) → exception check → the response
    /// must echo the request (FC06: address + value, FC16: address + quantity), otherwise InvalidResponse.
    /// An invalid request (e.g. FC16 with 0 or more than 123 values) is rejected before anything is sent.
    /// </summary>
    public async Task<ModbusWriteResult> WriteRegistersAsync(ModbusWriteRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Function is not (ModbusFunction.WriteSingleRegister or ModbusFunction.WriteMultipleRegisters))
            throw new ArgumentException($"{request.Function.ToShortName()} is not a register write.", nameof(request));

        byte[] pdu;
        ushort pduAddress;
        try
        {
            pduAddress = request.PduAddress;
            if (request.Function == ModbusFunction.WriteSingleRegister)
            {
                if (request.Values.Count != 1)
                    throw new ArgumentException($"FC06 writes exactly one value, got {request.Values.Count}.", nameof(request));
                pdu = ModbusPdu.BuildWriteSingleRegister(pduAddress, request.Values[0]);
            }
            else
            {
                pdu = ModbusPdu.BuildWriteMultipleRegisters(pduAddress, request.Values);
            }
        }
        catch (ArgumentException ex)
        {
            Logger.Log(LogKind.Fail, $"Invalid request: {ex.Message}");
            return new ModbusWriteResult { Request = request, Status = ResultStatus.InvalidRequest, ErrorMessage = ex.Message };
        }

        string what = request.Function == ModbusFunction.WriteSingleRegister
            ? $"Value = {request.Values[0]}"
            : $"Quantity = {request.Quantity}  Values = {string.Join(", ", request.Values)}";
        Logger.Log(LogKind.Tx,
            $"{request.Function.ToShortName()}  {_transport?.UnitIdLabel ?? "Unit ID"} = {request.UnitId}  Address = {request.StartAddress} " +
            $"(PDU 0x{pduAddress:X4}, {BaseText(request.AddressBase)})  {what}");

        Exchange<bool> x = await ExchangeAsync(request.UnitId, pdu,
            response =>
            {
                if (request.Function == ModbusFunction.WriteSingleRegister)
                    ModbusPdu.ParseWriteSingleRegisterResponse(response, pduAddress, request.Values[0]);
                else
                    ModbusPdu.ParseWriteMultipleRegistersResponse(response, pduAddress, request.Quantity);
                return true;
            },
            _ => { },
            cancellationToken);

        return new ModbusWriteResult
        {
            Request = request,
            Status = x.Error?.Status ?? ResultStatus.Pass,
            TxFrame = x.TxFrame,
            RxFrame = x.RxFrame,
            ElapsedMs = x.ElapsedMs,
            ExceptionCode = (x.Error as ModbusSlaveException)?.Code,
            ErrorMessage = x.Error?.Message,
        };
    }

    private sealed record Exchange<T>(T? Value, ModbusCommException? Error, byte[]? TxFrame, byte[]? RxFrame, double ElapsedMs);

    /// <summary>
    /// One request/response, shared by reads and writes: send through the transport, log the raw frames and transport
    /// details, parse the response PDU, then log Response Time. Failures are logged with a "Result:" line and returned
    /// (never thrown); a successful exchange ends at Response Time.
    /// </summary>
    private async Task<Exchange<T>> ExchangeAsync<T>(byte unitId, byte[] pdu, Func<byte[], T> parse, Action<T> logParsed,
        CancellationToken cancellationToken)
    {
        if (_transport is null)
        {
            var notConnected = new ModbusCommException(ResultStatus.NotConnected, "Not connected");
            LogFailure(notConnected, 0);
            return new(default, notConnected, null, null, 0);
        }

        var stopwatch = Stopwatch.StartNew();
        TransportReply? reply = null;
        try
        {
            reply = await _transport.SendAsync(unitId, pdu, cancellationToken);
            stopwatch.Stop();
            Logger.Log(LogKind.Tx, $"[{HexFormatter.Format(reply.TxFrame)}]");
            Logger.Log(LogKind.Rx, $"[{HexFormatter.Format(reply.RxFrame)}]");
            LogDetails(reply.Details);

            T value = parse(reply.Pdu);
            logParsed(value);
            Logger.Info($"Response Time: {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
            // A successful exchange ends the log at Response Time; only failures get a "Result:" line.
            return new(value, null, reply.TxFrame, reply.RxFrame, stopwatch.Elapsed.TotalMilliseconds);
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
            LogFailure(ex, stopwatch.Elapsed.TotalMilliseconds);
            return new(default, ex, ex.TxFrame, ex.RxFrame, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private void LogFailure(ModbusCommException ex, double elapsedMs)
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
    }

    private static string BaseText(AddressBase addressBase) => addressBase == AddressBase.OneBased ? "1-based" : "0-based";

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
