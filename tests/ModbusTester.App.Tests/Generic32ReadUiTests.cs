using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ModbusTester.App.ViewModels;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Services;

namespace ModbusTester.App.Tests;

/// <summary>Phase 5B in the real MainWindow: Generic Data Type / Order in the Register Read row, hidden for a profile.</summary>
[Collection(WpfUiCollection.Name)]
public class Generic32ReadUiTests
{
    private static void Pump(Window window)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static T Named<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);

    [Fact]
    public void DataTypeAndOrder_FollowGenericAndType()
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

                var panel = Named<StackPanel>(window, "GenericDecodePanel");
                var dataType = Named<ComboBox>(window, "GenericDataType");
                var order = Named<ComboBox>(window, "Register32Order");
                var row = Named<Grid>(window, "ReadRow");
                Button read = row.Children.OfType<Button>().Single(b => (b.Content as string) == "Read");

                // Generic: visible, UInt16 by default, Order disabled
                Assert.True(panel.IsVisible);
                Assert.Equal(["UInt16", "Int16", "UInt32", "Int32", "Float32"], dataType.Items.Cast<Option<DataType>>().Select(o => o.Text));
                Assert.Equal(["ABCD", "BADC", "CDAB", "DCBA"], order.Items.Cast<Option<Register32Order>>().Select(o => o.Text));
                Assert.Equal("UInt16", ((Option<DataType>)dataType.SelectedItem).Text);
                Assert.Equal("ABCD", ((Option<Register32Order>)order.SelectedItem).Text);
                Assert.False(order.IsEnabled);

                // 32-bit: Order enabled; 16-bit again: disabled (layout does not move)
                double readLeft = read.TranslatePoint(new Point(), row).X;
                vm.SelectedGenericDataType = vm.GenericDataTypes.Single(t => t.Value == DataType.Float32);
                Pump(window);
                Assert.True(order.IsEnabled);
                Assert.Equal(readLeft, read.TranslatePoint(new Point(), row).X, 1);
                order.SelectedItem = vm.Register32Orders.Single(o => o.Value == Register32Order.CDAB);   // UI → ViewModel binding
                Pump(window);
                Assert.Equal(Register32Order.CDAB, vm.SelectedRegister32Order.Value);
                dataType.SelectedItem = vm.GenericDataTypes.Single(t => t.Value == DataType.Int16);
                Pump(window);
                Assert.Equal(DataType.Int16, vm.SelectedGenericDataType.Value);
                Assert.False(order.IsEnabled);

                // The read row fits the default width and Read stays right of the decode panel
                Assert.True(read.TranslatePoint(new Point(), row).X >= panel.TranslatePoint(new Point(), row).X + panel.ActualWidth - 0.5);
                Assert.True(row.ActualWidth <= ((FrameworkElement)row.Parent).ActualWidth + 0.5);

                // Profile: hidden (the profile decides from its definitions)
                vm.SelectedGenericDataType = vm.GenericDataTypes.Single(t => t.Value == DataType.Float32);
                vm.SelectedDeviceProfile = A6V12258321Profile.Profile;
                Pump(window);
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                Assert.False(vm.IsOrderEnabled);

                // Back to Generic: visible again with the kept choice
                vm.SelectedDeviceProfile = DeviceProfile.Generic;
                Pump(window);
                Assert.True(panel.IsVisible);
                Assert.True(order.IsEnabled);
                Assert.Equal("Float32", ((Option<DataType>)dataType.SelectedItem).Text);
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
