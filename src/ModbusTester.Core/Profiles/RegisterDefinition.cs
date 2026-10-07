using ModbusTester.Core.Services;

namespace ModbusTester.Core.Profiles;

public enum RegisterAccess
{
    Read,
    Write,
    ReadWrite,
}

/// <summary>Meaning of one value of a status / enum register.</summary>
public sealed record RegisterState(string Text, string ChineseText);

/// <summary>
/// What one holding register means, as written in a device document.
/// <see cref="RegisterNumber"/> is the document's register number (1-based, "Register 1"), NOT the PDU address:
/// Register N is PDU address N - 1. Lookups go through <see cref="DeviceProfile.Find"/> with the PDU address,
/// so the UI Address Base never has to be applied a second time.
/// </summary>
public sealed record RegisterDefinition
{
    public required int RegisterNumber { get; init; }
    public required string Description { get; init; }
    public string ChineseDescription { get; init; } = "";

    /// <summary>UInt16 / Int16 for numbers, Status for enum-like registers (no scaling).</summary>
    public required DataType DataType { get; init; }

    /// <summary>Engineering value = raw (after sign conversion) × Scale; null means no scaling (factor 1).</summary>
    public double? Scale { get; init; }

    public string Unit { get; init; } = "";

    /// <summary>Document range of the engineering value; null = not limited on that side.</summary>
    public double? Min { get; init; }
    public double? Max { get; init; }

    public RegisterAccess Access { get; init; } = RegisterAccess.Read;

    /// <summary>Document default (engineering value), e.g. 0 for the offsets; null if the document gives none.
    /// Kept for later write / reset features; not shown in the grid yet.</summary>
    public double? DefaultValue { get; init; }

    /// <summary>Document grouping, e.g. "Measurement" or "Offset".</summary>
    public string Group { get; init; } = "";

    public bool IsWritable => Access is RegisterAccess.Write or RegisterAccess.ReadWrite;

    /// <summary>For <see cref="DataType.Status"/>: raw value → meaning. Values not listed are reported as unknown.</summary>
    public IReadOnlyDictionary<ushort, RegisterState> States { get; init; } = new Dictionary<ushort, RegisterState>();

    public ushort PduAddress => (ushort)(RegisterNumber - 1);
}
