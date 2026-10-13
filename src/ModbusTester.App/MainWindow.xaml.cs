using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ModbusTester.App.ViewModels;

namespace ModbusTester.App;

public partial class MainWindow : Window
{
    /// <summary>Cleanup (disconnect, release TCP / serial port) is awaited before the window really closes.</summary>
    private readonly WindowCloseGuard _closeGuard;

    public MainWindow()
    {
        InitializeComponent();
        _closeGuard = new WindowCloseGuard(
            () => DataContext is MainViewModel vm ? vm.ShutdownAsync() : Task.CompletedTask,
            TimeSpan.FromSeconds(5));
        // Keep the newest log line in view.
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    /// <summary>A Scale defined by the device profile comes from the document and cannot be edited.</summary>
    private void RegistersGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Column == ScaleColumn && e.Row.Item is RegisterRowViewModel { IsScaleEditable: false })
            e.Cancel = true;
    }

    /// <summary>
    /// First close: cancelled until the cleanup has finished (an async void handler would let the window close before
    /// the serial port is released); then the window closes for real. Repeated close requests do not clean up twice.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeGuard.OnClosing(() => Dispatcher.BeginInvoke(Close)))
            e.Cancel = true;
        base.OnClosing(e);
    }
}
