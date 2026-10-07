using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Services;
using ModbusTester.Core.Transport;

namespace ModbusTester.Tests;

/// <summary>Phase 3A: A6V12258321 registers 1–6 decoded from the document, shared by TCP and RTU.</summary>
public class A6V12258321ProfileTests
{
    private static readonly DeviceProfile Profile = A6V12258321Profile.Profile;

    /// <summary>Decodes one raw value as document register <paramref name="register"/>.</summary>
    private static DecodedRegister Decode(int register, ushort raw) =>
        RegisterDecoder.DecodeEngineering(register, (ushort)(register - 1), raw, Profile.Find((ushort)(register - 1))!);

    private static ModbusResult Result(int startAddress, AddressBase addressBase, params ushort[] registers) => new()
    {
        Request = new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, startAddress, (ushort)registers.Length, addressBase),
        Status = ResultStatus.Pass,
        Registers = registers,
    };

    [Fact]
    public void Profile_DefinesRegisters1To6_ByDocumentRegisterNumber()
    {
        Assert.Equal([1, 2, 3, 4, 5, 6], Profile.Registers.Select(r => r.RegisterNumber));
        Assert.Equal("Temperature value", Profile.Find(0)!.Description);   // Register 1 = PDU 0
        Assert.Null(Profile.Find(6));                                 // Register 7 is not defined
        Assert.All(Profile.Registers, r => Assert.Equal(RegisterAccess.Read, r.Access));
        Assert.Same(DeviceProfile.Generic, DeviceProfile.All[0]);
        Assert.True(DeviceProfile.Generic.IsGeneric);
    }

    // Test 1
    [Fact]
    public void Reg1_2534_Is25_34DegC()
    {
        DecodedRegister r = Decode(1, 2534);
        Assert.Equal("Temperature value", r.Description);
        Assert.Equal("溫度", r.ChineseDescription);
        Assert.Equal(25.34, r.Value);
        Assert.Equal("25.34", r.ValueText);
        Assert.Equal("°C", r.Unit);
        Assert.Equal("0.01", r.ScaleText);
        Assert.Equal(RegisterQuality.Ok, r.Quality);
    }

    // Test 2
    [Fact]
    public void Reg1_Negative_IsSignedInt16()
    {
        ushort raw = unchecked((ushort)(short)-1234);   // 0xFB2E = 64302 on the wire
        DecodedRegister r = Decode(1, raw);
        Assert.Equal(64302, r.Raw);
        Assert.Equal(-1234, r.Integer);
        Assert.Equal(-12.34, r.Value);
        Assert.Equal("-12.34", r.ValueText);
        Assert.Equal(RegisterQuality.Ok, r.Quality);
    }

    [Theory]
    [InlineData(-32700, RegisterQuality.Ok)]        // -327.00 °C, document minimum
    [InlineData(32700, RegisterQuality.Ok)]         //  327.00 °C, document maximum
    [InlineData(-32701, RegisterQuality.OutOfRange)]
    [InlineData(32701, RegisterQuality.OutOfRange)]
    public void Reg1_RangeLimits(short integer, RegisterQuality expected) =>
        Assert.Equal(expected, Decode(1, unchecked((ushort)integer)).Quality);

    // Tests 3, 4, 5 (and the same table for registers 4 and 6)
    [Theory]
    [InlineData(2, 0, "No error", RegisterQuality.Ok)]
    [InlineData(2, 1, "Bad reliability, unavailable", RegisterQuality.Ok)]
    [InlineData(2, 5, "Unknown (5)", RegisterQuality.UnknownState)]
    [InlineData(4, 0, "No error", RegisterQuality.Ok)]
    [InlineData(4, 1, "Bad reliability, unavailable", RegisterQuality.Ok)]
    [InlineData(6, 0, "No error", RegisterQuality.Ok)]
    [InlineData(6, 65535, "Unknown (65535)", RegisterQuality.UnknownState)]
    public void ReliabilityRegisters_MapStates_NeverScaled(int register, int raw, string text, RegisterQuality quality)
    {
        DecodedRegister r = Decode(register, (ushort)raw);
        Assert.Equal(text, r.ValueText);
        Assert.Equal(quality, r.Quality);
        Assert.Null(r.Scale);
        Assert.Null(r.Value);
        Assert.Equal("-", r.ScaleText);
        Assert.Equal((ushort)raw, r.Raw);   // raw kept even when unknown
    }

    // Test 6
    [Fact]
    public void Reg3_4856_Is48_56Percent()
    {
        DecodedRegister r = Decode(3, 4856);
        Assert.Equal(("Relative humidity value", 48.56, "%", RegisterQuality.Ok), (r.Description, r.Value!.Value, r.Unit, r.Quality));
    }

    // Test 7
    [Fact]
    public void Reg5_650_Is650Ppm_NotScaledBy0_01()
    {
        DecodedRegister r = Decode(5, 650);
        Assert.Equal("CO2 value", r.Description);
        Assert.Equal(650, r.Value);                 // used as-is (x1), not x0.01 and not x0
        Assert.Equal("650", r.ValueText);
        Assert.Equal("ppm", r.Unit);
        Assert.Null(r.Scale);                       // document Scaling "-"
        Assert.Equal("-", r.ScaleText);
    }

    // Test 8 + 9 (decode level)
    [Theory]
    [InlineData(3, 52312, "523.12")]   // 523.12 % > 100 %
    [InlineData(5, 57005, "57005")]    // 57005 ppm > 2000 ppm
    [InlineData(5, 2001, "2001")]
    public void OutOfRange_IsFlagged_ValueAndRawKept(int register, int raw, string valueText)
    {
        DecodedRegister r = Decode(register, (ushort)raw);
        Assert.Equal(RegisterQuality.OutOfRange, r.Quality);
        Assert.Equal(valueText, r.ValueText);
        Assert.Equal($"{valueText} (Out of range)", r.DisplayValue);
        Assert.Equal((ushort)raw, r.Raw);
    }

    // Test 10 (Core level): Generic decoding is exactly the old raw × manual scale
    [Fact]
    public void GenericProfile_MatchesPreviousBuildRows()
    {
        ModbusResult result = Result(1, AddressBase.OneBased, 2534, 0, 4856, 64302, 650, 57005);
        IReadOnlyList<RegisterRow> old = RegisterService.BuildRows(result, a => a == 2 ? 1 : 0.01);

        IReadOnlyList<DecodedRegister> decoded = RegisterService.Decode(result, DeviceProfile.Generic, a => a == 2 ? 1 : 0.01);

        Assert.Equal(old.Select(r => (r.Address, r.Raw, r.Value, r.Scale, r.Hex, r.Binary)),
            decoded.Select(r => (r.Address, r.Raw, r.Value!.Value, r.Scale!.Value, r.Hex, r.Binary)));
        Assert.All(decoded, r => Assert.False(r.IsProfileControlled));
        Assert.Equal(64302, decoded[3].Integer);   // generic stays unsigned
    }

    // Test 11: the definition is found by PDU address, so the Address Base is applied once only
    [Theory]
    [InlineData(1, AddressBase.OneBased)]
    [InlineData(0, AddressBase.ZeroBased)]
    public void AddressBase_Register1_IsPdu0_AndFound(int startAddress, AddressBase addressBase)
    {
        ModbusResult result = Result(startAddress, addressBase, 2534, 0, 4856, 0, 650, 0);
        Assert.Equal(0, result.Request.PduAddress);

        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode(result, Profile, _ => 1);

        Assert.Equal(["Temperature value", "Temperature reliability", "Relative humidity value", "Humidity reliability", "CO2 value", "CO2 reliability"],
            rows.Select(r => r.Description));
        Assert.Equal(startAddress, rows[0].Address);   // user-facing address unchanged
        Assert.Equal(["25.34", "No error", "48.56", "No error", "650", "No error"], rows.Select(r => r.DisplayValue));
        Assert.Equal(["0.01", "-", "0.01", "-", "-", "-"], rows.Select(r => r.ScaleText));   // as in the document
    }

    [Fact]
    public void PartialRead_FromRegister3_AndUndefinedRegisters_UseGeneric()
    {
        ModbusResult result = Result(3, AddressBase.OneBased, 4856, 0, 650, 0, 100, 7);   // registers 3..8

        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode(result, Profile, _ => 0.5);

        Assert.Equal(["Relative humidity value", "Humidity reliability", "CO2 value", "CO2 reliability", "", ""], rows.Select(r => r.Description));
        Assert.Equal(50, rows[4].Value);           // register 7: generic raw × manual 0.5
        Assert.False(rows[4].IsProfileControlled);
        Assert.True(rows[0].IsProfileControlled);
    }

    // Test 9 (communication level) + Test 12: TCP and RTU, same registers → same decoding, read still PASS
    [Fact]
    public async Task TcpAndRtu_SameRegisters_DecodeIdentically_OutOfRangeIsNotATransportError()
    {
        ushort[] registers = [2534, 0, 52312, 1, 57005, 5];   // humidity and CO2 out of range, unknown CO2 state
        var request = new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, 1, 6, AddressBase.OneBased);

        await using var slave = new FakeModbusTcpSlave { Registers = registers };
        await using var tcp = new ModbusService(new CommunicationLogger());
        Assert.True(await tcp.ConnectAsync(new ModbusTcpTransport(new TcpSettings("127.0.0.1", slave.Port))));
        ModbusResult tcpResult = await tcp.ReadRegistersAsync(request);

        var channel = new FakeSerialChannel { Responder = req => [(TimeSpan.Zero, RtuResponse(req[0], registers))] };
        await using var rtu = new ModbusService(new CommunicationLogger());
        Assert.True(await rtu.ConnectAsync(new ModbusRtuTransport(new RtuSettings("COM_TEST"), _ => channel)));
        ModbusResult rtuResult = await rtu.ReadRegistersAsync(request);

        Assert.Equal(ResultStatus.Pass, tcpResult.Status);
        Assert.Equal(ResultStatus.Pass, rtuResult.Status);
        Assert.Equal(ConnectionState.Connected, tcp.State);
        Assert.Equal(ConnectionState.Connected, rtu.State);
        Assert.DoesNotContain(tcp.Logger.Entries, e => e.Kind == LogKind.Fail);

        IReadOnlyList<DecodedRegister> fromTcp = RegisterService.Decode(tcpResult, Profile, _ => 1);
        IReadOnlyList<DecodedRegister> fromRtu = RegisterService.Decode(rtuResult, Profile, _ => 1);
        Assert.Equal(fromTcp, fromRtu);
        Assert.Equal(["25.34", "No error", "523.12 (Out of range)", "Bad reliability, unavailable", "57005 (Out of range)", "Unknown (5)"],
            fromTcp.Select(r => r.DisplayValue));
    }

    private static byte[] RtuResponse(byte slaveId, ushort[] registers)
    {
        var body = new List<byte> { slaveId, 0x03, (byte)(registers.Length * 2) };
        foreach (ushort value in registers)
            body.AddRange([(byte)(value >> 8), (byte)value]);
        return Crc16.Append(body.ToArray());
    }
}
