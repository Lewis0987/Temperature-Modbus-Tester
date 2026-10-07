using ModbusTester.Core.Services;

namespace ModbusTester.Core.Profiles;

/// <summary>
/// Siemens A6V12258321 – Room humidity and air quality sensors QFA2050/MO, QFA2050D/MO, QPA2052/MO,
/// section 4.3 "Modbus registers" (holding registers, register numbers start at 1).
/// Phase 3A covers registers 1–6 (measured values and their reliability); configuration registers
/// (223–225, 401, 764–766, 1286–1287) follow in a later phase.
/// Not every sensor variant has every point; e.g. a model without CO2 may not provide registers 5/6.
/// </summary>
public static class A6V12258321Profile
{
    public const string Name = "A6V12258321 (QFA/QPA)";

    private static readonly IReadOnlyDictionary<ushort, RegisterState> Reliability = new Dictionary<ushort, RegisterState>
    {
        [0] = new("No error", "正常"),
        [1] = new("Bad reliability, unavailable", "資料不可靠／無法使用"),
    };

    public static DeviceProfile Profile { get; } = new(Name, "Siemens QFA2050/MO, QFA2050D/MO, QPA2052/MO – registers 1–6",
    [
        new RegisterDefinition
        {
            RegisterNumber = 1, Description = "Temperature value", ChineseDescription = "溫度",
            DataType = DataType.Int16, Scale = 0.01, Unit = "°C", Min = -327, Max = 327,
        },
        new RegisterDefinition
        {
            RegisterNumber = 2, Description = "Temperature reliability", ChineseDescription = "溫度可靠性",
            DataType = DataType.Status, States = Reliability,
        },
        new RegisterDefinition
        {
            RegisterNumber = 3, Description = "Relative humidity value", ChineseDescription = "相對濕度",
            DataType = DataType.UInt16, Scale = 0.01, Unit = "%", Min = 0, Max = 100,
        },
        new RegisterDefinition
        {
            RegisterNumber = 4, Description = "Humidity reliability", ChineseDescription = "濕度可靠性",
            DataType = DataType.Status, States = Reliability,
        },
        new RegisterDefinition
        {
            // Document Scaling is "-": no scaling, the register is the value in ppm (shown as "-", computed as x1, never x0).
            RegisterNumber = 5, Description = "CO2 value", ChineseDescription = "CO₂ 濃度",
            DataType = DataType.UInt16, Scale = null, Unit = "ppm", Min = 0, Max = 2000,
        },
        new RegisterDefinition
        {
            RegisterNumber = 6, Description = "CO2 reliability", ChineseDescription = "CO₂ 可靠性",
            DataType = DataType.Status, States = Reliability,
        },
    ]);
}
