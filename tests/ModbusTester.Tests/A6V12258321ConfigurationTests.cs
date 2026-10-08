using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Services;
using ModbusTester.Core.Transport;

namespace ModbusTester.Tests;

/// <summary>Phase 3C: configuration registers 401 and 764–766 (read/decode only).</summary>
public class A6V12258321ConfigurationTests
{
    private static readonly DeviceProfile Profile = A6V12258321Profile.Profile;

    private static DecodedRegister Decode(int register, ushort raw) =>
        RegisterDecoder.DecodeEngineering(register, (ushort)(register - 1), raw, Profile.Find((ushort)(register - 1))!);

    // Metadata straight from the document
    [Theory]
    [InlineData(401, "System unit", DataType.Status, 0)]
    [InlineData(764, "Modbus address", DataType.UInt16, 1)]
    [InlineData(765, "Baud rate", DataType.Status, 0)]
    [InlineData(766, "Transmission format", DataType.Status, 0)]
    public void Definitions_MatchDocument(int register, string description, DataType dataType, double defaultValue)
    {
        RegisterDefinition d = Profile.Find((ushort)(register - 1))!;
        Assert.Equal(register, d.RegisterNumber);
        Assert.Equal(description, d.Description);
        Assert.Equal(dataType, d.DataType);
        Assert.Null(d.Scale);                                  // document Scaling "-"
        Assert.Equal(RegisterAccess.ReadWrite, d.Access);
        Assert.True(d.IsWritable);
        Assert.Equal(defaultValue, d.DefaultValue);
        Assert.Equal(A6V12258321Profile.Configuration, d.Group);
        Assert.Equal("", d.Unit);
    }

    [Fact]
    public void Register764_Range1To247()
    {
        RegisterDefinition d = Profile.Find(763)!;
        Assert.Equal((1.0, 247.0), (d.Min!.Value, d.Max!.Value));
    }

    // Register 401
    [Theory]
    [InlineData(0, "Celsius", RegisterQuality.Ok)]
    [InlineData(1, "Fahrenheit", RegisterQuality.Ok)]
    [InlineData(2, "Unknown (2)", RegisterQuality.UnknownState)]
    [InlineData(65535, "Unknown (65535)", RegisterQuality.UnknownState)]
    public void Register401_SystemUnit(int raw, string text, RegisterQuality quality) => AssertState(401, raw, text, quality);

    // Register 765
    [Theory]
    [InlineData(0, "Auto")]
    [InlineData(1, "9600bps")]
    [InlineData(2, "19200bps")]
    [InlineData(3, "38400bps")]
    [InlineData(4, "57600bps")]
    [InlineData(5, "76800bps")]
    [InlineData(6, "115200bps")]
    public void Register765_BaudRate(int raw, string text) => AssertState(765, raw, text, RegisterQuality.Ok);

    [Theory]
    [InlineData(7)]
    [InlineData(100)]
    public void Register765_UndefinedValue_IsUnknown(int raw) => AssertState(765, raw, $"Unknown ({raw})", RegisterQuality.UnknownState);

    // Register 766
    [Theory]
    [InlineData(0, "1-8-E-1", RegisterQuality.Ok)]
    [InlineData(1, "1-8-O-1", RegisterQuality.Ok)]
    [InlineData(2, "1-8-N-1", RegisterQuality.Ok)]
    [InlineData(3, "1-8-N-2", RegisterQuality.Ok)]
    [InlineData(4, "Unknown (4)", RegisterQuality.UnknownState)]
    public void Register766_TransmissionFormat(int raw, string text, RegisterQuality quality) => AssertState(766, raw, text, quality);

    private static void AssertState(int register, int raw, string text, RegisterQuality quality)
    {
        DecodedRegister r = Decode(register, (ushort)raw);
        Assert.Equal(text, r.ValueText);
        Assert.Equal(quality, r.Quality);
        Assert.Equal((ushort)raw, r.Raw);
        Assert.Null(r.Scale);
        Assert.Equal("-", r.ScaleText);
        Assert.Null(r.Value);   // states are never scaled
    }

    // Register 764: a number, no scaling, document range
    [Theory]
    [InlineData(1, "1", RegisterQuality.Ok)]
    [InlineData(247, "247", RegisterQuality.Ok)]
    [InlineData(0, "0", RegisterQuality.OutOfRange)]
    [InlineData(248, "248", RegisterQuality.OutOfRange)]
    public void Register764_ModbusAddress(int raw, string value, RegisterQuality quality)
    {
        DecodedRegister r = Decode(764, (ushort)raw);
        Assert.Equal(value, r.ValueText);
        Assert.Equal(raw, r.Integer);
        Assert.Equal(quality, r.Quality);
        Assert.Equal("-", r.ScaleText);   // Scaling "-": shown as "-", computed as x1 (never x0)
        Assert.Equal(raw, r.Value);
    }

    // Cumulative read: the planner groups the new registers by itself
    [Theory]
    [InlineData(400, "1x6 | 223x3", 9)]
    [InlineData(401, "1x6 | 223x3 | 401x1", 10)]
    [InlineData(763, "1x6 | 223x3 | 401x1", 10)]
    [InlineData(764, "1x6 | 223x3 | 401x1 | 764x1", 11)]
    [InlineData(765, "1x6 | 223x3 | 401x1 | 764x2", 12)]
    [InlineData(766, "1x6 | 223x3 | 401x1 | 764x3", 13)]
    [InlineData(1000, "1x6 | 223x3 | 401x1 | 764x3", 13)]
    public void Cumulative_GroupsIncludeConfiguration(int address, string groups, int rows)
    {
        IReadOnlyList<ModbusRequest> plan = ProfileReadPlanner.Plan(Profile, address, AddressBase.OneBased, 1);
        Assert.Equal(groups, string.Join(" | ", plan.Select(r => $"{r.StartAddress}x{r.Quantity}")));
        Assert.Equal(rows, ProfileReadPlanner.Select(Profile, address).Count);
        Assert.All(plan, r => Assert.Equal(ModbusFunction.ReadHoldingRegisters, r.Function));
    }

    [Fact]
    public void Address766_Pdus()
    {
        IReadOnlyList<ModbusRequest> plan = ProfileReadPlanner.Plan(Profile, 766, AddressBase.OneBased, 1);
        Assert.Equal([(0, 6), (222, 3), (400, 1), (763, 3)], plan.Select(r => ((int)r.PduAddress, (int)r.Quantity)));
    }

    // End to end: four real FC03 requests, 13 rows merged in register order
    [Fact]
    public async Task Address766_FourRequests_ThirteenRows()
    {
        var registers = new ushort[766];
        new ushort[] { 2534, 0, 4856, 0, 650, 0 }.CopyTo(registers, 0);
        registers[222] = 15;
        registers[223] = unchecked((ushort)(short)-25);
        registers[224] = 150;
        registers[400] = 0;     // Celsius
        registers[763] = 1;     // Modbus address 1
        registers[764] = 1;     // 9600bps
        registers[765] = 0;     // 1-8-E-1
        await using var slave = new FakeModbusTcpSlave { Registers = registers };
        await using var service = new ModbusService(new CommunicationLogger());
        Assert.True(await service.ConnectAsync(new ModbusTcpTransport(new TcpSettings("127.0.0.1", slave.Port))));

        ProfileReadResult read = await ProfileReader.ReadAsync(service, ProfileReadPlanner.Plan(Profile, 766, AddressBase.OneBased, 1));

        Assert.True(read.IsSuccess);
        Assert.Equal(["03 00 00 00 06", "03 00 DE 00 03", "03 01 90 00 01", "03 02 FB 00 03"],
            slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..])));
        Assert.Equal(4, service.Logger.Entries.Count(e => e.Message.StartsWith("FC03")));
        Assert.Contains(service.Logger.Entries, e => e.Message.Contains("Address = 401 (PDU 0x0190, 1-based)  Quantity = 1"));
        Assert.Contains(service.Logger.Entries, e => e.Message.Contains("Address = 764 (PDU 0x02FB, 1-based)  Quantity = 3"));
        Assert.DoesNotContain(service.Logger.Entries, e => e.Message.StartsWith("Result:"));

        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode(read.Results, Profile, _ => 1);
        Assert.Equal([1, 2, 3, 4, 5, 6, 223, 224, 225, 401, 764, 765, 766], rows.Select(r => r.Address));
        Assert.Equal(["Celsius", "1", "9600bps", "1-8-E-1"], rows.Skip(9).Select(r => r.ValueText));
        Assert.Equal(["System unit", "Modbus address", "Baud rate", "Transmission format"], rows.Skip(9).Select(r => r.Description));
        Assert.All(rows.Skip(9), r => Assert.Equal("-", r.ScaleText));
    }

    // Generic isolation: no enum mapping without the profile
    [Theory]
    [InlineData(401, 1)]
    [InlineData(765, 1)]
    [InlineData(766, 2)]
    public void Generic_ShowsRawNumbers_NoEnumText(int register, ushort raw)
    {
        var result = new ModbusResult
        {
            Request = new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, register, 1),
            Status = ResultStatus.Pass,
            Registers = [raw],
        };

        DecodedRegister r = RegisterService.Decode(result, DeviceProfile.Generic, _ => 1).Single();

        Assert.Equal(raw.ToString(), r.ValueText);
        Assert.Equal("1", r.ScaleText);
        Assert.Equal("", r.Description);
        Assert.False(r.IsProfileControlled);
    }
}
