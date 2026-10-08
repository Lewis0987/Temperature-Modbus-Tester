using ModbusTester.App.ViewModels;
using ModbusTester.Core.Profiles;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>
/// Phase 3E in the ViewModel with the values read from the real sensor (Phase 4F-1, 2026-10-08).
/// Reg 1: physically verified. Reg 223 °F: document-based, physical verification pending.
/// </summary>
public class SystemUnitViewModelTests
{
    /// <summary>Real sensor values; 401 and Reg 1 as given.</summary>
    private static ushort[] PhysicalMap(ushort reg1, ushort systemUnit)
    {
        var registers = new ushort[1300];
        new ushort[] { reg1, 0, 5128, 0, 57005, 1 }.CopyTo(registers, 0);
        registers[400] = systemUnit;
        new ushort[] { 1, 1, 0 }.CopyTo(registers, 763);
        new ushort[] { 256, 7 }.CopyTo(registers, 1285);
        return registers;
    }

    private static async Task<(MainViewModel Vm, FakeModbusTcpSlave Slave)> ConnectAsync(DeviceProfile profile, ushort[] registers)
    {
        var slave = new FakeModbusTcpSlave { Registers = registers };
        var vm = new MainViewModel(null, TestVm.TempStore())
        {
            IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString(), SelectedDeviceProfile = profile,
        };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        return (vm, slave);
    }

    private static RegisterRowViewModel Row(MainViewModel vm, int address) => vm.Rows.Single(r => r.Address == address);

    [Theory]
    [InlineData(2793, 0, "27.93", "°C")]
    [InlineData(8225, 1, "82.25", "°F")]
    public async Task Profile1287_PhysicalValues(int reg1, int systemUnit, string value, string unit)
    {
        var (vm, slave) = await ConnectAsync(A6V12258321Profile.Profile, PhysicalMap((ushort)reg1, (ushort)systemUnit));
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "1287";

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.True(vm.LastResultPassed);
        Assert.Equal(15, vm.Rows.Count);
        Assert.Equal(5, slave.ReceivedAdus.Count);
        Assert.Equal((value, unit, "0.01"), (Row(vm, 1).ValueText, Row(vm, 1).Unit, Row(vm, 1).ScaleText));
        Assert.Equal(unit, Row(vm, 223).Unit);
        Assert.Equal(systemUnit == 0 ? "Celsius" : "Fahrenheit", Row(vm, 401).ValueText);
        Assert.Equal("57005 (Out of range)", Row(vm, 5).ValueText);
        Assert.Equal("Bad reliability, unavailable", Row(vm, 6).ValueText);
        Assert.Equal("1.00.7", vm.SoftwareVersionText);
    }

    [Fact]
    public async Task UnknownSystemUnit_ReadPasses_NoTemperatureUnit()
    {
        var (vm, slave) = await ConnectAsync(A6V12258321Profile.Profile, PhysicalMap(2793, 7));
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "401";

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.True(vm.LastResultPassed);
        Assert.Equal("Unknown (7)", Row(vm, 401).ValueText);
        Assert.Equal(("27.93", ""), (Row(vm, 1).ValueText, Row(vm, 1).Unit));
        Assert.Equal("", Row(vm, 223).Unit);
    }

    // Test 14: Profile → Generic → Profile leaves no System unit context behind
    [Fact]
    public async Task ProfileGenericProfile_NoResidualContext()
    {
        var (vm, slave) = await ConnectAsync(A6V12258321Profile.Profile, PhysicalMap(8225, 1));
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "401";
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.Equal("°F", Row(vm, 1).Unit);

        vm.SelectedDeviceProfile = DeviceProfile.Generic;
        Assert.All(vm.Rows, r => Assert.Equal("", r.Unit));
        Assert.Equal("8225", Row(vm, 1).ValueText);
        Assert.Equal("1", Row(vm, 401).ValueText);                    // raw, not "Fahrenheit"

        vm.SelectedDeviceProfile = A6V12258321Profile.Profile;        // decoded again from the same read
        Assert.Equal(("82.25", "°F"), (Row(vm, 1).ValueText, Row(vm, 1).Unit));

        vm.StartAddressText = "6";                                     // a new read without 401 → document default °C
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.Equal("°C", Row(vm, 1).Unit);
    }

    // Tests 12–13: Generic Reg 1 / 401 stay raw × manual scale, no unit, no status
    [Fact]
    public async Task Generic_Reg1And401_Raw()
    {
        var (vm, slave) = await ConnectAsync(DeviceProfile.Generic, PhysicalMap(2795, 1));
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "1";
        vm.QuantityText = "1";
        vm.DefaultScaleText = "0.01";
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.Equal(("27.95", ""), (Row(vm, 1).ValueText, Row(vm, 1).Unit));

        vm.StartAddressText = "401";
        vm.DefaultScaleText = "1";
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.Equal(("1", ""), (Row(vm, 401).ValueText, Row(vm, 401).Description));
    }
}
