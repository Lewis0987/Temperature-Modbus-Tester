using ModbusTester.Core.Models;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Services;
using ModbusTester.Core.Transport;

namespace ModbusTester.Tests;

/// <summary>Phase 4A: FC06 / FC16 PDUs (protocol level).</summary>
public class ModbusWritePduTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    // 1
    [Fact]
    public void Fc06_Build()
    {
        Assert.Equal("06 00 DE 00 0F", HexFormatter.Format(ModbusPdu.BuildWriteSingleRegister(0x00DE, 15)));
        Assert.Equal("06 00 DF FF E7", HexFormatter.Format(ModbusPdu.BuildWriteSingleRegister(0x00DF, 65511)));
    }

    // 12, 13, 17
    [Theory]
    [InlineData(new ushort[] { 15 }, "10 00 DE 00 01 02 00 0F")]
    [InlineData(new ushort[] { 15, 25 }, "10 00 DE 00 02 04 00 0F 00 19")]
    [InlineData(new ushort[] { 15, 65511, 150 }, "10 00 DE 00 03 06 00 0F FF E7 00 96")]
    public void Fc16_Build_ByteCountIsQuantityTimesTwo(ushort[] values, string expected)
    {
        byte[] pdu = ModbusPdu.BuildWriteMultipleRegisters(0x00DE, values);
        Assert.Equal(expected, HexFormatter.Format(pdu));
        Assert.Equal(values.Length * 2, pdu[5]);
    }

    // 14, 15, 16
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(123, true)]
    [InlineData(124, false)]
    public void Fc16_QuantityLimits(int count, bool accepted)
    {
        var values = new ushort[count];
        if (accepted)
            Assert.Equal(6 + count * 2, ModbusPdu.BuildWriteMultipleRegisters(0, values).Length);
        else
            Assert.Throws<ArgumentOutOfRangeException>(() => ModbusPdu.BuildWriteMultipleRegisters(0, values));
    }

    [Fact]
    public void Fc06_Response_EchoAccepted()
    {
        ModbusPdu.ParseWriteSingleRegisterResponse(Hex("06 00 DE 00 0F"), 0x00DE, 15);
    }

    // 4, 5, 9, 10 (protocol level)
    [Theory]
    [InlineData("06 00 DE 00 10", "Value 16")]                  // value mismatch
    [InlineData("06 00 DF 00 0F", "Address 0x00DF")]            // address mismatch
    [InlineData("06 00 DE", "PDU length 3")]                    // truncated
    [InlineData("03 00 DE 00 0F", "Function code 0x03")]        // wrong function
    [InlineData("10 00 DE 00 01", "Function code 0x10")]
    [InlineData("", "Empty")]
    public void Fc06_Response_Invalid(string pdu, string reason)
    {
        var ex = Assert.Throws<ModbusCommException>(() => ModbusPdu.ParseWriteSingleRegisterResponse(Hex(pdu), 0x00DE, 15));
        Assert.Equal(ResultStatus.InvalidResponse, ex.Status);
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void Fc16_Response_EchoAccepted()
    {
        ModbusPdu.ParseWriteMultipleRegistersResponse(Hex("10 00 DE 00 02"), 0x00DE, 2);
    }

    // 20, 21, 24 (protocol level)
    [Theory]
    [InlineData("10 00 DF 00 02", "Address 0x00DF")]
    [InlineData("10 00 DE 00 03", "Quantity 3")]
    [InlineData("10 00 DE 00", "PDU length 4")]
    [InlineData("06 00 DE 00 02", "Function code 0x06")]
    public void Fc16_Response_Invalid(string pdu, string reason)
    {
        var ex = Assert.Throws<ModbusCommException>(() => ModbusPdu.ParseWriteMultipleRegistersResponse(Hex(pdu), 0x00DE, 2));
        Assert.Equal(ResultStatus.InvalidResponse, ex.Status);
        Assert.Contains(reason, ex.Message);
    }

    // 6, 7, 22: the existing exception parser handles 0x86 / 0x90
    [Theory]
    [InlineData("86 02", ModbusExceptionCode.IllegalDataAddress)]
    [InlineData("86 03", ModbusExceptionCode.IllegalDataValue)]
    public void Fc06_ExceptionResponse(string pdu, ModbusExceptionCode code)
    {
        var ex = Assert.Throws<ModbusSlaveException>(() => ModbusPdu.ParseWriteSingleRegisterResponse(Hex(pdu), 0x00DE, 15));
        Assert.Equal(code, ex.Code);
    }

    [Theory]
    [InlineData("90 02", ModbusExceptionCode.IllegalDataAddress)]
    [InlineData("90 03", ModbusExceptionCode.IllegalDataValue)]
    [InlineData("90 04", ModbusExceptionCode.SlaveDeviceFailure)]
    public void Fc16_ExceptionResponse(string pdu, ModbusExceptionCode code)
    {
        var ex = Assert.Throws<ModbusSlaveException>(() => ModbusPdu.ParseWriteMultipleRegistersResponse(Hex(pdu), 0x00DE, 2));
        Assert.Equal(code, ex.Code);
    }

    [Theory]
    [InlineData("01 06 00 DE 00 0F", 8)]
    [InlineData("01 10 00 DE 00 02", 8)]
    [InlineData("01 86", 5)]
    [InlineData("01 90", 5)]
    [InlineData("01", ModbusRtuFrame.NeedMoreBytes)]
    public void RtuResponseLength_ForWrites(string received, int expected)
    {
        var function = Hex(received).Length > 1 && (Hex(received)[1] & 0x7F) == 0x10
            ? ModbusFunction.WriteMultipleRegisters : ModbusFunction.WriteSingleRegister;
        Assert.Equal(expected, ModbusRtuFrame.ExpectedResponseLength(function, Hex(received)));
    }

    [Fact]
    public void WriteRequest_AddressBase_ConvertedOnce()
    {
        Assert.Equal(222, ModbusWriteRequest.Single(1, 223, 15).PduAddress);
        Assert.Equal(222, ModbusWriteRequest.Single(1, 222, 15, AddressBase.ZeroBased).PduAddress);
        Assert.Equal(222, ModbusWriteRequest.Multiple(1, 223, [15, 25]).PduAddress);
    }
}

/// <summary>Phase 4A: FC06 / FC16 through ModbusService over TCP and RTU (one shared write path).</summary>
public class ModbusWriteServiceTests
{
    // ---------------- TCP ----------------

    private static async Task<(ModbusService Service, FakeModbusTcpSlave Slave)> TcpAsync(int timeoutMs = 1000)
    {
        var slave = new FakeModbusTcpSlave { Registers = new ushort[300] };
        var service = new ModbusService(new CommunicationLogger());
        Assert.True(await service.ConnectAsync(new ModbusTcpTransport(new TcpSettings("127.0.0.1", slave.Port, timeoutMs))));
        return (service, slave);
    }

    // 2 + 11 + log
    [Fact]
    public async Task Tcp_Fc06_Success_ExactAdu_ReadBack()
    {
        var (service, slave) = await TcpAsync();
        await using var _ = slave;
        await using var __ = service;

        ModbusWriteResult result = await service.WriteSingleRegisterAsync(1, 223, 15);

        Assert.Equal(ResultStatus.Pass, result.Status);
        Assert.Equal("00 01 00 00 00 06 01 06 00 DE 00 0F", HexFormatter.Format(result.TxFrame));
        Assert.Equal("00 01 00 00 00 06 01 06 00 DE 00 0F", HexFormatter.Format(result.RxFrame));
        Assert.Equal(15, slave.Registers[222]);

        ModbusResult read = await service.ReadRegistersAsync(new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, 223, 1));
        Assert.Equal(new ushort[] { 15 }, read.Registers);

        string[] log = [.. service.Logger.Entries.Select(e => e.Message)];
        Assert.Contains("FC06  Unit ID = 1  Address = 223 (PDU 0x00DE, 1-based)  Value = 15", log);
        Assert.Contains("[00 01 00 00 00 06 01 06 00 DE 00 0F]", log);
        Assert.Contains(log, m => m.StartsWith("Response Time:"));
        Assert.DoesNotContain(log, m => m.StartsWith("Result:"));
    }

    // 18 + 25
    [Fact]
    public async Task Tcp_Fc16_Success_ExactAdu_ReadBack()
    {
        var (service, slave) = await TcpAsync();
        await using var _ = slave;
        await using var __ = service;

        ModbusWriteResult result = await service.WriteMultipleRegistersAsync(1, 223, [15, 25]);

        Assert.Equal(ResultStatus.Pass, result.Status);
        Assert.Equal("00 01 00 00 00 0B 01 10 00 DE 00 02 04 00 0F 00 19", HexFormatter.Format(result.TxFrame));
        Assert.Equal("00 01 00 00 00 06 01 10 00 DE 00 02", HexFormatter.Format(result.RxFrame));

        ModbusResult read = await service.ReadRegistersAsync(new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, 223, 2));
        Assert.Equal(new ushort[] { 15, 25 }, read.Registers);
        Assert.Contains(service.Logger.Entries, e => e.Message == "FC16  Unit ID = 1  Address = 223 (PDU 0x00DE, 1-based)  Quantity = 2  Values = 15, 25");
        Assert.DoesNotContain(service.Logger.Entries, e => e.Message.StartsWith("Result:"));
    }

    // 4, 5, 9, 10 (TCP) and 20, 21, 24 (TCP)
    [Theory]
    [InlineData(false, "06 00 DE 00 10", "Value 16")]
    [InlineData(false, "06 00 DF 00 0F", "Address 0x00DF")]
    [InlineData(false, "06 00 DE", "PDU length 3")]
    [InlineData(false, "03 02 00 0F", "Function code 0x03")]
    [InlineData(true, "10 00 DF 00 02", "Address 0x00DF")]
    [InlineData(true, "10 00 DE 00 01", "Quantity 1")]
    [InlineData(true, "10 00 DE", "PDU length 3")]
    public async Task Tcp_BadResponse_IsInvalidResponse_StaysConnected(bool fc16, string response, string reason)
    {
        var (service, slave) = await TcpAsync();
        await using var _ = slave;
        await using var __ = service;
        slave.ResponseMutator = _ => Convert.FromHexString(response.Replace(" ", ""));

        ModbusWriteResult result = fc16
            ? await service.WriteMultipleRegistersAsync(1, 223, [15, 25])
            : await service.WriteSingleRegisterAsync(1, 223, 15);

        Assert.Equal(ResultStatus.InvalidResponse, result.Status);
        Assert.Contains(reason, result.ErrorMessage);
        Assert.Equal(ConnectionState.Connected, service.State);
        Assert.Contains(service.Logger.Entries, e => e.Kind == LogKind.Fail && e.Message.StartsWith("Result: FAIL [InvalidResponse]"));
    }

    // 6, 7, 22 (TCP)
    [Theory]
    [InlineData(false, 0x02, ModbusExceptionCode.IllegalDataAddress, "86 02")]
    [InlineData(false, 0x03, ModbusExceptionCode.IllegalDataValue, "86 03")]
    [InlineData(true, 0x02, ModbusExceptionCode.IllegalDataAddress, "90 02")]
    [InlineData(true, 0x03, ModbusExceptionCode.IllegalDataValue, "90 03")]
    public async Task Tcp_Exception(bool fc16, byte code, ModbusExceptionCode expected, string pdu)
    {
        var (service, slave) = await TcpAsync();
        await using var _ = slave;
        await using var __ = service;
        slave.ForcedExceptionCode = code;

        ModbusWriteResult result = fc16
            ? await service.WriteMultipleRegistersAsync(1, 223, [15, 25])
            : await service.WriteSingleRegisterAsync(1, 223, 15);

        Assert.Equal(ResultStatus.ModbusException, result.Status);
        Assert.Equal(expected, result.ExceptionCode);
        Assert.EndsWith(pdu, HexFormatter.Format(result.RxFrame));
        Assert.Equal(0, slave.Registers[222]);   // nothing written
    }

    [Fact]
    public async Task Tcp_Fc06_OutsideSlaveRange_IsException02()
    {
        var (service, slave) = await TcpAsync();
        await using var _ = slave;
        await using var __ = service;

        ModbusWriteResult result = await service.WriteSingleRegisterAsync(1, 1000, 1);

        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, result.ExceptionCode);
    }

    // 8, 23 (TCP)
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tcp_Timeout(bool fc16)
    {
        var (service, slave) = await TcpAsync(timeoutMs: 150);
        await using var _ = slave;
        await using var __ = service;
        slave.ResponseDelay = TimeSpan.FromMilliseconds(500);

        ModbusWriteResult result = fc16
            ? await service.WriteMultipleRegistersAsync(1, 223, [15, 25])
            : await service.WriteSingleRegisterAsync(1, 223, 15);

        Assert.Equal(ResultStatus.Timeout, result.Status);
        Assert.NotNull(result.TxFrame);
        Assert.Contains(service.Logger.Entries, e => e.Message.StartsWith("Result: TIMEOUT [Timeout]"));
        Assert.Equal(ConnectionState.Connected, service.State);
    }

    // ---------------- RTU ----------------

    private static async Task<(ModbusService Service, FakeRtuSlave Slave)> RtuAsync(int timeoutMs = 300)
    {
        var slave = new FakeRtuSlave();
        var channel = new FakeSerialChannel { Responder = slave.Respond };
        var service = new ModbusService(new CommunicationLogger());
        Assert.True(await service.ConnectAsync(new ModbusRtuTransport(new RtuSettings("COM_TEST", TimeoutMs: timeoutMs), _ => channel)));
        return (service, slave);
    }

    // 3 + 11, frames checked against independently computed CRCs
    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // response arrives one byte at a time
    public async Task Rtu_Fc06_Success_ExactFrames_ReadBack(bool fragmented)
    {
        var (service, slave) = await RtuAsync();
        await using var _ = service;
        slave.Fragmented = fragmented;

        ModbusWriteResult result = await service.WriteSingleRegisterAsync(1, 223, 15);

        Assert.Equal(ResultStatus.Pass, result.Status);
        Assert.Equal("01 06 00 DE 00 0F A9 F4", HexFormatter.Format(result.TxFrame));
        Assert.Equal("01 06 00 DE 00 0F A9 F4", HexFormatter.Format(result.RxFrame));
        Assert.Contains(service.Logger.Entries, e => e.Message == "FC06  Slave ID = 1  Address = 223 (PDU 0x00DE, 1-based)  Value = 15");
        Assert.Contains(service.Logger.Entries, e => e.Message.EndsWith("CRC PASS"));

        ModbusResult read = await service.ReadRegistersAsync(new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, 223, 1));
        Assert.Equal(new ushort[] { 15 }, read.Registers);
    }

    // 19 + 25
    [Fact]
    public async Task Rtu_Fc16_Success_ExactFrames_ReadBack()
    {
        var (service, slave) = await RtuAsync();
        await using var _ = service;

        ModbusWriteResult result = await service.WriteMultipleRegistersAsync(1, 223, [15, 25]);

        Assert.Equal(ResultStatus.Pass, result.Status);
        Assert.Equal("01 10 00 DE 00 02 04 00 0F 00 19 8E B6", HexFormatter.Format(result.TxFrame));
        Assert.Equal("01 10 00 DE 00 02 21 F2", HexFormatter.Format(result.RxFrame));
        Assert.Equal([15, 25], slave.Registers[222..224]);

        ModbusResult read = await service.ReadRegistersAsync(new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, 223, 2));
        Assert.Equal(new ushort[] { 15, 25 }, read.Registers);
    }

    // 6, 7, 22 (RTU): exception frames are CRC-checked first, then parsed by the existing parser
    [Theory]
    [InlineData(false, 0x02, "01 86 02 C3 A1")]
    [InlineData(true, 0x03, "01 90 03 0C 01")]
    public async Task Rtu_Exception(bool fc16, byte code, string frame)
    {
        var (service, slave) = await RtuAsync();
        await using var _ = service;
        slave.ForcedExceptionCode = code;

        ModbusWriteResult result = fc16
            ? await service.WriteMultipleRegistersAsync(1, 223, [15, 25])
            : await service.WriteSingleRegisterAsync(1, 223, 15);

        Assert.Equal(ResultStatus.ModbusException, result.Status);
        Assert.Equal((ModbusExceptionCode)code, result.ExceptionCode);
        Assert.Equal(frame, HexFormatter.Format(result.RxFrame));
    }

    // 4, 5, 10, 20, 21 (RTU): CRC-valid frames with wrong content
    [Theory]
    [InlineData(false, "06 00 DE 00 10", "Value 16")]
    [InlineData(false, "06 00 DF 00 0F", "Address 0x00DF")]
    [InlineData(true, "10 00 DE 00 03", "Quantity 3")]
    [InlineData(true, "10 00 DF 00 02", "Address 0x00DF")]
    public async Task Rtu_BadEcho_IsInvalidResponse(bool fc16, string pdu, string reason)
    {
        var (service, slave) = await RtuAsync();
        await using var _ = service;
        slave.PduMutator = _ => Convert.FromHexString(pdu.Replace(" ", ""));

        ModbusWriteResult result = fc16
            ? await service.WriteMultipleRegistersAsync(1, 223, [15, 25])
            : await service.WriteSingleRegisterAsync(1, 223, 15);

        Assert.Equal(ResultStatus.InvalidResponse, result.Status);
        Assert.Contains(reason, result.ErrorMessage);
    }

    // 10 (RTU): an unexpected function code ends on line silence and is rejected
    [Fact]
    public async Task Rtu_WrongFunction_IsInvalidResponse()
    {
        var (service, slave) = await RtuAsync();
        await using var _ = service;
        slave.PduMutator = _ => [0x03, 0x02, 0x00, 0x0F];

        ModbusWriteResult result = await service.WriteSingleRegisterAsync(1, 223, 15);

        Assert.Equal(ResultStatus.InvalidResponse, result.Status);
        Assert.Contains("Function code 0x03", result.ErrorMessage);
    }

    // 9, 24 (RTU)
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rtu_TruncatedResponse_IsInvalidResponse(bool fc16)
    {
        var (service, slave) = await RtuAsync(timeoutMs: 150);
        await using var _ = service;
        slave.TruncateTo = 5;

        ModbusWriteResult result = fc16
            ? await service.WriteMultipleRegistersAsync(1, 223, [15, 25])
            : await service.WriteSingleRegisterAsync(1, 223, 15);

        Assert.Equal(ResultStatus.InvalidResponse, result.Status);
        Assert.Contains("truncated", result.ErrorMessage);
        Assert.Equal(5, result.RxFrame!.Length);
    }

    // 8, 23 (RTU)
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rtu_Timeout(bool fc16)
    {
        var (service, slave) = await RtuAsync(timeoutMs: 150);
        await using var _ = service;
        slave.Silent = true;

        ModbusWriteResult result = fc16
            ? await service.WriteMultipleRegistersAsync(1, 223, [15, 25])
            : await service.WriteSingleRegisterAsync(1, 223, 15);

        Assert.Equal(ResultStatus.Timeout, result.Status);
        Assert.Contains(service.Logger.Entries, e => e.Message.StartsWith("Result: TIMEOUT [Timeout] - Timeout after 150 ms"));
    }

    // ---------------- shared ----------------

    [Theory]
    [InlineData(0)]
    [InlineData(124)]
    public async Task Fc16_InvalidQuantity_IsInvalidRequest_NothingSent(int count)
    {
        var (service, slave) = await TcpAsync();
        await using var _ = slave;
        await using var __ = service;

        ModbusWriteResult result = await service.WriteMultipleRegistersAsync(1, 1, new ushort[count]);

        Assert.Equal(ResultStatus.InvalidRequest, result.Status);
        Assert.Empty(slave.ReceivedAdus);
        Assert.Contains(service.Logger.Entries, e => e.Kind == LogKind.Fail && e.Message.StartsWith("Invalid request:"));
    }

    [Fact]
    public async Task Fc16_Quantity123_IsSent()
    {
        var (service, slave) = await TcpAsync();
        await using var _ = slave;
        await using var __ = service;

        ModbusWriteResult result = await service.WriteMultipleRegistersAsync(1, 1, Enumerable.Range(1, 123).Select(i => (ushort)i).ToArray());

        Assert.Equal(ResultStatus.Pass, result.Status);
        Assert.Equal(123, slave.Registers[122]);
    }

    [Fact]
    public async Task Fc06_WithTwoValues_IsInvalidRequest()
    {
        var (service, slave) = await TcpAsync();
        await using var _ = slave;
        await using var __ = service;

        ModbusWriteResult result = await service.WriteRegistersAsync(new ModbusWriteRequest(ModbusFunction.WriteSingleRegister, 1, 1, [1, 2]));

        Assert.Equal(ResultStatus.InvalidRequest, result.Status);
        Assert.Empty(slave.ReceivedAdus);
    }

    [Fact]
    public async Task AddressOverflow_IsInvalidRequest()
    {
        var (service, slave) = await TcpAsync();
        await using var _ = slave;
        await using var __ = service;

        ModbusWriteResult result = await service.WriteMultipleRegistersAsync(1, 65536, [1, 2]);

        Assert.Equal(ResultStatus.InvalidRequest, result.Status);
        Assert.Empty(slave.ReceivedAdus);
    }

    // 31: 0-based and 1-based reach the same PDU address, converted once
    [Fact]
    public async Task AddressBase_ZeroAndOneBased_SamePdu()
    {
        var (service, slave) = await TcpAsync();
        await using var _ = slave;
        await using var __ = service;

        await service.WriteSingleRegisterAsync(1, 223, 1);
        await service.WriteSingleRegisterAsync(1, 222, 2, AddressBase.ZeroBased);

        Assert.All(slave.ReceivedAdus, a => Assert.Equal("06 00 DE", HexFormatter.Format(a[7..10])));
        Assert.Contains(service.Logger.Entries, e => e.Message.Contains("Address = 222 (PDU 0x00DE, 0-based)"));
        Assert.Equal(2, slave.Registers[222]);
    }

    [Fact]
    public async Task NotConnected_IsNotConnected()
    {
        await using var service = new ModbusService(new CommunicationLogger());
        ModbusWriteResult result = await service.WriteSingleRegisterAsync(1, 223, 15);
        Assert.Equal(ResultStatus.NotConnected, result.Status);
    }
}
