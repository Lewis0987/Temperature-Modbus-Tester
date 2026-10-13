using ModbusTester.App.ViewModels;
using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Services;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>
/// Phase 5B: Generic 32-bit read end to end – fake slave → response registers → ViewModel rows. Data type / order only
/// change the decoding; Quantity is always registers and the function code is unchanged.
/// </summary>
public class Generic32ReadViewModelTests
{
    private static ushort[] Map(int pduStart, params ushort[] words)
    {
        var registers = new ushort[300];
        words.CopyTo(registers, pduStart);
        return registers;
    }

    private static async Task<(MainViewModel Vm, FakeModbusTcpSlave Slave)> ConnectAsync(ushort[] registers, DeviceProfile? profile = null)
    {
        var slave = new FakeModbusTcpSlave { Registers = registers };
        var vm = new MainViewModel(null, TestVm.TempStore())
        {
            IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString(), SelectedDeviceProfile = profile ?? DeviceProfile.Generic,
        };
        vm.Write.Confirm = _ => true;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        return (vm, slave);
    }

    private static void Set(MainViewModel vm, DataType type, Register32Order order = Register32Order.ABCD)
    {
        vm.SelectedGenericDataType = vm.GenericDataTypes.Single(t => t.Value == type);
        vm.SelectedRegister32Order = vm.Register32Orders.Single(o => o.Value == order);
    }

    private static async Task ReadAsync(MainViewModel vm, string address, string quantity, string scale = "1")
    {
        vm.StartAddressText = address;
        vm.QuantityText = quantity;
        vm.DefaultScaleText = scale;
        await vm.ReadCommand.ExecuteAsync(null);
    }

    private static string[] Requests(FakeModbusTcpSlave slave) => [.. slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..]))];

    private static string Row(RegisterRowViewModel r) => $"{r.Address}|{r.RawText}|{r.Decimal}|{r.Hex}|{r.ValueText}|{r.Quality}";

    // UInt16 (default) – exactly the original Generic rows
    [Fact]
    public async Task Default_IsUInt16_SameRowsAsBefore()
    {
        var (vm, slave) = await ConnectAsync(Map(99, 0x1234, 0x5678, 0xFFFE));
        await using var _ = slave;
        await using var __ = vm;
        Assert.Equal(DataType.UInt16, vm.SelectedGenericDataType.Value);
        Assert.Equal(Register32Order.ABCD, vm.SelectedRegister32Order.Value);
        Assert.False(vm.IsOrderEnabled);

        await ReadAsync(vm, "100", "3");

        Assert.True(vm.LastResultPassed);
        Assert.Equal(["100|4660|4660|1234|4660|Ok", "101|22136|22136|5678|22136|Ok", "102|65534|65534|FFFE|65534|Ok"], vm.Rows.Select(Row));
        ModbusResult result = new()
        {
            Request = new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, 100, 3), Status = ResultStatus.Pass, Registers = [0x1234, 0x5678, 0xFFFE],
        };
        Assert.Equal(RegisterService.Decode(result, DeviceProfile.Generic, _ => 1).Select(r => r.ValueText), vm.Rows.Select(r => r.ValueText));
    }

    [Fact]
    public async Task Int16_Signed()
    {
        var (vm, slave) = await ConnectAsync(Map(99, 0xFFFE, 0x0005));
        await using var _ = slave;
        await using var __ = vm;
        Set(vm, DataType.Int16);
        Assert.False(vm.IsOrderEnabled);

        await ReadAsync(vm, "100", "2");

        Assert.Equal(["-2", "5"], vm.Rows.Select(r => r.ValueText));
    }

    // 32-bit: 2 registers → 1 row at the first register's address
    [Fact]
    public async Task UInt32_ABCD_OneRow()
    {
        var (vm, slave) = await ConnectAsync(Map(99, 0x1234, 0x5678));
        await using var _ = slave;
        await using var __ = vm;
        Set(vm, DataType.UInt32);
        Assert.True(vm.IsOrderEnabled);

        await ReadAsync(vm, "100", "2");

        Assert.Equal(["03 00 63 00 02"], Requests(slave));                // Quantity = registers
        RegisterRowViewModel r = Assert.Single(vm.Rows);
        Assert.Equal("100|1234 5678|305419896|12345678|305419896|Ok", Row(r));
        Assert.Equal("0001 0010 0011 0100 0101 0110 0111 1000", r.Binary);
        Assert.Equal(2, r.RegisterCount);
    }

    [Fact]
    public async Task Int32_TwosComplement()
    {
        var (vm, slave) = await ConnectAsync(Map(99, 0xFFFF, 0xFFFF, 0x8000, 0x0000));
        await using var _ = slave;
        await using var __ = vm;
        Set(vm, DataType.Int32);

        await ReadAsync(vm, "100", "4");

        Assert.Equal(["-1", "-2147483648"], vm.Rows.Select(r => r.ValueText));
        Assert.Equal([100, 102], vm.Rows.Select(r => r.Address));
    }

    // Float32 1.0 in each order, as the bytes appear on the wire
    [Theory]
    [InlineData(Register32Order.ABCD, 0x3F80, 0x0000)]
    [InlineData(Register32Order.BADC, 0x803F, 0x0000)]
    [InlineData(Register32Order.CDAB, 0x0000, 0x3F80)]
    [InlineData(Register32Order.DCBA, 0x0000, 0x803F)]
    public async Task Float32_FourOrders(Register32Order order, int first, int second)
    {
        var (vm, slave) = await ConnectAsync(Map(99, (ushort)first, (ushort)second));
        await using var _ = slave;
        await using var __ = vm;
        Set(vm, DataType.Float32, order);

        await ReadAsync(vm, "100", "2");

        RegisterRowViewModel r = Assert.Single(vm.Rows);
        Assert.Equal(("1", "3F800000", $"{first:X4} {second:X4}"), (r.ValueText, r.Hex, r.RawText));
    }

    // Quantity 3 with Float32: one value + one Incomplete row; the wire quantity stays 3
    [Fact]
    public async Task OddQuantity_LastWordIncomplete()
    {
        var (vm, slave) = await ConnectAsync(Map(99, 0x3F80, 0x0000, 0x4120));
        await using var _ = slave;
        await using var __ = vm;
        Set(vm, DataType.Float32);

        await ReadAsync(vm, "100", "3");

        Assert.True(vm.LastResultPassed);
        Assert.Equal(["03 00 63 00 03"], Requests(slave));
        Assert.Equal(["100|3F80 0000|1065353216|3F800000|1|Ok", "102|16672|16672|4120|Incomplete|Incomplete"], vm.Rows.Select(Row));
        Assert.Null(vm.Rows[1].Value);
    }

    [Fact]
    public async Task Quantity10_FiveFloats()
    {
        var (vm, slave) = await ConnectAsync(Map(99, 0x3F80, 0, 0x4000, 0, 0x4040, 0, 0x4080, 0, 0x40A0, 0));
        await using var _ = slave;
        await using var __ = vm;
        Set(vm, DataType.Float32);

        await ReadAsync(vm, "100", "10");

        Assert.Equal(["03 00 63 00 0A"], Requests(slave));               // 10 registers, not 20
        Assert.Equal(["1", "2", "3", "4", "5"], vm.Rows.Select(r => r.ValueText));
        Assert.Equal([100, 102, 104, 106, 108], vm.Rows.Select(r => r.Address));
    }

    // NaN / ±Infinity: shown as text, InvalidValue, no value – and the read itself is a success
    [Theory]
    [InlineData(0x7FC0, "NaN")]
    [InlineData(0x7F80, "+Infinity")]
    [InlineData(0xFF80, "-Infinity")]
    public async Task Float32_NonFinite_NotACommunicationError(int first, string text)
    {
        var (vm, slave) = await ConnectAsync(Map(99, (ushort)first, 0x0000));
        await using var _ = slave;
        await using var __ = vm;
        Set(vm, DataType.Float32);

        await ReadAsync(vm, "100", "2", scale: "0.1");

        Assert.True(vm.LastResultPassed);
        RegisterRowViewModel r = Assert.Single(vm.Rows);
        Assert.Equal((text, RegisterQuality.InvalidValue), (r.ValueText, r.Quality));
        Assert.Null(r.Value);
        Assert.DoesNotContain(vm.LogEntries, e => e.Kind == LogKind.Fail);
    }

    // Scale: decoded × scale; Raw / HEX / Binary unchanged; Apply to all rows and per-row edit keep the 32-bit decoding
    [Fact]
    public async Task Scale_DefaultApplyAndRowEdit()
    {
        var (vm, slave) = await ConnectAsync(Map(99, 0x3FC0, 0x0000, 0x0000, 0x0064));
        await using var _ = slave;
        await using var __ = vm;
        vm.SelectedGenericDataType = vm.GenericDataTypes.Single(t => t.Value == DataType.Float32);

        await ReadAsync(vm, "100", "2", scale: "0.1");
        Assert.Equal(("0.15", "3FC00000", "3FC0 0000"), (vm.Rows[0].ValueText, vm.Rows[0].Hex, vm.Rows[0].RawText));

        vm.Rows[0].ScaleText = "10";
        Assert.Equal(("15", "3FC00000", Register32Order.ABCD), (vm.Rows[0].ValueText, vm.Rows[0].Hex, vm.SelectedRegister32Order.Value));

        Set(vm, DataType.UInt32);
        await ReadAsync(vm, "102", "2", scale: "1");
        Assert.Equal("100", vm.Rows[0].ValueText);                       // 0x00000064
        vm.DefaultScaleText = "0.5";
        vm.ApplyDefaultScaleCommand.Execute(null);
        Assert.Equal(("50", "00000064"), (vm.Rows[0].ValueText, vm.Rows[0].Hex));
    }

    // Address base: rows show the user address of the first register; the wire address is the same
    [Theory]
    [InlineData(AddressBase.OneBased, "100", 100)]
    [InlineData(AddressBase.ZeroBased, "99", 99)]
    public async Task AddressBase_NoOffByOne(AddressBase addressBase, string address, int rowAddress)
    {
        var (vm, slave) = await ConnectAsync(Map(99, 0x3F80, 0x0000, 0x4000, 0x0000));
        await using var _ = slave;
        await using var __ = vm;
        vm.SelectedAddressBase = vm.AddressBases.Single(b => b.Value == addressBase);
        Set(vm, DataType.Float32);

        await ReadAsync(vm, address, "4");

        Assert.Equal(["03 00 63 00 04"], Requests(slave));
        Assert.Equal([rowAddress, rowAddress + 2], vm.Rows.Select(r => r.Address));
        Assert.Equal([(ushort)99, (ushort)101], vm.Rows.Select(r => r.PduAddress));
        Assert.Equal(["1", "2"], vm.Rows.Select(r => r.ValueText));
    }

    // FC04: same decoding, function code unchanged
    [Fact]
    public async Task Fc04_SameDecoding()
    {
        var (vm, slave) = await ConnectAsync(Map(99, 0x0000, 0x3F80));
        await using var _ = slave;
        await using var __ = vm;
        vm.SelectedFunction = vm.Functions[1];
        Set(vm, DataType.Float32, Register32Order.CDAB);

        await ReadAsync(vm, "100", "2");

        Assert.Equal(["04 00 63 00 02"], Requests(slave));
        Assert.Equal("1", Assert.Single(vm.Rows).ValueText);
    }

    // RTU: the same decode path as TCP
    [Fact]
    public async Task Rtu_SameRows()
    {
        var rtu = new FakeRtuSlave { Registers = Map(99, 0x1234, 0x5678, 0xFFFF) };
        var channel = new FakeSerialChannel { Responder = rtu.Respond };
        var vm = new MainViewModel(_ => channel, TestVm.TempStore()) { IsRtu = true, SelectedComPort = "COM_TEST" };
        await using var _ = vm;
        await vm.ConnectCommand.ExecuteAsync(null);
        Set(vm, DataType.UInt32);

        await ReadAsync(vm, "100", "3");

        Assert.Equal(["01 03 00 63 00 03 F5 D5"], rtu.Requests.Select(HexFormatter.Format));
        Assert.Equal(["100|1234 5678|305419896|12345678|305419896|Ok", "102|65535|65535|FFFF|Incomplete|Incomplete"], vm.Rows.Select(Row));
    }

    // Changing data type / order re-decodes the last read – no new request
    [Fact]
    public async Task ChangingTypeOrOrder_RedecodesWithoutReading()
    {
        var (vm, slave) = await ConnectAsync(Map(99, 0x1234, 0x5678));
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "100", "2");
        Assert.Equal(2, vm.Rows.Count);

        Set(vm, DataType.UInt32);
        Assert.Equal("305419896", Assert.Single(vm.Rows).ValueText);
        vm.SelectedRegister32Order = vm.Register32Orders.Single(o => o.Value == Register32Order.CDAB);
        Assert.Equal("56781234", Assert.Single(vm.Rows).Hex);
        vm.SelectedGenericDataType = vm.GenericDataTypes[0];
        Assert.Equal(["4660", "22136"], vm.Rows.Select(r => r.ValueText));
        Assert.Single(slave.ReceivedAdus);
    }

    // Profile isolation: Generic Float32 / CDAB never reaches a profile read; the Generic choice is kept for later
    [Fact]
    public async Task ProfileIsolation_AndGenericChoiceKept()
    {
        var registers = new ushort[1300];
        new ushort[] { 2534, 0, 4856, 0, 650, 0 }.CopyTo(registers, 0);
        registers[222] = 15;
        var (vm, slave) = await ConnectAsync(registers);
        await using var _ = slave;
        await using var __ = vm;
        Set(vm, DataType.Float32, Register32Order.CDAB);

        vm.SelectedDeviceProfile = A6V12258321Profile.Profile;
        Assert.False(vm.IsOrderEnabled);
        vm.StartAddressText = "1287";
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.Equal(15, vm.Rows.Count);
        Assert.Equal(5, slave.ReceivedAdus.Count);
        Assert.Equal(("25.34", "°C"), (vm.Rows[0].ValueText, vm.Rows[0].Unit));
        Assert.All(vm.Rows, r => Assert.Equal(1, r.RegisterCount));

        vm.SelectedDeviceProfile = DeviceProfile.Generic;
        Assert.Equal((DataType.Float32, Register32Order.CDAB), (vm.SelectedGenericDataType.Value, vm.SelectedRegister32Order.Value));
        Assert.True(vm.IsOrderEnabled);
        // The same read (requests 1×6, 223×3, 401×1, 764×3, 1286×2), now decoded as Generic Float32 per request:
        // pairs of words, an odd request ends with an Incomplete word; no profile meaning remains.
        Assert.Equal([1, 3, 5, 223, 225, 401, 764, 766, 1286], vm.Rows.Select(r => r.Address));
        Assert.Equal([2, 2, 2, 2, 1, 1, 2, 1, 2], vm.Rows.Select(r => r.RegisterCount));
        Assert.Equal([225, 401, 766], vm.Rows.Where(r => r.Quality == RegisterQuality.Incomplete).Select(r => r.Address));
        Assert.All(vm.Rows, r => Assert.Equal("", r.Description));
    }

    // Write row sync: a write to either word of a 32-bit row re-decodes it from both current words
    [Theory]
    [InlineData(0x1234, "101", "0x0000", "1")]          // second word only: 3F80 1234 → 3F80 0000 = 1
    [InlineData(0x0000, "100", "0x4000", "2")]          // first word only: 3F80 0000 → 4000 0000 = 2
    [InlineData(0x1234, "100", "0x4000", "2.001111")]   // first word only, the unchanged second word is kept: 4000 1234
    public async Task WriteSync_EitherWord_FullRedecode(int secondWord, string address, string value, string expected)
    {
        var (vm, slave) = await ConnectAsync(Map(99, 0x3F80, (ushort)secondWord));
        await using var _ = slave;
        await using var __ = vm;
        Set(vm, DataType.Float32);
        await ReadAsync(vm, "100", "2");
        Assert.NotEqual(expected, vm.Rows[0].ValueText);

        vm.Write.GenericAddressText = address;
        vm.Write.GenericValuesText = value;
        await vm.Write.WriteCommand.ExecuteAsync(null);

        Assert.Equal(WriteStatus.Completed, vm.Write.Status);
        RegisterRowViewModel r = Assert.Single(vm.Rows);
        Assert.Equal(expected, r.ValueText);
        Assert.Equal(100, r.Address);
    }

    [Fact]
    public async Task WriteSync_Fc16BothWords()
    {
        var (vm, slave) = await ConnectAsync(Map(99, 0x0000, 0x0000, 0x0007));
        await using var _ = slave;
        await using var __ = vm;
        Set(vm, DataType.UInt32);
        await ReadAsync(vm, "100", "3");

        vm.Write.SelectedGenericFunction = vm.Write.GenericFunctions[1];
        vm.Write.GenericAddressText = "100";
        vm.Write.GenericValuesText = "0x1234,0x5678";
        await vm.Write.WriteCommand.ExecuteAsync(null);

        Assert.Equal(["305419896", "Incomplete"], vm.Rows.Select(r => r.ValueText));
        Assert.Equal("12345678", vm.Rows[0].Hex);
    }

    [Fact]
    public async Task WriteSync_UInt16_Unchanged()
    {
        var (vm, slave) = await ConnectAsync(Map(99, 1, 2, 3));
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "100", "3");

        vm.Write.GenericAddressText = "101";
        vm.Write.GenericValuesText = "20";
        await vm.Write.WriteCommand.ExecuteAsync(null);

        Assert.Equal(["1", "20", "3"], vm.Rows.Select(r => r.ValueText));
    }
}
