using System.Globalization;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Services;

namespace ModbusTester.Core.Models;

/// <summary>Plausibility of a decoded value. Never a communication failure: the read itself succeeded.</summary>
public enum RegisterQuality
{
    Ok,
    /// <summary>Engineering value outside the document range.</summary>
    OutOfRange,
    /// <summary>Status register value that the document does not define.</summary>
    UnknownState,
    /// <summary>Float32 NaN / ±Infinity: a valid bit pattern but not a usable engineering value (no range check).</summary>
    InvalidValue,
    /// <summary>Not all registers of the value were read (e.g. only the first word of a 32-bit value): no value.</summary>
    Incomplete,
}

/// <summary>One register after decoding, either by a profile definition or generically (raw × manual scale).</summary>
public sealed record DecodedRegister
{
    /// <summary>User-facing address (as entered with the current Address Base).</summary>
    public required int Address { get; init; }
    public required ushort PduAddress { get; init; }

    /// <summary>First (or only) register word as read. For 32-bit values see <see cref="Words"/> / <see cref="Bits32"/>.</summary>
    public required ushort Raw { get; init; }

    /// <summary>Second register word of a 32-bit value (wire order); null for 16-bit values and incomplete 32-bit values.</summary>
    public ushort? SecondWord { get; init; }

    /// <summary>All register words of this value in wire order: 1 for 16-bit (or an incomplete 32-bit value), 2 for 32-bit.</summary>
    public IReadOnlyList<ushort> Words => SecondWord is { } second ? [Raw, second] : [Raw];

    /// <summary>The 32-bit pattern after the register order (A = most significant byte); null for 16-bit values.</summary>
    public uint? Bits32 { get; init; }

    /// <summary>
    /// Data type and 32-bit order the value was decoded with (generic rows: the user's choice; profile rows: see
    /// <see cref="Definition"/>). Lets a generic row be re-scaled with exactly the same decoding.
    /// </summary>
    public DataType DataType { get; init; } = DataType.UInt16;
    public Register32Order Order32 { get; init; } = Register32Order.ABCD;

    /// <summary>Number of registers this value occupies on the wire.</summary>
    public int RegisterCount => Words.Count;

    /// <summary>Null when the profile does not define this register (generic decoding with a manual scale).</summary>
    public RegisterDefinition? Definition { get; init; }

    /// <summary>Raw after sign conversion (Int16 → negative values), before scaling.</summary>
    public required long Integer { get; init; }

    /// <summary>
    /// Scale as defined: the manual scale (generic) or the document scaling. Null when the document says "-"
    /// (status registers, and values without scaling such as CO2, which are used as-is).
    /// </summary>
    public double? Scale { get; init; }

    /// <summary>Engineering value; null for status registers.</summary>
    public double? Value { get; init; }

    /// <summary>Value as shown: "25.34", "No error", "Unknown (5)".</summary>
    public required string ValueText { get; init; }

    public RegisterQuality Quality { get; init; } = RegisterQuality.Ok;

    public bool IsProfileControlled => Definition is not null;
    public string Description => Definition?.Description ?? "";
    public string ChineseDescription => Definition?.ChineseDescription ?? "";
    public string Unit => Definition?.Unit ?? "";
    public string Hex => Bits32 is { } bits ? RegisterFormat.Hex32(bits) : RegisterFormat.Hex(Raw);
    public string Binary => Bits32 is { } bits ? RegisterFormat.Binary32(bits) : RegisterFormat.Binary(Raw);

    /// <summary>The raw words as read, e.g. "1234 5678" (wire order, before the 32-bit register order is applied).</summary>
    public string RawWordsText => string.Join(" ", Words.Select(RegisterFormat.Hex));

    /// <summary>"0.01", or "-" when the document defines no scaling (status registers, CO2).</summary>
    public string ScaleText => Scale is { } scale ? RegisterFormat.Number(scale) : "-";

    /// <summary>Value plus a plausibility note, e.g. "523.12 (Out of range)".</summary>
    public string DisplayValue => Quality == RegisterQuality.OutOfRange ? $"{ValueText} (Out of range)" : ValueText;
}

public static class RegisterFormat
{
    public static string Hex(ushort raw) => raw.ToString("X4");

    public static string Binary(ushort raw) =>
        Convert.ToString(raw, 2).PadLeft(16, '0').Insert(12, " ").Insert(8, " ").Insert(4, " ");

    public static string Hex32(uint bits) => bits.ToString("X8");

    public static string Binary32(uint bits) => $"{Binary((ushort)(bits >> 16))} {Binary((ushort)bits)}";

    public static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
}
