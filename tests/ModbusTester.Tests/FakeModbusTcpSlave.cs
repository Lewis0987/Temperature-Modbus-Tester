using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ModbusTester.Tests;

/// <summary>Minimal in-process Modbus TCP slave on 127.0.0.1 for transport/service tests.</summary>
public sealed class FakeModbusTcpSlave : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = [];
    private readonly Task _acceptLoop;

    /// <summary>Holding registers indexed by 0-based PDU address.</summary>
    public ushort[] Registers { get; set; } = new ushort[10];
    public TimeSpan ResponseDelay { get; set; }
    public bool CloseOnRequest { get; set; }
    public byte? OverrideUnitId { get; set; }
    public List<byte[]> ReceivedAdus { get; } = [];

    /// <summary>When set, every request is answered with this Modbus exception code (function | 0x80).</summary>
    public byte? ForcedExceptionCode { get; set; }

    /// <summary>Optional hook to corrupt the normal response PDU (wrong echo, truncation, other function...).</summary>
    public Func<byte[], byte[]>? ResponseMutator { get; set; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FakeModbusTcpSlave()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                lock (_clients)
                    _clients.Add(client);
                _ = ServeAsync(client);
            }
        }
        catch (Exception) { /* stopped */ }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            while (!_cts.IsCancellationRequested)
            {
                var header = new byte[7];
                await stream.ReadExactlyAsync(header, _cts.Token);
                int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                var pdu = new byte[length - 1];
                await stream.ReadExactlyAsync(pdu, _cts.Token);
                lock (ReceivedAdus)
                    ReceivedAdus.Add([.. header, .. pdu]);

                if (CloseOnRequest)
                {
                    client.Close();
                    return;
                }

                byte[] response = ForcedExceptionCode is { } code ? [(byte)(pdu[0] | 0x80), code] : Handle(pdu);
                if (ResponseMutator is not null)
                    response = ResponseMutator(response);
                var adu = new byte[7 + response.Length];
                header.AsSpan(0, 4).CopyTo(adu); // transaction id + protocol id
                BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(4), (ushort)(response.Length + 1));
                adu[6] = OverrideUnitId ?? header[6];
                response.CopyTo(adu, 7);

                if (ResponseDelay > TimeSpan.Zero)
                    await Task.Delay(ResponseDelay, _cts.Token);
                await stream.WriteAsync(adu, _cts.Token);
            }
        }
        catch (Exception) { /* client gone or stopped */ }
    }

    private byte[] Handle(byte[] pdu)
    {
        byte fc = pdu[0];
        if (fc is 0x06 or 0x10)
            return HandleWrite(pdu);
        if (fc is not (0x03 or 0x04))
            return [(byte)(fc | 0x80), 0x01];

        int address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        int quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
        if (address + quantity > Registers.Length)
            return [(byte)(fc | 0x80), 0x02];

        var response = new byte[2 + quantity * 2];
        response[0] = fc;
        response[1] = (byte)(quantity * 2);
        for (int i = 0; i < quantity; i++)
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + i * 2), Registers[address + i]);
        return response;
    }

    /// <summary>FC06 / FC16: store into <see cref="Registers"/> and echo as a real slave does.</summary>
    private byte[] HandleWrite(byte[] pdu)
    {
        byte fc = pdu[0];
        int address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        if (fc == 0x06)
        {
            if (address >= Registers.Length)
                return [0x86, 0x02];
            Registers[address] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
            return pdu[..5];   // echo
        }

        int quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
        if (quantity is < 1 or > 123 || pdu[5] != quantity * 2 || pdu.Length != 6 + quantity * 2)
            return [0x90, 0x03];
        if (address + quantity > Registers.Length)
            return [0x90, 0x02];
        for (int i = 0; i < quantity; i++)
            Registers[address + i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + i * 2));
        return pdu[..5];       // 10 | start address | quantity
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        lock (_clients)
            _clients.ForEach(c => c.Dispose());
        await _acceptLoop;
        _cts.Dispose();
    }
}
