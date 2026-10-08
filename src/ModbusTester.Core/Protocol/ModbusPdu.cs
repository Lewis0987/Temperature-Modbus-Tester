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

    /// <summary>FC16 limit: 123 registers (246 data bytes) per request.</summary>
    public const int MaxWriteRegisters = 123;

    /// <summary>FC06 request: 06 | address | value.</summary>
    public static byte[] BuildWriteSingleRegister(ushort address, ushort value)
    {
        var pdu = new byte[5];
        pdu[0] = (byte)ModbusFunction.WriteSingleRegister;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        return pdu;
    }

    /// <summary>FC16 request: 10 | start address | quantity | byte count (= quantity × 2) | values.</summary>
    public static byte[] BuildWriteMultipleRegisters(ushort address, IReadOnlyList<ushort> values)
    {
        if (values.Count < 1 || values.Count > MaxWriteRegisters)
            throw new ArgumentOutOfRangeException(nameof(values), values.Count, $"Quantity must be 1..{MaxWriteRegisters} for FC16.");

        var pdu = new byte[6 + values.Count * 2];
        pdu[0] = (byte)ModbusFunction.WriteMultipleRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Count);
        pdu[5] = (byte)(values.Count * 2);
        for (int i = 0; i < values.Count; i++)
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + i * 2), values[i]);
        return pdu;
    }

    /// <summary>FC06 response must echo the request: 06 | same address | same value. Anything else is InvalidResponse.</summary>
    public static void ParseWriteSingleRegisterResponse(ReadOnlySpan<byte> pdu, ushort address, ushort value)
    {
        ThrowIfException(ModbusFunction.WriteSingleRegister, pdu);
        if (pdu.Length != 5)
            throw Invalid($"PDU length {pdu.Length}, expected 5.");
        ushort echoedAddress = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(1));
        ushort echoedValue = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(3));
        if (echoedAddress != address)
            throw Invalid($"Address 0x{echoedAddress:X4} in response, expected 0x{address:X4}.");
        if (echoedValue != value)
            throw Invalid($"Value {echoedValue} in response, expected {value}.");
    }

    /// <summary>FC16 response: 10 | same start address | same quantity. Anything else is InvalidResponse.</summary>
    public static void ParseWriteMultipleRegistersResponse(ReadOnlySpan<byte> pdu, ushort address, ushort quantity)
    {
        ThrowIfException(ModbusFunction.WriteMultipleRegisters, pdu);
        if (pdu.Length != 5)
            throw Invalid($"PDU length {pdu.Length}, expected 5.");
        ushort echoedAddress = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(1));
        ushort echoedQuantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(3));
        if (echoedAddress != address)
            throw Invalid($"Address 0x{echoedAddress:X4} in response, expected 0x{address:X4}.");
        if (echoedQuantity != quantity)
            throw Invalid($"Quantity {echoedQuantity} in response, expected {quantity}.");
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
