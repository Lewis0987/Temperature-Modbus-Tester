using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using ModbusTester.App.ViewModels;
using ModbusTester.Core.Profiles;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>Phase 3D.1: the software version (A6V12045847 format) next to the Registers title; Generic shows none.</summary>
public class SoftwareVersionDisplayTests
{
    private static ushort[] DeviceMap(ushort majorMinor, ushort build)
    {
        var registers = new ushort[1290];
        new ushort[] { 2534, 0, 4856, 0, 650, 0 }.CopyTo(registers, 0);
        registers[222] = 15;
        registers[763] = 1;
        registers[1285] = majorMinor;
        registers[1286] = build;
        return registers;
    }

    private static async Task<(MainViewModel Vm, FakeModbusTcpSlave Slave)> ConnectAsync(DeviceProfile profile, ushort majorMinor, ushort build)
    {
        var slave = new FakeModbusTcpSlave { Registers = DeviceMap(majorMinor, build) };
        var vm = new MainViewModel(null, TestVm.TempStore())
        {
            IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString(), SelectedDeviceProfile = profile,
        };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        return (vm, slave);
    }

    // Tests 7, 8, 11: document example and simulator data through a real profile cumulative read
    [Theory]
    [InlineData(0x0201, 0x0021, "2.01", "33", "2.01.33")]
    [InlineData(258, 123, "1.02", "123", "1.02.123")]
    public async Task Profile1287_ShowsVersion(int majorMinor, int build, string row1286, string row1287, string version)
    {
        var (vm, slave) = await ConnectAsync(A6V12258321Profile.Profile, (ushort)majorMinor, (ushort)build);
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "1287";

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal(15, vm.Rows.Count);
        Assert.Equal(row1286, vm.Rows.Single(r => r.Address == 1286).ValueText);
        Assert.Equal(row1287, vm.Rows.Single(r => r.Address == 1287).ValueText);
        Assert.Equal((ushort)majorMinor, vm.Rows.Single(r => r.Address == 1286).Raw);    // Raw / HEX / Decimal unchanged
        Assert.Equal(version, vm.SoftwareVersionText);
    }

    [Fact]
    public async Task ReadWithoutVersionRegisters_NoVersion()
    {
        var (vm, slave) = await ConnectAsync(A6V12258321Profile.Profile, 0x0201, 0x0021);
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "1287";
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.Equal("2.01.33", vm.SoftwareVersionText);

        vm.StartAddressText = "766";                       // a later read without 1286/1287 replaces it
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.Equal("", vm.SoftwareVersionText);
    }

    // Test 10: switching the same data to Generic drops the version meaning
    [Fact]
    public async Task ProfileToGeneric_VersionCleared_RowsRaw()
    {
        var (vm, slave) = await ConnectAsync(A6V12258321Profile.Profile, 0x0201, 0x0021);
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "1287";
        await vm.ReadCommand.ExecuteAsync(null);

        vm.SelectedDeviceProfile = DeviceProfile.Generic;

        Assert.Equal("", vm.SoftwareVersionText);
        Assert.Equal(["513", "33"], vm.Rows.Where(r => r.Address >= 1286).Select(r => r.ValueText));

        vm.SelectedDeviceProfile = A6V12258321Profile.Profile;
        Assert.Equal("2.01.33", vm.SoftwareVersionText);
    }

    [Fact]
    public async Task Generic_Read1286_NoVersion()
    {
        var (vm, slave) = await ConnectAsync(DeviceProfile.Generic, 0x0201, 0x0021);
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "1286";
        vm.QuantityText = "2";

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal(["513", "33"], vm.Rows.Select(r => r.ValueText));
        Assert.Equal("", vm.SoftwareVersionText);
    }
}

/// <summary>The version text in the real window (WPF UI tests run one at a time).</summary>
[Collection(WpfUiCollection.Name)]
public class SoftwareVersionUiTests
{
    [Fact]
    public void Header_ShowsVersionOnlyWhenRead()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true };
                window = new MainWindow { Left = -20000, Top = -20000, Width = 1100, ShowActivated = false, DataContext = vm };
                window.Show();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var text = (TextBlock)window.FindName("SoftwareVersionText");
                Assert.False(text.IsVisible);

                vm.SoftwareVersionText = "2.01.33";
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(text.IsVisible);
                Assert.Equal("Software version 2.01.33", string.Concat(text.Inlines.OfType<Run>().Select(r => r.Text)));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }
}
