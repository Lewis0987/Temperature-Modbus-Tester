using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ModbusTester.App.Controls;
using ModbusTester.App.History;
using ModbusTester.App.ViewModels;
using ModbusTester.Core.Models;
using ModbusTester.Core.Protocol;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>List rules shared by every field (<see cref="FieldHistory"/>).</summary>
public class FieldHistoryTests
{
    private static FieldHistory New(Action<string>? apply = null, Action? changed = null, params string[] initial) =>
        new("test", initial, apply ?? (_ => { }), changed ?? (() => { }));

    [Fact]
    public void Empty_HasNoItems()
    {
        FieldHistory history = New();
        Assert.False(history.HasItems);
    }

    // 5 + 6
    [Fact]
    public void Record_NoDuplicates_ReusedValueMovesToTop()
    {
        FieldHistory history = New();
        history.Record("3000");
        history.Record("2000");
        history.Record("1000");

        history.Record("2000");

        Assert.Equal(["2000", "1000", "3000"], history.Items);
    }

    // 4 + 7
    [Fact]
    public void Record_KeepsFive_SixthEvictsOldest()
    {
        FieldHistory history = New();
        foreach (string v in new[] { "E", "D", "C", "B", "A" })
            history.Record(v);
        Assert.Equal(FieldHistory.MaxEntries, history.Items.Count);

        history.Record("F");

        Assert.Equal(["F", "A", "B", "C", "D"], history.Items);
    }

    [Fact]
    public void Record_TrimsAndIgnoresBlank_CaseInsensitiveDuplicate()
    {
        FieldHistory history = New();
        history.Record(" temperature.local ");
        history.Record("TEMPERATURE.local");
        history.Record("   ");
        Assert.Equal(["TEMPERATURE.local"], history.Items);
    }

    // 8 + 9
    [Fact]
    public void Remove_DeletesOne_DoesNotApply()
    {
        string? applied = null;
        int saves = 0;
        FieldHistory history = New(v => applied = v, () => saves++, "1", "2", "5");

        history.RemoveCommand.Execute("2");

        Assert.Equal(["1", "5"], history.Items);
        Assert.Null(applied);
        Assert.Equal(1, saves);
    }

    // Test 5: deleting one by one down to zero closes the list
    [Fact]
    public void RemovingEveryEntry_EmptiesAndCloses_DoesNotApply()
    {
        string? applied = null;
        FieldHistory history = New(v => applied = v, null, "1000", "2000");
        history.IsOpen = true;

        history.RemoveCommand.Execute("1000");
        Assert.True(history.IsOpen);              // still entries left: stays open
        history.RemoveCommand.Execute("2000");

        Assert.Empty(history.Items);
        Assert.False(history.HasItems);
        Assert.False(history.IsOpen);
        Assert.Null(applied);
    }

    [Fact]
    public void ClearAll_NoLongerExists()
    {
        Assert.Null(typeof(FieldHistory).GetProperty("ClearCommand"));
    }

    [Fact]
    public void Apply_WritesValue_AndCloses()
    {
        string? applied = null;
        FieldHistory history = New(v => applied = v, null, "0.01");
        history.IsOpen = true;

        history.ApplyCommand.Execute("0.01");

        Assert.Equal("0.01", applied);
        Assert.False(history.IsOpen);
    }

    [Fact]
    public void HasItems_RaisesPropertyChanged()
    {
        FieldHistory history = New();
        var changed = new List<string?>();
        history.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        history.Record("1");
        history.RemoveCommand.Execute("1");

        Assert.Equal(2, changed.Count(p => p == nameof(FieldHistory.HasItems)));
    }
}

/// <summary>JSON file, corruption handling and migration of the old TCP history.</summary>
public class InputHistoryStoreTests
{
    [Fact]
    public void SaveLoad_RoundTrip_Version2Fields()
    {
        string path = TestVm.TempHistoryPath();
        var store = new InputHistoryStore(path);
        store.Save(new Dictionary<string, List<string>>
        {
            [HistoryKeys.TcpHost] = ["192.168.137.189", "127.0.0.1"],
            [HistoryKeys.DefaultScale] = ["1", "0.01"],
        });

        string json = File.ReadAllText(path);
        Assert.Contains("\"version\": 2", json);
        Assert.Contains("\"tcpHost\"", json);
        Assert.Contains("\"0.01\"", json);

        Dictionary<string, List<string>> loaded = new InputHistoryStore(path).Load();
        Assert.Equal(["192.168.137.189", "127.0.0.1"], loaded[HistoryKeys.TcpHost]);
        Assert.Equal(["1", "0.01"], loaded[HistoryKeys.DefaultScale]);
    }

    // 20
    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void CorruptFile_LoadsEmpty_WithErrorMessage(string content)
    {
        string path = TestVm.TempHistoryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        var store = new InputHistoryStore(path);

        Assert.Empty(store.Load());
        Assert.Contains(store.Messages, m => m.IsError && m.Text.Contains("could not be loaded"));
    }

    [Fact]
    public void LegacyTcpHistory_IsMigratedOnce_HostsOnly_OldFileKept()
    {
        string path = TestVm.TempHistoryPath();
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string legacyPath = Path.Combine(dir, InputHistoryStore.LegacyFileName);
        File.WriteAllText(legacyPath, """
            {"version":1,"entries":[
              {"host":"192.168.137.189","port":5020},{"host":"127.0.0.1","port":5020},
              {"host":"192.168.137.189","port":502},{"host":"192.168.128.110","port":502}]}
            """);
        var store = new InputHistoryStore(path);

        Dictionary<string, List<string>> loaded = store.Load();

        Assert.Equal(["192.168.137.189", "127.0.0.1", "192.168.128.110"], loaded[HistoryKeys.TcpHost]);
        Assert.Single(loaded);                                                     // no port anywhere
        Assert.Contains(store.Messages, m => !m.IsError && m.Text.Contains("imported 3"));
        Assert.False(File.Exists(path));                                           // loading never writes
        Assert.True(File.Exists(legacyPath));                                      // old file left alone

        store.Save(loaded);                                                        // first history change
        Assert.DoesNotContain("5020", File.ReadAllText(path));
        File.WriteAllText(legacyPath, """{"version":1,"entries":[{"host":"10.0.0.9","port":1}]}""");
        Assert.DoesNotContain("10.0.0.9", new InputHistoryStore(path).Load()[HistoryKeys.TcpHost]);   // not re-migrated
    }

    [Fact]
    public async Task OpeningTheApp_NeverWritesHistoryFiles()
    {
        string path = TestVm.TempHistoryPath();
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string legacyPath = Path.Combine(dir, InputHistoryStore.LegacyFileName);
        File.WriteAllText(legacyPath, """{"version":1,"entries":[{"host":"127.0.0.1","port":5020}]}""");

        await using (var vm = new MainViewModel(null, new InputHistoryStore(path)))
            Assert.Equal(["127.0.0.1"], vm.TcpHostHistory.Items);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CorruptLegacyFile_DoesNotCrash()
    {
        string path = TestVm.TempHistoryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, InputHistoryStore.LegacyFileName), "garbage");
        var store = new InputHistoryStore(path);

        Assert.Empty(store.Load());
        Assert.Contains(store.Messages, m => m.IsError);
    }
}

/// <summary>When each field's history is written, and the "no connection type yet" start state.</summary>
public class InputHistoryViewModelTests
{
    private static readonly ushort[] Registers = [2534, 0, 4856, 0, 650, 0];

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static int PortOf(TcpListener l) => ((IPEndPoint)l.LocalEndpoint).Port;

    private static int ClosedPort()
    {
        TcpListener probe = Listen();
        int port = PortOf(probe);
        probe.Stop();
        return port;
    }

    private static byte[] RtuSlave(byte[] request)
    {
        int address = request[2] << 8 | request[3];
        int quantity = request[4] << 8 | request[5];
        if (address + quantity > Registers.Length)
            return Crc16.Append([request[0], 0x83, 0x02]);
        var body = new List<byte> { request[0], 0x03, (byte)(quantity * 2) };
        foreach (ushort value in Registers.Skip(address).Take(quantity))
            body.AddRange([(byte)(value >> 8), (byte)value]);
        return Crc16.Append(body.ToArray());
    }

    private static IEnumerable<FieldHistory> All(MainViewModel vm) =>
    [
        vm.TcpHostHistory, vm.TcpUnitIdHistory, vm.TcpTimeoutHistory, vm.RtuSlaveIdHistory,
        vm.RtuTimeoutHistory, vm.AddressHistory, vm.QuantityHistory, vm.DefaultScaleHistory,
    ];

    // 21 + 22
    [Fact]
    public async Task Startup_NoConnectionTypeSelected_ConnectDisabled_FieldDefaultsKept()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore());

        Assert.Null(vm.ConnectionType);
        Assert.False(vm.IsTcp);
        Assert.False(vm.IsRtu);
        Assert.False(vm.HasConnectionType);
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.False(vm.HasErrors);
        Assert.Equal(("5020", "1", "1000", "1", "1000", "1", "6", "1"),
            (vm.PortText, vm.UnitIdText, vm.TimeoutText, vm.SlaveIdText, vm.RtuTimeoutText, vm.StartAddressText, vm.QuantityText, vm.DefaultScaleText));

        await vm.ConnectCommand.ExecuteAsync(null);              // gate: nothing to connect
        Assert.DoesNotContain(vm.LogEntries, e => e.Message.EndsWith(" Connect"));
    }

    [Fact]
    public async Task Startup_AllHistoriesEmpty()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore());
        Assert.All(All(vm), h => Assert.False(h.HasItems));
    }

    [Fact]
    public async Task TypingDoesNotRecord()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true };
        foreach (string partial in new[] { "192", "192.168", "192.168.137", "192.168.137.189" })
            vm.HostText = partial;
        Assert.Empty(vm.TcpHostHistory.Items);
    }

    // 13 + 11 + 12
    [Fact]
    public async Task TcpConnectSuccess_RecordsHostUnitIdTimeout_IpWithoutPort_OnePerIp()
    {
        TcpListener a = Listen(), b = Listen();
        try
        {
            await using var vm = new MainViewModel(null, TestVm.TempStore())
            {
                IsTcp = true, HostText = "127.0.0.1", PortText = PortOf(a).ToString(), UnitIdText = "2", TimeoutText = "2000",
            };
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.True(vm.IsConnected);
            await vm.DisconnectCommand.ExecuteAsync(null);
            vm.PortText = PortOf(b).ToString();                  // same IP, other port
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.True(vm.IsConnected);

            Assert.Equal(["127.0.0.1"], vm.TcpHostHistory.Items);
            Assert.DoesNotContain(vm.TcpHostHistory.Items, v => v.Contains(':'));
            Assert.Equal(["2"], vm.TcpUnitIdHistory.Items);
            Assert.Equal(["2000"], vm.TcpTimeoutHistory.Items);
            Assert.Empty(vm.RtuSlaveIdHistory.Items);
            Assert.Empty(vm.AddressHistory.Items);              // nothing read yet
        }
        finally
        {
            a.Stop();
            b.Stop();
        }
    }

    // 14
    [Fact]
    public async Task TcpConnectFail_OrValidationError_RecordsNothing()
    {
        await using var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, HostText = "127.0.0.1", PortText = ClosedPort().ToString(), TimeoutText = "5000" };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Error, vm.ConnectionState);
        vm.PortText = "";
        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.All(All(vm), h => Assert.Empty(h.Items));
    }

    // 15
    [Fact]
    public async Task RtuConnectSuccess_RecordsSlaveIdAndTimeout_Only()
    {
        await using var vm = new MainViewModel(_ => new FakeSerialChannel(), TestVm.TempStore())
        {
            IsRtu = true, SelectedComPort = "COM_TEST", SlaveIdText = "5", RtuTimeoutText = "3000",
        };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);

        Assert.Equal(["5"], vm.RtuSlaveIdHistory.Items);
        Assert.Equal(["3000"], vm.RtuTimeoutHistory.Items);
        Assert.Empty(vm.TcpHostHistory.Items);
        Assert.Empty(vm.TcpTimeoutHistory.Items);
    }

    // 16
    [Fact]
    public async Task RtuConnectFail_RecordsNothing()
    {
        await using var vm = new MainViewModel(_ => new FakeSerialChannel { IsPresent = false }, TestVm.TempStore())
        {
            IsRtu = true, SelectedComPort = "COM_TEST",
        };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Error, vm.ConnectionState);
        Assert.All(All(vm), h => Assert.Empty(h.Items));
    }

    // 17
    [Fact]
    public async Task ReadPass_RecordsAddressQuantityScale()
    {
        await using var vm = new MainViewModel(_ => new FakeSerialChannel { Responder = req => [(TimeSpan.Zero, RtuSlave(req))] }, TestVm.TempStore())
        {
            IsRtu = true, SelectedComPort = "COM_TEST", StartAddressText = "3", QuantityText = "2", DefaultScaleText = "0.01",
        };
        await vm.ConnectCommand.ExecuteAsync(null);
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.True(vm.LastResultPassed);

        Assert.Equal(["3"], vm.AddressHistory.Items);
        Assert.Equal(["2"], vm.QuantityHistory.Items);
        Assert.Equal(["0.01"], vm.DefaultScaleHistory.Items);
    }

    // 18
    [Theory]
    [InlineData("timeout")]
    [InlineData("crc")]
    [InlineData("exception")]
    public async Task ReadFail_RecordsNothing(string failure)
    {
        await using var vm = new MainViewModel(_ => new FakeSerialChannel
        {
            Responder = req => failure switch
            {
                "timeout" => [],
                "crc" => [(TimeSpan.Zero, [.. RtuSlave(req)[..^1], 0x00])],
                _ => [(TimeSpan.Zero, RtuSlave(req))],
            },
        }, TestVm.TempStore())
        {
            IsRtu = true, SelectedComPort = "COM_TEST", RtuTimeoutText = "150",
            StartAddressText = failure == "exception" ? "6" : "1", QuantityText = failure == "exception" ? "2" : "6",
        };
        await vm.ConnectCommand.ExecuteAsync(null);
        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal("FAIL", vm.LastResult);
        Assert.Empty(vm.AddressHistory.Items);
        Assert.Empty(vm.QuantityHistory.Items);
        Assert.Empty(vm.DefaultScaleHistory.Items);
    }

    // × never changes the field, down to the last entry
    [Fact]
    public async Task RemovingEntries_DoesNotChangeFieldValue()
    {
        string path = TestVm.TempHistoryPath();
        new InputHistoryStore(path).Save(new Dictionary<string, List<string>> { [HistoryKeys.TcpTimeout] = ["1000", "2000", "3000"] });
        await using var vm = new MainViewModel(null, new InputHistoryStore(path)) { IsTcp = true, TimeoutText = "4000" };

        vm.TcpTimeoutHistory.RemoveCommand.Execute("2000");
        Assert.Equal(["1000", "3000"], vm.TcpTimeoutHistory.Items);
        Assert.Equal("4000", vm.TimeoutText);

        vm.TcpTimeoutHistory.RemoveCommand.Execute("1000");
        vm.TcpTimeoutHistory.RemoveCommand.Execute("3000");
        Assert.Empty(vm.TcpTimeoutHistory.Items);
        Assert.Equal("4000", vm.TimeoutText);
    }

    // Test 6: what was not deleted survives a restart
    [Fact]
    public async Task RemainingEntries_SurviveRestart()
    {
        string path = TestVm.TempHistoryPath();
        new InputHistoryStore(path).Save(new Dictionary<string, List<string>>
        {
            [HistoryKeys.Quantity] = ["6", "1", "10"], [HistoryKeys.Address] = ["1"],
        });
        await using (var first = new MainViewModel(null, new InputHistoryStore(path)))
        {
            first.QuantityHistory.RemoveCommand.Execute("1");
            first.AddressHistory.RemoveCommand.Execute("1");
        }

        await using var reopened = new MainViewModel(null, new InputHistoryStore(path));

        Assert.Equal(["6", "10"], reopened.QuantityHistory.Items);
        Assert.Empty(reopened.AddressHistory.Items);
    }

    [Fact]
    public async Task Apply_WritesIntoTheMatchingField()
    {
        string path = TestVm.TempHistoryPath();
        new InputHistoryStore(path).Save(new Dictionary<string, List<string>>
        {
            [HistoryKeys.TcpHost] = ["192.168.128.110"], [HistoryKeys.Quantity] = ["10"], [HistoryKeys.RtuSlaveId] = ["7"],
        });
        await using var vm = new MainViewModel(null, new InputHistoryStore(path)) { IsTcp = true, PortText = "5020" };

        vm.TcpHostHistory.ApplyCommand.Execute("192.168.128.110");
        vm.QuantityHistory.ApplyCommand.Execute("10");
        vm.RtuSlaveIdHistory.ApplyCommand.Execute("7");

        Assert.Equal(("192.168.128.110", "5020", "10", "7"), (vm.HostText, vm.PortText, vm.QuantityText, vm.SlaveIdText));
    }

    // 19
    [Fact]
    public async Task Histories_SurviveRestart()
    {
        string path = TestVm.TempHistoryPath();
        TcpListener listener = Listen();
        try
        {
            await using (var first = new MainViewModel(null, new InputHistoryStore(path)) { IsTcp = true, HostText = "127.0.0.1", PortText = PortOf(listener).ToString() })
                await first.ConnectCommand.ExecuteAsync(null);

            await using var reopened = new MainViewModel(null, new InputHistoryStore(path));

            Assert.Equal(["127.0.0.1"], reopened.TcpHostHistory.Items);
            Assert.Equal(["1"], reopened.TcpUnitIdHistory.Items);
            Assert.Equal(["1000"], reopened.TcpTimeoutHistory.Items);
        }
        finally
        {
            listener.Stop();
        }
    }

    // 20 through the ViewModel
    [Fact]
    public async Task CorruptHistoryFile_AppStarts_AndLogsError()
    {
        string path = TestVm.TempHistoryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ broken");

        await using var vm = new MainViewModel(null, new InputHistoryStore(path));

        Assert.All(All(vm), h => Assert.Empty(h.Items));
        Assert.Contains(vm.LogEntries, e => e.Kind == LogKind.Error && e.Message.Contains("could not be loaded"));
    }

    // 25
    [Fact]
    public async Task SwitchingType_ClearsConnectedStatusAndPass_KeepsHistory()
    {
        await using var vm = new MainViewModel(_ => new FakeSerialChannel { Responder = req => [(TimeSpan.Zero, RtuSlave(req))] }, TestVm.TempStore())
        {
            IsRtu = true, SelectedComPort = "COM_TEST",
        };
        await vm.ConnectCommand.ExecuteAsync(null);
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        Assert.StartsWith("PASS", vm.LastResult);

        vm.IsTcp = true;

        Assert.Equal(ConnectionState.Disconnected, vm.ConnectionState);
        Assert.Equal("", vm.LastResult);
        Assert.False(vm.LastResultPassed);
        Assert.Equal(["1"], vm.RtuSlaveIdHistory.Items);
        Assert.Equal(["1"], vm.AddressHistory.Items);
    }
}

/// <summary>Through the real MainWindow: ▼ visibility, popup clicks, and which areas are enabled.</summary>
public class InputHistoryUiTests
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

    private static void Click(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();

    private static void Pump() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

    private static void RunWithWindow(string historyPath, Action<Window, MainViewModel> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false };
                var vm = new MainViewModel(null, new InputHistoryStore(historyPath));
                window.DataContext = vm;
                window.Show();
                Pump();
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

    private static HistoryField FieldOf(Window w, FieldHistory history) => All<HistoryField>(w).Single(f => ReferenceEquals(f.History, history));

    private static ToggleButton ArrowOf(HistoryField field) => (ToggleButton)field.Template.FindName("PART_Arrow", field);

    // 1-3 + 21-24
    [Fact]
    public void Arrow_HiddenWhenEmpty_VisibleWithHistory_HiddenAfterClear_AndAreasFollowConnectionType()
    {
        RunWithWindow(TestVm.TempHistoryPath(), (w, vm) =>
        {
            GroupBox Group(string header) => All<GroupBox>(w).Single(g => (g.Header as string) == header);
            RadioButton Radio(string text) => All<RadioButton>(w).Single(r => (r.Content as string) == text);
            Button Connect() => All<Button>(w).Single(b => (b.Content as string) == "Connect");

            // Start: nothing selected, everything off
            Assert.False(Radio("Modbus TCP").IsChecked);
            Assert.False(Radio("Modbus RTU").IsChecked);
            Assert.False(Group("Modbus TCP").IsEnabled);
            Assert.False(Group("Modbus RTU").IsEnabled);
            Assert.False(Group("Register Read").IsEnabled);
            Assert.False(Connect().IsEnabled);

            // 8 fields wrapped, every ▼ collapsed (not just disabled) while empty
            HistoryField[] fields = All<HistoryField>(w).ToArray();
            Assert.Equal(8, fields.Length);
            Assert.All(fields, f => Assert.Equal(Visibility.Collapsed, ArrowOf(f).Visibility));

            Radio("Modbus TCP").IsChecked = true;
            Pump();
            Assert.True(vm.IsTcp);
            Assert.True(Group("Modbus TCP").IsEnabled);
            Assert.False(Group("Modbus RTU").IsEnabled);
            Assert.True(Group("Register Read").IsEnabled);
            Assert.True(Connect().IsEnabled);

            Radio("Modbus RTU").IsChecked = true;
            Pump();
            Assert.True(vm.IsRtu);
            Assert.False(vm.IsTcp);
            Assert.False(Group("Modbus TCP").IsEnabled);
            Assert.True(Group("Modbus RTU").IsEnabled);

            vm.QuantityHistory.Record("6");
            Pump();
            Assert.Equal(Visibility.Visible, ArrowOf(FieldOf(w, vm.QuantityHistory)).Visibility);
            Assert.Equal(Visibility.Collapsed, ArrowOf(FieldOf(w, vm.AddressHistory)).Visibility);

            vm.QuantityHistory.RemoveCommand.Execute("6");
            Pump();
            Assert.Equal(Visibility.Collapsed, ArrowOf(FieldOf(w, vm.QuantityHistory)).Visibility);
        });
    }

    // 8, 9, 11, 13 in the real popup + light ▼ style
    [Fact]
    public void Popup_XDeletesWithoutApplying_RowApplies_IpRowsHaveNoPort()
    {
        string path = TestVm.TempHistoryPath();
        new InputHistoryStore(path).Save(new Dictionary<string, List<string>>
        {
            [HistoryKeys.TcpHost] = ["192.168.137.189", "127.0.0.1", "192.168.128.110"],
        });
        RunWithWindow(path, (w, vm) =>
        {
            vm.IsTcp = true;
            vm.HostText = "10.9.9.9";
            Pump();
            HistoryField field = FieldOf(w, vm.TcpHostHistory);
            ToggleButton arrow = ArrowOf(field);
            Assert.Equal(Visibility.Visible, arrow.Visibility);
            var arrowBorder = (Border)VisualTreeHelper.GetChild(arrow, 0);
            Assert.Equal(new Thickness(0), arrowBorder.BorderThickness);
            Assert.Equal(Colors.Transparent, ((SolidColorBrush)arrowBorder.Background).Color);

            vm.TcpHostHistory.IsOpen = true;
            Pump();
            var popup = (Popup)field.Template.FindName("PART_Popup", field);
            Assert.True(popup.IsOpen);
            Button[] buttons = All<Button>(popup.Child).ToArray();
            Button[] rows = buttons.Where(b => b.Content is string s && s != "×").ToArray();
            Assert.Equal(["192.168.137.189", "127.0.0.1", "192.168.128.110"], rows.Select(b => (string)b.Content));
            Assert.DoesNotContain(rows, b => ((string)b.Content).Contains(':'));
            // Test 3: only value rows and ×; no "清除全部紀錄" (or any other element) in the list
            Assert.Equal(6, buttons.Length);
            Assert.DoesNotContain(All<TextBlock>(popup.Child), t => t.Text.Contains("清除"));

            Click(buttons.Where(b => (b.Content as string) == "×").ElementAt(1));   // × on 127.0.0.1
            Pump();
            Assert.Equal(["192.168.137.189", "192.168.128.110"], vm.TcpHostHistory.Items);
            Assert.Equal("10.9.9.9", vm.HostText);                                  // × did not apply

            Click(All<Button>(popup.Child).Single(b => (b.Content as string) == "192.168.128.110"));
            Pump();
            Assert.Equal("192.168.128.110", vm.HostText);                           // row applied
            Assert.False(popup.IsOpen);

            // Test 5: delete one by one with × until empty → popup closes, ▼ hides, field kept
            vm.TcpHostHistory.IsOpen = true;
            Pump();
            Click(All<Button>(popup.Child).First(b => (b.Content as string) == "×"));
            Pump();
            Assert.True(popup.IsOpen);                                              // one entry left
            Assert.Equal(Visibility.Visible, arrow.Visibility);
            Click(All<Button>(popup.Child).Single(b => (b.Content as string) == "×"));
            Pump();
            Assert.Empty(vm.TcpHostHistory.Items);
            Assert.False(popup.IsOpen);
            Assert.Equal(Visibility.Collapsed, arrow.Visibility);
            Assert.Equal("192.168.128.110", vm.HostText);
        });
    }
}
