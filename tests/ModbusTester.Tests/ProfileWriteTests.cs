using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Services;
using ModbusTester.Core.Transport;

namespace ModbusTester.Tests;

/// <summary>Phase 4B: A6V12258321 engineering value / status → validated raw word (encoder only, nothing sent).</summary>
public class RegisterWriteEncoderTests
{
    private static readonly DeviceProfile A6V = A6V12258321Profile.Profile;

    private static RegisterDefinition Reg(int number) => A6V.Find((ushort)(number - 1))!;

    private static RegisterWriteEncoding Encode(int register, string input) => RegisterWriteEncoder.Encode(Reg(register), input);

    private static void AssertRaw(int register, string input, ushort raw)
    {
        RegisterWriteEncoding e = Encode(register, input);
        Assert.True(e.IsValid, e.Message);
        Assert.Equal(ProfileWriteError.None, e.Error);
        Assert.Equal(raw, e.Raw);
    }

    private static void AssertError(int register, string input, ProfileWriteError error)
    {
        RegisterWriteEncoding e = Encode(register, input);
        Assert.False(e.IsValid);
        Assert.Equal(error, e.Error);
        Assert.Null(e.Raw);
        Assert.NotEmpty(e.Message);
    }

    // Tests 1–8: 223 Temperature offset, Int16 × 0.1, -100…100
    [Theory]
    [InlineData("1.5", 15)]                  // 1
    [InlineData("-2.5", 65511)]              // 2: -25 → 0xFFE7
    [InlineData("0", 0)]                     // 3
    [InlineData("100", 1000)]                // 4: boundary inclusive
    [InlineData("-100", 64536)]              // 5: -1000 → 0xFC18
    public void Reg223_Encodes(string input, ushort raw) => AssertRaw(223, input, raw);

    [Theory]
    [InlineData("100.1", ProfileWriteError.OutOfRange)]                // 6
    [InlineData("-100.1", ProfileWriteError.OutOfRange)]               // 7
    [InlineData("1.55", ProfileWriteError.NotRepresentableByScale)]    // 8: never rounded to 1.5 / 1.6
    public void Reg223_Rejects(string input, ProfileWriteError error) => AssertError(223, input, error);

    [Fact]
    public void Reg223_RawIsHexFFE7ForMinus2Point5() => Assert.Equal((ushort?)0xFFE7, Encode(223, "-2.5").Raw);

    // Tests 9–11: 224 Relative humidity offset, Int16 × 0.1, -100…100
    [Theory]
    [InlineData("2.5", 25)]                  // 9
    [InlineData("-100", 64536)]              // 10
    public void Reg224_Encodes(string input, ushort raw) => AssertRaw(224, input, raw);

    [Theory]
    [InlineData("100.1", ProfileWriteError.OutOfRange)]                // 11
    [InlineData("0.05", ProfileWriteError.NotRepresentableByScale)]
    public void Reg224_Rejects(string input, ProfileWriteError error) => AssertError(224, input, error);

    // Tests 12–16: 225 CO2 offset, Int16 × 1, -2000…2000
    [Theory]
    [InlineData("150", 150)]                 // 12
    [InlineData("-150", 65386)]              // 13
    [InlineData("2000", 2000)]               // 14
    [InlineData("-2000", 63536)]
    public void Reg225_Encodes(string input, ushort raw) => AssertRaw(225, input, raw);

    [Theory]
    [InlineData("2001", ProfileWriteError.OutOfRange)]                 // 15
    [InlineData("-2001", ProfileWriteError.OutOfRange)]
    [InlineData("1.5", ProfileWriteError.NotRepresentableByScale)]     // 16
    public void Reg225_Rejects(string input, ProfileWriteError error) => AssertError(225, input, error);

    // Tests 17–19: 401 System unit, Status
    [Theory]
    [InlineData("Celsius", 0)]               // 17
    [InlineData("Fahrenheit", 1)]            // 18
    public void Reg401_Encodes(string input, ushort raw) => AssertRaw(401, input, raw);

    [Theory]                                 // 19: unknown names and bare numbers are not states
    [InlineData("Kelvin")]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("")]
    public void Reg401_RejectsUnknown(string input) => AssertError(401, input, ProfileWriteError.UnknownStatus);

    // Tests 20–25: 764 Modbus address, UInt16, Scale null (×1), 1…247, 246 excluded
    [Theory]
    [InlineData("1", 1)]                     // 20
    [InlineData("245", 245)]                 // 21
    [InlineData("247", 247)]                 // 22
    public void Reg764_Encodes(string input, ushort raw) => AssertRaw(764, input, raw);

    [Fact]                                   // 23
    public void Reg764_246_IsExcluded_WithDocumentReason()
    {
        RegisterWriteEncoding e = Encode(764, "246");
        Assert.Equal(ProfileWriteError.ExcludedValue, e.Error);
        Assert.Contains("Reserved for on-event addressing", e.Message);
    }

    [Theory]
    [InlineData("0")]                        // 24
    [InlineData("248")]                      // 25
    public void Reg764_OutsideRange(string input) => AssertError(764, input, ProfileWriteError.OutOfRange);

    [Fact]
    public void Reg764_Fraction_NotRepresentable() => AssertError(764, "1.5", ProfileWriteError.NotRepresentableByScale);

    // Tests 26–29: 765 Baud rate, Status
    [Theory]
    [InlineData("Auto", 0)]                  // 26
    [InlineData("9600bps", 1)]               // 27
    [InlineData("19200bps", 2)]
    [InlineData("38400bps", 3)]
    [InlineData("57600bps", 4)]
    [InlineData("76800bps", 5)]
    [InlineData("115200bps", 6)]             // 28
    public void Reg765_Encodes(string input, ushort raw) => AssertRaw(765, input, raw);

    [Theory]                                 // 29
    [InlineData("576000bps")]
    [InlineData("9600")]
    [InlineData("7")]
    public void Reg765_RejectsUnknown(string input) => AssertError(765, input, ProfileWriteError.UnknownStatus);

    // Tests 30–31: 766 Transmission format, Status
    [Theory]                                 // 30
    [InlineData("1-8-E-1", 0)]
    [InlineData("1-8-O-1", 1)]
    [InlineData("1-8-N-1", 2)]
    [InlineData("1-8-N-2", 3)]
    public void Reg766_Encodes(string input, ushort raw) => AssertRaw(766, input, raw);

    [Theory]                                 // 31
    [InlineData("1-7-E-1")]
    [InlineData("8E1")]
    [InlineData("4")]
    public void Reg766_RejectsUnknown(string input) => AssertError(766, input, ProfileWriteError.UnknownStatus);

    // Tests 32–36 (encoder level): read-only registers are refused whatever the value
    [Theory]
    [InlineData(1, "25")]
    [InlineData(2, "No error")]
    [InlineData(3, "50")]
    [InlineData(4, "No error")]
    [InlineData(5, "650")]
    [InlineData(6, "No error")]
    [InlineData(1286, "1")]
    [InlineData(1287, "1")]
    public void ReadOnlyRegisters_AreRefused(int register, string input) => AssertError(register, input, ProfileWriteError.ReadOnly);

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,5")]                      // invariant culture only
    public void NonNumericInput_IsInvalidEngineeringValue(string input) => AssertError(223, input, ProfileWriteError.InvalidEngineeringValue);

    // Tests 40–42: data type limits and two's complement (synthetic definitions without a document range)
    private static RegisterDefinition Synthetic(DataType type, double? scale = null, double? min = null, double? max = null) => new()
    {
        RegisterNumber = 10, Description = "Synthetic", DataType = type, Scale = scale, Min = min, Max = max, Access = RegisterAccess.ReadWrite,
    };

    [Theory]
    [InlineData(-1, 65535)]
    [InlineData(-32768, 32768)]
    [InlineData(32767, 0x7FFF)]
    [InlineData(0, 0)]
    public void Int16_TwosComplement(double value, ushort raw) => Assert.Equal(raw, RegisterWriteEncoder.Encode(Synthetic(DataType.Int16), value).Raw);

    [Theory]
    [InlineData(DataType.Int16, 32768)]
    [InlineData(DataType.Int16, -32769)]
    [InlineData(DataType.UInt16, -1)]
    [InlineData(DataType.UInt16, 65536)]
    public void DataTypeOverflow_IsItsOwnError(DataType type, double value) =>
        Assert.Equal(ProfileWriteError.DataTypeOverflow, RegisterWriteEncoder.Encode(Synthetic(type), value).Error);

    [Fact]
    public void Int16_ScaledOverflow_IsDataTypeOverflow() =>
        Assert.Equal(ProfileWriteError.DataTypeOverflow, RegisterWriteEncoder.Encode(Synthetic(DataType.Int16, 0.1), 3276.8).Error);

    [Fact]
    public void UInt16_FullRange_ScaleNullUsesValueDirectly()
    {
        Assert.Equal((ushort)65535, RegisterWriteEncoder.Encode(Synthetic(DataType.UInt16), 65535).Raw);
        Assert.Equal((ushort)1234, RegisterWriteEncoder.Encode(Synthetic(DataType.UInt16, scale: null), 1234).Raw);   // not ×0, not refused
    }

    [Fact]
    public void FloatingPointNoise_IsNotTreatedAsAFraction()
    {
        Assert.Equal((ushort)3, RegisterWriteEncoder.Encode(Reg(223), 0.1 + 0.2).Raw);   // 0.30000000000000004
        Assert.Equal((ushort)7, RegisterWriteEncoder.Encode(Reg(223), 0.7).Raw);         // 0.7 / 0.1 = 6.999999999999999
    }

    [Theory]
    [InlineData(DataType.UInt32)]
    [InlineData(DataType.Int32)]
    [InlineData(DataType.Float32)]
    public void MultiRegisterTypes_AreUnsupported(DataType type) =>
        Assert.Equal(ProfileWriteError.UnsupportedDataType, RegisterWriteEncoder.Encode(Synthetic(type), 1).Error);

    [Fact]
    public void ExcludedValues_IsGenericMetadata()
    {
        RegisterDefinition d = Synthetic(DataType.UInt16, min: 0, max: 10) with
        {
            ExcludedValues = new Dictionary<double, string> { [3] = "Reserved" },
        };
        Assert.Equal(ProfileWriteError.ExcludedValue, RegisterWriteEncoder.Encode(d, 3).Error);
        Assert.True(RegisterWriteEncoder.Encode(d, 4).IsValid);
    }

    [Fact]
    public void NumericValueForStatusRegister_IsRefused() =>
        Assert.Equal(ProfileWriteError.UnknownStatus, RegisterWriteEncoder.Encode(Reg(401), 1.0).Error);

    // Test 43: DefaultValue is metadata only; encoding it gives the documented raw value (nothing is written)
    [Theory]
    [InlineData(223, 0)]
    [InlineData(224, 0)]
    [InlineData(225, 0)]
    [InlineData(401, 0)]
    [InlineData(764, 1)]
    [InlineData(765, 0)]
    [InlineData(766, 0)]
    public void DefaultValues_Encode(int register, ushort raw)
    {
        RegisterWriteEncoding e = RegisterWriteEncoder.EncodeDefault(Reg(register));
        Assert.True(e.IsValid, e.Message);
        Assert.Equal(raw, e.Raw);
    }

    [Fact]
    public void ReadOnlyRegister_HasNoDefaultToEncode() =>
        Assert.Equal(ProfileWriteError.ReadOnly, RegisterWriteEncoder.EncodeDefault(Reg(1)).Error);

    // Test 44: encode → decode round trip through the existing read path
    [Theory]
    [InlineData(223, "1.5", "1.5")]
    [InlineData(223, "-2.5", "-2.5")]
    [InlineData(224, "-100", "-100")]
    [InlineData(225, "-150", "-150")]
    [InlineData(401, "Fahrenheit", "Fahrenheit")]
    [InlineData(764, "247", "247")]
    [InlineData(765, "115200bps", "115200bps")]
    [InlineData(766, "1-8-N-2", "1-8-N-2")]
    public void EncodeThenDecode_RoundTrips(int register, string input, string valueText)
    {
        RegisterDefinition d = Reg(register);
        ushort raw = RegisterWriteEncoder.Encode(d, input).Raw!.Value;
        DecodedRegister decoded = RegisterDecoder.DecodeEngineering(register, d.PduAddress, raw, d);
        Assert.Equal(valueText, decoded.ValueText);
        Assert.Equal(RegisterQuality.Ok, decoded.Quality);
    }

    // Metadata the write path relies on
    [Fact]
    public void A6V_WritableRegisters_AndHolds()
    {
        Assert.Equal([223, 224, 225, 401, 764, 765, 766], A6V.Registers.Where(r => r.IsWritable).Select(r => r.RegisterNumber));
        Assert.Equal([764, 765, 766], A6V.Registers.Where(r => r.WriteHoldReason is not null).Select(r => r.RegisterNumber));
        Assert.Equal([764], A6V.Registers.Where(r => r.ExcludedValues.Count > 0).Select(r => r.RegisterNumber));
    }
}

/// <summary>Phase 4B: ProfileWriteService – validation before the transport, FC06 only, holds for 764–766.</summary>
public class ProfileWriteServiceTests
{
    private static readonly DeviceProfile A6V = A6V12258321Profile.Profile;

    /// <summary>The device's System unit as read before writing (Phase 4F-1.1: Reg 223 needs a known 401).</summary>
    private static readonly IReadOnlyDictionary<int, ushort> Celsius = new Dictionary<int, ushort> { [401] = 0 };

    private static async Task<(ProfileWriteService Writer, ModbusService Service, FakeModbusTcpSlave Slave)> ConnectAsync()
    {
        var slave = new FakeModbusTcpSlave { Registers = new ushort[1300] };
        var service = new ModbusService(new CommunicationLogger());
        Assert.True(await service.ConnectAsync(new ModbusTcpTransport(new TcpSettings("127.0.0.1", slave.Port))));
        return (new ProfileWriteService(service), service, slave);
    }

    // Tests 1, 2, 12 end-to-end: one FC06 frame with the encoded raw value, register updated
    [Theory]
    [InlineData(223, "1.5", "06 00 DE 00 0F", 15)]
    [InlineData(223, "-2.5", "06 00 DE FF E7", 65511)]
    [InlineData(224, "2.5", "06 00 DF 00 19", 25)]
    [InlineData(225, "150", "06 00 E0 00 96", 150)]
    [InlineData(401, "Fahrenheit", "06 01 90 00 01", 1)]
    public async Task Writable_SendsOneFc06(int register, string input, string pdu, ushort raw)
    {
        var (writer, service, slave) = await ConnectAsync();
        await using var _ = slave;
        await using var __ = service;

        ProfileWriteResult result = await writer.WriteAsync(A6V, 1, register, AddressBase.OneBased, input, Celsius);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal([pdu], slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..])));
        Assert.Equal(raw, slave.Registers[register - 1]);
        Assert.Equal(ModbusFunction.WriteSingleRegister, result.Request!.Function);
        Assert.Contains(service.Logger.Entries, e => e.Message.StartsWith($"FC06  Unit ID = 1  Address = {register} "));
    }

    // Tests 32–36: read-only registers never reach the transport (send count 0)
    [Theory]
    [InlineData(1, "25")]
    [InlineData(2, "No error")]
    [InlineData(5, "650")]
    [InlineData(1286, "1")]
    [InlineData(1287, "1")]
    public async Task ReadOnly_NothingSent(int register, string input)
    {
        var (writer, service, slave) = await ConnectAsync();
        await using var _ = slave;
        await using var __ = service;

        ProfileWriteResult result = await writer.WriteAsync(A6V, 1, register, AddressBase.OneBased, input, Celsius);

        Assert.Equal(ProfileWriteError.ReadOnly, result.Error);
        Assert.False(result.IsSent);
        Assert.Null(result.Request);
        Assert.Empty(slave.ReceivedAdus);
        Assert.DoesNotContain(service.Logger.Entries, e => e.Message.StartsWith("FC06"));
    }

    // Test 37: an address that is not in the profile
    [Theory]
    [InlineData(7)]
    [InlineData(222)]
    [InlineData(226)]
    [InlineData(1288)]
    public async Task NotDefined_NothingSent(int register)
    {
        var (writer, service, slave) = await ConnectAsync();
        await using var _ = slave;
        await using var __ = service;

        ProfileWriteResult result = await writer.WriteAsync(A6V, 1, register, AddressBase.OneBased, "1", Celsius);

        Assert.Equal(ProfileWriteError.RegisterNotDefined, result.Error);
        Assert.Empty(slave.ReceivedAdus);
    }

    [Theory]
    [InlineData(0, AddressBase.OneBased)]
    [InlineData(-1, AddressBase.ZeroBased)]
    [InlineData(70000, AddressBase.OneBased)]
    public void InvalidAddress_IsNotDefined(int address, AddressBase addressBase) =>
        Assert.Equal(ProfileWriteError.RegisterNotDefined, ProfileWriteService.Prepare(A6V, 1, address, addressBase, "1").Error);

    [Fact]
    public void GenericProfile_HasNoRegistersToWrite() =>
        Assert.Equal(ProfileWriteError.RegisterNotDefined, ProfileWriteService.Prepare(DeviceProfile.Generic, 1, 223, AddressBase.OneBased, "1").Error);

    // Tests 38–39: 1-based 223 and 0-based 222 are the same register and the same PDU 0x00DE
    [Theory]
    [InlineData(223, AddressBase.OneBased)]
    [InlineData(222, AddressBase.ZeroBased)]
    public async Task AddressBase_SameRegisterSamePdu(int address, AddressBase addressBase)
    {
        var (writer, service, slave) = await ConnectAsync();
        await using var _ = slave;
        await using var __ = service;

        ProfileWriteResult result = await writer.WriteAsync(A6V, 1, address, addressBase, "1.5", Celsius);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(223, result.Definition!.RegisterNumber);
        Assert.Equal(0x00DE, result.Request!.PduAddress);
        Assert.Equal(["06 00 DE 00 0F"], slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..])));
    }

    [Fact]
    public void ZeroBased223_IsRegister224()
    {
        ProfileWriteResult result = ProfileWriteService.Prepare(A6V, 1, 223, AddressBase.ZeroBased, "1.5");
        Assert.Equal(224, result.Definition!.RegisterNumber);
        Assert.Equal(0x00DF, result.Request!.PduAddress);
    }

    // Validation failures of writable registers are also stopped before the transport
    [Theory]
    [InlineData(223, "100.1", ProfileWriteError.OutOfRange)]
    [InlineData(223, "1.55", ProfileWriteError.NotRepresentableByScale)]
    [InlineData(223, "abc", ProfileWriteError.InvalidEngineeringValue)]
    [InlineData(401, "Kelvin", ProfileWriteError.UnknownStatus)]
    [InlineData(764, "246", ProfileWriteError.ExcludedValue)]
    [InlineData(764, "248", ProfileWriteError.OutOfRange)]
    [InlineData(765, "576000bps", ProfileWriteError.UnknownStatus)]
    public async Task InvalidValue_NothingSent(int register, string input, ProfileWriteError error)
    {
        var (writer, service, slave) = await ConnectAsync();
        await using var _ = slave;
        await using var __ = service;

        ProfileWriteResult result = await writer.WriteAsync(A6V, 1, register, AddressBase.OneBased, input, Celsius);

        Assert.Equal(error, result.Error);
        Assert.False(result.IsSent);
        Assert.Empty(slave.ReceivedAdus);
    }

    // Test 45: 764–766 are encoded and validated, but not sent yet
    [Theory]
    [InlineData(764, "247", 247)]
    [InlineData(765, "9600bps", 1)]
    [InlineData(766, "1-8-N-1", 2)]
    public async Task CommunicationRegisters_EncodedButHeld(int register, string input, ushort raw)
    {
        var (writer, service, slave) = await ConnectAsync();
        await using var _ = slave;
        await using var __ = service;

        ProfileWriteResult result = await writer.WriteAsync(A6V, 1, register, AddressBase.OneBased, input, Celsius);

        Assert.Equal(ProfileWriteError.WriteOnHold, result.Error);
        Assert.Equal(raw, result.Encoding!.Raw);
        Assert.Equal([raw], result.Request!.Values);
        Assert.False(result.IsSent);
        Assert.False(result.IsSuccess);
        Assert.Empty(slave.ReceivedAdus);
        Assert.Equal(0, slave.Registers[register - 1]);
    }

    // Test 46: profile writes are FC06 only (no FC16 capability is claimed), and a device error is a communication failure
    [Fact]
    public void ProfileWriteFunction_IsFc06() => Assert.Equal(ModbusFunction.WriteSingleRegister, ProfileWriteService.WriteFunction);

    [Fact]
    public async Task ModbusException_IsCommunicationFailed()
    {
        var (writer, service, slave) = await ConnectAsync();
        await using var _ = slave;
        await using var __ = service;
        slave.ForcedExceptionCode = 0x02;

        ProfileWriteResult result = await writer.WriteAsync(A6V, 1, 223, AddressBase.OneBased, "1.5", Celsius);

        Assert.Equal(ProfileWriteError.CommunicationFailed, result.Error);
        Assert.True(result.IsSent);
        Assert.False(result.IsSuccess);
        Assert.Equal(ResultStatus.ModbusException, result.Transport!.Status);
        Assert.Single(slave.ReceivedAdus);
    }

    [Fact]
    public async Task NotConnected_IsCommunicationFailed()
    {
        await using var service = new ModbusService(new CommunicationLogger());
        ProfileWriteResult result = await new ProfileWriteService(service).WriteAsync(A6V, 1, 223, AddressBase.OneBased, "1.5", Celsius);
        Assert.Equal(ProfileWriteError.CommunicationFailed, result.Error);
        Assert.Equal(ResultStatus.NotConnected, result.Transport!.Status);
    }

    // Over RTU the same FC06 frame (with CRC) is produced
    [Fact]
    public async Task Rtu_SameFc06Frame()
    {
        var rtu = new FakeRtuSlave();
        var channel = new FakeSerialChannel { Responder = rtu.Respond };
        await using var service = new ModbusService(new CommunicationLogger());
        Assert.True(await service.ConnectAsync(new ModbusRtuTransport(new RtuSettings("COM_TEST", TimeoutMs: 1000), _ => channel)));

        ProfileWriteResult result = await new ProfileWriteService(service).WriteAsync(A6V, 1, 223, AddressBase.OneBased, "1.5", Celsius);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(["01 06 00 DE 00 0F A9 F4"], rtu.Requests.Select(HexFormatter.Format));
    }
}
