using System.Windows;
using System.Windows.Controls;
using ModbusTester.App.History;

namespace ModbusTester.App.Controls;

/// <summary>
/// Wraps an input (normally a TextBox) and adds a light ▼ that opens the field's <see cref="FieldHistory"/>.
/// The ▼ is not shown at all while the history is empty. The wrapped TextBox keeps its own binding and
/// validation, so this control only adds the history UI. Look and behaviour live once in Themes/Generic.xaml.
/// </summary>
public class HistoryField : ContentControl
{
    static HistoryField()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(HistoryField), new FrameworkPropertyMetadata(typeof(HistoryField)));
        // Let Tab go straight into the wrapped TextBox.
        FocusableProperty.OverrideMetadata(typeof(HistoryField), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(
        nameof(History), typeof(FieldHistory), typeof(HistoryField));

    public FieldHistory? History
    {
        get => (FieldHistory?)GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }
}
