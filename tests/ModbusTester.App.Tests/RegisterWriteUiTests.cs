using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ModbusTester.App.ViewModels;
using ModbusTester.Core.Profiles;

namespace ModbusTester.App.Tests;

/// <summary>Phase 4C in the real MainWindow: Register Write below Register Read, inputs follow the profile / register type.</summary>
[Collection(WpfUiCollection.Name)]
public class RegisterWriteUiTests
{
    private static void Pump(Window window)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static T Named<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);

    private static void RunSta(Action<MainWindow, MainViewModel> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true };
                vm.Write.Confirm = _ => false;
                window = new MainWindow { Left = -20000, Top = -20000, Width = 1100, ShowActivated = false, DataContext = vm };
                window.Show();
                Pump(window);
                body(window, vm);
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

    [Fact]
    public void WriteArea_FollowsProfileAndRegisterType()
    {
        RunSta((window, vm) =>
        {
            var writeArea = Named<Grid>(window, "WriteArea");
            var readRow = Named<Grid>(window, "ReadRow");
            Assert.Same(vm.Write, writeArea.DataContext);
            // Below Register Read, separate group
            Assert.True(writeArea.TranslatePoint(new Point(), window).Y > readRow.TranslatePoint(new Point(), window).Y + readRow.ActualHeight);

            // Generic: FC06/FC16 selectable, Address + Raw Value, no Register list / Reset
            Assert.True(Named<ComboBox>(window, "GenericWriteFunction").IsVisible);
            Assert.False(Named<ComboBox>(window, "ProfileWriteFunction").IsVisible);
            Assert.True(Named<TextBox>(window, "WriteAddress").IsVisible);
            Assert.True(Named<TextBox>(window, "WriteRawValues").IsVisible);
            Assert.False(Named<ComboBox>(window, "WriteRegister").IsVisible);
            Assert.False(Named<Button>(window, "ResetToDefaultButton").IsVisible);
            Assert.False(Named<Button>(window, "WriteButton").IsEnabled);

            // Profile: FC06 fixed and disabled, Register list, numeric value for 223
            vm.SelectedDeviceProfile = A6V12258321Profile.Profile;
            Pump(window);
            var profileFunction = Named<ComboBox>(window, "ProfileWriteFunction");
            Assert.True(profileFunction.IsVisible);
            Assert.False(profileFunction.IsEnabled);
            Assert.Equal("FC06 - Write Single Register", ((Option<Core.Models.ModbusFunction>)profileFunction.SelectedItem).Text);
            Assert.False(Named<ComboBox>(window, "GenericWriteFunction").IsVisible);
            Assert.False(Named<TextBox>(window, "WriteAddress").IsVisible);
            Assert.Equal(7, Named<ComboBox>(window, "WriteRegister").Items.Count);

            // 223 without a known System unit (nothing read yet): the Core's message, no unit, Write disabled
            vm.Write.SelectedRegister = vm.Write.WritableRegisters[0];
            vm.Write.ValueText = "-2.5";
            Pump(window);
            Assert.Contains("read Register 401 first", Named<TextBlock>(window, "WriteValidationMessage").Text);
            Assert.False(Named<TextBlock>(window, "WritePreview").IsVisible);
            Assert.False(Named<Button>(window, "WriteButton").IsEnabled);

            // 224 (fixed unit %) previews as before
            vm.Write.SelectedRegister = vm.Write.WritableRegisters.Single(r => r.Definition.RegisterNumber == 224);
            vm.Write.ValueText = "-2.5";
            Pump(window);
            Assert.True(Named<TextBox>(window, "WriteValue").IsVisible);
            Assert.False(Named<ComboBox>(window, "WriteState").IsVisible);
            Assert.True(Named<TextBlock>(window, "WritePreview").IsVisible);
            Assert.Contains("FFE7", new TextRange(Named<TextBlock>(window, "WritePreview")).Text);
            Assert.False(Named<Button>(window, "WriteButton").IsEnabled);          // not connected

            // 401: ComboBox with the metadata states
            vm.Write.SelectedRegister = vm.Write.WritableRegisters.Single(r => r.Definition.RegisterNumber == 401);
            Pump(window);
            var state = Named<ComboBox>(window, "WriteState");
            Assert.True(state.IsVisible);
            Assert.False(Named<TextBox>(window, "WriteValue").IsVisible);
            Assert.Equal(["Celsius", "Fahrenheit"], state.Items.Cast<string>());

            // 764: hold reason shown, 246 shows the Core message
            vm.Write.SelectedRegister = vm.Write.WritableRegisters.Single(r => r.Definition.RegisterNumber == 764);
            vm.Write.ValueText = "246";
            Pump(window);
            Assert.True(Named<TextBlock>(window, "WriteHoldReason").IsVisible);
            Assert.Contains("Reserved for on-event addressing", Named<TextBlock>(window, "WriteValidationMessage").Text);
            Assert.False(Named<Button>(window, "WriteButton").IsEnabled);

            // The write row fits the default window width (no clipping / no wider window)
            var row = Named<StackPanel>(window, "WriteRow");
            Assert.True(row.ActualWidth <= writeArea.ActualWidth + 0.5, $"{row.ActualWidth} > {writeArea.ActualWidth}");
            Assert.Equal(1100, window.ActualWidth, 1);

            // Back to Generic: nothing of the profile remains visible
            vm.SelectedDeviceProfile = DeviceProfile.Generic;
            Pump(window);
            Assert.False(Named<TextBlock>(window, "WriteHoldReason").IsVisible);
            Assert.Equal("", Named<TextBlock>(window, "WriteValidationMessage").Text);
            Assert.False(Named<TextBlock>(window, "WritePreview").IsVisible);
            Assert.True(row.ActualWidth <= writeArea.ActualWidth + 0.5, $"Generic {row.ActualWidth} > {writeArea.ActualWidth}");
        });
    }

    /// <summary>Text of a TextBlock made of Runs.</summary>
    private sealed class TextRange(TextBlock block)
    {
        public string Text => string.Concat(block.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
    }
}
