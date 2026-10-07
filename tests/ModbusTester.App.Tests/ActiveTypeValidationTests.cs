using System.Net;
using System.Net.Sockets;
using System.Windows.Controls;
using System.Windows.Data;
using ModbusTester.App.Validation;
using ModbusTester.App.ViewModels;
using ModbusTester.Core.Models;
using WpfValidation = System.Windows.Controls.Validation;

namespace ModbusTester.App.Tests;

/// <summary>
/// Red borders are shown only for the active connection type; the inactive type keeps its values and
/// its (hidden) errors, which reappear when switching back.
/// </summary>
public class ActiveTypeValidationTests
{
    /// <summary>What WPF sees, i.e. whether the red border is drawn.</summary>
    private static bool Shown(MainViewModel vm, string property) => vm.GetErrors(property).Cast<object>().Any();

    private static int CountLog(MainViewModel vm, string text) => vm.LogEntries.Count(e => e.Message.Contains(text));

    private static MainViewModel RtuVm(string? comPort) => new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, IsRtu = true, SelectedComPort = comPort };

    // Test 1
    [Fact]
    public async Task Tcp_EmptyIp_ShowsError_ConnectDisabled()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, HostText = "" };
        Assert.True(Shown(vm, nameof(vm.HostText)));
        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    // Test 2
    [Fact]
    public async Task Tcp_EmptyPort_ShowsError_ConnectDisabled()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, PortText = "" };
        Assert.True(Shown(vm, nameof(vm.PortText)));
        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    // Test 3 + 4
    [Fact]
    public async Task TcpInvalid_SwitchToRtu_HidesBorder_KeepsValue_SwitchBack_ShowsAgain()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, HostText = "", PortText = "" };
        Assert.True(Shown(vm, nameof(vm.HostText)));
        Assert.True(Shown(vm, nameof(vm.PortText)));

        vm.IsRtu = true;

        Assert.Equal("", vm.HostText);
        Assert.Equal("", vm.PortText);
        Assert.False(Shown(vm, nameof(vm.HostText)));
        Assert.False(Shown(vm, nameof(vm.PortText)));
        Assert.False(InputValidator.Port(vm.PortText).IsValid);    // still invalid, only not displayed

        vm.IsTcp = true;

        Assert.Equal("", vm.PortText);
        Assert.True(Shown(vm, nameof(vm.HostText)));
        Assert.True(Shown(vm, nameof(vm.PortText)));
        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    // Test 5
    [Fact]
    public async Task Rtu_EmptyComPort_ShowsError_ConnectDisabled()
    {
        await using var vm = RtuVm(null);
        Assert.True(Shown(vm, nameof(vm.SelectedComPort)));
        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    // Test 6 + 7
    [Fact]
    public async Task RtuInvalid_SwitchToTcp_HidesBorder_KeepsValue_SwitchBack_ShowsAgain()
    {
        await using var vm = RtuVm(null);
        vm.SlaveIdText = "";
        Assert.True(Shown(vm, nameof(vm.SelectedComPort)));
        Assert.True(Shown(vm, nameof(vm.SlaveIdText)));

        vm.IsTcp = true;

        Assert.Null(vm.SelectedComPort);
        Assert.Equal("", vm.SlaveIdText);
        Assert.False(Shown(vm, nameof(vm.SelectedComPort)));
        Assert.False(Shown(vm, nameof(vm.SlaveIdText)));

        vm.IsRtu = true;

        Assert.Null(vm.SelectedComPort);
        Assert.True(Shown(vm, nameof(vm.SelectedComPort)));
        Assert.True(Shown(vm, nameof(vm.SlaveIdText)));
        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    // Test 8
    [Fact]
    public async Task TcpError_SwitchToRtu_StatusDisconnected_LogKept()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, HostText = "127.0.0.1", PortText = closedPort.ToString(), TimeoutText = "5000" };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Error, vm.ConnectionState);

        vm.IsRtu = true;

        Assert.Equal(ConnectionState.Disconnected, vm.ConnectionState);
        Assert.Equal(1, CountLog(vm, "Connect FAIL: Connection Refused"));
    }

    // Test 9
    [Fact]
    public async Task RtuError_SwitchToTcp_StatusDisconnected_LogKept()
    {
        await using var vm = RtuVm("COM_DOES_NOT_EXIST");
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Error, vm.ConnectionState);

        vm.IsTcp = true;

        Assert.Equal(ConnectionState.Disconnected, vm.ConnectionState);
        Assert.Equal(1, CountLog(vm, "COM_DOES_NOT_EXIST is not available"));
    }

    // Test 10
    [Fact]
    public async Task ActiveTypeAllValid_ConnectEnabled()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, HostText = "192.168.137.189", PortText = "5020" };
        Assert.False(vm.HasErrors);
        Assert.True(vm.ConnectCommand.CanExecute(null));

        vm.IsRtu = true;
        vm.SelectedComPort = "COM3";
        Assert.False(vm.HasErrors);
        Assert.True(vm.ConnectCommand.CanExecute(null));
    }

    // Test 11
    [Fact]
    public async Task InactiveTypeInvalid_DoesNotAffectActiveConnect()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, PortText = "", HostText = "abc" };
        vm.IsRtu = true;
        vm.SelectedComPort = "COM3";
        Assert.False(vm.HasErrors);
        Assert.True(vm.ConnectCommand.CanExecute(null));

        vm.SelectedComPort = null;     // RTU now invalid
        vm.IsTcp = true;
        vm.HostText = "192.168.137.189";
        vm.PortText = "5020";
        Assert.False(vm.HasErrors);
        Assert.True(vm.ConnectCommand.CanExecute(null));
    }

    // Test 12
    [Theory]
    [InlineData(nameof(MainViewModel.StartAddressText))]
    [InlineData(nameof(MainViewModel.QuantityText))]
    [InlineData(nameof(MainViewModel.DefaultScaleText))]
    public async Task RegisterReadErrors_AreShownForBothTypes(string property)
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true };
        typeof(MainViewModel).GetProperty(property)!.SetValue(vm, "");
        Assert.True(Shown(vm, property));

        vm.IsRtu = true;
        Assert.True(Shown(vm, property));
        vm.IsTcp = true;
        Assert.True(Shown(vm, property));
    }

    [Fact]
    public async Task Switching_NotifiesWpfToRequeryConnectionFields()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true };
        var notified = new List<string?>();
        vm.ErrorsChanged += (_, e) => notified.Add(e.PropertyName);

        vm.IsRtu = true;

        Assert.Contains(nameof(vm.PortText), notified);
        Assert.Contains(nameof(vm.SelectedComPort), notified);
    }

    /// <summary>Through real WPF bindings: Validation.HasError is what draws the red border.</summary>
    [Fact]
    public void WpfControls_RedBorderFollowsActiveType()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, PortText = "" };
                var port = new TextBox { DataContext = vm };
                port.SetBinding(TextBox.TextProperty, new Binding(nameof(vm.PortText)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                var com = new ComboBox { DataContext = vm, ItemsSource = new[] { "COM3" } };
                com.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding(nameof(vm.SelectedComPort)));
                vm.SelectedComPort = null;

                Assert.True(WpfValidation.GetHasError(port));
                Assert.False(WpfValidation.GetHasError(com));

                vm.IsRtu = true;
                Assert.Equal("", port.Text);
                Assert.False(WpfValidation.GetHasError(port));
                Assert.True(WpfValidation.GetHasError(com));

                vm.IsTcp = true;
                Assert.True(WpfValidation.GetHasError(port));
                Assert.False(WpfValidation.GetHasError(com));
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
}
