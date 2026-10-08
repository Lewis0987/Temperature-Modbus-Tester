using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Services;
using ModbusTester.Core.Transport;

namespace ModbusTester.Tests;

/// <summary>
/// Phase 3E: System unit (401) selects the unit and range of Reg 1 and Reg 223; values are never converted.
/// Reg 1 is PHYSICALLY VERIFIED with the real sensor (2026-10-08: 401 = 0 raw 2793 → 27.93 °C,
/// 401 = 1 raw 8225 → 82.25 °F). Reg 223 °F is DOCUMENT-BASED, physical verification pending.
/// Data below marked "physical" is from the Phase 4F-1 read of the real sensor.
/// </summary>
public class SystemUnitContextTests
{
    private static readonly DeviceProfile A6V = A6V12258321Profile.Profile;

    private static ModbusResult Result(int address, params ushort[] registers) => new()
    {
        Request = new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, address, (ushort)registers.Length),
        Status = ResultStatus.Pass,
        Registers = registers,
    };

    private static ushort S(short value) => unchecked((ushort)value);

    /// <summary>Profile read "up to 401" as the planner sends it: 1–6, 223–225, 401 (three requests).</summary>
    private static IReadOnlyList<DecodedRegister> Read(ushort reg1, ushort reg223, ushort systemUnit) =>
        RegisterService.Decode([Result(1, reg1, 0, 5128, 0, 57005, 1), Result(223, reg223, 0, 0), Result(401, systemUnit)], A6V, _ => 1);

    private static DecodedRegister Row(IReadOnlyList<DecodedRegister> rows, int address) => rows.Single(r => r.Address == address);

    // Tests 1, 3, 4, 5 – physical: 401 = 0, raw 2793 → 27.93 °C
    [Fact]
    public void Physical_Celsius_Reg1()
    {
        DecodedRegister r = Row(Read(2793, 0, 0), 1);
        Assert.Equal("27.93", r.ValueText);
        Assert.Equal("°C", r.Unit);
        Assert.Equal(0.01, r.Scale);
        Assert.Equal((-327.0, 327.0), (r.Definition!.Min!.Value, r.Definition.Max!.Value));
        Assert.Equal(RegisterQuality.Ok, r.Quality);
    }

    // Tests 2, 3, 4, 6 – physical: 401 = 1, raw 8225 → 82.25 °F, not converted again (would be 180.05)
    [Fact]
    public void Physical_Fahrenheit_Reg1_NotConvertedAgain()
    {
        DecodedRegister r = Row(Read(8225, 0, 1), 1);
        Assert.Equal("82.25", r.ValueText);
        Assert.Equal(82.25, r.Value);
        Assert.NotEqual("180.05", r.ValueText);         // 82.25 × 9/5 + 32
        Assert.NotEqual("27.92", r.ValueText);          // (82.25 − 32) × 5/9
        Assert.Equal("°F", r.Unit);
        Assert.Equal(0.01, r.Scale);
        Assert.Equal((-556.6, 620.6), (r.Definition!.Min!.Value, r.Definition.Max!.Value));
        Assert.Equal(8225, r.Raw);
        Assert.Equal("2021", r.Hex);
    }

    // Tests 5–6: the range follows 401 (327.5 is outside the °C range but inside the °F range)
    [Theory]
    [InlineData(0, RegisterQuality.OutOfRange)]
    [InlineData(1, RegisterQuality.Ok)]
    public void Reg1_RangeFollowsSystemUnit(ushort systemUnit, RegisterQuality quality) =>
        Assert.Equal(quality, Row(Read(32750, 0, systemUnit), 1).Quality);

    // Tests 7–9: Reg 223 document-based (physical verification pending): unit / range follow 401, value not converted
    [Theory]
    [InlineData(0, "°C", "1.5")]
    [InlineData(1, "°F", "1.5")]
    public void Reg223_DocumentBased_UnitFollows401_NoConversion(ushort systemUnit, string unit, string value)
    {
        DecodedRegister r = Row(Read(2793, 15, systemUnit), 223);
        Assert.Equal(unit, r.Unit);
        Assert.Equal(value, r.ValueText);
        Assert.Equal(0.1, r.Scale);
    }

    [Theory]
    [InlineData(0, RegisterQuality.OutOfRange)]   // 150 outside -100…100 °C
    [InlineData(1, RegisterQuality.Ok)]           // 150 inside -180…180 °F
    public void Reg223_DocumentBased_RangeFollows401(ushort systemUnit, RegisterQuality quality) =>
        Assert.Equal(quality, Row(Read(2793, 1500, systemUnit), 223).Quality);

    [Fact]
    public void Reg223_NegativeOffset_SignedNotConverted() =>
        Assert.Equal("-2.5", Row(Read(2793, S(-25), 1), 223).ValueText);

    // Tests 10–11: unknown 401 → no unit claimed, no range flag, read still fine
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(65535)]
    public void UnknownSystemUnit_NoUnitNoRange(ushort systemUnit)
    {
        IReadOnlyList<DecodedRegister> rows = Read(32750, 1500, systemUnit);
        Assert.Equal($"Unknown ({systemUnit})", Row(rows, 401).ValueText);
        Assert.Equal(RegisterQuality.UnknownState, Row(rows, 401).Quality);
        foreach (int address in new[] { 1, 223 })
        {
            DecodedRegister r = Row(rows, address);
            Assert.Equal("", r.Unit);
            Assert.Equal(RegisterQuality.Ok, r.Quality);    // neither range applied
            Assert.Null(r.Definition!.Min);
            Assert.Null(r.Definition.Max);
        }
        Assert.Equal("327.5", Row(rows, 1).ValueText);

        Assert.Equal(10, rows.Count);
    }

    // Existing behaviour kept: a read that does not reach 401 uses its document default 0 (°C)
    [Fact]
    public void SystemUnitNotRead_UsesDocumentDefault_Celsius()
    {
        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode([Result(1, 2793, 0, 5128, 0, 57005, 1), Result(223, 15, 0, 0)], A6V, _ => 1);
        Assert.Equal("°C", Row(rows, 1).Unit);
        Assert.Equal("°C", Row(rows, 223).Unit);
    }

    // Only Reg 1 and Reg 223 depend on 401; Reg 224 (%) / 225 (ppm) / 3 (%) do not
    [Fact]
    public void OnlyTemperatureRegistersFollow401()
    {
        Assert.Equal([1, 223], A6V.Registers.Where(r => r.UnitSelectorRegister == 401).Select(r => r.RegisterNumber));
        IReadOnlyList<DecodedRegister> rows = Read(8225, 0, 1);
        Assert.Equal(["%", "%", "ppm"], new[] { 3, 224, 225 }.Select(a => Row(rows, a).Unit));
    }

    // Tests 12–13: Generic ignores the whole profile context
    [Fact]
    public void Generic_NoSystemUnitContext()
    {
        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode([Result(1, 8225), Result(401, 1)], DeviceProfile.Generic, _ => 0.01);
        Assert.Equal(["82.25", "0.01"], rows.Select(r => r.ValueText));
        Assert.All(rows, r => Assert.Equal("", r.Unit));
        Assert.All(rows, r => Assert.Null(r.Definition));
    }

    // Tests 21–22: CO2 unavailable as read from the real sensor – existing range / status decoding only
    [Fact]
    public void Physical_Co2Unavailable()
    {
        IReadOnlyList<DecodedRegister> rows = Read(2795, 0, 0);
        Assert.Equal(("DEAD", "57005", RegisterQuality.OutOfRange), (Row(rows, 5).Hex, Row(rows, 5).ValueText, Row(rows, 5).Quality));
        Assert.Equal("57005 (Out of range)", Row(rows, 5).DisplayValue);
        Assert.Equal("Bad reliability, unavailable", Row(rows, 6).ValueText);
        Assert.Equal(["27.95", "No error", "51.28", "No error"], new[] { 1, 2, 3, 4 }.Select(a => Row(rows, a).ValueText));
    }

    // Tests 15–20: physical cumulative read up to 1287 – 5 FC03 requests, 15 rows, 401 (request 3) drives Reg 1 / 223
    [Theory]
    [InlineData(0, "°C")]
    [InlineData(1, "°F")]
    public async Task Physical_Cumulative1287_FiveRequests_401FromThirdRequest(ushort systemUnit, string unit)
    {
        var rtu = new FakeRtuSlave { Registers = new ushort[1300] };
        new ushort[] { 2795, 0, 5128, 0, 57005, 1 }.CopyTo(rtu.Registers, 0);   // physical values
        rtu.Registers[400] = systemUnit;
        new ushort[] { 1, 1, 0 }.CopyTo(rtu.Registers, 763);                    // 764 = 1, 765 = 9600bps, 766 = 1-8-E-1
        new ushort[] { 256, 7 }.CopyTo(rtu.Registers, 1285);                    // software version 1.00.7
        var channel = new FakeSerialChannel { Responder = rtu.Respond };
        await using var service = new ModbusService(new CommunicationLogger());
        Assert.True(await service.ConnectAsync(new ModbusRtuTransport(new RtuSettings("COM3"), _ => channel)));

        ProfileReadResult read = await ProfileReader.ReadAsync(service, ProfileReadPlanner.Plan(A6V, 1287, AddressBase.OneBased, 1));

        Assert.True(read.IsSuccess);
        Assert.Equal(["01 03 00 00 00 06 C5 C8", "01 03 00 DE 00 03 65 F1", "01 03 01 90 00 01 85 DB", "01 03 02 FB 00 03 75 82", "01 03 05 05 00 02 D4 C6"],
            rtu.Requests.Select(HexFormatter.Format));
        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode(read.Results, A6V, _ => 1);
        Assert.Equal(15, rows.Count);
        Assert.Equal((unit, "27.95"), (Row(rows, 1).Unit, Row(rows, 1).ValueText));
        Assert.Equal(unit, Row(rows, 223).Unit);
        Assert.Equal(["1", "9600bps", "1-8-E-1"], new[] { 764, 765, 766 }.Select(a => Row(rows, a).ValueText));
        Assert.Equal(["1.00", "7"], new[] { 1286, 1287 }.Select(a => Row(rows, a).ValueText));
        Assert.Equal("1.00.7", SoftwareVersion.Find(rows).ToString());
    }

    // Physical frames from the 4F-1 log: CRC PASS and the recorded raw values
    [Theory]
    [InlineData("01 03 06 00 00 00 00 00 00 21 75", new ushort[] { 0, 0, 0 })]        // 223–225
    [InlineData("01 03 06 00 01 00 01 00 00 4D 75", new ushort[] { 1, 1, 0 })]        // 764–766
    [InlineData("01 03 04 01 00 00 07 BA 0D", new ushort[] { 256, 7 })]               // 1286–1287
    [InlineData("01 03 02 20 21 61 9C", new ushort[] { 8225 })]                       // Reg 1 with 401 = 1
    public void Physical_RtuFrames(string frame, ushort[] values)
    {
        byte[] bytes = [.. frame.Split(' ').Select(b => Convert.ToByte(b, 16))];
        var details = new List<string>();
        byte[] pdu = ModbusRtuFrame.Validate(bytes, 1, details);
        Assert.Contains(details, d => d.EndsWith("CRC PASS"));
        Assert.Equal(values, ModbusPdu.ParseReadRegistersResponse(ModbusFunction.ReadHoldingRegisters, pdu, (ushort)values.Length));
    }

    [Fact]
    public void Physical_Fc06RestoreFrame_Crc() =>
        Assert.Equal("01 06 01 90 00 00 88 1B", HexFormatter.Format(ModbusRtuFrame.Build(1, ModbusPdu.BuildWriteSingleRegister(400, 0))));

    // Test 16 / 20: physical software version 256 / 7
    [Fact]
    public void Physical_SoftwareVersion_1_00_7() =>
        Assert.Equal("1.00.7", SoftwareVersion.FromRegisters(256, 7).ToString());

    // The physical 765 = 1 differs from the document default 0 (Auto); the metadata keeps the document default
    [Fact]
    public void Register765_DocumentDefaultStaysAuto() => Assert.Equal(0, A6V.Find(764)!.DefaultValue);
}
