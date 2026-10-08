using System.Buffers.Binary;
using ModbusTester.Core.Protocol;

namespace ModbusTester.Tests;

/// <summary>
/// RTU slave logic for <see cref="FakeSerialChannel.Responder"/>: FC03 / FC06 / FC16 against an in-memory register
/// store (indexed by PDU address), with knobs to simulate exceptions, silence, truncation and corrupted responses.
/// </summary>
public sealed class FakeRtuSlave
{
    public ushort[] Registers { get; set; } = new ushort[300];
    public byte SlaveId { get; set; } = 1;
    public byte? ForcedExceptionCode { get; set; }
    public bool Silent { get; set; }
    /// <summary>Send only the first N bytes of the response frame.</summary>
    public int? TruncateTo { get; set; }
    /// <summary>Deliver the response one byte at a time (fragmented serial reads).</summary>
    public bool Fragmented { get; set; }
    /// <summary>Corrupt the response PDU (before CRC is added).</summary>
    public Func<byte[], byte[]>? PduMutator { get; set; }
    public List<byte[]> Requests { get; } = [];

    public IEnumerable<(TimeSpan Delay, byte[] Bytes)> Respond(byte[] requestFrame)
    {
        Requests.Add(requestFrame);
        if (Silent)
            return [];
        byte[] pdu = requestFrame[1..^2];
        byte[] responsePdu = ForcedExceptionCode is { } code ? [(byte)(pdu[0] | 0x80), code] : Handle(pdu);
        if (PduMutator is not null)
            responsePdu = PduMutator(responsePdu);
        byte[] frame = ModbusRtuFrame.Build(SlaveId, responsePdu);
        if (TruncateTo is { } n)
            frame = frame[..n];
        return Fragmented ? frame.Select(b => (TimeSpan.FromMilliseconds(1), new[] { b })).ToArray() : [(TimeSpan.Zero, frame)];
    }

    private byte[] Handle(byte[] pdu)
    {
        byte fc = pdu[0];
        int address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        int second = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
        switch (fc)
        {
            case 0x03:
            {
                if (address + second > Registers.Length)
                    return [0x83, 0x02];
                var response = new byte[2 + second * 2];
                response[0] = 0x03;
                response[1] = (byte)(second * 2);
                for (int i = 0; i < second; i++)
                    BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + i * 2), Registers[address + i]);
                return response;
            }
            case 0x06:
                if (address >= Registers.Length)
                    return [0x86, 0x02];
                Registers[address] = (ushort)second;
                return pdu[..5];
            case 0x10:
                if (address + second > Registers.Length)
                    return [0x90, 0x02];
                for (int i = 0; i < second; i++)
                    Registers[address + i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + i * 2));
                return pdu[..5];
            default:
                return [(byte)(fc | 0x80), 0x01];
        }
    }
}
