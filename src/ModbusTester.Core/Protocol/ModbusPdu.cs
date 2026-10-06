using System.Buffers.Binary;
using ModbusTester.Core.Models;

namespace ModbusTester.Core.Protocol;

/// <summary>
/// Builds and parses Modbus PDUs (function code + data). Framing (MBAP for TCP, address + CRC for RTU)
/// is the transport's job, so everything here is shared by TCP and RTU.
/// </summary>
public static class ModbusPdu
{
    public const int MaxReadRegisters = 125;
    public const int MaxReadBits = 2000;

    public static byte[] BuildReadRequest(ModbusFunction function, ushort address, ushort quantity)
    {
        int max = function switch
        {
            ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters => MaxReadRegisters,
            ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs => MaxReadBits,
            _ => throw new ArgumentException($"{function.ToShortName()} is not a read function.", nameof(function)),
        };
        if (quantity < 1 || quantity > max)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, $"Quantity must be 1..{max} for {function.ToShortName()}.");

        var pdu = new byte[5];
        pdu[0] = (byte)function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), quantity);
        return pdu;
    }

    /// <summary>Parses an FC03/FC04 response. Throws <see cref="ModbusSlaveException"/> or InvalidResponse.</summary>
    public static ushort[] ParseReadRegistersResponse(ModbusFunction function, ReadOnlySpan<byte> pdu, ushort expectedQuantity)
    {
        ThrowIfException(function, pdu);

        if (pdu.Length < 2)
            throw Invalid($"Response too short ({pdu.Length} bytes).");
        int byteCount = pdu[1];
        if (byteCount != expectedQuantity * 2)
            throw Invalid($"Byte count {byteCount}, expected {expectedQuantity * 2}.");
        if (pdu.Length != 2 + byteCount)
            throw Invalid($"PDU length {pdu.Length}, expected {2 + byteCount}.");

        var values = new ushort[expectedQuantity];
        for (int i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(2 + i * 2));
        return values;
    }

    /// <summary>Throws if the PDU is empty, an exception response, or answers a different function.</summary>
    public static void ThrowIfException(ModbusFunction function, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length == 0)
            throw Invalid("Empty response PDU.");
        if (pdu[0] == ((byte)function | 0x80))
        {
            if (pdu.Length != 2)
                throw Invalid($"Exception response length {pdu.Length}, expected 2.");
            throw new ModbusSlaveException((ModbusExceptionCode)pdu[1]);
        }
        if (pdu[0] != (byte)function)
            throw Invalid($"Function code 0x{pdu[0]:X2} in response, expected 0x{(byte)function:X2}.");
    }

    private static ModbusCommException Invalid(string message) =>
        new(ResultStatus.InvalidResponse, "Invalid Response: " + message);
}
