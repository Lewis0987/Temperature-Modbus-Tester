using ModbusTester.App.Validation;
using ModbusTester.App.ViewModels;
using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Tests;

namespace ModbusTester.App.Tests;

/// <summary>Phase 4C: Register Write area (ViewModel). Profile = metadata-driven FC06; Generic = raw FC06 / FC16.</summary>
public class RegisterWriteViewModelTests
{
    private static readonly DeviceProfile A6V = A6V12258321Profile.Profile;

    /// <summary>Slave with registers up to 1300 (PDU-indexed), offsets 15 / -25 / 150 as on the simulator.</summary>
    private static ushort[] DeviceMap()
    {
        var registers = new ushort[1300];
        registers[222] = 15;
        registers[223] = unchecked((ushort)(short)-25);
        registers[224] = 150;
        return registers;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required MainViewModel Vm { get; init; }
        public FakeModbusTcpSlave? Slave { get; init; }
        public List<WriteConfirmation> Confirmations { get; } = [];
        public bool Answer { get; set; } = true;
        public RegisterWriteViewModel W => Vm.Write;

        public string[] Requests() => Slave is null ? [] : [.. Slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..]))];

        public async ValueTask DisposeAsync()
        {
            await Vm.DisposeAsync();
            if (Slave is not null)
                await Slave.DisposeAsync();
        }
    }

    private static async Task<Harness> ConnectAsync(DeviceProfile profile, ushort[]? registers = null, AddressBase addressBase = AddressBase.OneBased)
    {
        var slave = new FakeModbusTcpSlave { Registers = registers ?? DeviceMap() };
        var vm = new MainViewModel(null, TestVm.TempStore())
        {
            IsTcp = true, HostText = "127.0.0.1", PortText = slave.Port.ToString(), SelectedDeviceProfile = profile,
        };
        vm.SelectedAddressBase = vm.AddressBases.Single(b => b.Value == addressBase);
        var h = new Harness { Vm = vm, Slave = slave };
        vm.Write.Confirm = c => { h.Confirmations.Add(c); return h.Answer; };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        if (!profile.IsGeneric)
        {
            // Like a user before a profile write: read up to 401 so the System unit of this device is known
            // (Phase 4F-1.1). That read is not part of what the tests below count.
            vm.StartAddressText = "401";
            await vm.ReadCommand.ExecuteAsync(null);
            Assert.True(vm.LastResultPassed);
            slave.ReceivedAdus.Clear();
        }
        return h;
    }

    private static Harness Offline(DeviceProfile profile)
    {
        var vm = new MainViewModel(null, TestVm.TempStore()) { IsTcp = true, SelectedDeviceProfile = profile };
        var h = new Harness { Vm = vm };
        vm.Write.Confirm = c => { h.Confirmations.Add(c); return h.Answer; };
        return h;
    }

    private static void Select(RegisterWriteViewModel w, int register) =>
        w.SelectedRegister = w.WritableRegisters.Single(r => r.Definition.RegisterNumber == register);

    // ---- Profile UI (Tests 1–20) ----

    // Tests 1–2: only the registers the metadata marks writable
    [Fact]
    public void Profile_ListsOnlyWritableRegisters()
    {
        Harness h = Offline(A6V);
        Assert.Equal([223, 224, 225, 401, 764, 765, 766], h.W.WritableRegisters.Select(r => r.Definition.RegisterNumber));
        Assert.Equal(["223 Temperature offset", "224 Relative humidity offset", "225 CO2 offset", "401 System unit",
                      "764 Modbus address", "765 Baud rate", "766 Transmission format"], h.W.WritableRegisters.Select(r => r.Text));
        Assert.DoesNotContain(h.W.WritableRegisters, r => r.Definition.RegisterNumber is >= 1 and <= 6 or 1286 or 1287);
        Assert.True(h.W.IsProfileMode);
        Assert.Equal("FC06 - Write Single Register", Assert.Single(h.W.ProfileFunctions).Text);
    }

    // Tests 3–8: numeric input, preview straight from the Core
    [Theory]
    [InlineData(223, "1.5", "1.5 °C", "15", "000F", "0x00DE")]
    [InlineData(223, "-2.5", "-2.5 °C", "65511", "FFE7", "0x00DE")]
    [InlineData(224, "2.5", "2.5 %", "25", "0019", "0x00DF")]
    [InlineData(224, "-100", "-100 %", "64536", "FC18", "0x00DF")]
    [InlineData(225, "150", "150 ppm", "150", "0096", "0x00E0")]
    [InlineData(225, "-2000", "-2000 ppm", "63536", "F830", "0x00E0")]
    public async Task Profile_NumericPreview(int register, string input, string engineering, string raw, string hex, string pdu)
    {
        await using Harness h = await ConnectAsync(A6V);
        Select(h.W, register);
        h.W.ValueText = input;

        Assert.True(h.W.ShowNumericInput);
        Assert.False(h.W.ShowStateInput);
        Assert.Equal(engineering, h.W.PreviewEngineering);
        Assert.Equal(raw, h.W.PreviewRaw);
        Assert.Equal(hex, h.W.PreviewHex);
        Assert.Equal(pdu, h.W.PreviewPdu);
        Assert.Equal(WriteStatus.Ready, h.W.Status);
        Assert.Equal("", h.W.ValidationMessage);
    }

    [Theory]
    [InlineData(223, "°C", "-100 ~ 100", "0.1")]
    [InlineData(224, "%", "-100 ~ 100", "0.1")]
    [InlineData(225, "ppm", "-2000 ~ 2000", "1")]
    [InlineData(764, "", "1 ~ 247", "-")]
    public async Task Profile_ShowsMetadata(int register, string unit, string range, string scale)
    {
        await using Harness h = await ConnectAsync(A6V);
        Select(h.W, register);
        Assert.Equal(unit, h.W.UnitText);
        Assert.Equal(range, h.W.RangeText);
        Assert.Equal(scale, h.W.ScaleText);
        Assert.Equal("Engineering Value", h.W.ValueLabel);
    }

    [Theory]
    [InlineData(223, "100.1", "outside")]                 // Test 5: OutOfRange
    [InlineData(223, "-100.1", "outside")]
    [InlineData(223, "1.55", "not a multiple of 0.1")]    // Test 6: not representable
    [InlineData(224, "100.1", "outside")]                 // Test 7
    [InlineData(225, "2001", "outside")]                  // Test 8
    [InlineData(225, "1.5", "not a multiple of 1")]
    [InlineData(223, "abc", "not a number")]
    public async Task Profile_ValidationMessage_FromCore(int register, string input, string message)
    {
        await using Harness h = await ConnectAsync(A6V);
        Select(h.W, register);
        h.W.ValueText = input;

        Assert.Equal(WriteStatus.ValidationError, h.W.Status);
        Assert.Contains(message, h.W.ValidationMessage);
        Assert.Equal("", h.W.PreviewRaw);
        Assert.False(h.W.WriteCommand.CanExecute(null));
    }

    // Tests 9–10, 13–14: status registers use a ComboBox fed by RegisterDefinition.States
    [Theory]
    [InlineData(401)]
    [InlineData(765)]
    [InlineData(766)]
    public void Profile_StatusOptions_ComeFromMetadata(int register)
    {
        Harness h = Offline(A6V);
        Select(h.W, register);
        RegisterDefinition d = A6V.Registers.Single(r => r.RegisterNumber == register);

        Assert.True(h.W.ShowStateInput);
        Assert.False(h.W.ShowNumericInput);
        Assert.Equal(d.States.OrderBy(s => s.Key).Select(s => s.Value.Text), h.W.StateOptions);
        Assert.Equal("Value", h.W.ValueLabel);
    }

    [Fact]
    public void Profile_StatusOptions_Texts()
    {
        Harness h = Offline(A6V);
        Select(h.W, 401);
        Assert.Equal(["Celsius", "Fahrenheit"], h.W.StateOptions);
        Select(h.W, 765);
        Assert.Equal(["Auto", "9600bps", "19200bps", "38400bps", "57600bps", "76800bps", "115200bps"], h.W.StateOptions);
        Select(h.W, 766);
        Assert.Equal(["1-8-E-1", "1-8-O-1", "1-8-N-1", "1-8-N-2"], h.W.StateOptions);
    }

    [Theory]
    [InlineData(401, "Celsius", "0", "0000")]
    [InlineData(401, "Fahrenheit", "1", "0001")]
    [InlineData(765, "Auto", "0", "0000")]
    [InlineData(765, "115200bps", "6", "0006")]
    [InlineData(766, "1-8-N-2", "3", "0003")]
    public void Profile_StatusPreview(int register, string state, string raw, string hex)
    {
        Harness h = Offline(A6V);
        Select(h.W, register);
        h.W.SelectedState = state;
        Assert.Equal(state, h.W.PreviewEngineering);
        Assert.Equal(raw, h.W.PreviewRaw);
        Assert.Equal(hex, h.W.PreviewHex);
    }

    // Tests 11, 15: 764–766 show the Core's WriteHoldReason and keep Write disabled, even with a valid value
    [Theory]
    [InlineData(764, null, "247")]
    [InlineData(765, "9600bps", null)]
    [InlineData(766, "1-8-N-1", null)]
    public async Task Profile_CommunicationRegisters_OnHold(int register, string? state, string? value)
    {
        await using Harness h = await ConnectAsync(A6V);
        Select(h.W, register);
        if (state is not null) h.W.SelectedState = state;
        if (value is not null) h.W.ValueText = value;

        Assert.True(h.W.IsOnHold);
        Assert.Equal(A6V12258321Profile.CommunicationWriteHold, h.W.HoldReason);
        Assert.Equal(WriteStatus.WriteOnHold, h.W.Status);
        Assert.NotEqual("", h.W.PreviewRaw);                  // still encoded and validated
        Assert.False(h.W.WriteCommand.CanExecute(null));
        await h.W.WriteCommand.ExecuteAsync(null);            // even if forced: nothing confirmed, nothing sent
        Assert.Empty(h.Confirmations);
        Assert.Empty(h.Requests());
    }

    // Test 12: 764 = 246 → the Core's ExcludedValue message
    [Theory]
    [InlineData("1", WriteStatus.WriteOnHold, "")]
    [InlineData("245", WriteStatus.WriteOnHold, "")]
    [InlineData("246", WriteStatus.ValidationError, "Reserved for on-event addressing")]
    [InlineData("247", WriteStatus.WriteOnHold, "")]
    [InlineData("248", WriteStatus.ValidationError, "outside")]
    public void Profile_764_Validation(string input, WriteStatus status, string message)
    {
        Harness h = Offline(A6V);
        Select(h.W, 764);
        h.W.ValueText = input;
        Assert.Equal(status, h.W.Status);
        if (message.Length == 0)
            Assert.Equal("", h.W.ValidationMessage);
        else
            Assert.Contains(message, h.W.ValidationMessage);
        Assert.False(h.W.WriteCommand.CanExecute(null));
    }

    // Test 16: Reset to Default only fills the input
    [Theory]
    [InlineData(223, "0", null, "0")]
    [InlineData(224, "0", null, "0")]
    [InlineData(225, "0", null, "0")]
    [InlineData(764, "1", null, "1")]
    [InlineData(401, null, "Celsius", "0")]
    [InlineData(765, null, "Auto", "0")]
    [InlineData(766, null, "1-8-E-1", "0")]
    public async Task Profile_ResetToDefault_FillsInputOnly(int register, string? value, string? state, string raw)
    {
        await using Harness h = await ConnectAsync(A6V);
        Select(h.W, register);
        if (h.W.ShowStateInput) h.W.SelectedState = h.W.StateOptions[^1];
        else h.W.ValueText = "5";

        Assert.True(h.W.ResetToDefaultCommand.CanExecute(null));
        h.W.ResetToDefaultCommand.Execute(null);

        if (value is not null) Assert.Equal(value, h.W.ValueText);
        if (state is not null) Assert.Equal(state, h.W.SelectedState);
        Assert.Equal(raw, h.W.PreviewRaw);
        Assert.Empty(h.Confirmations);
        Assert.Empty(h.Requests());
        Assert.Equal(register is 764 or 765 or 766 ? WriteStatus.WriteOnHold : WriteStatus.Ready, h.W.Status);
    }

    // Tests 17–18: Write enabled only when connected with a valid prepared value
    [Fact]
    public async Task WriteButton_EnabledOnlyWhenEverythingIsValid()
    {
        Harness offline = Offline(A6V);
        Select(offline.W, 224);
        offline.W.ValueText = "1.5";
        Assert.Equal(WriteStatus.Ready, offline.W.Status);
        Assert.False(offline.W.WriteCommand.CanExecute(null));      // not connected
        await offline.DisposeAsync();

        await using Harness h = await ConnectAsync(A6V);
        Assert.False(h.W.WriteCommand.CanExecute(null));            // no register
        Select(h.W, 223);
        Assert.False(h.W.WriteCommand.CanExecute(null));            // no value
        h.W.ValueText = "1.5";
        Assert.True(h.W.WriteCommand.CanExecute(null));
        h.W.ValueText = "1.55";
        Assert.False(h.W.WriteCommand.CanExecute(null));
        h.W.ValueText = "1.5";

        await h.Vm.DisconnectCommand.ExecuteAsync(null);            // Test 18
        Assert.False(h.W.WriteCommand.CanExecute(null));
    }

    // Test 19: no second Write (and no Read) while a write is in progress
    [Fact]
    public async Task Writing_DisablesWriteAndRead()
    {
        await using Harness h = await ConnectAsync(A6V);
        h.Slave!.ResponseDelay = TimeSpan.FromMilliseconds(300);
        Select(h.W, 223);
        h.W.ValueText = "2";

        Task write = h.W.WriteCommand.ExecuteAsync(null);
        Assert.True(h.W.IsWriting);
        Assert.Equal(WriteStatus.Writing, h.W.Status);
        Assert.False(h.W.WriteCommand.CanExecute(null));
        Assert.False(h.Vm.ReadCommand.CanExecute(null));
        Assert.False(h.Vm.DisconnectCommand.CanExecute(null));
        await write;

        Assert.False(h.W.IsWriting);
        Assert.True(h.W.WriteCommand.CanExecute(null));
        Assert.True(h.Vm.ReadCommand.CanExecute(null));
        Assert.Equal(["06 00 DE 00 14", "03 00 DE 00 01"], h.Requests());
    }

    // Test 20: switching profiles leaves nothing behind
    [Fact]
    public void ProfileSwitch_ClearsEverything()
    {
        Harness h = Offline(A6V);
        Select(h.W, 764);
        h.W.ValueText = "246";
        Assert.NotEqual("", h.W.ValidationMessage);

        h.Vm.SelectedDeviceProfile = DeviceProfile.Generic;
        Assert.True(h.W.IsGenericMode);
        Assert.Empty(h.W.WritableRegisters);
        Assert.Null(h.W.SelectedRegister);
        Assert.Equal("", h.W.ValueText);
        Assert.Null(h.W.SelectedState);
        Assert.Equal("", h.W.UnitText);
        Assert.Equal("", h.W.RangeText);
        Assert.Null(h.W.HoldReason);
        Assert.Equal("", h.W.ValidationMessage);
        Assert.Equal("", h.W.PreviewRaw);
        Assert.Equal("Raw Value", h.W.ValueLabel);

        h.Vm.SelectedDeviceProfile = A6V;
        Assert.Equal(7, h.W.WritableRegisters.Count);
        Assert.Null(h.W.SelectedRegister);
        Assert.Equal("", h.W.PreviewRaw);
        Assert.Equal("", h.W.ValidationMessage);
        Assert.Equal(WriteStatus.NotReady, h.W.Status);
    }

    [Fact]
    public void RegisterChange_ClearsValueAndState()
    {
        Harness h = Offline(A6V);
        Select(h.W, 401);
        h.W.SelectedState = "Fahrenheit";
        Select(h.W, 223);
        Assert.Null(h.W.SelectedState);
        Assert.Equal("", h.W.ValueText);
        Assert.Equal("", h.W.PreviewRaw);
    }

    // ---- Profile write (Tests 21–32) ----

    [Theory]
    [InlineData(223, "1.5", null, "06 00 DE 00 0F", 15, "1.5 °C")]          // 21
    [InlineData(223, "-2.5", null, "06 00 DE FF E7", 65511, "-2.5 °C")]     // 22
    [InlineData(401, null, "Celsius", "06 01 90 00 00", 0, "Celsius")]       // 23
    [InlineData(401, null, "Fahrenheit", "06 01 90 00 01", 1, "Fahrenheit")] // 24
    [InlineData(225, "150", null, "06 00 E0 00 96", 150, "150 ppm")]
    public async Task ProfileWrite_Fc06_ThenReadBack(int register, string? value, string? state, string fc06, ushort raw, string readBack)
    {
        await using Harness h = await ConnectAsync(A6V);
        Select(h.W, register);
        if (value is not null) h.W.ValueText = value;
        if (state is not null) h.W.SelectedState = state;

        await h.W.WriteCommand.ExecuteAsync(null);

        string verify = $"03 {(register - 1) >> 8:X2} {(register - 1) & 0xFF:X2} 00 01";
        Assert.Equal([fc06, verify], h.Requests());                         // Test 27: FC03 read back of the same register
        Assert.Equal(raw, h.Slave!.Registers[register - 1]);
        Assert.Equal(WriteStatus.Completed, h.W.Status);                    // Test 28
        Assert.Equal($"Write completed (read back {readBack})", h.W.StatusText);
        Assert.Single(h.Confirmations);
    }

    // Tests 25–26: confirmation text comes from the prepared result; Cancel sends nothing
    [Fact]
    public async Task Confirm_Cancel_SendsNothing()
    {
        await using Harness h = await ConnectAsync(A6V);
        h.Answer = false;
        Select(h.W, 223);
        h.W.ValueText = "-2.5";

        await h.W.WriteCommand.ExecuteAsync(null);

        WriteConfirmation c = Assert.Single(h.Confirmations);
        Assert.Equal("Write Register 223?", c.Title);
        Assert.Equal(string.Join(Environment.NewLine,
            "Temperature offset",
            "Engineering Value : -2.5 °C",
            "Raw               : 65511",
            "HEX               : FFE7",
            "Function          : FC06"), c.Message);
        Assert.Empty(h.Requests());
        Assert.Equal(WriteStatus.Cancelled, h.W.Status);
        Assert.True(h.W.WriteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Confirm_StatusRegister_Text_ThenOneSend()
    {
        await using Harness h = await ConnectAsync(A6V);
        Select(h.W, 401);
        h.W.SelectedState = "Fahrenheit";

        await h.W.WriteCommand.ExecuteAsync(null);

        WriteConfirmation c = Assert.Single(h.Confirmations);
        Assert.Equal("Write Register 401?", c.Title);
        Assert.Equal(string.Join(Environment.NewLine,
            "System unit",
            "Value             : Fahrenheit",
            "Raw               : 1",
            "HEX               : 0001",
            "Function          : FC06"), c.Message);
        Assert.Single(h.Requests(), r => r.StartsWith("06"));
    }

    // Test 29: FC06 OK but the register reads back differently
    [Fact]
    public async Task ReadBack_Mismatch_IsNotAWriteFailure()
    {
        await using Harness h = await ConnectAsync(A6V);
        h.Slave!.ResponseMutator = r => r[0] == 0x03 ? [0x03, 0x02, 0x00, 0x14] : r;   // device reports 20
        Select(h.W, 223);
        h.W.ValueText = "1.5";

        await h.W.WriteCommand.ExecuteAsync(null);

        Assert.Equal(WriteStatus.VerificationMismatch, h.W.Status);
        Assert.Equal("Write succeeded, verification mismatch (wrote 15, read back 20)", h.W.StatusText);
        Assert.Contains(h.Vm.LogEntries, e => e.Kind == LogKind.Fail && e.Message == "Result: FAIL [Verification mismatch] - wrote 15, read back 20");
    }

    // Test 30: FC06 OK, the read back times out (RTU slave that stays silent for FC03)
    [Fact]
    public async Task ReadBack_Timeout_IsVerificationFailure()
    {
        var rtu = new FakeRtuSlave { Registers = DeviceMap() };
        bool silenceReads = false;
        var channel = new FakeSerialChannel { Responder = frame => silenceReads && frame[1] == 0x03 ? [] : rtu.Respond(frame) };
        var vm = new MainViewModel(_ => channel, TestVm.TempStore())
        {
            IsRtu = true, SelectedComPort = "COM_TEST", RtuTimeoutText = "200", SelectedDeviceProfile = A6V,
        };
        await using var h = new Harness { Vm = vm };
        vm.Write.Confirm = _ => true;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        vm.StartAddressText = "401";                                   // System unit known first
        await vm.ReadCommand.ExecuteAsync(null);
        rtu.Requests.Clear();
        silenceReads = true;
        Select(h.W, 223);
        h.W.ValueText = "1.5";

        await h.W.WriteCommand.ExecuteAsync(null);

        Assert.Equal(15, rtu.Registers[222]);
        Assert.Equal(WriteStatus.VerificationFailed, h.W.Status);
        Assert.Equal("Write succeeded, verification failed: Timeout", h.W.StatusText);
        Assert.Equal(["01 06 00 DE 00 0F A9 F4"], rtu.Requests.Select(HexFormatter.Format));
    }

    // Test 31: the device answers FC06 with an exception
    [Fact]
    public async Task Fc06Exception_IsWriteFailed_NoReadBack()
    {
        await using Harness h = await ConnectAsync(A6V);
        h.Slave!.ForcedExceptionCode = 0x02;
        Select(h.W, 223);
        h.W.ValueText = "1.5";

        await h.W.WriteCommand.ExecuteAsync(null);

        Assert.Equal(WriteStatus.WriteFailed, h.W.Status);
        Assert.Equal("Write failed: ModbusException", h.W.StatusText);
        Assert.Equal(["06 00 DE 00 0F"], h.Requests());
        Assert.Contains(h.Vm.LogEntries, e => e.Kind == LogKind.Fail && e.Message.Contains("Illegal Data Address"));
    }

    // Test 32: invalid input never reaches confirmation or the transport
    [Theory]
    [InlineData(223, "100.1")]
    [InlineData(223, "1.55")]
    [InlineData(764, "246")]
    public async Task CoreValidationFailure_SendsNothing(int register, string input)
    {
        await using Harness h = await ConnectAsync(A6V);
        Select(h.W, register);
        h.W.ValueText = input;

        await h.W.WriteCommand.ExecuteAsync(null);

        Assert.Empty(h.Confirmations);
        Assert.Empty(h.Requests());
    }

    // Address Base: the profile register is the same; the user-facing address follows the base
    [Theory]
    [InlineData(AddressBase.OneBased, "FC06  Unit ID = 1  Address = 223 (PDU 0x00DE, 1-based)  Value = 15")]
    [InlineData(AddressBase.ZeroBased, "FC06  Unit ID = 1  Address = 222 (PDU 0x00DE, 0-based)  Value = 15")]
    public async Task ProfileWrite_AddressBase_SamePdu(AddressBase addressBase, string tx)
    {
        await using Harness h = await ConnectAsync(A6V, addressBase: addressBase);
        Select(h.W, 223);
        h.W.ValueText = "1.5";
        Assert.Equal("0x00DE", h.W.PreviewPdu);

        await h.W.WriteCommand.ExecuteAsync(null);

        Assert.Equal(["06 00 DE 00 0F", "03 00 DE 00 01"], h.Requests());
        Assert.Contains(h.Vm.LogEntries, e => e.Message == tx);
    }

    // Communication Log: FC06, then the verify FC03, no "Result: PASS"
    [Fact]
    public async Task Log_Fc06ThenVerifyFc03_NoPass()
    {
        await using Harness h = await ConnectAsync(A6V);
        Select(h.W, 223);
        h.W.ValueText = "1.5";
        int before = h.Vm.LogEntries.Count;

        await h.W.WriteCommand.ExecuteAsync(null);

        string[] log = [.. h.Vm.LogEntries.Skip(before).Select(e => e.Message)];
        int fc06 = Array.FindIndex(log, m => m.StartsWith("FC06  Unit ID = 1  Address = 223"));
        int verify = Array.FindIndex(log, m => m == "Verify: read back Address = 223 (1-based)  Quantity = 1");
        int fc03 = Array.FindIndex(log, m => m.StartsWith("FC03  Unit ID = 1  Address = 223"));
        Assert.True(fc06 >= 0 && verify > fc06 && fc03 > verify, string.Join(" | ", log));
        Assert.Equal(2, log.Count(m => m.StartsWith("Response Time:")));
        Assert.DoesNotContain(log, m => m.StartsWith("Result:"));
    }

    // ---- Generic (Tests 33–45) ----

    [Theory]
    [InlineData("223", "15", "06 00 DE 00 0F")]          // 33 decimal
    [InlineData("223", "0xFFE7", "06 00 DE FF E7")]      // 34 hex
    [InlineData("223", "0xffe7", "06 00 DE FF E7")]
    [InlineData("223", "65535", "06 00 DE FF FF")]
    public async Task Generic_Fc06(string address, string values, string pdu)
    {
        await using Harness h = await ConnectAsync(DeviceProfile.Generic);
        h.W.GenericAddressText = address;
        h.W.GenericValuesText = values;
        Assert.Equal("1", h.W.PreviewQuantity);

        await h.W.WriteCommand.ExecuteAsync(null);

        Assert.Equal([pdu, "03 00 DE 00 01"], h.Requests());
        Assert.Equal(WriteStatus.Completed, h.W.Status);
    }

    [Theory]
    [InlineData("15,25,150")]                       // 35
    [InlineData("0x000F,0x0019,0x0096")]            // 36
    [InlineData("15, 0x19 ,150")]                   // 37 mixed: each item decimal or 0x-hex
    public async Task Generic_Fc16(string values)
    {
        await using Harness h = await ConnectAsync(DeviceProfile.Generic);
        h.W.SelectedGenericFunction = h.W.GenericFunctions[1];
        h.W.GenericAddressText = "223";
        h.W.GenericValuesText = values;

        Assert.Equal("Raw Values", h.W.ValueLabel);
        Assert.Equal("3", h.W.PreviewQuantity);                              // 38
        Assert.Equal("15, 25, 150", h.W.PreviewRaw);
        Assert.Equal("000F, 0019, 0096", h.W.PreviewHex);
        Assert.Equal("0x00DE", h.W.PreviewPdu);

        await h.W.WriteCommand.ExecuteAsync(null);

        Assert.Equal(["10 00 DE 00 03 06 00 0F 00 19 00 96", "03 00 DE 00 03"], h.Requests());
        Assert.Equal(WriteStatus.Completed, h.W.Status);
        Assert.Equal("Write completed (read back 15, 25, 150)", h.W.StatusText);
        WriteConfirmation c = Assert.Single(h.Confirmations);
        Assert.Contains("Quantity : 3", c.Message);
        Assert.Contains("HEX      : 000F, 0019, 0096", c.Message);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("0x10000")]
    [InlineData("0x")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("15,,25")]
    [InlineData("FFE7")]       // hex needs the 0x prefix
    public void Generic_InvalidRaw(string values)
    {
        Harness h = Offline(DeviceProfile.Generic);
        h.W.SelectedGenericFunction = h.W.GenericFunctions[1];
        h.W.GenericAddressText = "1";
        h.W.GenericValuesText = values;
        Assert.Equal(WriteStatus.ValidationError, h.W.Status);
        Assert.NotEqual("", h.W.ValidationMessage);
    }

    [Fact]
    public void Generic_Fc06_OnlyOneValue()
    {
        Harness h = Offline(DeviceProfile.Generic);
        h.W.GenericAddressText = "1";
        h.W.GenericValuesText = "1,2";
        Assert.Equal("FC06 writes exactly one value (use FC16 for several).", h.W.ValidationMessage);
    }

    // Test 39
    [Fact]
    public async Task Generic_Fc16_MoreThan123_Rejected()
    {
        await using Harness h = await ConnectAsync(DeviceProfile.Generic);
        h.W.SelectedGenericFunction = h.W.GenericFunctions[1];
        h.W.GenericAddressText = "1";
        h.W.GenericValuesText = string.Join(",", Enumerable.Repeat("1", 124));
        Assert.Equal("At most 123 values (got 124).", h.W.ValidationMessage);
        Assert.False(h.W.WriteCommand.CanExecute(null));

        h.W.GenericValuesText = string.Join(",", Enumerable.Repeat("1", 123));
        Assert.Equal("123", h.W.PreviewQuantity);
        Assert.True(h.W.WriteCommand.CanExecute(null));
    }

    // Tests 40–41
    [Theory]
    [InlineData(AddressBase.OneBased, "223", "0x00DE", "06 00 DE 00 0F")]
    [InlineData(AddressBase.ZeroBased, "222", "0x00DE", "06 00 DE 00 0F")]
    [InlineData(AddressBase.ZeroBased, "223", "0x00DF", "06 00 DF 00 0F")]
    public async Task Generic_AddressBase(AddressBase addressBase, string address, string pdu, string request)
    {
        await using Harness h = await ConnectAsync(DeviceProfile.Generic, addressBase: addressBase);
        h.W.GenericAddressText = address;
        h.W.GenericValuesText = "15";
        Assert.Equal(pdu, h.W.PreviewPdu);

        await h.W.WriteCommand.ExecuteAsync(null);

        Assert.Equal(request, h.Requests()[0]);
    }

    [Fact]
    public void Generic_AddressBaseChange_UpdatesPreview()
    {
        Harness h = Offline(DeviceProfile.Generic);
        h.W.GenericAddressText = "223";
        h.W.GenericValuesText = "15";
        Assert.Equal("0x00DE", h.W.PreviewPdu);
        h.Vm.SelectedAddressBase = h.Vm.AddressBases[1];
        Assert.Equal("0x00DF", h.W.PreviewPdu);
        h.W.GenericAddressText = "0";
        Assert.Equal("0x0000", h.W.PreviewPdu);
    }

    // Tests 42–44: Generic knows nothing about the A6V metadata
    [Theory]
    [InlineData("223", "5000")]   // beyond the profile range of 223
    [InlineData("764", "246")]    // excluded in the profile
    [InlineData("764", "1")]      // on hold in the profile
    [InlineData("1", "100")]      // read-only in the profile
    public async Task Generic_IgnoresProfileRules(string address, string value)
    {
        await using Harness h = await ConnectAsync(DeviceProfile.Generic);
        h.W.GenericAddressText = address;
        h.W.GenericValuesText = value;

        Assert.Equal(WriteStatus.Ready, h.W.Status);
        Assert.Null(h.W.HoldReason);
        Assert.Equal("", h.W.UnitText);
        await h.W.WriteCommand.ExecuteAsync(null);
        Assert.Equal(ushort.Parse(value), h.Slave!.Registers[int.Parse(address) - 1]);
        Assert.Equal(WriteStatus.Completed, h.W.Status);
    }

    // Test 45: Generic read back compares raw words only (0xFFE7 = 65511, never "-2.5 °C")
    [Fact]
    public async Task Generic_ReadBack_RawOnly()
    {
        await using Harness h = await ConnectAsync(DeviceProfile.Generic);
        h.W.GenericAddressText = "223";
        h.W.GenericValuesText = "0xFFE7";

        await h.W.WriteCommand.ExecuteAsync(null);

        Assert.Equal("Write completed (read back 65511)", h.W.StatusText);
        Assert.DoesNotContain("°C", h.W.StatusText);
    }

    [Fact]
    public async Task Generic_ReadBack_Mismatch()
    {
        await using Harness h = await ConnectAsync(DeviceProfile.Generic);
        h.Slave!.ResponseMutator = r => r[0] == 0x03 ? [0x03, 0x02, 0x00, 0x00] : r;
        h.W.GenericAddressText = "223";
        h.W.GenericValuesText = "0xFFE7";

        await h.W.WriteCommand.ExecuteAsync(null);

        Assert.Equal("Write succeeded, verification mismatch (wrote 65511, read back 0)", h.W.StatusText);
    }

    [Fact]
    public async Task Generic_Confirm_Text()
    {
        await using Harness h = await ConnectAsync(DeviceProfile.Generic);
        h.Answer = false;
        h.W.GenericAddressText = "223";
        h.W.GenericValuesText = "0xFFE7";

        await h.W.WriteCommand.ExecuteAsync(null);

        WriteConfirmation c = Assert.Single(h.Confirmations);
        Assert.Equal("Write Address 223?", c.Title);
        Assert.Equal(string.Join(Environment.NewLine,
            "Address  : 223 (1-based, PDU 0x00DE)",
            "Function : FC06",
            "Quantity : 1",
            "Raw      : 65511",
            "HEX      : FFE7"), c.Message);
        Assert.Empty(h.Requests());
    }

    // ---- Regression: Read is unaffected by the write area ----

    [Fact]
    public async Task Read_StillWorks_AfterWrite()
    {
        await using Harness h = await ConnectAsync(A6V);
        Select(h.W, 223);
        h.W.ValueText = "2";
        await h.W.WriteCommand.ExecuteAsync(null);

        h.Vm.StartAddressText = "225";
        await h.Vm.ReadCommand.ExecuteAsync(null);

        Assert.True(h.Vm.LastResultPassed);
        Assert.Equal("2", h.Vm.Rows.Single(r => r.Address == 223).ValueText);
        Assert.Equal(["06 00 DE 00 14", "03 00 DE 00 01", "03 00 00 00 06", "03 00 DE 00 03"], h.Requests());
    }
}

public class RawValuesValidatorTests
{
    [Theory]
    [InlineData("0", new ushort[] { 0 })]
    [InlineData("65535", new ushort[] { 65535 })]
    [InlineData("0xFFFF", new ushort[] { 65535 })]
    [InlineData("0X1", new ushort[] { 1 })]
    [InlineData(" 15 , 0x19 ", new ushort[] { 15, 25 })]
    public void Valid(string text, ushort[] words) => Assert.Equal(words, InputValidator.RawValues(text, 123).Value);

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1e3")]
    [InlineData("0x12345")]
    [InlineData("1,")]
    public void Invalid(string text) => Assert.False(InputValidator.RawValues(text, 123).IsValid);
}
