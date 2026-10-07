using ModbusTester.Core.Models;
using ModbusTester.Core.Protocol;

namespace ModbusTester.Tests;

/// <summary>CRC16 and RTU framing. Reference CRCs are published Modbus examples, cross-checked with a bitwise implementation.</summary>
public class Crc16Tests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    [Theory]
    [InlineData("01 03 00 00 00 06", "C5 C8")]   // FC03 Register 1..6 (Siemens first read)
    [InlineData("01 03 00 00 00 01", "84 0A")]
    [InlineData("01 04 00 00 00 01", "31 CA")]
    [InlineData("01 03 02 00 01", "79 84")]      // response
    [InlineData("01 83 02", "C0 F1")]            // exception response
    [InlineData("01 03 0C 09 E6 00 00 12 F8 00 00 02 8A 00 00", "1F 6F")]
    public void Append_KnownVectors_LowByteFirst(string data, string crc)
    {
        Assert.Equal($"{data} {crc}", HexFormatter.Format(Crc16.Append(Hex(data))));
    }

    [Fact]
    public void Compute_EmptyIsInitialValue() => Assert.Equal(0xFFFF, Crc16.Compute([]));

    [Fact]
    public void Build_Fc03Register1_IsExactRtuFrame()
    {
        byte[] pdu = ModbusPdu.BuildReadRequest(ModbusFunction.ReadHoldingRegisters, ModbusAddress.ToPdu(1, 6, AddressBase.OneBased), 6);
        Assert.Equal("01 03 00 00 00 06 C5 C8", HexFormatter.Format(ModbusRtuFrame.Build(1, pdu)));
    }

    [Fact]
    public void Validate_NormalResponse_ReturnsPduAndCrcPass()
    {
        var details = new List<string>();
        byte[] pdu = ModbusRtuFrame.Validate(Hex("01 03 02 00 01 79 84"), 1, details);

        Assert.Equal(Hex("03 02 00 01"), pdu);
        Assert.Contains(details, d => d.Contains("CRC Received = 8479") && d.Contains("CRC Calculated = 8479") && d.EndsWith("CRC PASS"));
    }

    [Fact]
    public void Validate_OneByteCorrupted_IsCrcError()
    {
        var details = new List<string>();
        var ex = Assert.Throws<ModbusCommException>(() => ModbusRtuFrame.Validate(Hex("01 03 02 00 02 79 84"), 1, details));

        Assert.Equal(ResultStatus.CrcError, ex.Status);
        Assert.Contains(details, d => d.EndsWith("CRC ERROR"));
    }

    [Fact]
    public void Validate_CorruptedExceptionFrame_IsCrcErrorNotModbusException()
    {
        var ex = Assert.Throws<ModbusCommException>(() => ModbusRtuFrame.Validate(Hex("01 83 02 C0 F2"), 1, []));
        Assert.Equal(ResultStatus.CrcError, ex.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("01")]
    [InlineData("01 03 C5")]
    public void Validate_TooShort_IsInvalidResponse(string frame)
    {
        var ex = Assert.Throws<ModbusCommException>(() => ModbusRtuFrame.Validate(Hex(frame), 1, []));
        Assert.Equal(ResultStatus.InvalidResponse, ex.Status);
    }

    [Fact]
    public void Validate_WrongSlaveId_IsInvalidResponse_AfterCrcCheck()
    {
        var details = new List<string>();
        byte[] frame = Crc16.Append(Hex("02 03 02 00 01"));

        var ex = Assert.Throws<ModbusCommException>(() => ModbusRtuFrame.Validate(frame, 1, details));

        Assert.Equal(ResultStatus.InvalidResponse, ex.Status);
        Assert.Contains(details, d => d.EndsWith("CRC PASS"));
        Assert.Contains("Expected Slave ID = 1  Received Slave ID = 2", details);
    }

    [Theory]
    [InlineData("", ModbusRtuFrame.NeedMoreBytes)]
    [InlineData("01", ModbusRtuFrame.NeedMoreBytes)]
    [InlineData("01 03", ModbusRtuFrame.NeedMoreBytes)]
    [InlineData("01 03 0C", 17)]                     // 3 + 12 + 2
    [InlineData("01 83", 5)]                         // exception
    [InlineData("01 04", ModbusRtuFrame.Unknown)]    // unexpected function
    public void ExpectedResponseLength_Fc03(string received, int expected)
    {
        Assert.Equal(expected, ModbusRtuFrame.ExpectedResponseLength(ModbusFunction.ReadHoldingRegisters, Hex(received)));
    }
}
