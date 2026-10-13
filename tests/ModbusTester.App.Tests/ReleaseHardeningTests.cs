using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ModbusTester.App.ViewModels;
using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>Phase 5D: version / title, Communication Log save, shutdown cleanup.</summary>
public class ReleaseVersionTests
{
    [Fact]
    public void Version_FromProject_1_0_0()
    {
        Assembly app = typeof(AppInfo).Assembly;
        Assert.Equal("1.0.0", AppInfo.Version);
        Assert.Equal(new Version(1, 0, 0, 0), app.GetName().Version);                                  // AssemblyVersion
        Assert.Equal("1.0.0.0", app.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);       // FileVersion
        Assert.Equal("1.0.0", app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        Assert.Equal("1.0.0.0", FileVersionInfo.GetVersionInfo(app.Location).FileVersion);
    }

    [Fact]
    public void Title_FromAssemblyVersion()
    {
        Assert.Equal("Modbus Tester V1.0.0", AppInfo.Title);
        Assert.Equal($"Modbus Tester V{typeof(AppInfo).Assembly.GetName().Version!.ToString(3)}", AppInfo.Title);
        Assert.Equal(AppInfo.Title, new MainViewModel(null, TestVm.TempStore()).WindowTitle);
    }
}

public class CommunicationLogSaveTests
{
    private static string TempFile(string name = "log.txt")
    {
        string dir = Path.Combine(Path.GetTempPath(), "ModbusTester.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    [Fact]
    public void DefaultFileName_Format() =>
        Assert.Equal("ModbusLog_20261013_142501.txt", CommunicationLogExport.DefaultFileName(new DateTime(2026, 10, 13, 14, 25, 1)));

    [Fact]
    public void Save_Utf8WithoutBom_RoundTrip()
    {
        string path = TempFile();
        const string text = "line 1\r\n溫度 25.34 °C\r\nFC03  Unit ID = 1";
        CommunicationLogExport.Save(path, text);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal(text, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void SaveCommand_WritesTheLogAsShown()
    {
        var vm = new MainViewModel(null, TestVm.TempStore());
        ((IRegisterWriteHost)vm).Logger.Info("溫度 test entry");
        string expected = CommunicationLogExport.BuildText(vm.LogEntries);
        string path = TempFile();
        string? suggested = null;
        vm.PickLogFile = name => { suggested = name; return path; };

        vm.SaveLogCommand.Execute(null);

        Assert.Matches(@"^ModbusLog_\d{8}_\d{6}\.txt$", suggested);
        Assert.Equal(expected, File.ReadAllText(path, Encoding.UTF8));
        Assert.Contains("溫度 test entry", File.ReadAllText(path, Encoding.UTF8));
        Assert.Contains(vm.LogEntries, e => e.Kind == LogKind.Info && e.Message.StartsWith($"Communication Log saved: {path}"));
    }

    [Fact]
    public void SaveCommand_Cancel_WritesNothing()
    {
        var vm = new MainViewModel(null, TestVm.TempStore());
        int before = vm.LogEntries.Count;
        vm.PickLogFile = _ => null;

        vm.SaveLogCommand.Execute(null);

        Assert.Equal(before, vm.LogEntries.Count);                       // no error, no status
    }

    [Fact]
    public void SaveCommand_EmptyLog_NoDialog()
    {
        var vm = new MainViewModel(null, TestVm.TempStore());
        vm.ClearLogCommand.Execute(null);
        bool asked = false;
        vm.PickLogFile = _ => { asked = true; return TempFile(); };

        vm.SaveLogCommand.Execute(null);

        Assert.False(asked);
        Assert.Equal("Communication Log is empty: nothing saved", Assert.Single(vm.LogEntries).Message);
    }

    [Fact]
    public void SaveCommand_IoError_Reported_NoThrow()
    {
        var vm = new MainViewModel(null, TestVm.TempStore());
        string missingDir = Path.Combine(Path.GetTempPath(), "ModbusTester.App.Tests", Guid.NewGuid().ToString("N"), "missing", "log.txt");
        vm.PickLogFile = _ => missingDir;

        vm.SaveLogCommand.Execute(null);

        Assert.False(File.Exists(missingDir));
        Assert.Contains(vm.LogEntries, e => e.Kind == LogKind.Error && e.Message.StartsWith("Save log failed:"));
    }
}

public class ShutdownTests
{
    [Fact]
    public async Task TcpConnected_ShutdownReleasesTransport_NoMoreUiUpdates()
    {
        await using var slave = new FakeModbusTcpSlave();
        var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString() };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Connected, vm.ServiceState);

        await vm.ShutdownAsync();

        Assert.True(vm.IsShutDown);
        Assert.Equal(ConnectionState.Disconnected, vm.ServiceState);
        int entries = vm.LogEntries.Count;
        ((IRegisterWriteHost)vm).Logger.Info("after shutdown");
        Assert.Equal(entries, vm.LogEntries.Count);                      // the log no longer touches the UI collection
    }

    [Fact]
    public async Task RtuConnected_ShutdownClosesSerialPort()
    {
        var channel = new FakeSerialChannel();
        var vm = new MainViewModel(_ => channel, TestVm.TempStore()) { IsRtu = true, SelectedComPort = "COM_TEST" };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Connected, vm.ServiceState);

        await vm.ShutdownAsync();

        Assert.True(channel.IsDisposed);
        Assert.Equal(ConnectionState.Disconnected, vm.ServiceState);
    }

    [Fact]
    public async Task NotConnected_Shutdown_Ok()
    {
        var vm = new MainViewModel(null, TestVm.TempStore());
        await vm.ShutdownAsync();
        Assert.True(vm.IsShutDown);
        Assert.Equal(ConnectionState.Disconnected, vm.ServiceState);
    }

    [Fact]
    public async Task RepeatedShutdown_RunsOnce()
    {
        int opened = 0;
        var channel = new FakeSerialChannel();
        var vm = new MainViewModel(_ => { opened++; return channel; }, TestVm.TempStore()) { IsRtu = true, SelectedComPort = "COM_TEST" };
        await vm.ConnectCommand.ExecuteAsync(null);

        await Task.WhenAll(vm.ShutdownAsync(), vm.ShutdownAsync(), vm.DisposeAsync().AsTask());

        Assert.True(channel.IsDisposed);
        Assert.Equal(1, opened);
    }

    // A read in progress is cancelled by shutdown and finishes quietly (no exception, no table update)
    [Fact]
    public async Task BusyRead_CancelledByShutdown()
    {
        await using var slave = new FakeModbusTcpSlave { ResponseDelay = TimeSpan.FromSeconds(3) };
        var vm = new MainViewModel(null, TestVm.TempStore())
        {
            IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString(), TimeoutText = "5000",
        };
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.QuantityText = "1";
        Task read = vm.ReadCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);

        var timer = Stopwatch.StartNew();
        await vm.ShutdownAsync();
        await read;

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), timer.Elapsed.ToString());
        Assert.Empty(vm.Rows);
        Assert.Equal(ConnectionState.Disconnected, vm.ServiceState);
    }
}

public class WindowCloseGuardTests
{
    [Fact]
    public async Task FirstCloseCancelled_CleanupOnce_ThenCloses()
    {
        var gate = new TaskCompletionSource();
        int closes = 0;
        var guard = new WindowCloseGuard(() => gate.Task, TimeSpan.FromSeconds(5));

        Assert.False(guard.OnClosing(() => closes++));
        Assert.False(guard.OnClosing(() => closes++));                   // repeated close while cleaning up
        Assert.Equal((1, 0), (guard.CleanupRuns, closes));

        gate.SetResult();
        await guard.Completion;

        Assert.Equal((1, 1), (guard.CleanupRuns, closes));
        Assert.True(guard.OnClosing(() => closes++));                    // the real close goes through
        Assert.Equal(1, guard.CleanupRuns);
    }

    [Fact]
    public async Task CleanupThrows_StillCloses()
    {
        int closes = 0;
        var guard = new WindowCloseGuard(() => throw new InvalidOperationException("serial port broken"), TimeSpan.FromSeconds(5));
        Assert.False(guard.OnClosing(() => closes++));
        await guard.Completion;
        Assert.Equal(1, closes);
        Assert.True(guard.IsCleanedUp);
    }

    [Fact]
    public async Task CleanupFaultedTask_StillCloses()
    {
        int closes = 0;
        var guard = new WindowCloseGuard(() => Task.FromException(new IOException("gone")), TimeSpan.FromSeconds(5));
        guard.OnClosing(() => closes++);
        await guard.Completion;
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task CleanupHangs_TimeoutCloses()
    {
        int closes = 0;
        var guard = new WindowCloseGuard(() => new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(200));
        var timer = Stopwatch.StartNew();
        guard.OnClosing(() => closes++);
        await guard.Completion;
        Assert.Equal(1, closes);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
    }
}

/// <summary>The real MainWindow: title, Save button, and closing releases the connection before the window goes.</summary>
[Collection(WpfUiCollection.Name)]
public class ReleaseWindowUiTests
{
    private static void RunSta(Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var frame = new DispatcherFrame();
            _ = Run();
            Dispatcher.PushFrame(frame);

            async Task Run()
            {
                try { await body(); }
                catch (Exception ex) { failure = ex; }
                finally { frame.Continue = false; }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }

    [Fact]
    public void Title_SaveButton_CloseReleasesConnection()
    {
        RunSta(async () =>
        {
            await using var slave = new FakeModbusTcpSlave();
            var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString() };
            var window = new MainWindow { Left = -20000, Top = -20000, Width = 1100, ShowActivated = false, DataContext = vm };
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.Equal("Modbus Tester V1.0.0", window.Title);
            Assert.Equal("Save...", ((Button)window.FindName("SaveLogButton")).Content);

            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.Equal(ConnectionState.Connected, vm.ServiceState);

            window.Close();                                              // first close: cancelled while cleaning up
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!closed && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.True(closed);
            Assert.True(vm.IsShutDown);
            Assert.Equal(ConnectionState.Disconnected, vm.ServiceState);
        });
    }
}
