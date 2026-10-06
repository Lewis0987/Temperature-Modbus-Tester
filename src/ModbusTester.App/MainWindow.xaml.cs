using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
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

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is MainViewModel vm)
            await vm.DisposeAsync();
    }
}
