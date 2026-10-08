using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Services;
using ModbusTester.Core.Transport;

namespace ModbusTester.Tests;

/// <summary>
/// Phase 3D / 3D.1: version registers 1286–1287. A6V12045847 defines the layout: 1286 = Major (high byte) / Minor
/// (low byte) shown "1.02", 1287 = Build shown as a number; together "1.02.123".
/// </summary>
public class A6V12258321VersionTests
{
    private static readonly DeviceProfile Profile = A6V12258321Profile.Profile;

    private static DecodedRegister Decode(int register, ushort raw) =>
        RegisterDecoder.DecodeEngineering(register, (ushort)(register - 1), raw, Profile.Find((ushort)(register - 1))!);

    // A + B: metadata
    [Theory]
    [InlineData(1286, "SW version: Major & Minor versions", RegisterValueEncoding.VersionMajorMinor)]
    [InlineData(1287, "SW version: Build version", RegisterValueEncoding.VersionBuild)]
    public void Definitions_MatchDocument(int register, string description, RegisterValueEncoding encoding)
    {
        RegisterDefinition d = Profile.Find((ushort)(register - 1))!;
        Assert.Equal(encoding, d.ValueEncoding);
        Assert.Equal(register, d.RegisterNumber);
        Assert.Equal(description, d.Description);
        Assert.Equal(DataType.UInt16, d.DataType);
        Assert.Equal(RegisterAccess.Read, d.Access);
        Assert.False(d.IsWritable);
        Assert.Null(d.Scale);
        Assert.Null(d.DefaultValue);
        Assert.Null(d.Min);
        Assert.Null(d.Max);
        Assert.Equal("", d.Unit);
        Assert.Empty(d.States);
        Assert.Equal(A6V12258321Profile.Version, d.Group);
    }

    // C + D + E: 1286 = "Major.Minor" (two-digit Minor), raw / HEX / Decimal unchanged, no sign, Scale "-"
    [Theory]
    [InlineData(258, "0102", "1.02")]
    [InlineData(0x0201, "0201", "2.01")]
    [InlineData(65535, "FFFF", "255.255")]
    [InlineData(0, "0000", "0.00")]
    public void Decode1286_IsMajorMinor(int raw, string hex, string value)
    {
        DecodedRegister r = Decode(1286, (ushort)raw);
        Assert.Equal((ushort)raw, r.Raw);
        Assert.Equal(raw, r.Integer);       // Decimal column keeps the raw word
        Assert.Equal(hex, r.Hex);
        Assert.Equal("-", r.ScaleText);
        Assert.Null(r.Scale);
        Assert.Equal(value, r.ValueText);
        Assert.Null(r.Value);               // two numbers in one word: not one engineering value
        Assert.Equal(RegisterQuality.Ok, r.Quality);
        Assert.Equal("", r.Unit);
    }

    // 1287 = Build, a plain UInt16 number
    [Theory]
    [InlineData(123, "007B", "123")]
    [InlineData(0x0021, "0021", "33")]
    [InlineData(32768, "8000", "32768")]
    [InlineData(65535, "FFFF", "65535")]
    public void Decode1287_IsBuildNumber(int raw, string hex, string value)
    {
        DecodedRegister r = Decode(1287, (ushort)raw);
        Assert.Equal(raw, r.Integer);       // never negative
        Assert.Equal(hex, r.Hex);
        Assert.Equal(value, r.ValueText);
        Assert.Equal(raw, r.Value);
        Assert.Equal(RegisterQuality.Ok, r.Quality);
    }

    // F + G + H: cumulative read, planner unchanged
    [Theory]
    [InlineData(1285, "1x6 | 223x3 | 401x1 | 764x3", 13)]
    [InlineData(1286, "1x6 | 223x3 | 401x1 | 764x3 | 1286x1", 14)]
    [InlineData(1287, "1x6 | 223x3 | 401x1 | 764x3 | 1286x2", 15)]
    [InlineData(5000, "1x6 | 223x3 | 401x1 | 764x3 | 1286x2", 15)]
    public void Cumulative_AddsVersionGroup(int address, string groups, int rows)
    {
        IReadOnlyList<ModbusRequest> plan = ProfileReadPlanner.Plan(Profile, address, AddressBase.OneBased, 1);
        Assert.Equal(groups, string.Join(" | ", plan.Select(r => $"{r.StartAddress}x{r.Quantity}")));
        Assert.Equal(rows, ProfileReadPlanner.Select(Profile, address).Count);
        Assert.All(plan, r => Assert.Equal(ModbusFunction.ReadHoldingRegisters, r.Function));
    }

    [Fact]
    public void Address1287_LastRequest_IsPdu0x0505Quantity2()
    {
        ModbusRequest last = ProfileReadPlanner.Plan(Profile, 1287, AddressBase.OneBased, 1)[^1];
        Assert.Equal(0x0505, last.PduAddress);
        Assert.Equal(2, last.Quantity);
        Assert.Equal("03 05 05 00 02", HexFormatter.Format(ModbusPdu.BuildReadRequest(last.Function, last.PduAddress, last.Quantity)));
    }

    // K: the gap 767–1285 stays undefined
    [Fact]
    public void Gap767To1285_IsNotDefined()
    {
        Assert.All(Enumerable.Range(767, 1285 - 767 + 1), n => Assert.Null(Profile.Find((ushort)(n - 1))));
        Assert.DoesNotContain(ProfileReadPlanner.Select(Profile, 1287), r => r.RegisterNumber is > 766 and < 1286);
    }

    // I: end to end, five FC03 requests, fifteen rows in register order
    [Fact]
    public async Task Address1287_FiveRequests_FifteenRows()
    {
        var registers = new ushort[1287];
        new ushort[] { 2534, 0, 4856, 0, 650, 0 }.CopyTo(registers, 0);
        registers[222] = 15;
        registers[223] = unchecked((ushort)(short)-25);
        registers[224] = 150;
        registers[763] = 1;
        registers[764] = 1;
        registers[1285] = 258;
        registers[1286] = 123;
        await using var slave = new FakeModbusTcpSlave { Registers = registers };
        await using var service = new ModbusService(new CommunicationLogger());
        Assert.True(await service.ConnectAsync(new ModbusTcpTransport(new TcpSettings("127.0.0.1", slave.Port))));

        ProfileReadResult read = await ProfileReader.ReadAsync(service, ProfileReadPlanner.Plan(Profile, 1287, AddressBase.OneBased, 1));

        Assert.True(read.IsSuccess);
        Assert.Equal(["03 00 00 00 06", "03 00 DE 00 03", "03 01 90 00 01", "03 02 FB 00 03", "03 05 05 00 02"],
            slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..])));
        Assert.Contains(service.Logger.Entries, e => e.Message.Contains("Address = 1286 (PDU 0x0505, 1-based)  Quantity = 2"));
        Assert.Contains(service.Logger.Entries, e => e.Message == "258, 123");
        Assert.DoesNotContain(service.Logger.Entries, e => e.Message.StartsWith("Result:"));

        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode(read.Results, Profile, _ => 1);
        Assert.Equal([1, 2, 3, 4, 5, 6, 223, 224, 225, 401, 764, 765, 766, 1286, 1287], rows.Select(r => r.Address));
        Assert.Equal(["SW version: Major & Minor versions", "SW version: Build version"], rows.Skip(13).Select(r => r.Description));
        Assert.Equal(["1.02", "123"], rows.Skip(13).Select(r => r.ValueText));
        Assert.Equal("1.02.123", SoftwareVersion.Find(rows).ToString());
        Assert.Equal(["0102", "007B"], rows.Skip(13).Select(r => r.Hex));
        Assert.Equal(["-", "-"], rows.Skip(13).Select(r => r.ScaleText));
    }

    // J: Generic applies its manual scale, never the profile's meaning
    [Fact]
    public void Generic_Address1286_UsesManualScale()
    {
        var result = new ModbusResult
        {
            Request = new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, 1286, 2),
            Status = ResultStatus.Pass,
            Registers = [258, 123],
        };

        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode(result, DeviceProfile.Generic, _ => 0.01);

        Assert.Equal(["2.58", "1.23"], rows.Select(r => r.ValueText));
        Assert.Equal(["0.01", "0.01"], rows.Select(r => r.ScaleText));
        Assert.All(rows, r => Assert.Equal("", r.Description));
        Assert.All(rows, r => Assert.False(r.IsProfileControlled));
        Assert.Null(SoftwareVersion.Find(rows));
    }
}
