using System.IO.Ports;
using ModbusTester.Core.Models;

namespace ModbusTester.Core.Transport;

/// <summary>
/// <see cref="ISerialChannel"/> over System.IO.Ports.SerialPort. Received bytes are pumped from the
/// DataReceived event into a buffer, so <see cref="ReadAsync"/> is cancellable (timeout / disconnect /
/// window close) instead of blocking in SerialPort.Read.
/// </summary>
public sealed class SerialPortChannel(RtuSettings settings) : ISerialChannel
{
    private readonly object _lock = new();
    private readonly List<byte> _received = [];
    private readonly SemaphoreSlim _dataSignal = new(0);
    private SerialPort? _port;
    private Exception? _fault;

    public string PortName => settings.PortName;

    public bool IsPresent => SerialPort.GetPortNames().Contains(settings.PortName, StringComparer.OrdinalIgnoreCase);

    /// <summary>COM ports present on this machine, in natural order (COM2 before COM10).</summary>
    public static IReadOnlyList<string> GetPortNames() =>
        SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[3..], out int n) ? n : int.MaxValue)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void Open()
    {
        var port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
        {
            Handshake = Handshake.None,
            ReceivedBytesThreshold = 1,
            WriteTimeout = Math.Max(settings.TimeoutMs, 500),
        };
        port.DataReceived += OnDataReceived;
        try
        {
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
        }
        catch
        {
            port.DataReceived -= OnDataReceived;
            port.Dispose();
            throw;
        }
        lock (_lock)
        {
            _received.Clear();
            _fault = null;
        }
        _port = port;
    }

    public void Close()
    {
        SerialPort? port = _port;
        _port = null;
        if (port is null)
            return;
        port.DataReceived -= OnDataReceived;
        try
        {
            port.Close(); // may throw if the USB adapter was already removed
        }
        catch (Exception) { }
        port.Dispose();
        _dataSignal.Release(); // wake a pending ReadAsync so it sees the port is gone
    }

    public Task WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SerialPort port = _port ?? throw new ObjectDisposedException(PortName, "Serial port is closed");
        // A few bytes at the configured baud rate: a synchronous write (bounded by WriteTimeout) is fine.
        port.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_received.Count > 0)
                {
                    int count = Math.Min(buffer.Length, _received.Count);
                    for (int i = 0; i < count; i++)
                        buffer.Span[i] = _received[i];
                    _received.RemoveRange(0, count);
                    return count;
                }
                if (_fault is not null)
                    throw new IOException(_fault.Message, _fault);
            }
            if (_port is null)
                throw new ObjectDisposedException(PortName, "Serial port is closed");
            await _dataSignal.WaitAsync(cancellationToken);
        }
    }

    public byte[] DrainInput()
    {
        lock (_lock)
        {
            byte[] stale = [.. _received];
            _received.Clear();
            return stale;
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        var port = (SerialPort)sender;
        try
        {
            while (port.IsOpen && port.BytesToRead > 0)
            {
                var chunk = new byte[port.BytesToRead];
                int read = port.Read(chunk, 0, chunk.Length);
                lock (_lock)
                    _received.AddRange(chunk.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            lock (_lock)
                _fault = ex;
        }
        _dataSignal.Release();
    }

    public void Dispose()
    {
        Close();
        _dataSignal.Dispose();
    }
}
