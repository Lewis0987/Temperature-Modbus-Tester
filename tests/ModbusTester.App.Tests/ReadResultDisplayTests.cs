using System.Text.RegularExpressions;
using ModbusTester.App.ViewModels;
using ModbusTester.Core.Protocol;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>
/// The text next to Read is a short verdict ("PASS   8 ms" / "FAIL" / empty); the full reason stays in the
/// Communication Log.
/// </summary>
public class ReadResultDisplayTests
{
    private static readonly ushort[] Registers = [2534, 0, 4856, 0, 650, 0];
    private static readonly Regex PassText = new(@"^PASS   \d+ ms$");

    private static IEnumerable<(TimeSpan, byte[])> Whole(byte[] frame) => [(TimeSpan.Zero, frame)];

    /// <summary>RTU slave answering FC03 from <see cref="Registers"/>.</summary>
    private static byte[] RtuSlave(byte[] request)
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

    private static async Task<MainViewModel> ConnectedRtuVm(Func<byte[], IEnumerable<(TimeSpan, byte[])>> responder, string timeoutMs = "200")
    {
        var vm = new MainViewModel(_ => new FakeSerialChannel { Responder = responder }, TestVm.TempStore())
        { IsTcp = true,
            IsRtu = true,
            SelectedComPort = "COM_TEST",
            RtuTimeoutText = timeoutMs,
        };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        return vm;
    }

    private static bool LogHas(MainViewModel vm, string text) => vm.LogEntries.Any(e => e.Message.Contains(text));

    // 1. TCP FC03 PASS
    [Fact]
    public async Task Tcp_Pass_ShowsPassAndMs()
    {
        await using var slave = new FakeModbusTcpSlave { Registers = Registers };
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString() };
        await vm.ConnectCommand.ExecuteAsync(null);

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Matches(PassText, vm.LastResult);
        Assert.True(vm.LastResultPassed);
        Assert.Equal(Registers, vm.Rows.Select(r => r.Raw));
    }

    // 2. RTU FC03 PASS
    [Fact]
    public async Task Rtu_Pass_ShowsPassAndMs()
    {
        await using var vm = await ConnectedRtuVm(req => Whole(RtuSlave(req)));

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Matches(PassText, vm.LastResult);
        Assert.True(vm.LastResultPassed);
    }

    // 3. Timeout (TCP and RTU)
    [Fact]
    public async Task Tcp_Timeout_ShowsOnlyFail_LogHasDetails()
    {
        await using var slave = new FakeModbusTcpSlave { ResponseDelay = TimeSpan.FromMilliseconds(500) };
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString(), TimeoutText = "150" };
        await vm.ConnectCommand.ExecuteAsync(null);

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal("FAIL", vm.LastResult);
        Assert.False(vm.LastResultPassed);
        Assert.True(LogHas(vm, "Result: TIMEOUT [Timeout] - Timeout (150 ms)"));
    }

    [Fact]
    public async Task Rtu_Timeout_ShowsOnlyFail_LogHasDetails()
    {
        await using var vm = await ConnectedRtuVm(_ => []);

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal("FAIL", vm.LastResult);
        Assert.True(LogHas(vm, "Result: TIMEOUT [Timeout] - Timeout after 200 ms (no response)"));
    }

    // 4. CRC Error
    [Fact]
    public async Task Rtu_CrcError_ShowsOnlyFail_LogHasCrcDetails()
    {
        await using var vm = await ConnectedRtuVm(req =>
        {
            byte[] frame = RtuSlave(req);
            frame[4] ^= 0x01;
            return Whole(frame);
        });

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal("FAIL", vm.LastResult);
        Assert.True(LogHas(vm, "Result: CRC ERROR [CrcError]"));
        Assert.True(LogHas(vm, "CRC Calculated"));
    }

    // 5. Invalid Response
    [Fact]
    public async Task Rtu_InvalidResponse_ShowsOnlyFail_LogHasDetails()
    {
        await using var vm = await ConnectedRtuVm(req =>
        {
            byte[] frame = RtuSlave(req);
            frame[0] = 2;                                         // wrong slave ID, valid CRC
            return Whole(Crc16.Append(frame.AsSpan(0, frame.Length - 2)));
        });

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal("FAIL", vm.LastResult);
        Assert.True(LogHas(vm, "Result: FAIL [InvalidResponse]"));
        Assert.True(LogHas(vm, "Expected Slave ID = 1  Received Slave ID = 2"));
    }

    // 6. Modbus Exception (TCP and RTU)
    [Fact]
    public async Task Tcp_ModbusException_ShowsOnlyFail_LogHasDetails()
    {
        await using var slave = new FakeModbusTcpSlave { Registers = Registers };
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString(), StartAddressText = "6", QuantityText = "2" };
        await vm.ConnectCommand.ExecuteAsync(null);

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal("FAIL", vm.LastResult);
        Assert.True(LogHas(vm, "Result: FAIL [ModbusException] - Modbus Exception 0x02: Illegal Data Address"));
    }

    [Fact]
    public async Task Rtu_ModbusException_ShowsOnlyFail()
    {
        await using var vm = await ConnectedRtuVm(req => Whole(RtuSlave(req)));
        vm.StartAddressText = "6";
        vm.QuantityText = "2";

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal("FAIL", vm.LastResult);
        Assert.True(LogHas(vm, "Illegal Data Address"));
    }

    // 8. Switching TCP/RTU clears the previous result (log kept)
    [Fact]
    public async Task SwitchingType_ClearsResult_KeepsLog()
    {
        await using var vm = await ConnectedRtuVm(req => Whole(RtuSlave(req)));
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.StartsWith("PASS", vm.LastResult);

        vm.IsTcp = true;

        Assert.Equal("", vm.LastResult);
        Assert.False(vm.LastResultPassed);
        Assert.True(LogHas(vm, "Result: PASS"));
    }

    // 9. Disconnect clears the previous result (log kept)
    [Fact]
    public async Task Disconnect_ClearsResult_KeepsLog()
    {
        await using var vm = await ConnectedRtuVm(_ => []);
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.Equal("FAIL", vm.LastResult);

        await vm.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal("", vm.LastResult);
        Assert.True(LogHas(vm, "Result: TIMEOUT"));
    }

    [Fact]
    public async Task NotYetRead_ShowsNothing()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true };
        Assert.Equal("", vm.LastResult);
    }
}
