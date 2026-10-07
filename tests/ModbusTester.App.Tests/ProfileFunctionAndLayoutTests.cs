using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ModbusTester.App.Controls;
using ModbusTester.App.ViewModels;
using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>Phase 3C prep: the profile prescribes the read function; Generic keeps the user's choice.</summary>
public class ProfileFunctionViewModelTests
{
    private static ushort[] DeviceMap()
    {
        var registers = new ushort[225];
        new ushort[] { 2534, 0, 4856, 0, 650, 0 }.CopyTo(registers, 0);
        registers[222] = 15;
        registers[223] = unchecked((ushort)(short)-25);
        registers[224] = 150;
        return registers;
    }

    private static async Task<(MainViewModel Vm, FakeModbusTcpSlave Slave)> ConnectAsync()
    {
        var slave = new FakeModbusTcpSlave { Registers = DeviceMap() };
        var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString() };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        return (vm, slave);
    }

    private static Option<ModbusFunction> Fc(MainViewModel vm, ModbusFunction f) => vm.Functions.Single(o => o.Value == f);

    private static string[] Requests(FakeModbusTcpSlave slave) => [.. slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..]))];

    // Tests 1 + 2
    [Fact]
    public async Task Generic_FunctionSelectable_Fc03AndFc04()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore());
        Assert.True(vm.IsFunctionSelectable);
        Assert.Equal(ModbusFunction.ReadHoldingRegisters, vm.SelectedFunction.Value);
        vm.SelectedFunction = Fc(vm, ModbusFunction.ReadInputRegisters);
        Assert.Equal(ModbusFunction.ReadInputRegisters, vm.SelectedFunction.Value);
    }

    // Test 3 + 11: forced FC03 in the profile, Generic FC04 restored afterwards (design A)
    [Fact]
    public async Task SwitchToProfile_ForcesFc03_BackToGeneric_RestoresUserChoice()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore());
        vm.SelectedFunction = Fc(vm, ModbusFunction.ReadInputRegisters);

        vm.SelectedDeviceProfile = A6V12258321Profile.Profile;
        Assert.Equal(ModbusFunction.ReadHoldingRegisters, vm.SelectedFunction.Value);
        Assert.False(vm.IsFunctionSelectable);

        vm.SelectedDeviceProfile = DeviceProfile.Generic;
        Assert.Equal(ModbusFunction.ReadInputRegisters, vm.SelectedFunction.Value);
        Assert.True(vm.IsFunctionSelectable);
    }

    [Fact]
    public async Task GenericFc03_StaysFc03_AfterRoundTrip()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore());
        vm.SelectedDeviceProfile = A6V12258321Profile.Profile;
        vm.SelectedDeviceProfile = DeviceProfile.Generic;
        Assert.Equal(ModbusFunction.ReadHoldingRegisters, vm.SelectedFunction.Value);
    }

    // Test 5 + 10: profile reads are FC03 even if something sets FC04 behind the disabled box (final gate)
    [Fact]
    public async Task Profile_Address225_TwoFc03Requests_EvenIfFc04IsSelected()
    {
        var (vm, slave) = await ConnectAsync();
        await using var _ = slave;
        await using var __ = vm;
        vm.SelectedDeviceProfile = A6V12258321Profile.Profile;
        vm.SelectedFunction = Fc(vm, ModbusFunction.ReadInputRegisters);   // not possible from the UI
        vm.StartAddressText = "225";

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal(["03 00 00 00 06", "03 00 DE 00 03"], Requests(slave));
        Assert.Equal(9, vm.Rows.Count);
        Assert.DoesNotContain(vm.LogEntries, e => e.Message.StartsWith("FC04"));
        Assert.Equal(2, vm.LogEntries.Count(e => e.Message.StartsWith("FC03")));
    }

    // Test 11: Generic FC04 still works and is not polluted by the profile's FC03
    [Fact]
    public async Task Generic_Fc04_AfterProfileRoundTrip_SendsFc04()
    {
        var (vm, slave) = await ConnectAsync();
        await using var _ = slave;
        await using var __ = vm;
        vm.SelectedFunction = Fc(vm, ModbusFunction.ReadInputRegisters);
        vm.SelectedDeviceProfile = A6V12258321Profile.Profile;
        vm.SelectedDeviceProfile = DeviceProfile.Generic;
        vm.StartAddressText = "1";
        vm.QuantityText = "6";

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal(["04 00 00 00 06"], Requests(slave));
        Assert.True(vm.LastResultPassed);
        Assert.Equal(6, vm.Rows.Count);
    }

    // Test 12: switching profiles does not change the cumulative read
    [Fact]
    public async Task CumulativeRead_SameAfterProfileSwitches()
    {
        var (vm, slave) = await ConnectAsync();
        await using var _ = slave;
        await using var __ = vm;
        vm.SelectedDeviceProfile = A6V12258321Profile.Profile;
        vm.StartAddressText = "225";
        await vm.ReadCommand.ExecuteAsync(null);
        string first = string.Join("|", vm.Rows.Select(r => $"{r.Address}:{r.ValueText}"));

        vm.SelectedDeviceProfile = DeviceProfile.Generic;
        vm.SelectedDeviceProfile = A6V12258321Profile.Profile;
        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal(first, string.Join("|", vm.Rows.Select(r => $"{r.Address}:{r.ValueText}")));
        Assert.Equal("1:25.34|2:No error|3:48.56|4:No error|5:650|6:No error|223:1.5|224:-2.5|225:150", first);
        Assert.Equal(["03 00 00 00 06", "03 00 DE 00 03", "03 00 00 00 06", "03 00 DE 00 03"], Requests(slave));
    }
}

/// <summary>The real MainWindow: Function box disabled in the profile; Quantity collapses and Read moves left.</summary>
[Collection(WpfUiCollection.Name)]
public class ProfileFunctionAndLayoutUiTests
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

    /// <summary>Lets bindings update, then forces layout synchronously (no reliance on timing when tests run in parallel).</summary>
    private static void Pump(Window? window = null)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window?.UpdateLayout();
    }

    /// <summary>Left/right of an element in the coordinates of <paramref name="relativeTo"/>.</summary>
    private static (double Left, double Right) Span(FrameworkElement e, Visual relativeTo)
    {
        Point p = e.TranslatePoint(new Point(0, 0), (UIElement)relativeTo);
        return (p.X, p.X + e.ActualWidth);
    }

    // Tests 4, 6, 7, 8, 9
    [Fact]
    public void FunctionDisabled_QuantityCollapsed_ReadMovesLeft_AndBack()
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
                Pump(window);

                var row = (Grid)window.FindName("ReadRow");
                ComboBox function = All<ComboBox>(row).Single(c => c.ItemsSource == vm.Functions);
                HistoryField address = All<HistoryField>(row).Single(f => ReferenceEquals(f.History, vm.AddressHistory));
                HistoryField quantity = All<HistoryField>(row).Single(f => ReferenceEquals(f.History, vm.QuantityHistory));
                Label quantityLabel = All<Label>(row).Single(l => (l.Content as string) == "Quantity");
                Button read = All<Button>(row).Single(b => (b.Content as string) == "Read");

                // Generic: Function selectable, Read right of Quantity (Test 9)
                Assert.True(function.IsEnabled);
                Assert.True(quantity.IsVisible);
                Assert.True(Span(read, row).Left >= Span(quantity, row).Right);
                double genericReadLeft = Span(read, row).Left;

                // Profile: FC03 fixed and disabled (Test 4), Quantity collapsed (Test 6), Read next to Address (Test 8)
                vm.SelectedDeviceProfile = A6V12258321Profile.Profile;
                Pump(window);
                Assert.False(function.IsEnabled);
                Assert.Equal("FC03 - Read Holding Registers", ((Option<ModbusFunction>)function.SelectedItem).Text);
                Assert.Equal(Visibility.Collapsed, quantity.Visibility);
                Assert.Equal(Visibility.Collapsed, quantityLabel.Visibility);
                double gap = Span(read, row).Left - Span(address, row).Right;
                Assert.InRange(gap, 0, 12);                                   // only the normal control margins
                Assert.True(Span(read, row).Left < genericReadLeft - 80);     // moved left by at least the Quantity field

                // Back to Generic: everything restored (Test 7)
                vm.SelectedDeviceProfile = DeviceProfile.Generic;
                Pump(window);
                Assert.True(function.IsEnabled);
                Assert.True(quantity.IsVisible);
                Assert.Equal(genericReadLeft, Span(read, row).Left, 1);
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
