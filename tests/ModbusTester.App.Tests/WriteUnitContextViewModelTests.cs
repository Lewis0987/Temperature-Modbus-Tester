using ModbusTester.App.ViewModels;
using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>
/// Phase 4F-1.1 in the ViewModel: the Reg 223 write area follows the System unit read from (or verified on) the
/// connected device; stale context is dropped; Generic is untouched. Reg 223 °F: document-based, physical pending.
/// </summary>
public class WriteUnitContextViewModelTests
{
    private static readonly DeviceProfile A6V = A6V12258321Profile.Profile;

    private static ushort[] DeviceMap(ushort systemUnit)
    {
        var registers = new ushort[1300];
        new ushort[] { 2795, 0, 5128, 0, 57005, 1 }.CopyTo(registers, 0);
        registers[400] = systemUnit;
        registers[763] = 1;
        registers[764] = 1;
        return registers;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required MainViewModel Vm { get; init; }
        public required FakeModbusTcpSlave Slave { get; init; }
        public bool Answer { get; set; } = true;
        public RegisterWriteViewModel W => Vm.Write;
        public string[] Requests() => [.. Slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..]))];

        public async ValueTask DisposeAsync()
        {
            await Vm.DisposeAsync();
            await Slave.DisposeAsync();
        }
    }

    private static async Task<Harness> ConnectAsync(ushort systemUnit, DeviceProfile? profile = null)
    {
        var slave = new FakeModbusTcpSlave { Registers = DeviceMap(systemUnit) };
        var vm = new MainViewModel(null, TestVm.TempStore())
        {
            IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString(), SelectedDeviceProfile = profile ?? A6V,
        };
        var h = new Harness { Vm = vm, Slave = slave };
        vm.Write.Confirm = _ => h.Answer;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        return h;
    }

    private static async Task ReadAsync(MainViewModel vm, string address)
    {
        vm.StartAddressText = address;
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.True(vm.LastResultPassed);
    }

    private static void Select(RegisterWriteViewModel w, int register) =>
        w.SelectedRegister = w.WritableRegisters.Single(r => r.Definition.RegisterNumber == register);

    private static void AssertUnknown(RegisterWriteViewModel w)
    {
        Select(w, 223);
        w.ValueText = "1.5";
        Assert.Equal("", w.UnitText);
        Assert.Equal("", w.RangeText);
        Assert.Equal(WriteStatus.ValidationError, w.Status);
        Assert.Equal("System unit unknown", w.StatusText);
        Assert.Contains("read Register 401 first", w.ValidationMessage);
        Assert.Equal("", w.PreviewRaw);
        Assert.False(w.WriteCommand.CanExecute(null));
    }

    private static void AssertKnown(RegisterWriteViewModel w, string unit, string range)
    {
        Select(w, 223);
        Assert.Equal(unit, w.UnitText);
        Assert.Equal(range, w.RangeText);
        Assert.Equal("0.1", w.ScaleText);
        Assert.Null(w.UnitContextError);
    }

    // Tests 1–5 (UI): a profile read that reached 401 sets the Reg 223 unit / range immediately
    [Theory]
    [InlineData(0, "°C", "-100 ~ 100")]
    [InlineData(1, "°F", "-180 ~ 180")]
    public async Task ReadOf401_SetsUnitAndRange(ushort systemUnit, string unit, string range)
    {
        await using Harness h = await ConnectAsync(systemUnit);
        await ReadAsync(h.Vm, "401");
        AssertKnown(h.W, unit, range);
    }

    // Tests 8–10 (UI): Fahrenheit 1.5 → raw 15, 180 OK, 180.1 out of range; value never converted
    [Fact]
    public async Task Fahrenheit_PreviewAndWrite_NoConversion()
    {
        await using Harness h = await ConnectAsync(1);
        await ReadAsync(h.Vm, "401");
        Select(h.W, 223);

        h.W.ValueText = "180.1";
        Assert.Equal(WriteStatus.ValidationError, h.W.Status);
        Assert.Contains("outside -180..180", h.W.ValidationMessage);
        h.W.ValueText = "180";
        Assert.Equal("1800", h.W.PreviewRaw);
        h.W.ValueText = "1.5";
        Assert.Equal(("1.5 °F", "15", "000F"), (h.W.PreviewEngineering, h.W.PreviewRaw, h.W.PreviewHex));
        h.Slave.ReceivedAdus.Clear();

        await h.W.WriteCommand.ExecuteAsync(null);

        Assert.Equal(["06 00 DE 00 0F", "03 00 DE 00 01"], h.Requests());
        Assert.Equal("Write completed (read back 1.5 °F)", h.W.StatusText);
    }

    [Fact]
    public async Task Celsius_Boundaries()
    {
        await using Harness h = await ConnectAsync(0);
        await ReadAsync(h.Vm, "401");
        Select(h.W, 223);
        h.W.ValueText = "100";
        Assert.Equal("1000", h.W.PreviewRaw);
        h.W.ValueText = "100.1";
        Assert.Contains("outside -100..100", h.W.ValidationMessage);
    }

    // Test 11: connected but 401 never read (a read up to 225 does not reach it) → no °C pretence
    [Fact]
    public async Task NotRead_Refused_NothingSent()
    {
        await using Harness h = await ConnectAsync(1);
        await ReadAsync(h.Vm, "225");
        AssertUnknown(h.W);
        h.Slave.ReceivedAdus.Clear();

        await h.W.WriteCommand.ExecuteAsync(null);                 // even if forced

        Assert.Empty(h.Requests());
    }

    // Test 12: 401 holds an undefined state
    [Fact]
    public async Task UnknownState_Refused()
    {
        await using Harness h = await ConnectAsync(7);
        await ReadAsync(h.Vm, "401");
        Select(h.W, 223);
        h.W.ValueText = "1.5";
        Assert.Contains("= 7 is not a known state", h.W.ValidationMessage);
        Assert.False(h.W.WriteCommand.CanExecute(null));
    }

    // Test 13: disconnect drops the context
    [Fact]
    public async Task Disconnect_InvalidatesContext()
    {
        await using Harness h = await ConnectAsync(1);
        await ReadAsync(h.Vm, "401");
        AssertKnown(h.W, "°F", "-180 ~ 180");

        await h.Vm.DisconnectCommand.ExecuteAsync(null);
        AssertUnknown(h.W);

        await h.Vm.ConnectCommand.ExecuteAsync(null);              // reconnect: still unknown until 401 is read again
        AssertUnknown(h.W);
        await ReadAsync(h.Vm, "401");
        AssertKnown(h.W, "°F", "-180 ~ 180");
    }

    // Test 14: profile switch does not carry the context over
    [Fact]
    public async Task ProfileSwitch_InvalidatesContext()
    {
        await using Harness h = await ConnectAsync(1);
        await ReadAsync(h.Vm, "401");
        h.Vm.SelectedDeviceProfile = DeviceProfile.Generic;
        h.Vm.SelectedDeviceProfile = A6V;
        AssertUnknown(h.W);
    }

    // Test 15: connection type switch (another device) drops the context
    [Fact]
    public async Task ConnectionTypeSwitch_InvalidatesContext()
    {
        await using Harness h = await ConnectAsync(1);
        await ReadAsync(h.Vm, "401");
        h.Vm.IsRtu = true;
        h.Vm.IsTcp = true;
        Assert.False(h.Vm.IsConnected);
        AssertUnknown(h.W);
    }

    // Tests 16–17: a verified 401 write sets the context
    [Theory]
    [InlineData(0, "Fahrenheit", "°F", "-180 ~ 180")]
    [InlineData(1, "Celsius", "°C", "-100 ~ 100")]
    public async Task VerifiedWriteOf401_UpdatesContext(ushort before, string state, string unit, string range)
    {
        await using Harness h = await ConnectAsync(before);
        await ReadAsync(h.Vm, "401");
        Select(h.W, 401);
        h.W.SelectedState = state;

        await h.W.WriteCommand.ExecuteAsync(null);

        Assert.Equal(WriteStatus.Completed, h.W.Status);
        AssertKnown(h.W, unit, range);
    }

    [Fact]
    public async Task VerifiedWriteOf401_WithoutEarlierRead_SetsContext()
    {
        await using Harness h = await ConnectAsync(0);
        Select(h.W, 401);
        h.W.SelectedState = "Fahrenheit";
        await h.W.WriteCommand.ExecuteAsync(null);
        AssertKnown(h.W, "°F", "-180 ~ 180");
    }

    // Tests 18–21: failed / mismatched / timed out / cancelled 401 writes leave the context as it was
    [Fact]
    public async Task WriteFail_ContextUnchanged()
    {
        await using Harness h = await ConnectAsync(0);
        await ReadAsync(h.Vm, "401");
        h.Slave.ForcedExceptionCode = 0x04;
        Select(h.W, 401);
        h.W.SelectedState = "Fahrenheit";
        await h.W.WriteCommand.ExecuteAsync(null);
        Assert.Equal(WriteStatus.WriteFailed, h.W.Status);
        AssertKnown(h.W, "°C", "-100 ~ 100");
    }

    [Fact]
    public async Task VerifyMismatch_ContextUnchanged()
    {
        await using Harness h = await ConnectAsync(0);
        await ReadAsync(h.Vm, "401");
        h.Slave.ResponseMutator = r => r[0] == 0x03 && r[1] == 2 ? [0x03, 0x02, 0x00, 0x00] : r;   // device still reports 0
        Select(h.W, 401);
        h.W.SelectedState = "Fahrenheit";
        await h.W.WriteCommand.ExecuteAsync(null);
        Assert.Equal(WriteStatus.VerificationMismatch, h.W.Status);
        AssertKnown(h.W, "°C", "-100 ~ 100");
    }

    [Fact]
    public async Task VerifyTimeout_ContextUnchanged()
    {
        var rtu = new FakeRtuSlave { Registers = DeviceMap(0) };
        bool silenceReads = false;
        var channel = new FakeSerialChannel { Responder = f => silenceReads && f[1] == 0x03 ? [] : rtu.Respond(f) };
        var vm = new MainViewModel(_ => channel, TestVm.TempStore())
        {
            IsRtu = true, SelectedComPort = "COM_TEST", RtuTimeoutText = "200", SelectedDeviceProfile = A6V,
        };
        await using var _ = vm;
        vm.Write.Confirm = _ => true;
        await vm.ConnectCommand.ExecuteAsync(null);
        await ReadAsync(vm, "401");
        silenceReads = true;
        Select(vm.Write, 401);
        vm.Write.SelectedState = "Fahrenheit";

        await vm.Write.WriteCommand.ExecuteAsync(null);

        Assert.Equal(WriteStatus.VerificationFailed, vm.Write.Status);
        Assert.Equal(1, rtu.Registers[400]);                                    // the device did change …
        AssertKnown(vm.Write, "°C", "-100 ~ 100");                               // … but unverified: context unchanged
    }

    [Fact]
    public async Task Cancel_ContextUnchanged()
    {
        await using Harness h = await ConnectAsync(0);
        await ReadAsync(h.Vm, "401");
        h.Answer = false;
        Select(h.W, 401);
        h.W.SelectedState = "Fahrenheit";
        await h.W.WriteCommand.ExecuteAsync(null);
        Assert.Equal(WriteStatus.Cancelled, h.W.Status);
        AssertKnown(h.W, "°C", "-100 ~ 100");
    }

    // Tests 22–23: after writing 401 the old Reg 1 row keeps its unit; the next real read uses the new 401
    [Fact]
    public async Task Write401_OldReg1RowKept_NextReadUsesNewUnit()
    {
        await using Harness h = await ConnectAsync(0);
        await ReadAsync(h.Vm, "401");
        Assert.Equal(("27.95", "°C"), (h.Vm.Rows.Single(r => r.Address == 1).ValueText, h.Vm.Rows.Single(r => r.Address == 1).Unit));
        Select(h.W, 401);
        h.W.SelectedState = "Fahrenheit";

        await h.W.WriteCommand.ExecuteAsync(null);

        Assert.Equal(("27.95", "°C"), (h.Vm.Rows.Single(r => r.Address == 1).ValueText, h.Vm.Rows.Single(r => r.Address == 1).Unit));
        Assert.Equal("Fahrenheit", h.Vm.Rows.Single(r => r.Address == 401).ValueText);

        h.Slave.Registers[0] = 8225;                                             // the sensor now outputs °F × 100
        await ReadAsync(h.Vm, "401");
        Assert.Equal(("82.25", "°F"), (h.Vm.Rows.Single(r => r.Address == 1).ValueText, h.Vm.Rows.Single(r => r.Address == 1).Unit));
    }

    // Test 24: Generic write knows nothing about 401 / °C / °F / ranges
    [Fact]
    public async Task Generic_Unaffected()
    {
        await using Harness h = await ConnectAsync(1, DeviceProfile.Generic);
        await ReadAsync(h.Vm, "401");
        h.W.GenericAddressText = "223";
        h.W.GenericValuesText = "5000";                                          // far outside any profile range
        Assert.Equal(WriteStatus.Ready, h.W.Status);
        Assert.Equal("", h.W.UnitText);
        Assert.Null(h.W.UnitContextError);
        h.Slave.ReceivedAdus.Clear();

        await h.W.WriteCommand.ExecuteAsync(null);

        Assert.Equal(["06 00 DE 13 88", "03 00 DE 00 01"], h.Requests());
        Assert.Equal("Write completed (read back 5000)", h.W.StatusText);
    }

    // Test 25: holds unchanged with a known System unit
    [Fact]
    public async Task CommunicationRegisters_StillOnHold()
    {
        await using Harness h = await ConnectAsync(1);
        await ReadAsync(h.Vm, "766");
        Select(h.W, 764);
        h.W.ValueText = "5";
        Assert.Equal(WriteStatus.WriteOnHold, h.W.Status);
        Assert.False(h.W.WriteCommand.CanExecute(null));
    }
}
