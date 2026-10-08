using ModbusTester.Core.Profiles;

namespace ModbusTester.Core.Models;

/// <summary>
/// Device software version split over two registers, as defined by the device document
/// (Siemens A6V12045847: Major 1 byte, Minor 1 byte, Build 2 bytes; example [2.01.33] = 0x02010021):
/// the <see cref="RegisterValueEncoding.VersionMajorMinor"/> word holds Major (high byte) and Minor (low byte),
/// the <see cref="RegisterValueEncoding.VersionBuild"/> word holds Build.
/// Shown as "Major.Minor.Build" with a two-digit Minor, like the document example "2.01.33".
/// </summary>
public readonly record struct SoftwareVersion(byte Major, byte Minor, ushort Build)
{
    public static byte MajorOf(ushort majorMinor) => (byte)(majorMinor >> 8);
    public static byte MinorOf(ushort majorMinor) => (byte)(majorMinor & 0xFF);

    /// <summary>"2.01" for 0x0201.</summary>
    public static string MajorMinorText(ushort majorMinor) => $"{MajorOf(majorMinor)}.{MinorOf(majorMinor):D2}";

    public static SoftwareVersion FromRegisters(ushort majorMinor, ushort build) => new(MajorOf(majorMinor), MinorOf(majorMinor), build);

    /// <summary>
    /// The version shown by a set of decoded rows: needs one profile row of each version encoding.
    /// Null when either word was not read (or the rows were decoded generically).
    /// </summary>
    public static SoftwareVersion? Find(IEnumerable<DecodedRegister> registers)
    {
        DecodedRegister[] rows = [.. registers];
        DecodedRegister? majorMinor = rows.FirstOrDefault(r => r.Definition?.ValueEncoding == RegisterValueEncoding.VersionMajorMinor);
        DecodedRegister? build = rows.FirstOrDefault(r => r.Definition?.ValueEncoding == RegisterValueEncoding.VersionBuild);
        return majorMinor is not null && build is not null ? FromRegisters(majorMinor.Raw, build.Raw) : null;
    }

    /// <summary>"2.01.33".</summary>
    public override string ToString() => $"{Major}.{Minor:D2}.{Build}";
}
