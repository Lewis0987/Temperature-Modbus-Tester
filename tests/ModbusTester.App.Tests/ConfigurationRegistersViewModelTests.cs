using ModbusTester.App.ViewModels;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>Phase 3C in the ViewModel: registers 401 and 764–766 through the profile; Generic stays numeric.</summary>
public class ConfigurationRegistersViewModelTests
{
    /// <summary>Same layout as the simulator: 1–6, 223–225, 401, 764–766 (plus zeros elsewhere for Generic reads).</summary>
    private static ushort[] DeviceMap()
    {
        var registers = new ushort[770];
        new ushort[] { 2534, 0, 4856, 0, 650, 0 }.CopyTo(registers, 0);
        registers[222] = 15;
        registers[223] = unchecked((ushort)(short)-25);
        registers[224] = 150;
        registers[400] = 0;     // 401 Celsius
        registers[763] = 1;     // 764 Modbus address 1
        registers[764] = 1;     // 765 9600bps
        registers[765] = 0;     // 766 1-8-E-1
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

    private static string[] Requests(FakeModbusTcpSlave slave) => [.. slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..]))];

    [Theory]
    [InlineData("401", new[] { 1, 2, 3, 4, 5, 6, 223, 224, 225, 401 })]
    [InlineData("764", new[] { 1, 2, 3, 4, 5, 6, 223, 224, 225, 401, 764 })]
    [InlineData("765", new[] { 1, 2, 3, 4, 5, 6, 223, 224, 225, 401, 764, 765 })]
    [InlineData("766", new[] { 1, 2, 3, 4, 5, 6, 223, 224, 225, 401, 764, 765, 766 })]
    public async Task Profile_CumulativeRowsIncludeConfiguration(string address, int[] rows)
    {
        var (vm, slave) = await ConnectAsync(A6V12258321Profile.Profile);
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = address;

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.True(vm.LastResultPassed);
        Assert.Equal(rows, vm.Rows.Select(r => r.Address));
    }

    [Fact]
    public async Task Profile_Address766_FourFc03Requests_ConfigurationDecoded()
    {
        var (vm, slave) = await ConnectAsync(A6V12258321Profile.Profile);
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = "766";

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal(["03 00 00 00 06", "03 00 DE 00 03", "03 01 90 00 01", "03 02 FB 00 03"], Requests(slave));
        Assert.Equal(13, vm.Rows.Count);
        RegisterRowViewModel[] config = [.. vm.Rows.Skip(9)];
        Assert.Equal(["System unit", "Modbus address", "Baud rate", "Transmission format"], config.Select(r => r.Description));
        Assert.Equal(["Celsius", "1", "9600bps", "1-8-E-1"], config.Select(r => r.ValueText));
        Assert.Equal(["-", "-", "-", "-"], config.Select(r => r.ScaleText));
        Assert.All(config, r => Assert.False(r.IsScaleEditable));
        Assert.DoesNotContain(vm.LogEntries, e => e.Message.StartsWith("Result:"));
    }

    // Generic isolation: Address 765, Raw 1, Scale 1 → "1", never "9600bps"
    [Theory]
    [InlineData("401", "0")]
    [InlineData("765", "1")]
    [InlineData("766", "0")]
    public async Task Generic_ConfigurationAddresses_ShowNumbers(string address, string value)
    {
        var (vm, slave) = await ConnectAsync(DeviceProfile.Generic);
        await using var _ = slave;
        await using var __ = vm;
        vm.StartAddressText = address;
        vm.QuantityText = "1";
        vm.DefaultScaleText = "1";

        await vm.ReadCommand.ExecuteAsync(null);

        RegisterRowViewModel row = vm.Rows.Single();
        Assert.Equal(value, row.ValueText);
        Assert.Equal("1", row.ScaleText);
        Assert.Equal("", row.Description);
        Assert.True(row.IsScaleEditable);
    }
}
