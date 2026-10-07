using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Services;
using ModbusTester.Core.Transport;

namespace ModbusTester.Tests;

/// <summary>Profile cumulative read: "Address" = show every defined register up to it, read in consecutive groups.</summary>
public class ProfileCumulativeReadTests
{
    private static readonly DeviceProfile A6V = A6V12258321Profile.Profile;

    private static string Groups(IReadOnlyList<ModbusRequest> plan) =>
        string.Join(" | ", plan.Select(r => $"{r.StartAddress}x{r.Quantity}"));

    private static DeviceProfile Synthetic(params int[] registers) => new("Synthetic", "test",
        registers.Select(n => new RegisterDefinition { RegisterNumber = n, Description = $"R{n}", DataType = DataType.UInt16 }));

    // Tests 1–8: selection and request groups (1-based)
    [Theory]
    [InlineData(1, "1", "1x1")]
    [InlineData(2, "1,2", "1x2")]
    [InlineData(3, "1,2,3", "1x3")]
    [InlineData(6, "1,2,3,4,5,6", "1x6")]
    [InlineData(100, "1,2,3,4,5,6", "1x6")]
    [InlineData(222, "1,2,3,4,5,6", "1x6")]
    [InlineData(223, "1,2,3,4,5,6,223", "1x6 | 223x1")]
    [InlineData(224, "1,2,3,4,5,6,223,224", "1x6 | 223x2")]
    [InlineData(225, "1,2,3,4,5,6,223,224,225", "1x6 | 223x3")]
    [InlineData(300, "1,2,3,4,5,6,223,224,225", "1x6 | 223x3")]   // never 226..300
    public void A6V_SelectionAndGroups(int address, string selection, string groups)
    {
        Assert.Equal(selection, string.Join(",", ProfileReadPlanner.Select(A6V, address).Select(r => r.RegisterNumber)));
        IReadOnlyList<ModbusRequest> plan = ProfileReadPlanner.Plan(A6V, address, AddressBase.OneBased, 1);
        Assert.Equal(groups, Groups(plan));
        Assert.All(plan, r => Assert.Equal(ModbusFunction.ReadHoldingRegisters, r.Function));
        Assert.All(plan, r => Assert.Equal(AddressBase.OneBased, r.AddressBase));
    }

    // Test 16: the PDU conversion happens once, in ModbusRequest.PduAddress
    [Fact]
    public void Address225_Requests_HavePdu0And222()
    {
        IReadOnlyList<ModbusRequest> plan = ProfileReadPlanner.Plan(A6V, 225, AddressBase.OneBased, 1);
        Assert.Equal([(0, 6), (222, 3)], plan.Select(r => ((int)r.PduAddress, (int)r.Quantity)));
    }

    [Fact]
    public void ZeroBased_Address222_IsUpToRegister223_SamePdus()
    {
        IReadOnlyList<ModbusRequest> plan = ProfileReadPlanner.Plan(A6V, 222, AddressBase.ZeroBased, 1);
        Assert.Equal("0x6 | 222x1", Groups(plan));                       // user-facing, 0-based
        Assert.Equal([0, 222], plan.Select(r => (int)r.PduAddress));       // same wire addresses as 1-based 223
    }

    [Fact]
    public void A6V_RequiresFc03_GenericDoesNot()
    {
        Assert.Equal(ModbusFunction.ReadHoldingRegisters, A6V.RequiredReadFunction);
        Assert.Null(DeviceProfile.Generic.RequiredReadFunction);
    }

    // Test 5/10 at plan level: the profile's FC03 wins even if FC04 is asked for
    [Fact]
    public void A6V_Plan_IsAlwaysFc03()
    {
        IReadOnlyList<ModbusRequest> plan = ProfileReadPlanner.Plan(A6V, 225, AddressBase.OneBased, 1, ModbusFunction.ReadInputRegisters);
        Assert.Equal("1x6 | 223x3", Groups(plan));
        Assert.All(plan, r => Assert.Equal(ModbusFunction.ReadHoldingRegisters, r.Function));
    }

    [Fact]
    public void ProfileWithoutRequiredFunction_UsesTheGivenOne()
    {
        IReadOnlyList<ModbusRequest> plan = ProfileReadPlanner.Plan(Synthetic(1, 2), 2, AddressBase.OneBased, 1, ModbusFunction.ReadInputRegisters);
        Assert.All(plan, r => Assert.Equal(ModbusFunction.ReadInputRegisters, r.Function));
    }

    [Fact]
    public void Generic_HasNothingToPlan()
    {
        Assert.Empty(ProfileReadPlanner.Plan(DeviceProfile.Generic, 300, AddressBase.OneBased, 1));
    }

    [Fact]
    public void FutureRegisters_GroupAsInTheRequirement()
    {
        DeviceProfile future = Synthetic(1, 2, 3, 4, 5, 6, 223, 224, 225, 401, 764, 765, 766, 1286, 1287);
        Assert.Equal("1x6 | 223x3 | 401x1", Groups(ProfileReadPlanner.Plan(future, 401, AddressBase.OneBased, 1)));
        Assert.Equal("1x6 | 223x3 | 401x1 | 764x1", Groups(ProfileReadPlanner.Plan(future, 764, AddressBase.OneBased, 1)));
        Assert.Equal("1x6 | 223x3 | 401x1 | 764x3", Groups(ProfileReadPlanner.Plan(future, 766, AddressBase.OneBased, 1)));
        Assert.Equal("1x6 | 223x3 | 401x1 | 764x3 | 1286x2", Groups(ProfileReadPlanner.Plan(future, 1287, AddressBase.OneBased, 1)));
        Assert.Equal(13, ProfileReadPlanner.Select(future, 766).Count);
    }

    [Fact]
    public void Group_SplitsRunsLongerThanTheFc03Limit()
    {
        Assert.Equal([new RegisterGroup(1, 125), new RegisterGroup(126, 125), new RegisterGroup(251, 50)],
            ProfileReadPlanner.Group(Enumerable.Range(1, 300)));
        Assert.Equal([new RegisterGroup(1, 2), new RegisterGroup(5, 1)], ProfileReadPlanner.Group([5, 2, 1, 2]));
    }

    // ---- Execution through ModbusService (TCP fake slave holding registers 1–6 and 223–225) ----

    private static ushort[] DeviceMap()
    {
        var registers = new ushort[225];
        new ushort[] { 2534, 0, 4856, 0, 650, 0 }.CopyTo(registers, 0);
        registers[222] = 15;
        registers[223] = unchecked((ushort)(short)-25);
        registers[224] = 150;
        return registers;
    }

    private static async Task<(ModbusService Service, FakeModbusTcpSlave Slave)> ConnectAsync(ushort[] registers)
    {
        var slave = new FakeModbusTcpSlave { Registers = registers };
        var service = new ModbusService(new CommunicationLogger());
        Assert.True(await service.ConnectAsync(new ModbusTcpTransport(new TcpSettings("127.0.0.1", slave.Port))));
        return (service, slave);
    }

    // Tests 9, 10, 18, 19
    [Theory]
    [InlineData(225)]
    [InlineData(300)]
    public async Task Address225Or300_TwoRealRequests_MergedRowsAscending(int address)
    {
        var (service, slave) = await ConnectAsync(DeviceMap());
        await using var _ = slave;
        await using var __ = service;

        ProfileReadResult read = await ProfileReader.ReadAsync(service, ProfileReadPlanner.Plan(A6V, address, AddressBase.OneBased, 1));

        Assert.True(read.IsSuccess);
        Assert.Equal(["03 00 00 00 06", "03 00 DE 00 03"], slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..])));
        string[] tx = [.. service.Logger.Entries.Where(e => e.Message.StartsWith("FC03")).Select(e => e.Message)];
        Assert.Equal(["FC03  Unit ID = 1  Address = 1 (PDU 0x0000, 1-based)  Quantity = 6",
                      "FC03  Unit ID = 1  Address = 223 (PDU 0x00DE, 1-based)  Quantity = 3"], tx);
        Assert.Contains(service.Logger.Entries, e => e.Message == "2534, 0, 4856, 0, 650, 0");
        Assert.Contains(service.Logger.Entries, e => e.Message == "15, 65511, 150");
        Assert.Equal(2, service.Logger.Entries.Count(e => e.Message.StartsWith("Response Time:")));
        Assert.DoesNotContain(service.Logger.Entries, e => e.Message.StartsWith("Result:"));

        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode(read.Results.Reverse(), A6V, _ => 1);   // completion order must not matter
        Assert.Equal([1, 2, 3, 4, 5, 6, 223, 224, 225], rows.Select(r => r.Address));
        Assert.Equal(["25.34", "No error", "48.56", "No error", "650", "No error", "1.5", "-2.5", "150"], rows.Select(r => r.ValueText));
    }

    [Fact]
    public async Task Address1_SingleRequestForRegister1()
    {
        var (service, slave) = await ConnectAsync(DeviceMap());
        await using var _ = slave;
        await using var __ = service;

        ProfileReadResult read = await ProfileReader.ReadAsync(service, ProfileReadPlanner.Plan(A6V, 1, AddressBase.OneBased, 1));

        Assert.Equal(["03 00 00 00 01"], slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..])));
        Assert.Equal(["Temperature value"], RegisterService.Decode(read.Results, A6V, _ => 1).Select(r => r.Description));
    }

    // Test 20: any failing group makes the whole read FAIL; later groups are not sent; nothing is invented
    [Fact]
    public async Task SecondGroupFails_WholeReadFails_FirstResultKept_LaterRequestsNotSent()
    {
        var (service, slave) = await ConnectAsync([2534, 0, 4856, 0, 650, 0]);   // slave without 223–225
        await using var _ = slave;
        await using var __ = service;
        ModbusRequest[] plan =
        [
            new(ModbusFunction.ReadHoldingRegisters, 1, 1, 6),
            new(ModbusFunction.ReadHoldingRegisters, 1, 223, 3),
            new(ModbusFunction.ReadHoldingRegisters, 1, 1, 1),
        ];

        ProfileReadResult read = await ProfileReader.ReadAsync(service, plan);

        Assert.False(read.IsSuccess);
        Assert.Equal(2, read.Results.Count);                                   // stopped after the failure
        Assert.Equal(2, slave.ReceivedAdus.Count);
        Assert.True(read.Results[0].IsSuccess);
        Assert.Equal(ResultStatus.ModbusException, read.Failure!.Status);
        Assert.Contains(service.Logger.Entries, e => e.Kind == LogKind.Fail && e.Message.Contains("Illegal Data Address"));
        Assert.Equal(ConnectionState.Connected, service.State);
    }
}
