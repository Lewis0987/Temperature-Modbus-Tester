namespace ModbusTester.App.Tests;

/// <summary>
/// Test classes that create WPF elements (MainWindow, TextBox, ...) on their own STA threads. WPF's XAML/BAML
/// caches are not safe for several threads loading the same XAML for the first time at once, which showed up as
/// sporadic "The given key 'AutoGenerateColumns' was not present in the dictionary" parse errors when xUnit ran
/// these classes in parallel. They run one after another; all other test classes stay parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfUiCollection
{
    public const string Name = "WPF UI (sequential)";
}
