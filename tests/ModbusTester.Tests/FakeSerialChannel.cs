using ModbusTester.Core.Transport;

namespace ModbusTester.Tests;

/// <summary>
/// Test double for <see cref="ISerialChannel"/>: every write is answered by <see cref="Responder"/>,
/// whose chunks are delivered with optional delays to mimic fragmented serial reads.
/// </summary>
public sealed class FakeSerialChannel(string portName = "COM_TEST") : ISerialChannel
{
    private readonly object _lock = new();
    private readonly List<byte> _received = [];
    private readonly SemaphoreSlim _signal = new(0);
    private bool _open;

    public string PortName { get; } = portName;
    public bool IsPresent { get; set; } = true;
    public Exception? OpenException { get; set; }
    public List<byte[]> Written { get; } = [];
    public int OpenCount { get; private set; }
    public bool IsDisposed { get; private set; }

    /// <summary>Maps a written request frame to response chunks (delay before each chunk, bytes).</summary>
    public Func<byte[], IEnumerable<(TimeSpan Delay, byte[] Bytes)>> Responder { get; set; } = _ => [];

    public void Open()
    {
        if (OpenException is not null)
            throw OpenException;
        OpenCount++;
        _open = true;
    }

    public void Close()
    {
        _open = false;
        _signal.Release();
    }

    public Task WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (!_open)
            throw new ObjectDisposedException(PortName);
        lock (Written)
            Written.Add(data);
        var chunks = Responder(data).ToList();
        _ = Task.Run(async () =>
        {
            foreach (var (delay, bytes) in chunks)
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay);
                Inject(bytes);
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>Delivers bytes as if they arrived on the line.</summary>
    public void Inject(byte[] bytes)
    {
        lock (_lock)
            _received.AddRange(bytes);
        _signal.Release();
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
            }
            if (!_open)
                throw new ObjectDisposedException(PortName);
            await _signal.WaitAsync(cancellationToken);
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

    public void Dispose()
    {
        Close();
        IsDisposed = true;
    }
}
