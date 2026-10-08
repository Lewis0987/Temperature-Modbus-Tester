using ModbusTester.App.ViewModels;
using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>
/// Phase 4C.1: a verified write updates the Registers table from the verify read back – only rows that already
/// exist, through the normal decode path, never after a failed / mismatched / cancelled / held write.
/// </summary>
public class RegisterTableSyncTests
{
    private static readonly DeviceProfile A6V = A6V12258321Profile.Profile;

    private static ushort[] DeviceMap()
    {
        var registers = new ushort[1300];
        new ushort[] { 2534, 0, 4856, 0, 650, 0 }.CopyTo(registers, 0);
        registers[222] = 15;
        registers[223] = unchecked((ushort)(short)-25);
        registers[224] = 150;
        return registers;
    }

    private static async Task<(MainViewModel Vm, FakeModbusTcpSlave Slave)> ConnectAsync(DeviceProfile profile, bool confirm = true)
    {
        var slave = new FakeModbusTcpSlave { Registers = DeviceMap() };
        var vm = new MainViewModel(null, TestVm.TempStore())
        {
            IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString(), SelectedDeviceProfile = profile,
        };
        vm.Write.Confirm = _ => confirm;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        return (vm, slave);
    }

    private static async Task ReadAsync(MainViewModel vm, string address, string? quantity = null)
    {
        vm.StartAddressText = address;
        if (quantity is not null)
            vm.QuantityText = quantity;
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.True(vm.LastResultPassed);
    }

    private static async Task WriteProfileAsync(MainViewModel vm, int register, string? value = null, string? state = null)
    {
        vm.Write.SelectedRegister = vm.Write.WritableRegisters.Single(r => r.Definition.RegisterNumber == register);
        if (value is not null) vm.Write.ValueText = value;
        if (state is not null) vm.Write.SelectedState = state;
        await vm.Write.WriteCommand.ExecuteAsync(null);
    }

    private static async Task WriteGenericAsync(MainViewModel vm, string address, string values, bool fc16 = false)
    {
        vm.Write.SelectedGenericFunction = vm.Write.GenericFunctions[fc16 ? 1 : 0];
        vm.Write.GenericAddressText = address;
        vm.Write.GenericValuesText = values;
        await vm.Write.WriteCommand.ExecuteAsync(null);
    }

    /// <summary>Everything a row shows.</summary>
    private static string Snapshot(RegisterRowViewModel r) =>
        $"{r.Address}|{r.Description}|{r.Raw}|{r.Decimal}|{r.Hex}|{r.Binary}|{r.ScaleText}|{r.ValueText}|{r.Unit}|{r.Quality}";

    private static string[] Table(MainViewModel vm) => [.. vm.Rows.Select(Snapshot)];

    private static int Requests(FakeModbusTcpSlave slave) => slave.ReceivedAdus.Count;

    // Tests 1, 2, 15
    [Fact]
    public async Task Profile_Row223_Updated_Rows1To6_Unchanged_SameAsRealRead()
    {
        var (vm, slave) = await ConnectAsync(A6V);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "223");
        string[] before = Table(vm);
        Assert.Equal([1, 2, 3, 4, 5, 6, 223], vm.Rows.Select(r => r.Address));

        await WriteProfileAsync(vm, 223, "2.0");

        Assert.Equal(WriteStatus.Completed, vm.Write.Status);
        Assert.Equal(before[..6], Table(vm)[..6]);                                   // 1–6 untouched
        RegisterRowViewModel row = vm.Rows.Single(r => r.Address == 223);
        Assert.Equal(20, row.Raw);
        Assert.Equal("0014", row.Hex);
        Assert.Equal("2", row.ValueText);
        Assert.Equal("Temperature offset", row.Description);
        Assert.Equal("°C", row.Unit);
        Assert.Equal("0.1", row.ScaleText);
        Assert.Equal(7, vm.Rows.Count);
        Assert.Equal(2 + 2, Requests(slave));                                      // read (2 groups) + FC06 + verify FC03 only

        string[] synced = Table(vm);
        await ReadAsync(vm, "223");                                                  // a real read gives identical rows
        Assert.Equal(synced, Table(vm));
    }

    // Test 3: the table only had 1–6 – 223 is not added
    [Fact]
    public async Task Profile_RowNotInTable_NotAdded()
    {
        var (vm, slave) = await ConnectAsync(A6V);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "6");
        string[] before = Table(vm);

        await WriteProfileAsync(vm, 223, "2.0");

        Assert.Equal(WriteStatus.Completed, vm.Write.Status);
        Assert.Equal(before, Table(vm));
        Assert.Equal(20, slave.Registers[222]);
    }

    [Fact]
    public async Task NoReadYet_TableStaysEmpty()
    {
        var (vm, slave) = await ConnectAsync(A6V);
        await using var _ = slave;
        await using var __ = vm;

        await WriteProfileAsync(vm, 223, "2.0");

        Assert.Equal(WriteStatus.Completed, vm.Write.Status);
        Assert.Empty(vm.Rows);
    }

    // Tests 4–5: Generic FC06
    [Fact]
    public async Task Generic_ExistingRowUpdated_OthersUnchanged()
    {
        var (vm, slave) = await ConnectAsync(DeviceProfile.Generic);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "222", "4");                                             // 222..225
        string[] before = Table(vm);

        await WriteGenericAsync(vm, "223", "0xFFE7");

        string[] after = Table(vm);
        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[2..], after[2..]);
        RegisterRowViewModel row = vm.Rows.Single(r => r.Address == 223);
        Assert.Equal(65511, row.Raw);
        Assert.Equal("FFE7", row.Hex);
        Assert.Equal("65511", row.ValueText);                                       // Generic: raw × manual scale, no °C
        Assert.Equal("", row.Unit);
    }

    [Fact]
    public async Task Generic_AddressNotInTable_NotAdded()
    {
        var (vm, slave) = await ConnectAsync(DeviceProfile.Generic);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "1", "6");
        string[] before = Table(vm);

        await WriteGenericAsync(vm, "223", "20");

        Assert.Equal(WriteStatus.Completed, vm.Write.Status);
        Assert.Equal(before, Table(vm));
    }

    [Fact]
    public async Task Generic_KeepsManualScaleOfTheRow()
    {
        var (vm, slave) = await ConnectAsync(DeviceProfile.Generic);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "223", "1");
        vm.Rows[0].Scale = 0.1;

        await WriteGenericAsync(vm, "223", "20");

        Assert.Equal("0.1", vm.Rows[0].ScaleText);
        Assert.Equal("2", vm.Rows[0].ValueText);
    }

    // Tests 6–7: FC16 updates the rows that exist, adds none
    [Fact]
    public async Task Fc16_UpdatesAllExistingRows()
    {
        var (vm, slave) = await ConnectAsync(DeviceProfile.Generic);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "223", "3");

        await WriteGenericAsync(vm, "223", "20,30,40", fc16: true);

        Assert.Equal([20, 30, 40], vm.Rows.Select(r => (int)r.Raw));
    }

    [Fact]
    public async Task Fc16_OnlyExistingRows()
    {
        var (vm, slave) = await ConnectAsync(DeviceProfile.Generic);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "223", "2");

        await WriteGenericAsync(vm, "223", "20,30,40", fc16: true);

        Assert.Equal([223, 224], vm.Rows.Select(r => r.Address));
        Assert.Equal([20, 30], vm.Rows.Select(r => (int)r.Raw));
        Assert.Equal(40, slave.Registers[224]);
    }

    [Fact]
    public async Task Fc16_ProfileTable_UpdatesProfileRowsThroughProfileDecode()
    {
        var (vm, slave) = await ConnectAsync(A6V);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "225");

        vm.SelectedDeviceProfile = DeviceProfile.Generic;                           // Generic FC16 on the same table
        await WriteGenericAsync(vm, "223", "20,30,40", fc16: true);
        vm.SelectedDeviceProfile = A6V;                                               // back: rows decoded by the profile

        Assert.Equal(["2", "3", "40"], vm.Rows.Where(r => r.Address >= 223).Select(r => r.ValueText));
    }

    // Tests 8–12: nothing changes unless the write is verified
    [Fact]
    public async Task Mismatch_DoesNotUpdate()
    {
        var (vm, slave) = await ConnectAsync(A6V);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "225");
        string[] before = Table(vm);
        slave.ResponseMutator = r => r[0] == 0x03 && r[1] == 2 ? [0x03, 0x02, 0x00, 0x63] : r;

        await WriteProfileAsync(vm, 223, "2.0");

        Assert.Equal(WriteStatus.VerificationMismatch, vm.Write.Status);
        Assert.Equal(before, Table(vm));
    }

    [Fact]
    public async Task VerifyTimeout_DoesNotUpdate()
    {
        var rtu = new FakeRtuSlave { Registers = DeviceMap() };
        bool silenceReads = false;
        var channel = new FakeSerialChannel { Responder = f => silenceReads && f[1] == 0x03 ? [] : rtu.Respond(f) };
        var vm = new MainViewModel(_ => channel, TestVm.TempStore())
        {
            IsRtu = true, SelectedComPort = "COM_TEST", RtuTimeoutText = "200", SelectedDeviceProfile = A6V,
        };
        await using var _ = vm;
        vm.Write.Confirm = _ => true;
        await vm.ConnectCommand.ExecuteAsync(null);
        await ReadAsync(vm, "225");
        string[] before = Table(vm);
        silenceReads = true;

        await WriteProfileAsync(vm, 223, "2.0");

        Assert.Equal(WriteStatus.VerificationFailed, vm.Write.Status);
        Assert.Equal(before, Table(vm));
    }

    [Fact]
    public async Task WriteException_DoesNotUpdate()
    {
        var (vm, slave) = await ConnectAsync(A6V);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "225");
        string[] before = Table(vm);
        slave.ForcedExceptionCode = 0x04;

        await WriteProfileAsync(vm, 223, "2.0");

        Assert.Equal(WriteStatus.WriteFailed, vm.Write.Status);
        Assert.Equal(before, Table(vm));
    }

    [Fact]
    public async Task Cancel_DoesNotUpdate()
    {
        var (vm, slave) = await ConnectAsync(A6V, confirm: false);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "225");
        string[] before = Table(vm);

        await WriteProfileAsync(vm, 223, "2.0");

        Assert.Equal(WriteStatus.Cancelled, vm.Write.Status);
        Assert.Equal(before, Table(vm));
    }

    [Fact]
    public async Task WriteOnHold_DoesNotUpdate()
    {
        var (vm, slave) = await ConnectAsync(A6V);
        await using var _ = slave;
        await using var __ = vm;
        slave.Registers[763] = 1;
        await ReadAsync(vm, "766");
        string[] before = Table(vm);

        await WriteProfileAsync(vm, 764, "5");

        Assert.Equal(WriteStatus.WriteOnHold, vm.Write.Status);
        Assert.Equal(before, Table(vm));
    }

    [Fact]
    public async Task Fc04Table_IsNotUpdatedByAHoldingRegisterWrite()
    {
        var (vm, slave) = await ConnectAsync(DeviceProfile.Generic);
        await using var _ = slave;
        await using var __ = vm;
        vm.SelectedFunction = vm.Functions[1];                                        // FC04 input registers
        await ReadAsync(vm, "223", "1");
        string[] before = Table(vm);

        await WriteGenericAsync(vm, "223", "20");

        Assert.Equal(WriteStatus.Completed, vm.Write.Status);
        Assert.Equal(before, Table(vm));
    }

    // Tests 13–14: 401 only changes row 401; Register 1 / 223 keep °C
    [Fact]
    public async Task SystemUnit_OnlyRow401()
    {
        var (vm, slave) = await ConnectAsync(A6V);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "401");
        string[] before = Table(vm);

        await WriteProfileAsync(vm, 401, state: "Fahrenheit");

        string[] after = Table(vm);
        int i401 = vm.Rows.IndexOf(vm.Rows.Single(r => r.Address == 401));
        Assert.Equal(before.Where((_, i) => i != i401), after.Where((_, i) => i != i401));
        Assert.Equal("Fahrenheit", vm.Rows[i401].ValueText);
        Assert.Equal("1", vm.Rows[i401].Raw.ToString());
        Assert.Equal("°C", vm.Rows.Single(r => r.Address == 1).Unit);
        Assert.Equal("25.34", vm.Rows.Single(r => r.Address == 1).ValueText);
        Assert.Equal("°C", vm.Rows.Single(r => r.Address == 223).Unit);
        Assert.Equal("1.5", vm.Rows.Single(r => r.Address == 223).ValueText);

        // Phase 3E: the next real read labels Reg 1 / 223 by 401 = 1 (°F). The value is still raw × scale – the fake
        // slave does not change the raw value (on the real sensor the device does that itself).
        await ReadAsync(vm, "401");
        Assert.Equal("Fahrenheit", vm.Rows.Single(r => r.Address == 401).ValueText);
        Assert.Equal(("°F", "25.34"), (vm.Rows.Single(r => r.Address == 1).Unit, vm.Rows.Single(r => r.Address == 1).ValueText));
        Assert.Equal(("°F", "1.5"), (vm.Rows.Single(r => r.Address == 223).Unit, vm.Rows.Single(r => r.Address == 223).ValueText));
    }

    // A verified 401 write must not relabel values that were read under the old unit (profile switch rebuild)
    [Fact]
    public async Task SystemUnitWrite_RebuildKeepsConsistentSnapshot()
    {
        var (vm, slave) = await ConnectAsync(A6V);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "401");
        string[] before = Table(vm);

        await WriteProfileAsync(vm, 401, state: "Fahrenheit");
        vm.SelectedDeviceProfile = DeviceProfile.Generic;
        vm.SelectedDeviceProfile = A6V;

        Assert.Equal(before, Table(vm));                                             // 401 Celsius + Reg 1 °C, as read
        Assert.DoesNotContain(vm.Rows, r => r.Unit == "°F");
    }

    // Test 16: the read flow itself is unchanged
    [Fact]
    public async Task Read_AfterSync_StillReadsAndReplacesTable()
    {
        var (vm, slave) = await ConnectAsync(A6V);
        await using var _ = slave;
        await using var __ = vm;
        await ReadAsync(vm, "225");
        await WriteProfileAsync(vm, 224, "3.5");
        slave.Registers[224] = 160;                                                  // device changed meanwhile

        await ReadAsync(vm, "6");

        Assert.Equal([1, 2, 3, 4, 5, 6], vm.Rows.Select(r => r.Address));
        await ReadAsync(vm, "225");
        Assert.Equal(["1.5", "3.5", "160"], vm.Rows.Where(r => r.Address >= 223).Select(r => r.ValueText));
    }
}
