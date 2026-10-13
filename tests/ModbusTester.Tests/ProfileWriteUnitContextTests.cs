using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Services;

namespace ModbusTester.Tests;

/// <summary>
/// Phase 4F-1.1: Reg 223 profile writes use the System unit (401) known for the device – unit and range only, the
/// value is always value / 0.1 (no °C ↔ °F conversion). Reg 223 °F is DOCUMENT-BASED, physical verification pending.
/// Without a known 401 the write is refused (no fallback to the document default for writes).
/// </summary>
public class ProfileWriteUnitContextTests
{
    private static readonly DeviceProfile A6V = A6V12258321Profile.Profile;
    private static readonly IReadOnlyDictionary<int, ushort> Celsius = new Dictionary<int, ushort> { [401] = 0 };
    private static readonly IReadOnlyDictionary<int, ushort> Fahrenheit = new Dictionary<int, ushort> { [401] = 1 };

    private static RegisterDefinition Reg(int number) => A6V.Find((ushort)(number - 1))!;

    private static ProfileWriteResult Prepare223(string input, IReadOnlyDictionary<int, ushort>? context) =>
        ProfileWriteService.Prepare(A6V, 1, 223, AddressBase.OneBased, input, context);

    // Tests 1–5
    [Theory]
    [InlineData(0, "°C", -100.0, 100.0)]
    [InlineData(1, "°F", -180.0, 180.0)]
    public void Resolve_UnitAndRange(ushort systemUnit, string unit, double min, double max)
    {
        (RegisterDefinition? d, string? error) = ProfileWriteService.ResolveForWrite(A6V, Reg(223), new Dictionary<int, ushort> { [401] = systemUnit });
        Assert.Null(error);
        Assert.Equal(unit, d!.Unit);
        Assert.Equal(min, d.Min);
        Assert.Equal(max, d.Max);
        Assert.Equal(0.1, d.Scale);
    }

    // Tests 6–10
    [Theory]
    [InlineData(0, "100", ProfileWriteError.None, (ushort)1000)]
    [InlineData(0, "100.1", ProfileWriteError.OutOfRange, null)]
    [InlineData(0, "150", ProfileWriteError.OutOfRange, null)]
    [InlineData(1, "180", ProfileWriteError.None, (ushort)1800)]
    [InlineData(1, "-180", ProfileWriteError.None, (ushort)63736)]
    [InlineData(1, "180.1", ProfileWriteError.OutOfRange, null)]
    [InlineData(1, "150", ProfileWriteError.None, (ushort)1500)]
    [InlineData(0, "1.5", ProfileWriteError.None, (ushort)15)]
    [InlineData(1, "1.5", ProfileWriteError.None, (ushort)15)]    // °F: same raw – no conversion (not 27 / -169)
    [InlineData(1, "1.55", ProfileWriteError.NotRepresentableByScale, null)]
    public void Encode_ByKnownSystemUnit(ushort systemUnit, string input, ProfileWriteError error, ushort? raw)
    {
        ProfileWriteResult r = Prepare223(input, new Dictionary<int, ushort> { [401] = systemUnit });
        Assert.Equal(error, r.Error);
        Assert.Equal(raw, r.Encoding?.Raw is { } x && r.Error == ProfileWriteError.None ? x : null);
    }

    [Fact]
    public void Fahrenheit_PreparedDefinitionAndRequest()
    {
        ProfileWriteResult r = Prepare223("1.5", Fahrenheit);
        Assert.Equal("°F", r.Definition!.Unit);
        Assert.Equal([(ushort)15], r.Request!.Values);
        Assert.Equal(1.5, r.Encoding!.EngineeringValue);
    }

    // Tests 11–12: no reliable context → refused (no °C pretence), unknown state → refused
    [Fact]
    public void NoContext_Refused()
    {
        foreach (IReadOnlyDictionary<int, ushort>? context in new[] { null, new Dictionary<int, ushort>() })
        {
            ProfileWriteResult r = Prepare223("1.5", context);
            Assert.Equal(ProfileWriteError.UnitContextUnknown, r.Error);
            Assert.Null(r.Request);
            Assert.Contains("read Register 401 first", r.Message);
        }
        Assert.Null(ProfileWriteService.ResolveForWrite(A6V, Reg(223), null).Definition);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(65535)]
    public void UnknownSystemUnit_Refused(ushort state)
    {
        ProfileWriteResult r = Prepare223("1.5", new Dictionary<int, ushort> { [401] = state });
        Assert.Equal(ProfileWriteError.UnitContextUnknown, r.Error);
        Assert.Null(r.Request);
        Assert.Contains($"= {state} is not a known state", r.Message);
    }

    [Fact]
    public async Task NoContext_NothingSent()
    {
        await using var slave = new FakeModbusTcpSlave { Registers = new ushort[1300] };
        await using var service = new ModbusService(new CommunicationLogger());
        Assert.True(await service.ConnectAsync(new Core.Transport.ModbusTcpTransport(new TcpSettings("127.0.0.1", slave.Port))));

        ProfileWriteResult r = await new ProfileWriteService(service).WriteAsync(A6V, 1, 223, AddressBase.OneBased, "1.5");

        Assert.Equal(ProfileWriteError.UnitContextUnknown, r.Error);
        Assert.False(r.IsSent);
        Assert.Empty(slave.ReceivedAdus);
    }

    // Other registers do not need a System unit; read-only Reg 1 stays ReadOnly
    [Theory]
    [InlineData(224, "1.5")]
    [InlineData(225, "150")]
    [InlineData(401, "Fahrenheit")]
    public void FixedUnitRegisters_NoContextNeeded(int register, string input) =>
        Assert.Equal(ProfileWriteError.None, ProfileWriteService.Prepare(A6V, 1, register, AddressBase.OneBased, input).Error);

    [Fact]
    public void Reg1_StillReadOnly() =>
        Assert.Equal(ProfileWriteError.ReadOnly, ProfileWriteService.Prepare(A6V, 1, 1, AddressBase.OneBased, "25").Error);

    // Test 25: holds unchanged, context or not
    [Theory]
    [InlineData(764, "5")]
    [InlineData(765, "9600bps")]
    [InlineData(766, "1-8-N-1")]
    public void CommunicationRegisters_StillOnHold(int register, string input) =>
        Assert.Equal(ProfileWriteError.WriteOnHold, ProfileWriteService.Prepare(A6V, 1, register, AddressBase.OneBased, input, Fahrenheit).Error);

    // ---- ProfileUnitContext ----

    private static ModbusResult Result(int address, byte unitId = 1, params ushort[] registers) => new()
    {
        Request = new ModbusRequest(ModbusFunction.ReadHoldingRegisters, unitId, address, (ushort)registers.Length),
        Status = ResultStatus.Pass,
        Registers = registers,
    };

    [Fact]
    public void Context_RecordsOnlySelectorsFromReads()
    {
        var context = new ProfileUnitContext();
        Assert.False(context.Record(A6V, [Result(1, 1, 2795, 0, 5128, 0, 57005, 1), Result(223, 1, 0, 0, 0)]));
        Assert.Empty(context.For(1));                                   // no 401 in this read: nothing assumed

        Assert.True(context.Record(A6V, [Result(401, 1, 1)]));
        Assert.Equal(new Dictionary<int, ushort> { [401] = 1 }, context.For(1));
        Assert.False(context.Record(A6V, [Result(401, 1, 1)]));         // same state: unchanged
        Assert.True(context.Record(A6V, [Result(401, 1, 0)]));
        Assert.Equal(0, context.For(1)[401]);
    }

    // Test 15: never used for another Unit ID
    [Fact]
    public void Context_IsPerUnitId()
    {
        var context = new ProfileUnitContext();
        context.Record(A6V, [Result(401, 1, 1)]);
        Assert.Empty(context.For(2));
        context.Record(A6V, [Result(1, 2, 2795)]);                      // a read from Unit 2 replaces Unit 1's state
        Assert.Empty(context.For(1));
        Assert.Empty(context.For(2));
    }

    [Fact]
    public void Context_IgnoresFailedResultsAndClears()
    {
        var context = new ProfileUnitContext();
        context.Record(A6V, [Result(401, 1, 1) with { Status = ResultStatus.Timeout }]);
        Assert.Empty(context.For(1));
        context.Record(A6V, [Result(401, 1, 1)]);
        Assert.True(context.Clear());
        Assert.Empty(context.For(1));
        Assert.False(context.Clear());
    }

    [Fact]
    public void Context_GenericProfileHasNoSelectors()
    {
        var context = new ProfileUnitContext();
        Assert.False(context.Record(DeviceProfile.Generic, [Result(401, 1, 1)]));
        Assert.Empty(context.For(1));
    }
}
