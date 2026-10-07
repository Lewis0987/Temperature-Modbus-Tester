namespace ModbusTester.Core.Transport;

/// <summary>
/// Raw byte I/O over a serial line. Production: <see cref="SerialPortChannel"/>.
/// Tests supply their own implementation, so <see cref="ModbusRtuTransport"/> has no test-only code paths.
/// </summary>
public interface ISerialChannel : IDisposable
{
    string PortName { get; }

    /// <summary>Whether the port currently exists on the system (e.g. USB adapter still plugged in).</summary>
    bool IsPresent { get; }

    /// <exception cref="UnauthorizedAccessException">Port in use by another program.</exception>
    /// <exception cref="IOException">Port cannot be opened.</exception>
    void Open();
    void Close();

    Task WriteAsync(byte[] data, CancellationToken cancellationToken);

    /// <summary>Reads at least one byte, waiting until data arrives or <paramref name="cancellationToken"/> fires.</summary>
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Removes and returns everything already received but not yet read.</summary>
    byte[] DrainInput();
}
