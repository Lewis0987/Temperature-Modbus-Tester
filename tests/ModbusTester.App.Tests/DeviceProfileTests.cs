using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ModbusTester.App.ViewModels;
using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>Phase 3A in the ViewModel: Generic stays as before, the A6V12258321 profile decodes registers 1–6.</summary>
public class DeviceProfileViewModelTests
{
    private static readonly ushort[] DocumentValues = [2534, 0, 4856, 0, 650, 0];

    private static async Task<(MainViewModel Vm, FakeModbusTcpSlave Slave)> ReadAsync(ushort[] registers, string quantity = "6", DeviceProfile? profile = null, string defaultScale = "1")
    {
        var slave = new FakeModbusTcpSlave { Registers = registers };
        var vm = new MainViewModel(null, TestVm.TempStore())
        {
            IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString(), QuantityText = quantity, DefaultScaleText = defaultScale,
        };
        if (profile is not null)
            vm.SelectedDeviceProfile = profile;
        await vm.ConnectCommand.ExecuteAsync(null);
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.True(vm.LastResultPassed);
        return (vm, slave);
    }

    [Fact]
    public async Task DefaultProfile_IsGeneric()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore());
        Assert.Same(DeviceProfile.Generic, vm.SelectedDeviceProfile);
        Assert.True(vm.IsGenericProfile);
        Assert.Equal(["Generic Modbus", A6V12258321Profile.Name], vm.DeviceProfiles.Select(p => p.Name));
    }

    // Test 10: Generic mode = previous behaviour (manual scale, editable, Apply to all rows)
    [Fact]
    public async Task Generic_ManualScaleBehaviourUnchanged()
    {
        var (vm, slave) = await ReadAsync(DocumentValues, defaultScale: "0.01");
        await using var _ = slave;
        await using var __ = vm;

        Assert.Equal(["25.34", "0", "48.56", "0", "6.5", "0"], vm.Rows.Select(r => r.ValueText));
        Assert.All(vm.Rows, r => Assert.True(r.IsScaleEditable));
        Assert.All(vm.Rows, r => Assert.Equal("", r.Description));
        Assert.All(vm.Rows, r => Assert.Equal("", r.Unit));

        vm.Rows[4].ScaleText = "1";                            // edit one row
        Assert.Equal("650", vm.Rows[4].ValueText);
        Assert.Throws<FormatException>(() => vm.Rows[4].ScaleText = "abc");
        Assert.Equal("650", vm.Rows[4].ValueText);              // invalid edit keeps the old value

        vm.DefaultScaleText = "0.1";
        vm.ApplyDefaultScaleCommand.Execute(null);
        Assert.Equal(["253.4", "0", "485.6", "0", "65", "0"], vm.Rows.Select(r => r.ValueText));
    }

    [Fact]
    public async Task Generic_DefaultScale1_ShowsScale1_ForEveryRegister()
    {
        var (vm, slave) = await ReadAsync(DocumentValues, defaultScale: "1");
        await using var _ = slave;
        await using var __ = vm;

        Assert.All(vm.Rows, r => Assert.Equal("1", r.ScaleText));
        Assert.Equal("650", vm.Rows[4].ValueText);
    }

    [Fact]
    public async Task Profile_DecodesRegisters1To6()
    {
        var (vm, slave) = await ReadAsync(DocumentValues, profile: A6V12258321Profile.Profile, defaultScale: "0.01");
        await using var _ = slave;
        await using var __ = vm;

        Assert.Equal(["Temperature value", "Temperature reliability", "Relative humidity value", "Humidity reliability", "CO2 value", "CO2 reliability"],
            vm.Rows.Select(r => r.Description));
        Assert.Equal(["25.34", "No error", "48.56", "No error", "650", "No error"], vm.Rows.Select(r => r.ValueText));
        Assert.Equal(["°C", "", "%", "", "ppm", ""], vm.Rows.Select(r => r.Unit));
        Assert.Equal(["0.01", "-", "0.01", "-", "-", "-"], vm.Rows.Select(r => r.ScaleText));   // Default Scale 0.01 NOT applied
    }

    [Fact]
    public async Task Profile_ScaleCannotBeChanged_ApplyToAllOnlyTouchesUndefinedRegisters()
    {
        // Registers 1..8: 1–6 defined by the profile, 7–8 generic
        var (vm, slave) = await ReadAsync([.. DocumentValues, 100, 200], quantity: "8", profile: A6V12258321Profile.Profile);
        await using var _ = slave;
        await using var __ = vm;

        Assert.Equal([false, false, false, false, false, false, true, true], vm.Rows.Select(r => r.IsScaleEditable));
        vm.Rows[0].ScaleText = "1";
        vm.Rows[0].Scale = 5;
        Assert.Equal("25.34", vm.Rows[0].ValueText);

        vm.DefaultScaleText = "0.5";
        vm.ApplyDefaultScaleCommand.Execute(null);

        Assert.Equal(["25.34", "No error", "48.56", "No error", "650", "No error", "50", "100"], vm.Rows.Select(r => r.ValueText));
    }

    [Fact]
    public async Task SwitchingProfile_ReDecodesLastRead_WithoutReadingAgain()
    {
        var (vm, slave) = await ReadAsync(DocumentValues);
        await using var _ = slave;
        await using var __ = vm;
        int requests = slave.ReceivedAdus.Count;
        int logLines = vm.LogEntries.Count;

        vm.SelectedDeviceProfile = A6V12258321Profile.Profile;
        Assert.Equal("25.34", vm.Rows[0].ValueText);
        Assert.False(vm.IsGenericProfile);

        vm.SelectedDeviceProfile = DeviceProfile.Generic;
        Assert.Equal("2534", vm.Rows[0].ValueText);

        Assert.Equal(requests, slave.ReceivedAdus.Count);
        Assert.Equal(logLines, vm.LogEntries.Count);
    }

    // Tests 8 + 9 through the ViewModel: data plausibility, not a communication failure
    [Fact]
    public async Task OutOfRange_ReadStillPass_ConnectionNotError_NoFailLog()
    {
        var (vm, slave) = await ReadAsync([2534, 0, 52312, 0, 57005, 0], profile: A6V12258321Profile.Profile);
        await using var _ = slave;
        await using var __ = vm;

        Assert.True(vm.LastResultPassed);
        Assert.Equal(ConnectionState.Connected, vm.ConnectionState);
        Assert.DoesNotContain(vm.LogEntries, e => e.Kind == LogKind.Fail);
        Assert.Equal(RegisterQuality.OutOfRange, vm.Rows[2].Quality);
        Assert.Equal("523.12 (Out of range)", vm.Rows[2].ValueText);
        Assert.Equal("57005 (Out of range)", vm.Rows[4].ValueText);
        Assert.Equal(52312, vm.Rows[2].Raw);
    }

    [Fact]
    public async Task NegativeTemperature_ShowsSignedDecimal()
    {
        var (vm, slave) = await ReadAsync([unchecked((ushort)(short)-1234), 0, 4856, 0, 650, 0], profile: A6V12258321Profile.Profile);
        await using var _ = slave;
        await using var __ = vm;

        Assert.Equal(64302, vm.Rows[0].Raw);
        Assert.Equal(-1234, vm.Rows[0].Decimal);
        Assert.Equal("-12.34", vm.Rows[0].ValueText);
    }
}

/// <summary>The real DataGrid: profile-defined Scale cells refuse to enter edit mode; generic ones still edit.</summary>
public class DeviceProfileGridUiTests
{
    private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T t)
                yield return t;
            foreach (T d in All<T>(child))
                yield return d;
        }
    }

    [Fact]
    public void ScaleCell_EditableOnlyForUndefinedRegisters()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var slave = new FakeModbusTcpSlave { Registers = [2534, 0, 4856, 0, 650, 0, 100] };
            var vm = new MainViewModel(null, TestVm.TempStore())
            {
                IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString(), QuantityText = "7",
                SelectedDeviceProfile = A6V12258321Profile.Profile,
            };
            var window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false, DataContext = vm };
            window.Show();
            var frame = new DispatcherFrame();
            Task task = Body();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            window.Close();
            failure = task.Exception?.InnerException;

            async Task Body()
            {
                // Clean-up is awaited inside the frame: continuations need this dispatcher to still be pumping.
                await using var _ = slave;
                await using var __ = vm;
                await vm.ConnectCommand.ExecuteAsync(null);
                await vm.ReadCommand.ExecuteAsync(null);
                await Task.Delay(200);
                DataGrid grid = All<DataGrid>(window).Single();
                Assert.Equal(["Address", "Description", "Raw", "Decimal", "HEX", "Binary", "Scale", "Value", "Unit"],
                    grid.Columns.Select(c => (string)c.Header));
                DataGridColumn scale = grid.Columns.Single(c => (string)c.Header == "Scale");

                grid.CurrentCell = new DataGridCellInfo(vm.Rows[0], scale);   // Register 1: profile-defined
                Assert.False(grid.BeginEdit());

                grid.CurrentCell = new DataGridCellInfo(vm.Rows[6], scale);   // Register 7: generic
                Assert.True(grid.BeginEdit());
                await Task.Delay(100);
                TextBox editor = All<TextBox>(grid).Single(t => t.IsVisible && t.Text == "1");
                editor.Text = "0.5";
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                await Task.Delay(100);
                Assert.Equal("50", vm.Rows[6].ValueText);
                Assert.Equal("25.34", vm.Rows[0].ValueText);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }
}
