using System.IO.Ports;
using ModbusTester.App.ViewModels;
using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Services;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>
/// Phase 4F-0: the RTU Baud Rate list offers every fixed rate of the sensor's register 765, including 76800.
/// The PC port speed and register 765 stay independent (no Auto, no automatic switching).
/// </summary>
public class RtuBaudRateTests
{
    // Tests 1–8
    [Fact]
    public void BaudRates_AreTheRealRates_Initial9600()
    {
        var vm = new MainViewModel(null, TestVm.TempStore());
        Assert.Equal([9600, 19200, 38400, 57600, 76800, 115200], vm.BaudRates);   // no "Auto": a list of real speeds only
        Assert.Equal(9600, vm.BaudRate);
        Assert.Equal(Parity.Even, vm.SelectedParity.Value);
        Assert.Equal(StopBits.One, vm.SelectedStopBits.Value);
        Assert.Equal(8, vm.DataBits);
        Assert.Equal("1", vm.SlaveIdText);
    }

    /// <summary>Every fixed rate of register 765 (all states except 0 = Auto) can be selected as a port speed.</summary>
    [Fact]
    public void BaudRates_CoverRegister765FixedRates()
    {
        var vm = new MainViewModel(null, TestVm.TempStore());
        int[] documented = [.. A6V12258321Profile.Profile.Find(764)!.States
            .Where(s => s.Key != 0)
            .Select(s => int.Parse(s.Value.Text.Replace("bps", "")))];
        Assert.Equal(documented, vm.BaudRates);
    }

    // Tests 9–13: 76800 reaches the serial channel; 8E1, Slave ID and timeout unchanged
    [Fact]
    public async Task Baud76800_ReachesSerialChannel_OtherSettingsUnchanged()
    {
        var rtu = new FakeRtuSlave();
        rtu.Registers[0] = 2534;
        RtuSettings? opened = null;
        var channel = new FakeSerialChannel { Responder = rtu.Respond };
        var vm = new MainViewModel(s => { opened = s; return channel; }, TestVm.TempStore())
        {
            IsRtu = true, SelectedComPort = "COM_TEST", BaudRate = 76800, SlaveIdText = "1", RtuTimeoutText = "750",
        };
        await using var _ = vm;

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.True(vm.IsConnected);
        Assert.NotNull(opened);
        Assert.Equal(new RtuSettings("COM_TEST", 76800, 8, Parity.Even, StopBits.One, 750), opened);
        Assert.Equal("76800 / 8E1", opened.FrameFormat);
        Assert.Contains(vm.LogEntries, e => e.Message.Contains("COM_TEST 76800 / 8E1"));

        vm.StartAddressText = "1";
        vm.QuantityText = "1";
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.True(vm.LastResultPassed);
        Assert.Equal(["01 03 00 00 00 01 84 0A"], rtu.Requests.Select(HexFormatter.Format));   // Slave ID 1, same frame
    }

    /// <summary>System.IO.Ports accepts 76800 as-is (no special case in the tester).</summary>
    [Fact]
    public void SerialPort_Accepts76800()
    {
        using var port = new SerialPort("COM_TEST", 76800, Parity.Even, 8, StopBits.One);
        Assert.Equal(76800, port.BaudRate);
    }

    // Tests 14–15: register 765 metadata and hold unchanged
    [Fact]
    public void Register765_Metadata_Unchanged()
    {
        RegisterDefinition d = A6V12258321Profile.Profile.Find(764)!;
        Assert.Equal(["Auto", "9600bps", "19200bps", "38400bps", "57600bps", "76800bps", "115200bps"],
            d.States.OrderBy(s => s.Key).Select(s => s.Value.Text));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], d.States.Keys.OrderBy(k => k).Select(k => (int)k));
        Assert.Equal(0, d.DefaultValue);
        Assert.Equal(A6V12258321Profile.CommunicationWriteHold, d.WriteHoldReason);
        Assert.Equal([764, 765, 766], A6V12258321Profile.Profile.Registers.Where(r => r.WriteHoldReason is not null).Select(r => r.RegisterNumber));
    }

    /// <summary>Reading 765 = 5 (76800bps) only decodes it; the open port keeps its own speed.</summary>
    [Fact]
    public async Task Reading765_DoesNotChangePortSpeed()
    {
        var rtu = new FakeRtuSlave { Registers = new ushort[1300] };
        rtu.Registers[763] = 1;
        rtu.Registers[764] = 5;
        int opens = 0;
        var channel = new FakeSerialChannel { Responder = rtu.Respond };
        var vm = new MainViewModel(_ => { opens++; return channel; }, TestVm.TempStore())
        {
            IsRtu = true, SelectedComPort = "COM_TEST", SelectedDeviceProfile = A6V12258321Profile.Profile,
        };
        await using var _ = vm;
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.StartAddressText = "766";

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal("76800bps", vm.Rows.Single(r => r.Address == 765).ValueText);
        Assert.Equal(9600, vm.BaudRate);
        Assert.Equal(1, opens);
        Assert.True(vm.IsConnected);
    }
}
