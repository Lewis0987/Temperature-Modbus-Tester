using ModbusTester.Core.Models;
using ModbusTester.Core.Services;

namespace ModbusTester.Core.Profiles;

/// <summary>
/// Siemens A6V12258321 – Room humidity and air quality sensors QFA2050/MO, QFA2050D/MO, QPA2052/MO,
/// section 4.3 "Modbus registers" (holding registers, register numbers start at 1).
/// Reconciled with the Basic Documentation A6V12045847 (2020-07-23): "Supports only the holding register" (so the
/// profile reads with FC03 only) and "The register number starts at 1" (Register N = PDU address N - 1).
/// Defined so far: measurement registers 1–6 (Phase 3A), offset registers 223–225 (Phase 3B) and configuration
/// registers 401, 764–766 (Phase 3C) and version registers 1286–1287 (Phase 3D). Write encoding/validation (Phase 4B) uses
/// the same metadata via RegisterWriteEncoder / ProfileWriteService; single-register profile writes use FC06 only.
/// Not every sensor variant has every point; e.g. a model without CO2 may not provide registers 5/6/225.
/// </summary>
public static class A6V12258321Profile
{
    public const string Name = "A6V12258321 (QFA/QPA)";
    public const string Measurement = "Measurement";
    public const string Offset = "Offset";
    public const string Configuration = "Configuration";
    public const string Version = "Version";

    /// <summary>Register 401 System unit: selects the unit and range of the temperature registers 1 and 223.</summary>
    public const int SystemUnitRegister = 401;

    /// <summary>
    /// Why profile writes to 764–766 are encoded/validated but not sent yet. A6V12045847 confirms they are writable via
    /// a master only with DIP2 position 7 = OFF ("configurable via master", the default); with DIP2-7 = ON (DIP switch
    /// configuration) they are not writable via a master. The tester cannot see the DIP switches, and when new
    /// communication parameters take effect / whether a reconnect is needed is still not confirmed.
    /// (Factory reset of address, baud rate and format is a DIP operation – DIP1 1…8 = 255 with DIP2-7 = ON – not a
    /// register write; the tester has no such function.)
    /// </summary>
    public const string CommunicationWriteHold =
        "Communication parameter writes are on hold: effect timing / reconnect behaviour not confirmed, and the sensor's DIP2-7 setting (master vs. DIP switch configuration) is unknown";

    private static readonly IReadOnlyDictionary<ushort, RegisterState> Reliability = new Dictionary<ushort, RegisterState>
    {
        [0] = new("No error", "正常"),
        [1] = new("Bad reliability, unavailable", "資料不可靠／無法使用"),
    };

    public static DeviceProfile Profile { get; } = new(Name, "Siemens QFA2050/MO, QFA2050D/MO, QPA2052/MO – registers 1–6, 223–225, 401, 764–766, 1286–1287",
    [
        // ---- Measurement (R) ----
        new RegisterDefinition
        {
            // Phase 3E – PHYSICALLY VERIFIED (2026-10-08, real sensor, 401 0 → 1 → 0): the sensor itself outputs
            // °C × 100 with 401 = 0 (raw 2793 → 27.93 °C) and °F × 100 with 401 = 1 (raw 8225 → 82.25 °F).
            // The tester only switches unit and document range by 401; the value is always raw × 0.01, never converted.
            RegisterNumber = 1, Description = "Temperature value", ChineseDescription = "溫度", Group = Measurement,
            DataType = DataType.Int16, Scale = 0.01, Unit = "°C", Min = -327, Max = 327,
            UnitSelectorRegister = SystemUnitRegister,
            UnitVariants = new Dictionary<ushort, UnitVariant>
            {
                [0] = new("°C", -327, 327),
                [1] = new("°F", -556.6, 620.6),
            },
        },
        new RegisterDefinition
        {
            RegisterNumber = 2, Description = "Temperature reliability", ChineseDescription = "溫度可靠性", Group = Measurement,
            DataType = DataType.Status, States = Reliability,
        },
        new RegisterDefinition
        {
            RegisterNumber = 3, Description = "Relative humidity value", ChineseDescription = "相對濕度", Group = Measurement,
            DataType = DataType.UInt16, Scale = 0.01, Unit = "%", Min = 0, Max = 100,
        },
        new RegisterDefinition
        {
            RegisterNumber = 4, Description = "Humidity reliability", ChineseDescription = "濕度可靠性", Group = Measurement,
            DataType = DataType.Status, States = Reliability,
        },
        new RegisterDefinition
        {
            // Document Scaling is "-": no scaling, the register is the value in ppm (shown as "-", computed as x1, never x0).
            RegisterNumber = 5, Description = "CO2 value", ChineseDescription = "CO₂ 濃度", Group = Measurement,
            DataType = DataType.UInt16, Scale = null, Unit = "ppm", Min = 0, Max = 2000,
        },
        new RegisterDefinition
        {
            RegisterNumber = 6, Description = "CO2 reliability", ChineseDescription = "CO₂ 可靠性", Group = Measurement,
            DataType = DataType.Status, States = Reliability,
        },

        // ---- Offset (RW, signed: negative offsets are two's complement on the wire, e.g. -15 = 0xFFF1) ----
        new RegisterDefinition
        {
            // Phase 3E – DOCUMENT-BASED, PHYSICAL VERIFICATION PENDING: the document gives -100...100 °C and
            // -180...180 °F (scale 0.1). Like register 1 the unit and range follow 401 and the value is never
            // converted, but whether the sensor really outputs a °F offset × 10 with 401 = 1 is not proven yet
            // (the real sensor's offset was 0, which reads the same in both units).
            // Writes use the unit / range of the 401 state read from the device (Phase 4F-1.1, ProfileUnitContext);
            // without a known 401 a write is refused.
            // Phase 4F-2 – PHYSICALLY VERIFIED, scope limited to (2026-10-13, real sensor, 401 = 0 / Celsius):
            // profile FC06 0 → +0.1 °C (raw 1) → 0, echo, FC03 verify and final profile read all PASS.
            // Not verified: other values / the full range, and the °F behaviour above.
            RegisterNumber = 223, Description = "Temperature offset", ChineseDescription = "溫度偏移", Group = Offset,
            DataType = DataType.Int16, Scale = 0.1, Unit = "°C", Min = -100, Max = 100,
            Access = RegisterAccess.ReadWrite, DefaultValue = 0,
            UnitSelectorRegister = SystemUnitRegister,
            UnitVariants = new Dictionary<ushort, UnitVariant>
            {
                [0] = new("°C", -100, 100),
                [1] = new("°F", -180, 180),
            },
        },
        new RegisterDefinition
        {
            // Phase 4F-2 – PHYSICALLY VERIFIED, scope limited to (2026-10-13, real sensor): profile FC06
            // 0 → +0.1 % (raw 1) → 0, echo, FC03 verify and final profile read all PASS. Other values not tested.
            RegisterNumber = 224, Description = "Relative humidity offset", ChineseDescription = "相對濕度偏移", Group = Offset,
            DataType = DataType.Int16, Scale = 0.1, Unit = "%", Min = -100, Max = 100,
            Access = RegisterAccess.ReadWrite, DefaultValue = 0,
        },
        new RegisterDefinition
        {
            // Document Scaling 1.0 (unlike register 5, whose Scaling is "-"): shown as "1".
            // Physical write PENDING / do not write: on the real sensor Reg 5 read 57005 (outside 0…2000 ppm) with
            // Reg 6 = 1 (bad reliability, unavailable); its exact model / CO2 capability is not confirmed yet.
            RegisterNumber = 225, Description = "CO2 offset", ChineseDescription = "CO₂ 偏移", Group = Offset,
            DataType = DataType.Int16, Scale = 1.0, Unit = "ppm", Min = -2000, Max = 2000,
            Access = RegisterAccess.ReadWrite, DefaultValue = 0,
        },

        // ---- Configuration (RW, no scaling: Scale "-") ----
        new RegisterDefinition
        {
            // Document: "others = invalid value to be discard" → shown as Unknown (n), never Celsius/Fahrenheit;
            // registers 1 / 223 then show no unit and no range check (see RegisterDefinition.ForUnitState).
            RegisterNumber = 401, Description = "System unit", ChineseDescription = "系統單位", Group = Configuration,
            DataType = DataType.Status, Access = RegisterAccess.ReadWrite, DefaultValue = 0,
            States = new Dictionary<ushort, RegisterState>
            {
                [0] = new("Celsius", "攝氏"),
                [1] = new("Fahrenheit", "華氏"),
            },
        },
        new RegisterDefinition
        {
            // Document: 1…247, default 1; "246 cannot be configured via a master. 246 is reserved for on-event
            // addressing." → rejected via ExcludedValues (reading 246 back is still only range-checked).
            RegisterNumber = 764, Description = "Modbus address", ChineseDescription = "Modbus 位址", Group = Configuration,
            DataType = DataType.UInt16, Scale = null, Min = 1, Max = 247,
            Access = RegisterAccess.ReadWrite, DefaultValue = 1,
            ExcludedValues = new Dictionary<double, string> { [246] = "Reserved for on-event addressing" },
            WriteHoldReason = CommunicationWriteHold,
        },
        new RegisterDefinition
        {
            // Document default 0 = Auto (not 9600; the tester's 9600 is only its first connection attempt).
            RegisterNumber = 765, Description = "Baud rate", ChineseDescription = "鮑率", Group = Configuration,
            DataType = DataType.Status, Access = RegisterAccess.ReadWrite, DefaultValue = 0,
            WriteHoldReason = CommunicationWriteHold,
            States = new Dictionary<ushort, RegisterState>
            {
                [0] = new("Auto", "自動"),
                [1] = new("9600bps", "9600bps"),
                [2] = new("19200bps", "19200bps"),
                [3] = new("38400bps", "38400bps"),
                [4] = new("57600bps", "57600bps"),
                [5] = new("76800bps", "76800bps"),
                [6] = new("115200bps", "115200bps"),
            },
        },
        new RegisterDefinition
        {
            // Document: "Transmission format (start bits-data bits-parity-stop bits)".
            RegisterNumber = 766, Description = "Transmission format", ChineseDescription = "傳輸格式（起始位元-資料位元-同位-停止位元）",
            Group = Configuration, DataType = DataType.Status, Access = RegisterAccess.ReadWrite, DefaultValue = 0,
            WriteHoldReason = CommunicationWriteHold,
            States = new Dictionary<ushort, RegisterState>
            {
                [0] = new("1-8-E-1", "1-8-E-1"),
                [1] = new("1-8-O-1", "1-8-O-1"),
                [2] = new("1-8-N-1", "1-8-N-1"),
                [3] = new("1-8-N-2", "1-8-N-2"),
            },
        },

        // ---- Version (R, no scaling) ----
        // A6V12045847: Major 1 byte, Minor 1 byte, Build 2 bytes; example [2.01.33] = 0x02010021.
        // 1286 = Major (high byte) / Minor (low byte) → "2.01"; 1287 = Build → 33; together "2.01.33" (SoftwareVersion).
        new RegisterDefinition
        {
            RegisterNumber = 1286, Description = "SW version: Major & Minor versions", ChineseDescription = "軟體版本：主要與次要版本",
            Group = Version, DataType = DataType.UInt16, Scale = null, ValueEncoding = RegisterValueEncoding.VersionMajorMinor,
        },
        new RegisterDefinition
        {
            RegisterNumber = 1287, Description = "SW version: Build version", ChineseDescription = "軟體版本：建置版本",
            Group = Version, DataType = DataType.UInt16, Scale = null, ValueEncoding = RegisterValueEncoding.VersionBuild,
        },
    ],
    // Section 4.3 is a "Holding Register (16-bit) No." table and A6V12045847 says "Supports only the holding
    // register": always read with FC03 (FC04 stays a Generic Modbus option only).
    requiredReadFunction: ModbusFunction.ReadHoldingRegisters);
}
