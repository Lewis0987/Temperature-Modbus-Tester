using ModbusTester.App.ViewModels;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>Phase 3D in the ViewModel: Address 1287 reads 15 rows in 5 FC03 requests; Generic stays manual.</summary>
public class VersionRegistersViewModelTests
{
    /// <summary>Same values as the simulator for every profile register; zeros elsewhere (for Generic reads).</summary>
    private static ushort[] DeviceMap()
    {
        var registers = new ushort[1290];
        new ushort[] { 2534, 0, 4856, 0, 650, 0 }.CopyTo(registers, 0);
        registers[222] = 15;
        registers[223] = unchecked((ushort)(short)-25);
        registers[224] = 150;
        registers[763] = 1;
        registers[764] = 1;
        registers[1285] = 258;
        registers[1286] = 123;
        return registers;
    }

    private static async Task<(MainViewModel Vm, FakeModbusTcpSlave Slave)> ConnectAsync(DeviceProfile profile)
    {
        var slave = new FakeModbusTcpSlave { Registers = DeviceMap() };
        var vm = new MainViewModel(null, TestVm.TempStore())
        {
            IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString(), SelectedDeviceProfile = profile,
        };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        return (vm, slave);
    }

    [Theory]
    [InlineData("1286", 14)]
    [InlineData("1287", 15)]
    public async Task Profile_CumulativeRowCount(string address, int rows)
    {
        var (vm, slave) = await ConnectAsync(A6V12258321Profile.Profile);
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = address;

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.True(vm.LastResultPassed);
        Assert.Equal(rows, vm.Rows.Count);
    }

    [Fact]
    public async Task Profile_Address1287_FiveFc03_VersionRowsRaw()
    {
        var (vm, slave) = await ConnectAsync(A6V12258321Profile.Profile);
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "1287";

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal(["03 00 00 00 06", "03 00 DE 00 03", "03 01 90 00 01", "03 02 FB 00 03", "03 05 05 00 02"],
            slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..])));
        Assert.Equal([1, 2, 3, 4, 5, 6, 223, 224, 225, 401, 764, 765, 766, 1286, 1287], vm.Rows.Select(r => r.Address));
        RegisterRowViewModel[] version = [.. vm.Rows.Skip(13)];
        Assert.Equal(["SW version: Major & Minor versions", "SW version: Build version"], version.Select(r => r.Description));
        Assert.Equal([(ushort)258, (ushort)123], version.Select(r => r.Raw));
        Assert.Equal([258L, 123L], version.Select(r => r.Decimal));
        Assert.Equal(["0102", "007B"], version.Select(r => r.Hex));
        Assert.Equal(["-", "-"], version.Select(r => r.ScaleText));
        Assert.Equal(["258", "123"], version.Select(r => r.ValueText));
        Assert.Equal(["", ""], version.Select(r => r.Unit));
        Assert.All(version, r => Assert.False(r.IsScaleEditable));
    }

    [Fact]
    public async Task Generic_Address1286Quantity2_Scale0_01()
    {
        var (vm, slave) = await ConnectAsync(DeviceProfile.Generic);
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "1286";
        vm.QuantityText = "2";
        vm.DefaultScaleText = "0.01";

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal(["03 05 05 00 02"], slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..])));
        Assert.Equal(["2.58", "1.23"], vm.Rows.Select(r => r.ValueText));
        Assert.All(vm.Rows, r => Assert.Equal("", r.Description));
        Assert.All(vm.Rows, r => Assert.True(r.IsScaleEditable));
    }
}
