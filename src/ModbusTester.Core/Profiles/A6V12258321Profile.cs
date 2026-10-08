using ModbusTester.Core.Models;
using ModbusTester.Core.Services;

namespace ModbusTester.Core.Profiles;

/// <summary>
/// Siemens A6V12258321 – Room humidity and air quality sensors QFA2050/MO, QFA2050D/MO, QPA2052/MO,
/// section 4.3 "Modbus registers" (holding registers, register numbers start at 1).
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

    /// <summary>
    /// Why profile writes to 764–766 are encoded/validated but not sent yet: when new communication parameters take
    /// effect, which settings the response uses and whether a reconnect is needed are not confirmed.
    /// </summary>
    public const string CommunicationWriteHold =
        "Communication parameter writes are on hold: effect timing / reconnect behaviour not confirmed";

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
            RegisterNumber = 1, Description = "Temperature value", ChineseDescription = "溫度", Group = Measurement,
            DataType = DataType.Int16, Scale = 0.01, Unit = "°C", Min = -327, Max = 327,
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
            // The document also lists -180...180 °F; the unit setting is not modelled yet, so °C is shown.
            RegisterNumber = 223, Description = "Temperature offset", ChineseDescription = "溫度偏移", Group = Offset,
            DataType = DataType.Int16, Scale = 0.1, Unit = "°C", Min = -100, Max = 100,
            Access = RegisterAccess.ReadWrite, DefaultValue = 0,
        },
        new RegisterDefinition
        {
            RegisterNumber = 224, Description = "Relative humidity offset", ChineseDescription = "相對濕度偏移", Group = Offset,
            DataType = DataType.Int16, Scale = 0.1, Unit = "%", Min = -100, Max = 100,
            Access = RegisterAccess.ReadWrite, DefaultValue = 0,
        },
        new RegisterDefinition
        {
            // Document Scaling 1.0 (unlike register 5, whose Scaling is "-"): shown as "1".
            RegisterNumber = 225, Description = "CO2 offset", ChineseDescription = "CO₂ 偏移", Group = Offset,
            DataType = DataType.Int16, Scale = 1.0, Unit = "ppm", Min = -2000, Max = 2000,
            Access = RegisterAccess.ReadWrite, DefaultValue = 0,
        },

        // ---- Configuration (RW, no scaling: Scale "-") ----
        new RegisterDefinition
        {
            // Document: "others = invalid value to be discard" → shown as Unknown (n), never Celsius/Fahrenheit.
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
            // 246 is inside 1…247 but reserved for on-event addressing: a write of 246 is rejected via ExcludedValues
            // (reading 246 back is still only range-checked, nothing else is assumed).
            RegisterNumber = 764, Description = "Modbus address", ChineseDescription = "Modbus 位址", Group = Configuration,
            DataType = DataType.UInt16, Scale = null, Min = 1, Max = 247,
            Access = RegisterAccess.ReadWrite, DefaultValue = 1,
            ExcludedValues = new Dictionary<double, string> { [246] = "Reserved for on-event addressing" },
            WriteHoldReason = CommunicationWriteHold,
        },
        new RegisterDefinition
        {
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
        // The register table gives no encoding for these words (e.g. how Major/Minor share register 1286), so the
        // raw UInt16 is shown as-is; nothing is split or reformatted into "x.y".
        new RegisterDefinition
        {
            RegisterNumber = 1286, Description = "SW version: Major & Minor versions", ChineseDescription = "軟體版本：主要與次要版本",
            Group = Version, DataType = DataType.UInt16, Scale = null,
        },
        new RegisterDefinition
        {
            RegisterNumber = 1287, Description = "SW version: Build version", ChineseDescription = "軟體版本：建置版本",
            Group = Version, DataType = DataType.UInt16, Scale = null,
        },
    ],
    // Section 4.3 is a "Holding Register (16-bit) No." table: always read with FC03.
    requiredReadFunction: ModbusFunction.ReadHoldingRegisters);
}
