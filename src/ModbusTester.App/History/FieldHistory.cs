using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ModbusTester.App.History;

/// <summary>
/// Input history of one text field: most recent first, no duplicates, at most <see cref="MaxEntries"/>.
/// The same class backs all eight fields; the UI side is the single <c>HistoryField</c> control template.
/// </summary>
public sealed partial class FieldHistory : ObservableObject
{
    public const int MaxEntries = 5;

    private readonly Action<string> _apply;
    private readonly Action _changed;

    /// <param name="key">JSON key, e.g. "tcpHost".</param>
    /// <param name="apply">Writes a chosen value into the field.</param>
    /// <param name="changed">Called after every change so the owner can persist.</param>
    public FieldHistory(string key, IEnumerable<string> initial, Action<string> apply, Action changed)
    {
        Key = key;
        _apply = apply;
        _changed = changed;
        foreach (string value in Normalize(initial))
            Items.Add(value);
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            if (!HasItems)
                IsOpen = false;   // last entry deleted: close the list (the ▼ hides via HasItems)
        };
    }

    public string Key { get; }
    public ObservableCollection<string> Items { get; } = [];

    /// <summary>The ▼ is shown only when there is something to pick.</summary>
    public bool HasItems => Items.Count > 0;

    [ObservableProperty] private bool _isOpen;

    /// <summary>Records a value that was just used successfully: moves an existing one to the top or inserts it.</summary>
    public void Record(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return;
        int index = IndexOf(value);
        if (index == 0 && Items[0] == value)
            return;
        if (index >= 0)
            Items.RemoveAt(index);   // keep the spelling that was used last
        Items.Insert(0, value);
        while (Items.Count > MaxEntries)
            Items.RemoveAt(Items.Count - 1);
        _changed();
    }

    /// <summary>Puts the value into the field and closes the list.</summary>
    [RelayCommand]
    private void Apply(string? value)
    {
        if (value is null)
            return;
        _apply(value);
        IsOpen = false;
    }

    /// <summary>Deletes one entry (the × in the list); the field's current text is not touched.</summary>
    [RelayCommand]
    private void Remove(string? value)
    {
        if (value is null || !Items.Remove(value))
            return;
        _changed();
    }

    private int IndexOf(string value)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (string.Equals(Items[i], value, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>Guards against a hand-edited file: trims, drops blanks and duplicates, caps at 5.</summary>
    internal static List<string> Normalize(IEnumerable<string?> values)
    {
        var result = new List<string>();
        foreach (string? raw in values)
        {
            string value = raw?.Trim() ?? "";
            if (value.Length == 0 || result.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(value);
            if (result.Count == MaxEntries)
                break;
        }
        return result;
    }
}
