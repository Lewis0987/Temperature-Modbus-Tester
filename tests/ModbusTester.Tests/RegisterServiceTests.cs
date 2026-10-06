using ModbusTester.Core.Models;
using ModbusTester.Core.Services;

namespace ModbusTester.Tests;

public class RegisterServiceTests
{
    [Fact]
    public void BuildRows_AppliesAddressAndScale()
    {
        var result = new ModbusResult
        {
            Request = new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, 1, 3),
            Status = ResultStatus.Pass,
            Registers = [2534, 0, 4856],
        };

        var rows = RegisterService.BuildRows(result, address => address == 2 ? 1 : 0.01);

        Assert.Equal([1, 2, 3], rows.Select(r => r.Address));
        Assert.Equal(25.34, rows[0].Value);
        Assert.Equal(0, rows[1].Value);
        Assert.Equal(48.56, rows[2].Value);
        Assert.Equal("12F8", rows[2].Hex);
        Assert.Equal("0001 0010 1111 1000", rows[2].Binary);
    }

    [Fact]
    public void Decoder_Int16_IsSigned()
    {
        Assert.Equal(-1, RegisterDecoder.Decode([0xFFFF], DataType.Int16));
        Assert.Equal(65535, RegisterDecoder.Decode([0xFFFF], DataType.UInt16));
        Assert.Equal(0x3412, RegisterDecoder.Decode([0x1234], DataType.UInt16, ByteOrder.ByteSwap));
    }
}
