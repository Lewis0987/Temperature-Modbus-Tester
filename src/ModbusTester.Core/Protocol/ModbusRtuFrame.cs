using ModbusTester.Core.Models;

namespace ModbusTester.Core.Protocol;

/// <summary>
/// RTU framing: [Slave ID][PDU][CRC lo][CRC hi]. Pure functions so framing can be tested without a serial port.
/// Address conversion is NOT done here; the PDU already carries the 0-based PDU address.
/// </summary>
public static class ModbusRtuFrame
{
    public const int MinFrameLength = 4;     // slave + function + CRC(2)
    public const int MaxFrameLength = 256;

    /// <summary>Returned by <see cref="ExpectedResponseLength"/> when more bytes are needed to know the length.</summary>
    public const int NeedMoreBytes = -1;

    /// <summary>Returned when the length cannot be derived (unexpected function code); read until the line is silent.</summary>
    public const int Unknown = -2;

    public static byte[] Build(byte slaveId, ReadOnlySpan<byte> pdu) => Crc16.Append([slaveId, .. pdu]);

    /// <summary>
    /// Total response frame length derived from the bytes received so far.
    /// Exception: 5 bytes. FC01-04: 3 + byte count + 2. FC05/06/15/16: 8 bytes.
    /// </summary>
    public static int ExpectedResponseLength(ModbusFunction requestFunction, ReadOnlySpan<byte> received)
    {
        if (received.Length < 2)
            return NeedMoreBytes;

        byte function = received[1];
        if (function == ((byte)requestFunction | 0x80))
            return 5;
        if (function != (byte)requestFunction)
            return Unknown;

        switch (requestFunction)
        {
            case ModbusFunction.ReadCoils:
            case ModbusFunction.ReadDiscreteInputs:
            case ModbusFunction.ReadHoldingRegisters:
            case ModbusFunction.ReadInputRegisters:
                return received.Length < 3 ? NeedMoreBytes : 3 + received[2] + 2;
            case ModbusFunction.WriteSingleCoil:
            case ModbusFunction.WriteSingleRegister:
            case ModbusFunction.WriteMultipleCoils:
            case ModbusFunction.WriteMultipleRegisters:
                return 8;
            default:
                return Unknown;
        }
    }

    /// <summary>
    /// Checks a complete response frame in order: length → CRC → slave ID, and returns its PDU.
    /// Function code and exception code are checked afterwards by <see cref="ModbusPdu"/> (shared with TCP).
    /// </summary>
    /// <param name="details">Receives human-readable check results (CRC received/calculated, slave ID).</param>
    public static byte[] Validate(byte[] frame, byte expectedSlaveId, List<string> details)
    {
        if (frame.Length < MinFrameLength)
            throw new ModbusCommException(ResultStatus.InvalidResponse,
                $"Invalid Response: frame too short ({frame.Length} bytes, minimum {MinFrameLength})");

        ushort received = Crc16.ReadFromFrame(frame);
        ushort calculated = Crc16.Compute(frame.AsSpan(0, frame.Length - 2));
        string crcLine = $"CRC Received = {received:X4} [{frame[^2]:X2} {frame[^1]:X2}]  CRC Calculated = {calculated:X4}";
        if (received != calculated)
        {
            details.Add(crcLine + "  -> CRC ERROR");
            throw new ModbusCommException(ResultStatus.CrcError,
                $"CRC Error (received {received:X4}, calculated {calculated:X4})");
        }
        details.Add(crcLine + "  -> CRC PASS");

        if (frame[0] != expectedSlaveId)
        {
            details.Add($"Expected Slave ID = {expectedSlaveId}  Received Slave ID = {frame[0]}");
            throw new ModbusCommException(ResultStatus.InvalidResponse,
                $"Invalid Response: Expected Slave ID = {expectedSlaveId}, Received Slave ID = {frame[0]}");
        }

        return frame[1..^2];
    }
}
