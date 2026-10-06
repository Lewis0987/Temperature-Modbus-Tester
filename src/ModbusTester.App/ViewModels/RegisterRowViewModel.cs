using CommunityToolkit.Mvvm.ComponentModel;
using ModbusTester.Core.Models;

namespace ModbusTester.App.ViewModels;

/// <summary>One result row; Scale is editable and Value follows it.</summary>
public sealed partial class RegisterRowViewModel(RegisterRow row, Action<int, double> scaleChanged) : ObservableObject
{
    private RegisterRow _row = row;

    public int Address => _row.Address;
    public ushort Raw => _row.Raw;
    public double Decimal => _row.Decoded;
    public string Hex => _row.Hex;
    public string Binary => _row.Binary;
    public double Value => _row.Value;

    public double Scale
    {
        get => _row.Scale;
        set
        {
            if (_row.Scale == value)
                return;
            _row = _row with { Scale = value };
            scaleChanged(Address, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Value));
        }
    }
}
