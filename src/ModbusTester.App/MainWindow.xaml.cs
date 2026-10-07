using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ModbusTester.App.ViewModels;

namespace ModbusTester.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is MainViewModel vm)
            await vm.DisposeAsync();
    }
}
