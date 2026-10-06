using ModbusTester.Core.Models;
using ModbusTester.Core.Protocol;

namespace ModbusTester.Tests;

public class ModbusPduTests
{
    [Fact]
    public void BuildReadRequest_Fc03()
    {
        byte[] pdu = ModbusPdu.BuildReadRequest(ModbusFunction.ReadHoldingRegisters, 0x0000, 6);
        Assert.Equal(new byte[] { 0x03, 0x00, 0x00, 0x00, 0x06 }, pdu);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public void BuildReadRequest_RejectsQuantityOutOfRange(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModbusPdu.BuildReadRequest(ModbusFunction.ReadHoldingRegisters, 0, (ushort)quantity));
    }

    [Fact]
    public void ParseReadRegisters_Valid()
    {
        byte[] pdu = [0x03, 0x06, 0x09, 0xE6, 0x00, 0x00, 0x12, 0xF8];
        ushort[] values = ModbusPdu.ParseReadRegistersResponse(ModbusFunction.ReadHoldingRegisters, pdu, 3);
        Assert.Equal(new ushort[] { 2534, 0, 4856 }, values);
    }

    [Theory]
    [InlineData(0x01, ModbusExceptionCode.IllegalFunction)]
    [InlineData(0x02, ModbusExceptionCode.IllegalDataAddress)]
    [InlineData(0x03, ModbusExceptionCode.IllegalDataValue)]
    [InlineData(0x04, ModbusExceptionCode.SlaveDeviceFailure)]
    public void ParseReadRegisters_ExceptionResponse(byte code, ModbusExceptionCode expected)
    {
        var ex = Assert.Throws<ModbusSlaveException>(() =>
            ModbusPdu.ParseReadRegistersResponse(ModbusFunction.ReadHoldingRegisters, [0x83, code], 1));
        Assert.Equal(expected, ex.Code);
        Assert.Equal(ResultStatus.ModbusException, ex.Status);
    }

    [Theory]
    [InlineData(new byte[] { 0x04, 0x02, 0x00, 0x01 })]       // wrong function
    [InlineData(new byte[] { 0x03, 0x04, 0x00, 0x01 })]       // byte count != quantity*2
    [InlineData(new byte[] { 0x03, 0x02, 0x00 })]             // truncated
    [InlineData(new byte[] { })]                              // empty
    public void ParseReadRegisters_InvalidResponse(byte[] pdu)
    {
        var ex = Assert.Throws<ModbusCommException>(() =>
            ModbusPdu.ParseReadRegistersResponse(ModbusFunction.ReadHoldingRegisters, pdu, 1));
        Assert.Equal(ResultStatus.InvalidResponse, ex.Status);
    }

    [Theory]
    [InlineData(1, AddressBase.OneBased, 0)]
    [InlineData(3, AddressBase.OneBased, 2)]
    [InlineData(1, AddressBase.ZeroBased, 1)]
    [InlineData(65536, AddressBase.OneBased, 65535)]
    public void Address_ToPdu(int address, AddressBase addressBase, int expected)
    {
        Assert.Equal(expected, ModbusAddress.ToPdu(address, 1, addressBase));
    }

    [Theory]
    [InlineData(0, 1, AddressBase.OneBased)]
    [InlineData(-1, 1, AddressBase.ZeroBased)]
    [InlineData(65535, 2, AddressBase.ZeroBased)]
    public void Address_ToPdu_OutOfRange(int address, int quantity, AddressBase addressBase)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusAddress.ToPdu(address, quantity, addressBase));
    }

    [Fact]
    public void HexFormatter_Formats()
    {
        Assert.Equal("00 01 FF", HexFormatter.Format([0x00, 0x01, 0xFF]));
        Assert.Equal("", HexFormatter.Format([]));
    }
}
