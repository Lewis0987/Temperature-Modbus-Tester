using ModbusTester.App.ViewModels;
using ModbusTester.Core.Profiles;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>
/// Phase 4F-3 – Reg 223 Fahrenheit SOFTWARE verification in the ViewModel (fake slaves, no real sensor): the planned
/// experiment's workflow (223 = +1.0 °C → 401 = Fahrenheit → read → restore) as the tester handles it.
/// The fake slave never changes Reg 223 by itself, so these tests prove the tester's handling only; the real
/// sensor's Reg 223 behaviour on a 401 change is PENDING PHYSICAL VERIFICATION.
/// </summary>
public class Reg223FahrenheitViewModelTests
{
    private static readonly DeviceProfile A6V = A6V12258321Profile.Profile;

    private static ushort[] DeviceMap()
    {
        var registers = new ushort[1300];
        new ushort[] { 2795, 0, 5128, 0, 57005, 1 }.CopyTo(registers, 0);
        registers[763] = 1;
        registers[764] = 1;
        return registers;
    }

    private static async Task<(MainViewModel Vm, FakeModbusTcpSlave Slave)> ConnectAsync(DeviceProfile profile)
    {
        var slave = new FakeModbusTcpSlave { Registers = DeviceMap() };
        var vm = new MainViewModel(null, TestVm.TempStore())
        {
            IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString(), SelectedDeviceProfile = profile,
        };
        vm.Write.Confirm = _ => true;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        return (vm, slave);
    }

    private static async Task ReadAsync(MainViewModel vm, string address)
    {
        vm.StartAddressText = address;
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.True(vm.LastResultPassed);
    }

    private static async Task WriteAsync(MainViewModel vm, int register, string? value = null, string? state = null)
    {
        vm.Write.SelectedRegister = vm.Write.WritableRegisters.Single(r => r.Definition.RegisterNumber == register);
        if (value is not null) vm.Write.ValueText = value;
        if (state is not null) vm.Write.SelectedState = state;
        await vm.Write.WriteCommand.ExecuteAsync(null);
    }

    private static (string Value, string Unit) Row(MainViewModel vm, int address)
    {
        RegisterRowViewModel r = vm.Rows.Single(x => x.Address == address);
        return (r.ValueText, r.Unit);
    }

    private static void Select223(MainViewModel vm) =>
        vm.Write.SelectedRegister = vm.Write.WritableRegisters.Single(r => r.Definition.RegisterNumber == 223);

    // D + the whole planned workflow: non-zero Reg 223 rows stay as read, the next real read relabels, restore works
    [Fact]
    public async Task PlannedWorkflow_StaleRowsKept_NextReadRelabels_Restore()
    {
        var (vm, slave) = await ConnectAsync(A6V);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "401");

        // STEP 1: 223 = +1.0 °C → raw 10
        await WriteAsync(vm, 223, "1.0");
        Assert.Equal("Write completed (read back 1 °C)", vm.Write.StatusText);
        Assert.Equal(10, slave.Registers[222]);
        Assert.Equal(("1", "°C"), Row(vm, 223));

        // STEP 2: 401 = Fahrenheit (verified) → only row 401 changes; Reg 1 / 223 rows keep their °C reading
        await WriteAsync(vm, 401, state: "Fahrenheit");
        Assert.Equal("Write completed (read back Fahrenheit)", vm.Write.StatusText);
        Assert.Equal(("Fahrenheit", ""), Row(vm, 401));
        Assert.Equal(("1", "°C"), Row(vm, 223));
        Assert.Equal(("27.95", "°C"), Row(vm, 1));
        Select223(vm);
        Assert.Equal(("°F", "-180 ~ 180"), (vm.Write.UnitText, vm.Write.RangeText));   // write context follows the verified 401

        // STEP 3: a real read relabels with 401 = 1; the fake slave keeps raw 10 (SIMULATED – not sensor behaviour)
        await ReadAsync(vm, "401");
        Assert.Equal(("1", "°F"), Row(vm, 223));
        Assert.Equal(10, vm.Rows.Single(r => r.Address == 223).Raw);

        // Restore: 401 = Celsius, read, 223 = 0
        await WriteAsync(vm, 401, state: "Celsius");
        Select223(vm);
        Assert.Equal(("°C", "-100 ~ 100"), (vm.Write.UnitText, vm.Write.RangeText));
        await ReadAsync(vm, "401");
        Assert.Equal(("1", "°C"), Row(vm, 223));
        await WriteAsync(vm, 223, "0");
        Assert.Equal("Write completed (read back 0 °C)", vm.Write.StatusText);
        await ReadAsync(vm, "1287");
        Assert.Equal((0, 0), (slave.Registers[222], slave.Registers[400]));
        Assert.Equal(("0", "°C"), Row(vm, 223));
        Assert.Equal(("Celsius", ""), Row(vm, 401));
    }

    // Emergency restore path: with 401 = 1 known, 223 = 0 can be written directly (raw 0 in any unit)
    [Fact]
    public async Task EmergencyRestore_223Zero_InFahrenheitContext()
    {
        var (vm, slave) = await ConnectAsync(A6V);
        await using var _ = slave;
        await using var __ = vm;
        slave.Registers[222] = 10;
        slave.Registers[400] = 1;
        await ReadAsync(vm, "401");

        await WriteAsync(vm, 223, "0");
        await WriteAsync(vm, 401, state: "Celsius");

        Assert.Equal((0, 0), (slave.Registers[222], slave.Registers[400]));
        Assert.Equal(WriteStatus.Completed, vm.Write.Status);
    }

    // C: FC06 itself times out (response lost) → write failed, context unchanged; the slave may still have applied it
    [Fact]
    public async Task Write401_Fc06Timeout_ContextUnchanged()
    {
        var rtu = new FakeRtuSlave { Registers = DeviceMap() };
        bool dropFc06Response = false;
        var channel = new FakeSerialChannel
        {
            Responder = f =>
            {
                var response = rtu.Respond(f);
                return dropFc06Response && f[1] == 0x06 ? [] : response;
            },
        };
        var vm = new MainViewModel(_ => channel, TestVm.TempStore())
        {
            IsRtu = true, SelectedComPort = "COM_TEST", RtuTimeoutText = "200", SelectedDeviceProfile = A6V,
        };
        await using var _ = vm;
        vm.Write.Confirm = _ => true;
        await vm.ConnectCommand.ExecuteAsync(null);
        await ReadAsync(vm, "401");
        dropFc06Response = true;

        await WriteAsync(vm, 401, state: "Fahrenheit");

        Assert.Equal("Write failed: Timeout", vm.Write.StatusText);
        Assert.Equal(1, rtu.Registers[400]);                        // applied, response lost
        Select223(vm);
        Assert.NotEqual("°F", vm.Write.UnitText);                   // never learnt from an unverified write
        // A read of the device is what tells the truth (and only then the context becomes °F)
        await ReadAsync(vm, "401");
        Select223(vm);
        Assert.Equal("°F", vm.Write.UnitText);
    }

    // F: a Generic read of 401 never feeds the profile write context
    [Fact]
    public async Task GenericRead401_ThenProfile_StillUnknown()
    {
        var (vm, slave) = await ConnectAsync(DeviceProfile.Generic);
        await using var _ = slave;
        await using var __ = vm;
        slave.Registers[400] = 1;
        vm.QuantityText = "1";
        await ReadAsync(vm, "401");
        Assert.Equal("1", vm.Rows.Single().ValueText);             // raw only

        vm.SelectedDeviceProfile = A6V;
        Select223(vm);
        vm.Write.ValueText = "1.0";
        Assert.Equal(("", ""), (vm.Write.UnitText, vm.Write.RangeText));
        Assert.Equal(WriteStatus.ValidationError, vm.Write.Status);
        Assert.Contains("read Register 401 first", vm.Write.ValidationMessage);
        Assert.False(vm.Write.WriteCommand.CanExecute(null));
    }

    // E (with a non-zero offset): disconnect drops the Fahrenheit context; reconnect needs a new read of 401
    [Fact]
    public async Task Disconnect_FahrenheitContextDropped_ReconnectNeedsRead()
    {
        var (vm, slave) = await ConnectAsync(A6V);
        await using var _ = slave;
        await using var __ = vm;
        slave.Registers[222] = 10;
        slave.Registers[400] = 1;
        await ReadAsync(vm, "401");
        Select223(vm);
        Assert.Equal("°F", vm.Write.UnitText);

        await vm.DisconnectCommand.ExecuteAsync(null);
        await vm.ConnectCommand.ExecuteAsync(null);
        Select223(vm);
        vm.Write.ValueText = "0";
        Assert.Equal("", vm.Write.UnitText);
        Assert.False(vm.Write.WriteCommand.CanExecute(null));

        await ReadAsync(vm, "401");
        Select223(vm);
        vm.Write.ValueText = "0";
        Assert.Equal("°F", vm.Write.UnitText);
        Assert.True(vm.Write.WriteCommand.CanExecute(null));
    }
}
