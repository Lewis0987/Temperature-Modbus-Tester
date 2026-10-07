using System.Net;
using System.Net.Sockets;
using ModbusTester.Core.Models;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Services;
using ModbusTester.Core.Transport;

namespace ModbusTester.Tests;

/// <summary>End-to-end: ModbusService + ModbusTcpTransport against an in-process fake slave.</summary>
public class ModbusTcpServiceTests
{
    private static ModbusRequest Fc03(int address, ushort quantity, byte unitId = 1) =>
        new(ModbusFunction.ReadHoldingRegisters, unitId, address, quantity, AddressBase.OneBased);

    private static async Task<(ModbusService Service, CommunicationLogger Log)> ConnectAsync(int port, int timeoutMs = 1000)
    {
        var log = new CommunicationLogger();
        var service = new ModbusService(log);
        Assert.True(await service.ConnectAsync(new ModbusTcpTransport(new TcpSettings("127.0.0.1", port, timeoutMs))));
        Assert.Equal(ConnectionState.Connected, service.State);
        return (service, log);
    }

    [Fact]
    public async Task Fc03_ReadsRegisters_OneBasedAddressSendsPduZero()
    {
        await using var slave = new FakeModbusTcpSlave { Registers = [2534, 0, 4856, 0, 410, 0] };
        var (service, log) = await ConnectAsync(slave.Port);
        await using var _ = service;

        ModbusResult result = await service.ReadRegistersAsync(Fc03(1, 6));

        Assert.Equal(ResultStatus.Pass, result.Status);
        Assert.Equal(new ushort[] { 2534, 0, 4856, 0, 410, 0 }, result.Registers);
        // MBAP: tid 0001, pid 0000, len 0006, unit 01 | PDU: 03 0000 0006
        Assert.Equal("00 01 00 00 00 06 01 03 00 00 00 06", HexFormatter.Format(slave.ReceivedAdus.Single()));
        // Success: Tx, Tx frame, Rx frame, Rx values, Response Time, and no "Result:" line.
        Assert.Equal(
            [LogKind.Tx, LogKind.Tx, LogKind.Rx, LogKind.Rx, LogKind.Info],
            log.Entries.SkipWhile(e => e.Kind != LogKind.Tx).Select(e => e.Kind));
        Assert.Equal("2534, 0, 4856, 0, 410, 0", log.Entries[^2].Message);
        Assert.StartsWith("Response Time:", log.Entries[^1].Message);
        Assert.DoesNotContain(log.Entries, e => e.Message.StartsWith("Result:") || e.Kind == LogKind.Pass);
    }

    [Fact]
    public async Task Fc04_ZeroBasedAddress_IsSentAsIs()
    {
        await using var slave = new FakeModbusTcpSlave { Registers = [10, 20, 30] };
        var (service, _) = await ConnectAsync(slave.Port);
        await using var __ = service;

        ModbusResult result = await service.ReadRegistersAsync(
            new ModbusRequest(ModbusFunction.ReadInputRegisters, 1, 1, 2, AddressBase.ZeroBased));

        Assert.Equal(ResultStatus.Pass, result.Status);
        Assert.Equal(new ushort[] { 20, 30 }, result.Registers);
        Assert.Equal("04 00 01 00 02", HexFormatter.Format(slave.ReceivedAdus.Single()[7..]));
    }

    [Fact]
    public async Task Fc03_SequentialReads_IncrementTransactionId()
    {
        await using var slave = new FakeModbusTcpSlave();
        var (service, _) = await ConnectAsync(slave.Port);
        await using var __ = service;

        Assert.True((await service.ReadRegistersAsync(Fc03(1, 1))).IsSuccess);
        Assert.True((await service.ReadRegistersAsync(Fc03(1, 1))).IsSuccess);

        Assert.Equal([1, 2], slave.ReceivedAdus.Select(a => a[0] << 8 | a[1]));
    }

    [Fact]
    public async Task Fc03_IllegalDataAddress_ReturnsModbusException()
    {
        await using var slave = new FakeModbusTcpSlave { Registers = new ushort[6] };
        var (service, log) = await ConnectAsync(slave.Port);
        await using var _ = service;

        ModbusResult result = await service.ReadRegistersAsync(Fc03(5, 6));

        Assert.Equal(ResultStatus.ModbusException, result.Status);
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, result.ExceptionCode);
        Assert.NotNull(result.RxFrame);
        Assert.Contains(log.Entries, e => e.Kind == LogKind.Fail && e.Message.Contains("Illegal Data Address"));
        Assert.Equal(ConnectionState.Connected, service.State);
    }

    [Fact]
    public async Task Fc03_SlowSlave_TimesOut_ThenLateReplyIsDiscarded()
    {
        await using var slave = new FakeModbusTcpSlave { Registers = [111, 222], ResponseDelay = TimeSpan.FromMilliseconds(400) };
        var (service, log) = await ConnectAsync(slave.Port, timeoutMs: 150);
        await using var _ = service;

        ModbusResult timedOut = await service.ReadRegistersAsync(Fc03(1, 1));
        Assert.Equal(ResultStatus.Timeout, timedOut.Status);
        Assert.NotNull(timedOut.TxFrame);
        Assert.Equal(ConnectionState.Connected, service.State);

        // Let the late reply arrive, then read again with a fast slave: the stale reply must not be mistaken for it.
        await Task.Delay(400);
        slave.ResponseDelay = TimeSpan.Zero;
        ModbusResult next = await service.ReadRegistersAsync(Fc03(2, 1));

        Assert.Equal(ResultStatus.Pass, next.Status);
        Assert.Equal(new ushort[] { 222 }, next.Registers);
        Assert.Contains(log.Entries, e => e.Message.StartsWith("Discarded"));
    }

    [Fact]
    public async Task Fc03_WrongUnitIdInReply_IsInvalidResponse()
    {
        await using var slave = new FakeModbusTcpSlave { OverrideUnitId = 9 };
        var (service, _) = await ConnectAsync(slave.Port);
        await using var __ = service;

        ModbusResult result = await service.ReadRegistersAsync(Fc03(1, 1));

        Assert.Equal(ResultStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public async Task Fc03_SlaveClosesConnection_IsConnectionLost()
    {
        await using var slave = new FakeModbusTcpSlave { CloseOnRequest = true };
        var (service, _) = await ConnectAsync(slave.Port);
        await using var __ = service;

        ModbusResult result = await service.ReadRegistersAsync(Fc03(1, 1));

        Assert.Equal(ResultStatus.ConnectionLost, result.Status);
        Assert.Equal(ConnectionState.Error, service.State);
    }

    [Fact]
    public async Task Connect_NoListener_IsConnectionRefused()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var log = new CommunicationLogger();
        await using var service = new ModbusService(log);
        bool connected = await service.ConnectAsync(new ModbusTcpTransport(new TcpSettings("127.0.0.1", closedPort, TimeoutMs: 5000)));

        Assert.False(connected);
        Assert.Equal(ConnectionState.Error, service.State);
        Assert.Contains(log.Entries, e => e.Kind == LogKind.Fail && e.Message.Contains("Connection Refused"));
    }

    [Fact]
    public async Task Connect_UnroutableHost_TimesOut()
    {
        // 10.255.255.1 is non-routable from the test host, so SYN gets no answer.
        await using var service = new ModbusService(new CommunicationLogger());
        bool connected = await service.ConnectAsync(new ModbusTcpTransport(new TcpSettings("10.255.255.1", 502, ConnectTimeoutMs: 300)));

        Assert.False(connected);
        Assert.True(service.State is ConnectionState.Timeout or ConnectionState.Error);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(800)]
    public async Task Connect_TimeoutFollowsTimeoutMs_AndLogMatches(int timeoutMs)
    {
        // 10.255.255.1 is non-routable from the test host, so SYN gets no answer.
        var log = new CommunicationLogger();
        await using var service = new ModbusService(log);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        bool connected = await service.ConnectAsync(new ModbusTcpTransport(new TcpSettings("10.255.255.1", 5020, timeoutMs)));

        stopwatch.Stop();
        Assert.False(connected);
        Assert.Equal(ConnectionState.Timeout, service.State);
        Assert.Contains(log.Entries, e => e.Message == $"Connect FAIL: Connect timeout ({timeoutMs} ms)");
        Assert.InRange(stopwatch.ElapsedMilliseconds, timeoutMs - 50, timeoutMs + 1500);
    }

    [Fact]
    public void Settings_ConnectTimeout_DefaultsToTimeout_UnlessGiven()
    {
        Assert.Equal(10000, new TcpSettings("h", 1, 10000).EffectiveConnectTimeoutMs);
        Assert.Equal(300, new TcpSettings("h", 1, 10000, ConnectTimeoutMs: 300).EffectiveConnectTimeoutMs);
    }

    [Fact]
    public async Task Read_WhenNotConnected_IsNotConnected()
    {
        await using var service = new ModbusService(new CommunicationLogger());

        ModbusResult result = await service.ReadRegistersAsync(Fc03(1, 1));

        Assert.Equal(ResultStatus.NotConnected, result.Status);
    }

    [Fact]
    public async Task Read_QuantityTooLarge_IsInvalidRequest_AndNothingIsSent()
    {
        await using var slave = new FakeModbusTcpSlave();
        var (service, _) = await ConnectAsync(slave.Port);
        await using var __ = service;

        ModbusResult result = await service.ReadRegistersAsync(Fc03(1, 126));

        Assert.Equal(ResultStatus.InvalidRequest, result.Status);
        Assert.Empty(slave.ReceivedAdus);
    }
}
