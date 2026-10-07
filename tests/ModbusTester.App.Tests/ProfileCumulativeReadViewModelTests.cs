using ModbusTester.App.ViewModels;
using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>Read in the ViewModel: device profile = cumulative read "up to Address"; Generic = Address + Quantity as before.</summary>
public class ProfileCumulativeReadViewModelTests
{
    /// <summary>Registers 1–6 (document values) and 223–225 (offsets), plus room for Generic reads up to 231.</summary>
    private static ushort[] DeviceMap()
    {
        var registers = new ushort[231];
        new ushort[] { 2534, 0, 4856, 0, 650, 0 }.CopyTo(registers, 0);
        registers[222] = 15;
        registers[223] = unchecked((ushort)(short)-25);
        registers[224] = 150;
        return registers;
    }

    private static async Task<(MainViewModel Vm, FakeModbusTcpSlave Slave)> ConnectAsync(DeviceProfile profile, ushort[]? registers = null)
    {
        var slave = new FakeModbusTcpSlave { Registers = registers ?? DeviceMap() };
        var vm = new MainViewModel(null, TestVm.TempStore())
        {
            IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString(), SelectedDeviceProfile = profile,
        };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        return (vm, slave);
    }

    private static string[] Requests(FakeModbusTcpSlave slave) => [.. slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..]))];

    // Simulator list: 1→1 row, 2→2, 6→6, 223→7, 225→9, 300→9
    [Theory]
    [InlineData("1", new[] { 1 }, new[] { "03 00 00 00 01" })]
    [InlineData("2", new[] { 1, 2 }, new[] { "03 00 00 00 02" })]
    [InlineData("6", new[] { 1, 2, 3, 4, 5, 6 }, new[] { "03 00 00 00 06" })]
    [InlineData("100", new[] { 1, 2, 3, 4, 5, 6 }, new[] { "03 00 00 00 06" })]
    [InlineData("223", new[] { 1, 2, 3, 4, 5, 6, 223 }, new[] { "03 00 00 00 06", "03 00 DE 00 01" })]
    [InlineData("224", new[] { 1, 2, 3, 4, 5, 6, 223, 224 }, new[] { "03 00 00 00 06", "03 00 DE 00 02" })]
    [InlineData("225", new[] { 1, 2, 3, 4, 5, 6, 223, 224, 225 }, new[] { "03 00 00 00 06", "03 00 DE 00 03" })]
    [InlineData("300", new[] { 1, 2, 3, 4, 5, 6, 223, 224, 225 }, new[] { "03 00 00 00 06", "03 00 DE 00 03" })]
    public async Task Profile_AddressIsUpperBound(string address, int[] rows, string[] requests)
    {
        var (vm, slave) = await ConnectAsync(A6V12258321Profile.Profile);
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = address;

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.True(vm.LastResultPassed);
        Assert.Equal(rows, vm.Rows.Select(r => r.Address));
        Assert.Equal(requests, Requests(slave));
        Assert.All(vm.Rows, r => Assert.NotEqual("", r.Description));   // only profile-defined registers
        Assert.DoesNotContain(vm.LogEntries, e => e.Message.StartsWith("Result:"));
    }

    // Test 13: Quantity is not part of a profile read (even an invalid one does not block or change it)
    [Theory]
    [InlineData("1")]
    [InlineData("125")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task Profile_QuantityIgnored(string quantity)
    {
        var (vm, slave) = await ConnectAsync(A6V12258321Profile.Profile);
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "225";
        vm.QuantityText = quantity;

        Assert.True(vm.ReadCommand.CanExecute(null));
        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal(["03 00 00 00 06", "03 00 DE 00 03"], Requests(slave));
        Assert.Equal(9, vm.Rows.Count);
    }

    [Fact]
    public async Task Generic_InvalidQuantity_StillBlocksRead()
    {
        var (vm, slave) = await ConnectAsync(DeviceProfile.Generic);
        await using var _ = slave;
        await using var __ = vm;
        vm.QuantityText = "abc";
        Assert.False(vm.ReadCommand.CanExecute(null));
        vm.SelectedDeviceProfile = A6V12258321Profile.Profile;
        Assert.True(vm.ReadCommand.CanExecute(null));
    }

    // Test 11 + Test 17: Generic Address + Quantity, one request, 1-based and 0-based as before
    [Theory]
    [InlineData("1", AddressBase.OneBased, "03 00 00 00 06")]
    [InlineData("0", AddressBase.ZeroBased, "03 00 00 00 06")]
    public async Task Generic_AddressQuantity_SingleRequest(string address, AddressBase addressBase, string request)
    {
        var (vm, slave) = await ConnectAsync(DeviceProfile.Generic);
        await using var _ = slave;
        await using var __ = vm;
        vm.SelectedAddressBase = vm.AddressBases.Single(b => b.Value == addressBase);
        vm.StartAddressText = address;
        vm.QuantityText = "6";

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal([request], Requests(slave));
        Assert.Equal(6, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Equal("", r.Description));
    }

    // Test 12: Generic Address 223 Quantity 9 is registers 223–231, no profile logic
    [Fact]
    public async Task Generic_Address223Quantity9_IsOneRequest223To231()
    {
        var (vm, slave) = await ConnectAsync(DeviceProfile.Generic);
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "223";
        vm.QuantityText = "9";

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal(["03 00 DE 00 09"], Requests(slave));
        Assert.Equal(Enumerable.Range(223, 9), vm.Rows.Select(r => r.Address));
        Assert.All(vm.Rows, r => Assert.True(r.IsScaleEditable));
    }

    // Input History: profile reads record Address (and Default Scale) but never Quantity
    [Fact]
    public async Task History_ProfileRecordsAddressNotQuantity_GenericRecordsBoth()
    {
        var (vm, slave) = await ConnectAsync(A6V12258321Profile.Profile);
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "225";
        vm.QuantityText = "3";

        await vm.ReadCommand.ExecuteAsync(null);
        Assert.Equal(["225"], vm.AddressHistory.Items);
        Assert.Empty(vm.QuantityHistory.Items);
        Assert.Equal(["1"], vm.DefaultScaleHistory.Items);

        vm.SelectedDeviceProfile = DeviceProfile.Generic;
        vm.StartAddressText = "1";
        vm.QuantityText = "6";
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.Equal(["1", "225"], vm.AddressHistory.Items);
        Assert.Equal(["6"], vm.QuantityHistory.Items);
        Assert.Equal(["127.0.0.1"], vm.TcpHostHistory.Items);              // connection histories untouched
    }

    // Test 20 in the ViewModel: one failing group → whole read FAIL, rows keep the previous successful read
    [Fact]
    public async Task PartialFailure_IsFail_RowsNotReplaced()
    {
        var (vm, slave) = await ConnectAsync(A6V12258321Profile.Profile, [2534, 0, 4856, 0, 650, 0]);   // no 223–225
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "6";
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.True(vm.LastResultPassed);
        Assert.Equal(6, vm.Rows.Count);

        vm.StartAddressText = "225";
        await vm.ReadCommand.ExecuteAsync(null);

        Assert.False(vm.LastResultPassed);
        Assert.Equal("FAIL", vm.LastResult);
        Assert.Equal([1, 2, 3, 4, 5, 6], vm.Rows.Select(r => r.Address));      // previous read, nothing invented
        Assert.Equal(3, slave.ReceivedAdus.Count);                               // 1x6, then 1x6 + 223x3
        Assert.Contains(vm.LogEntries, e => e.Kind == LogKind.Fail && e.Message.Contains("Illegal Data Address"));
        Assert.Equal(ConnectionState.Connected, vm.ConnectionState);
        Assert.Equal(["6"], vm.AddressHistory.Items);                           // failed read not recorded
    }
}
