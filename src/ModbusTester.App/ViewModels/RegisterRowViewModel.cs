using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ModbusTester.Core.Models;
using ModbusTester.Core.Services;

namespace ModbusTester.App.ViewModels;

/// <summary>
/// One result row. Registers defined by the selected device profile are decoded from the document and their
/// Scale is fixed; all other registers (always, in Generic mode) keep the editable manual Scale.
/// </summary>
public sealed partial class RegisterRowViewModel(DecodedRegister register, Action<int, double> scaleChanged) : ObservableObject
{
    private DecodedRegister _register = register;

    public int Address => _register.Address;
    /// <summary>Wire address of the row (Address Base already applied); used to match a verified write.</summary>
    public ushort PduAddress => _register.PduAddress;
    public string Description => _register.Description;
    public string ChineseDescription => _register.ChineseDescription;
    public ushort Raw => _register.Raw;

    /// <summary>Raw column: the register value (16-bit, decimal as before) or both words as read, e.g. "1234 5678" (32-bit).</summary>
    public string RawText => _register.RegisterCount == 2 ? _register.RawWordsText : _register.Raw.ToString(CultureInfo.InvariantCulture);

    /// <summary>Registers the row covers (2 for a 32-bit value), starting at <see cref="PduAddress"/>.</summary>
    public int RegisterCount => _register.RegisterCount;

    /// <summary>Raw after sign conversion (e.g. -1234 for an Int16 temperature), before scaling.</summary>
    public long Decimal => _register.Integer;

    public string Hex => _register.Hex;
    public string Binary => _register.Binary;
    public string Unit => _register.Unit;
    public RegisterQuality Quality => _register.Quality;

    /// <summary>Engineering value; null for status registers.</summary>
    public double? Value => _register.Value;

    /// <summary>What the Value column shows: "25.34", "No error", "Unknown (5)", "523.12 (Out of range)".</summary>
    public string ValueText => _register.DisplayValue;

    /// <summary>False for profile-defined registers: the document decides their scale.</summary>
    public bool IsScaleEditable => !_register.IsProfileControlled;

    /// <summary>
    /// Scale as shown and edited in the grid: "0.01", "1" (no document scaling) or "-" (status register).
    /// Editing is ignored for profile-defined rows; text that is not a number is rejected (red cell, old value kept).
    /// </summary>
    public string ScaleText
    {
        get => _register.ScaleText;
        set
        {
            if (!IsScaleEditable)
                return;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) || !double.IsFinite(scale))
                throw new FormatException($"Scale '{value}' is not a number (e.g. 0.01).");
            Scale = scale;
        }
    }

    public double Scale
    {
        get => _register.Scale ?? 1;
        set
        {
            if (!IsScaleEditable || _register.Scale == value)
                return;
            // Same words, data type and 32-bit order – only the manual scale changes (decoding stays in the Core).
            _register = RegisterDecoder.Rescale(_register, value);
            scaleChanged(Address, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScaleText));
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(ValueText));
            OnPropertyChanged(nameof(Quality));
        }
    }
}
