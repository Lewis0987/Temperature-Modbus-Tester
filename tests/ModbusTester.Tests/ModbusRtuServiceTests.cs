using System.Diagnostics;
using ModbusTester.Core.Models;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Services;
using ModbusTester.Core.Transport;

namespace ModbusTester.Tests;

/// <summary>ModbusService + ModbusRtuTransport against <see cref="FakeSerialChannel"/> (no hardware).</summary>
public class ModbusRtuServiceTests
{
    private static readonly ushort[] Registers = [2534, 0, 4856, 0, 650, 0];

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    private static ModbusRequest Fc03(int address = 1, ushort quantity = 6, byte slaveId = 1) =>
        new(ModbusFunction.ReadHoldingRegisters, slaveId, address, quantity, AddressBase.OneBased);

    /// <summary>A well-behaved slave 1: answers FC03 from <see cref="Registers"/> (PDU address 0..5).</summary>
    private static byte[] SlaveResponse(byte[] request)
    {
        int address = request[2] << 8 | request[3];
        int quantity = request[4] << 8 | request[5];
        if (address + quantity > Registers.Length)
            return Crc16.Append([request[0], 0x83, 0x02]);
        var body = new List<byte> { request[0], 0x03, (byte)(quantity * 2) };
        foreach (ushort value in Registers.Skip(address).Take(quantity))
            body.AddRange([(byte)(value >> 8), (byte)value]);
        return Crc16.Append(body.ToArray());
    }

    private static IEnumerable<(TimeSpan, byte[])> Whole(byte[] frame) => [(TimeSpan.Zero, frame)];

    private static async Task<(ModbusService Service, CommunicationLogger Log, FakeSerialChannel Channel)> ConnectAsync(
        Func<byte[], IEnumerable<(TimeSpan, byte[])>> responder, int timeoutMs = 300)
    {
        var channel = new FakeSerialChannel { Responder = responder };
        var log = new CommunicationLogger();
        var service = new ModbusService(log);
        var transport = new ModbusRtuTransport(new RtuSettings("COM_TEST", 9600, 8, System.IO.Ports.Parity.Even, System.IO.Ports.StopBits.One, timeoutMs), _ => channel);
        Assert.True(await service.ConnectAsync(transport));
        return (service, log, channel);
    }

    // Test 3 + 9 + 10
    [Fact]
    public async Task Fc03_Register1Quantity6_SendsPdu0_DecodesSixRegisters()
    {
        var (service, log, channel) = await ConnectAsync(req => Whole(SlaveResponse(req)));
        await using var _ = service;

        ModbusResult result = await service.ReadRegistersAsync(Fc03());

        Assert.Equal(ResultStatus.Pass, result.Status);
        Assert.Equal("01 03 00 00 00 06 C5 C8", HexFormatter.Format(channel.Written.Single()));
        Assert.Equal(Registers, result.Registers);
        Assert.Equal("01 03 0C 09 E6 00 00 12 F8 00 00 02 8A 00 00 1F 6F", HexFormatter.Format(result.RxFrame));
        Assert.Contains(log.Entries, e => e.Message.Contains("Slave ID = 1") && e.Message.Contains("PDU 0x0000"));
        Assert.Contains(log.Entries, e => e.Message == "[01 03 00 00 00 06 C5 C8]");
        Assert.Contains(log.Entries, e => e.Message.EndsWith("CRC PASS"));
        // Success ends at Response Time; no "Result:" line.
        Assert.StartsWith("Response Time:", log.Entries[^1].Message);
        Assert.Equal("2534, 0, 4856, 0, 650, 0", log.Entries[^2].Message);
        Assert.DoesNotContain(log.Entries, e => e.Message.StartsWith("Result:") || e.Kind == LogKind.Pass);
    }

    [Fact]
    public async Task Fc03_ResponseArrivesOneByteAtATime_IsReassembled()
    {
        var (service, _, _) = await ConnectAsync(req =>
            SlaveResponse(req).Select(b => (TimeSpan.FromMilliseconds(2), new[] { b })));
        await using var __ = service;

        ModbusResult result = await service.ReadRegistersAsync(Fc03());

        Assert.Equal(ResultStatus.Pass, result.Status);
        Assert.Equal(Registers, result.Registers);
    }

    [Fact]
    public async Task Fc03_Register3_ZeroBasedAndOneBasedShareConversion_NoDoubleOffset()
    {
        var (service, _, channel) = await ConnectAsync(req => Whole(SlaveResponse(req)));
        await using var __ = service;

        ModbusResult oneBased = await service.ReadRegistersAsync(Fc03(address: 3, quantity: 1));
        ModbusResult zeroBased = await service.ReadRegistersAsync(
            new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, 2, 1, AddressBase.ZeroBased));

        Assert.Equal(new ushort[] { 4856 }, oneBased.Registers);
        Assert.Equal(new ushort[] { 4856 }, zeroBased.Registers);
        Assert.All(channel.Written, tx => Assert.Equal(0x0002, tx[2] << 8 | tx[3]));
    }

    // Test 2
    [Fact]
    public async Task Fc03_CorruptedResponse_IsCrcError_WithReceivedAndCalculatedLogged()
    {
        var (service, log, _) = await ConnectAsync(req =>
        {
            byte[] frame = SlaveResponse(req);
            frame[4] ^= 0x01;
            return Whole(frame);
        });
        await using var __ = service;

        ModbusResult result = await service.ReadRegistersAsync(Fc03());

        Assert.Equal(ResultStatus.CrcError, result.Status);
        Assert.NotNull(result.RxFrame);
        Assert.Contains(log.Entries, e => e.Message.Contains("CRC Received = 6F1F") && e.Message.EndsWith("CRC ERROR"));
        Assert.Contains(log.Entries, e => e.Kind == LogKind.Fail && e.Message.StartsWith("Result: CRC ERROR"));
    }

    // Test 4
    [Fact]
    public async Task Fc03_Exception02_IsModbusException()
    {
        var (service, log, _) = await ConnectAsync(req => Whole(SlaveResponse(req)));
        await using var __ = service;

        ModbusResult result = await service.ReadRegistersAsync(Fc03(address: 6, quantity: 2));

        Assert.Equal(ResultStatus.ModbusException, result.Status);
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, result.ExceptionCode);
        Assert.Equal("01 83 02 C0 F1", HexFormatter.Format(result.RxFrame));
        Assert.Contains(log.Entries, e => e.Message.EndsWith("CRC PASS"));
    }

    [Fact]
    public async Task Fc03_ExceptionWithBadCrc_IsCrcError()
    {
        var (service, _, _) = await ConnectAsync(_ => Whole(Hex("01 83 02 C0 F2")));
        await using var __ = service;

        ModbusResult result = await service.ReadRegistersAsync(Fc03());

        Assert.Equal(ResultStatus.CrcError, result.Status);
        Assert.Null(result.ExceptionCode);
    }

    // Test 5
    [Fact]
    public async Task Fc03_WrongSlaveId_IsInvalidResponse()
    {
        var (service, log, _) = await ConnectAsync(req =>
        {
            byte[] frame = SlaveResponse(req);
            frame[0] = 2;
            return Whole(Crc16.Append(frame.AsSpan(0, frame.Length - 2)));
        });
        await using var __ = service;

        ModbusResult result = await service.ReadRegistersAsync(Fc03());

        Assert.Equal(ResultStatus.InvalidResponse, result.Status);
        Assert.Contains(log.Entries, e => e.Message == "Expected Slave ID = 1  Received Slave ID = 2");
    }

    // Test 6
    [Fact]
    public async Task Fc03_WrongFunctionCode_IsInvalidResponse()
    {
        var (service, log, _) = await ConnectAsync(_ => Whole(Crc16.Append(Hex("01 04 02 00 01"))));
        await using var __ = service;

        var stopwatch = Stopwatch.StartNew();
        ModbusResult result = await service.ReadRegistersAsync(Fc03(quantity: 1));

        Assert.Equal(ResultStatus.InvalidResponse, result.Status);
        Assert.Equal("01 04 02 00 01 78 F0", HexFormatter.Format(result.RxFrame));
        Assert.Contains(log.Entries, e => e.Message.Contains("Function code 0x04"));
        Assert.True(stopwatch.ElapsedMilliseconds < 300, "unknown-length frame should end on line silence, not on timeout");
    }

    // Test 7
    [Fact]
    public async Task Fc03_TruncatedFrame_IsInvalidResponse()
    {
        var (service, _, _) = await ConnectAsync(req => Whole(SlaveResponse(req)[..9]));
        await using var __ = service;

        ModbusResult result = await service.ReadRegistersAsync(Fc03());

        Assert.Equal(ResultStatus.InvalidResponse, result.Status);
        Assert.Equal(9, result.RxFrame!.Length);
        Assert.Contains("truncated", result.ErrorMessage);
    }

    // Test 8
    [Fact]
    public async Task Fc03_NoResponse_IsTimeout_ThenLateReplyIsDiscarded()
    {
        int call = 0;
        var (service, log, _) = await ConnectAsync(req =>
            Interlocked.Increment(ref call) == 1
                ? [(TimeSpan.FromMilliseconds(250), SlaveResponse(req))]   // arrives after the 150 ms timeout
                : Whole(SlaveResponse(req)),
            timeoutMs: 150);
        await using var _ = service;

        ModbusResult timedOut = await service.ReadRegistersAsync(Fc03(address: 1, quantity: 1));
        Assert.Equal(ResultStatus.Timeout, timedOut.Status);
        Assert.Contains(log.Entries, e => e.Kind == LogKind.Fail && e.Message.StartsWith("Result: TIMEOUT") && e.Message.Contains("Timeout after 150 ms"));
        Assert.Equal(ConnectionState.Connected, service.State);

        await Task.Delay(200);
        ModbusResult next = await service.ReadRegistersAsync(Fc03(address: 3, quantity: 1));

        Assert.Equal(ResultStatus.Pass, next.Status);
        Assert.Equal(new ushort[] { 4856 }, next.Registers);
        Assert.Contains(log.Entries, e => e.Message.StartsWith("Discarded"));
    }

    [Fact]
    public async Task Connect_PortNotPresent_IsSerialPortUnavailable()
    {
        var channel = new FakeSerialChannel { IsPresent = false };
        var log = new CommunicationLogger();
        await using var service = new ModbusService(log);

        bool connected = await service.ConnectAsync(new ModbusRtuTransport(new RtuSettings("COM_TEST"), _ => channel));

        Assert.False(connected);
        Assert.Equal(ConnectionState.Error, service.State);
        Assert.Equal(0, channel.OpenCount);
        Assert.True(channel.IsDisposed);
        Assert.Contains(log.Entries, e => e.Kind == LogKind.Fail && e.Message.Contains("not available"));
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), "access denied")]
    [InlineData(typeof(IOException), "open failed")]
    public async Task Connect_OpenFails_IsClassified(Type exceptionType, string expectedText)
    {
        var channel = new FakeSerialChannel { OpenException = (Exception)Activator.CreateInstance(exceptionType, "boom")! };
        var log = new CommunicationLogger();
        await using var service = new ModbusService(log);

        Assert.False(await service.ConnectAsync(new ModbusRtuTransport(new RtuSettings("COM_TEST"), _ => channel)));

        Assert.Equal(ConnectionState.Error, service.State);
        Assert.Contains(log.Entries, e => e.Kind == LogKind.Fail && e.Message.Contains(expectedText));
    }

    [Fact]
    public async Task Fc03_PortRemovedWhileWaiting_IsSerialPortDisconnected()
    {
        FakeSerialChannel? channelRef = null;
        var (service, _, channel) = await ConnectAsync(_ =>
        {
            channelRef!.IsPresent = false; // adapter unplugged right after TX
            return [];
        });
        channelRef = channel;
        await using var __ = service;

        ModbusResult result = await service.ReadRegistersAsync(Fc03());

        Assert.Equal(ResultStatus.SerialPortDisconnected, result.Status);
        Assert.Equal(ConnectionState.Error, service.State);
        Assert.True(channel.IsDisposed);
    }

    [Fact]
    public async Task Disconnect_DuringPendingRead_AbortsPromptly_AndClosesPort()
    {
        var (service, _, channel) = await ConnectAsync(_ => [], timeoutMs: 5000);
        await using var __ = service;

        var stopwatch = Stopwatch.StartNew();
        Task<ModbusResult> pending = service.ReadRegistersAsync(Fc03());
        await Task.Delay(50);
        await service.DisconnectAsync();
        ModbusResult result = await pending;

        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(ResultStatus.NotConnected, result.Status);
        Assert.Equal(ConnectionState.Disconnected, service.State);
        Assert.True(channel.IsDisposed);
    }

    [Fact]
    public void Settings_FrameFormatAndDescription()
    {
        var settings = new RtuSettings("COM3", 9600, 8, System.IO.Ports.Parity.Even, System.IO.Ports.StopBits.One);
        Assert.Equal("9600 / 8E1", settings.FrameFormat);
        Assert.Equal("RTU COM3 9600 / 8E1", new ModbusRtuTransport(settings).Description);
        Assert.Equal("19200 / 8N2", (settings with { BaudRate = 19200, Parity = System.IO.Ports.Parity.None, StopBits = System.IO.Ports.StopBits.Two }).FrameFormat);
    }
}
