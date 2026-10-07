using System.Net;
using System.Net.Sockets;
using System.Windows.Controls;
using System.Windows.Data;
using WpfValidation = System.Windows.Controls.Validation;
using ModbusTester.App.ViewModels;
using ModbusTester.Core.Models;

namespace ModbusTester.App.Tests;

/// <summary>ViewModel behaviour without a window: validation, the Connect gate and connection-type switching.</summary>
[Collection(WpfUiCollection.Name)]
public class MainViewModelTests
{
    private static bool HasError(MainViewModel vm, string property) => vm.GetErrors(property).Cast<object>().Any();

    private static int CountLog(MainViewModel vm, string text) => vm.LogEntries.Count(e => e.Message.Contains(text));

    /// <summary>A loopback port with nothing listening (connect → Connection Refused).</summary>
    private static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static MainViewModel TcpVm(int port) => new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, HostText = "127.0.0.1", PortText = port.ToString(), TimeoutText = "5000" };

    [Fact]
    public async Task GuiTimeout_IsUsedForConnect()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, HostText = "10.255.255.1", TimeoutText = "600" };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionState.Timeout, vm.ConnectionState);
        Assert.Contains(vm.LogEntries, e => e.Message == "Connect FAIL: Connect timeout (600 ms)");
        Assert.InRange(stopwatch.ElapsedMilliseconds, 550, 2000);
    }

    [Fact]
    public async Task DefaultTcpPort_Is5020_AndValid()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true };
        Assert.Equal("5020", vm.PortText);
        Assert.False(HasError(vm, nameof(vm.PortText)));
        Assert.True(vm.ConnectCommand.CanExecute(null));
    }

    // Test 1 + final gate
    [Fact]
    public async Task EmptyPort_IsInvalid_ConnectDisabled_AndGateBlocksExecution()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, PortText = "" };

        Assert.True(HasError(vm, nameof(vm.PortText)));
        Assert.False(vm.ConnectCommand.CanExecute(null));

        // Even if the button state were stale, executing the command must not create a transport.
        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(0, CountLog(vm, " Connect"));
        Assert.Equal(1, CountLog(vm, "Validation FAIL: Port is required."));
        Assert.Equal(ConnectionState.Disconnected, vm.ConnectionState);
    }

    // Test 6 (main regression): 5020-like valid port → connect → clear → must not reuse the old port
    [Fact]
    public async Task ValidPortThenCleared_DoesNotReuseOldPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            await using var vm = TcpVm(port);
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.Equal(ConnectionState.Connected, vm.ConnectionState);
            await vm.DisconnectCommand.ExecuteAsync(null);
            Assert.Equal(1, CountLog(vm, $"TCP 127.0.0.1:{port} Connect"));

            vm.PortText = "";

            Assert.Equal("", vm.PortText);
            Assert.True(HasError(vm, nameof(vm.PortText)));
            Assert.False(vm.ConnectCommand.CanExecute(null));
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.Equal(1, CountLog(vm, $"TCP 127.0.0.1:{port} Connect"));   // no new connect attempt
            Assert.Equal(ConnectionState.Disconnected, vm.ConnectionState);
            Assert.DoesNotContain(vm.LogEntries, e => e.Message.Contains("Connection Refused"));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(nameof(MainViewModel.TimeoutText), "")]
    [InlineData(nameof(MainViewModel.HostText), "192.168.137.999")]
    [InlineData(nameof(MainViewModel.UnitIdText), "")]
    public async Task OtherInvalidTcpFields_DisableConnect(string property, string value)
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true };
        Assert.True(vm.ConnectCommand.CanExecute(null));

        typeof(MainViewModel).GetProperty(property)!.SetValue(vm, value);

        Assert.True(HasError(vm, property));
        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    // Test 10: invalid → valid clears the error (and with it the red border)
    [Fact]
    public async Task InvalidThenValid_ClearsErrorImmediately()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true };
        var changed = new List<string?>();
        vm.ErrorsChanged += (_, e) => changed.Add(e.PropertyName);

        vm.PortText = "abc";
        Assert.True(HasError(vm, nameof(vm.PortText)));
        vm.PortText = "5020";

        Assert.False(HasError(vm, nameof(vm.PortText)));
        Assert.False(vm.HasErrors);
        Assert.True(vm.ConnectCommand.CanExecute(null));
        Assert.Equal([nameof(vm.PortText), nameof(vm.PortText)], changed);
    }

    // Test 10 through a real WPF binding: the TextBox's Validation.HasError is what draws the red border.
    [Fact]
    public void WpfTextBox_RedBorderFollowsInput_AndEmptyTextReachesViewModel()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, PortText = "5020" };
                var box = new TextBox { DataContext = vm };
                box.SetBinding(TextBox.TextProperty, new Binding(nameof(vm.PortText)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                Assert.False(WpfValidation.GetHasError(box));

                box.Text = "";
                Assert.Equal("", vm.PortText);                 // the root cause: previously the VM kept 5020
                Assert.True(WpfValidation.GetHasError(box));
                Assert.Equal("Port is required.", WpfValidation.GetErrors(box)[0].ErrorContent);

                box.Text = "5020";
                Assert.False(WpfValidation.GetHasError(box));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }

    // Test 11 + 14
    [Fact]
    public async Task TcpError_SwitchToRtu_StatusDisconnected_LogKept()
    {
        await using var vm = TcpVm(ClosedPort());
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Error, vm.ConnectionState);
        Assert.Equal(1, CountLog(vm, "Connect FAIL: Connection Refused"));
        int logCount = vm.LogEntries.Count;

        vm.IsRtu = true;

        Assert.Equal(ConnectionState.Disconnected, vm.ConnectionState);
        Assert.Equal(1, CountLog(vm, "Connect FAIL: Connection Refused"));
        Assert.True(vm.LogEntries.Count >= logCount);
        Assert.False(vm.DisconnectCommand.CanExecute(null));
    }

    // Test 12
    [Fact]
    public async Task RtuError_SwitchToTcp_StatusDisconnected()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, IsRtu = true, SelectedComPort = "COM_DOES_NOT_EXIST" };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Error, vm.ConnectionState);
        Assert.Equal(1, CountLog(vm, "not available"));

        vm.IsTcp = true;

        Assert.Equal(ConnectionState.Disconnected, vm.ConnectionState);
        Assert.Equal(1, CountLog(vm, "not available"));
    }

    // Test 13
    [Fact]
    public async Task SwitchingTypes_KeepsTcpFieldsAndTheirValidation()
    {
        await using var vm = TcpVm(ClosedPort());
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Error, vm.ConnectionState);
        vm.PortText = "";

        vm.IsRtu = true;
        Assert.Equal(ConnectionState.Disconnected, vm.ConnectionState);
        vm.IsTcp = true;

        Assert.Equal("127.0.0.1", vm.HostText);
        Assert.Equal("", vm.PortText);
        Assert.True(HasError(vm, nameof(vm.PortText)));
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.Equal(ConnectionState.Disconnected, vm.ConnectionState);

        vm.PortText = "5020";
        Assert.False(HasError(vm, nameof(vm.PortText)));
        Assert.True(vm.ConnectCommand.CanExecute(null));
    }

    // Test 15
    [Fact]
    public async Task CanExecute_FollowsTransportType_Validation_AndState()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, PortText = "" };
        Assert.False(vm.ConnectCommand.CanExecute(null));          // TCP, invalid port

        vm.IsRtu = true;
        vm.SelectedComPort = null;
        Assert.False(vm.ConnectCommand.CanExecute(null));          // RTU, no COM port
        vm.SelectedComPort = "COM_DOES_NOT_EXIST";
        Assert.True(vm.ConnectCommand.CanExecute(null));           // RTU valid; TCP's invalid port is irrelevant
        vm.SlaveIdText = "";
        Assert.False(vm.ConnectCommand.CanExecute(null));
        vm.SlaveIdText = "1";

        await vm.ConnectCommand.ExecuteAsync(null);                 // → Error
        Assert.True(vm.DisconnectCommand.CanExecute(null));

        vm.IsTcp = true;
        Assert.False(vm.DisconnectCommand.CanExecute(null));
        Assert.False(vm.ConnectCommand.CanExecute(null));          // back to TCP with the still-invalid port
        vm.PortText = "502";
        Assert.True(vm.ConnectCommand.CanExecute(null));
    }

    // Section 16: RTU fields had the same int-binding stale value problem
    [Theory]
    [InlineData(nameof(MainViewModel.SlaveIdText))]
    [InlineData(nameof(MainViewModel.RtuTimeoutText))]
    public async Task RtuEmptyField_IsInvalid_AndGateBlocksConnect(string property)
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, IsRtu = true, SelectedComPort = "COM_DOES_NOT_EXIST" };
        typeof(MainViewModel).GetProperty(property)!.SetValue(vm, "");

        Assert.True(HasError(vm, property));
        Assert.False(vm.ConnectCommand.CanExecute(null));
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(0, CountLog(vm, "RTU COM_DOES_NOT_EXIST"));
        Assert.Equal(1, CountLog(vm, "Validation FAIL"));
    }

    [Fact]
    public async Task Connected_LocksConnectionFields()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var vm = TcpVm(((IPEndPoint)listener.LocalEndpoint).Port);
            Assert.True(vm.CanEditConnection);
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.False(vm.CanEditConnection);
            await vm.DisconnectCommand.ExecuteAsync(null);
            Assert.True(vm.CanEditConnection);
        }
        finally
        {
            listener.Stop();
        }
    }
}
