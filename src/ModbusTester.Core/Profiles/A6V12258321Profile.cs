using ModbusTester.Core.Models;
using ModbusTester.Core.Services;

namespace ModbusTester.Core.Profiles;

/// <summary>
/// Siemens A6V12258321 – Room humidity and air quality sensors QFA2050/MO, QFA2050D/MO, QPA2052/MO,
/// section 4.3 "Modbus registers" (holding registers, register numbers start at 1).
/// Defined so far: measurement registers 1–6 (Phase 3A) and offset registers 223–225 (Phase 3B, read/decode only;
/// writing them is a later phase). Registers 401, 764–766, 1286–1287 follow later.
/// Not every sensor variant has every point; e.g. a model without CO2 may not provide registers 5/6/225.
/// </summary>
public static class A6V12258321Profile
{
    public const string Name = "A6V12258321 (QFA/QPA)";
    public const string Measurement = "Measurement";
    public const string Offset = "Offset";

    private static readonly IReadOnlyDictionary<ushort, RegisterState> Reliability = new Dictionary<ushort, RegisterState>
    {
        [0] = new("No error", "正常"),
        [1] = new("Bad reliability, unavailable", "資料不可靠／無法使用"),
    };

    public static DeviceProfile Profile { get; } = new(Name, "Siemens QFA2050/MO, QFA2050D/MO, QPA2052/MO – registers 1–6, 223–225",
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
    ],
    // Section 4.3 is a "Holding Register (16-bit) No." table: always read with FC03.
    requiredReadFunction: ModbusFunction.ReadHoldingRegisters);
}
