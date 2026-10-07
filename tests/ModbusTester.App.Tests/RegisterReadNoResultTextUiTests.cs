using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using ModbusTester.App.ViewModels;
using ModbusTester.Core.Protocol;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>
/// The Register Read area shows no PASS / FAIL / response time anymore; the Communication Log keeps everything.
/// Runs the real MainWindow on an STA thread with a Dispatcher so async Connect/Read behave as in the app.
/// </summary>
[Collection(WpfUiCollection.Name)]
public class RegisterReadNoResultTextUiTests
{
    private static readonly ushort[] Registers = [2534, 0, 4856, 0, 650, 0];

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

    /// <summary>Runs <paramref name="body"/> on an STA dispatcher thread with the real MainWindow.</summary>
    private static void RunOnWindow(MainViewModel vm, Func<Window, Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false, DataContext = vm };
            window.Show();
            var frame = new DispatcherFrame();
            Task task = body(window);
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            window.Close();
            failure = task.Exception?.InnerException;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 60 && !condition(); i++)
            await Task.Delay(50);
    }

    private static string[] RegisterReadTexts(Window w)
    {
        GroupBox group = All<GroupBox>(w).Single(g => (g.Header as string) == "Register Read");
        // ▼ excluded: a PASS read records Address/Quantity/Scale history, so their ▼ legitimately appears.
        return All<TextBlock>(group).Where(t => t.IsVisible).Select(t => t.Text)
            .Where(t => !string.IsNullOrEmpty(t) && t != "▼").ToArray();
    }

    [Fact]
    public void NoElementBindsToTheReadResult()
    {
        RunOnWindow(new MainViewModel(null, TestVm.TempStore()), w =>
        {
            Assert.DoesNotContain(All<TextBlock>(w), t =>
                BindingOperations.GetBindingExpression(t, TextBlock.TextProperty)?.ParentBinding.Path.Path is "LastResult");
            return Task.CompletedTask;
        });
    }

    // Test 1 + Test 2
    [Theory]
    [InlineData("pass", "Response Time:")]   // success: no "Result:" line, the log ends at Response Time
    [InlineData("timeout", "Result: TIMEOUT [Timeout] - Timeout after 150 ms (no response)")]
    [InlineData("crc", "Result: CRC ERROR [CrcError]")]
    [InlineData("exception", "Result: FAIL [ModbusException] - Modbus Exception 0x02: Illegal Data Address")]
    public void ReadResult_NotShownInRegisterRead_ButFullyInLog(string scenario, string expectedLog)
    {
        var vm = new MainViewModel(_ => new FakeSerialChannel
        {
            Responder = req => scenario switch
            {
                "timeout" => [],
                "crc" => [(TimeSpan.Zero, [.. RtuSlave(req)[..^1], 0x00])],
                _ => [(TimeSpan.Zero, RtuSlave(req))],
            },
        }, TestVm.TempStore())
        {
            IsRtu = true, SelectedComPort = "COM_TEST", RtuTimeoutText = "150",
            StartAddressText = scenario == "exception" ? "6" : "1", QuantityText = scenario == "exception" ? "2" : "6",
        };

        RunOnWindow(vm, async w =>
        {
            Button Btn(string text) => All<Button>(w).Single(b => (b.Content as string) == text);
            string[] before = RegisterReadTexts(w);

            Click(Btn("Connect"));
            await Until(() => vm.IsConnected);
            Assert.True(vm.IsConnected);
            Click(Btn("Read"));
            await Until(() => vm.LogEntries.Any(e => e.Message.StartsWith(expectedLog)));
            await Task.Delay(100);

            string[] after = RegisterReadTexts(w);
            Assert.Equal(before, after);   // nothing appeared next to "Apply to all rows"
            Assert.DoesNotContain(after, t => t.Contains("PASS") || t.Contains("FAIL") || t.Contains(" ms") || t.Contains("Timeout") || t.Contains("CRC"));
            Assert.Contains(vm.LogEntries, e => e.Message.StartsWith(expectedLog));
            if (scenario == "pass")
            {
                Assert.DoesNotContain(vm.LogEntries, e => e.Message.StartsWith("Result:"));
                Assert.Equal(6, vm.Rows.Count);                                     // table still filled
            }
            else
            {
                Assert.Contains(vm.LogEntries, e => e.Message.StartsWith("Response Time:"));   // failures keep it too
            }
        });
    }
}
