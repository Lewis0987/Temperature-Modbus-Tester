using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Services;
using ModbusTester.Core.Transport;

namespace ModbusTester.Tests;

/// <summary>
/// Phase 4F-3 – Reg 223 Fahrenheit SOFTWARE verification (no real sensor). What the tester controls: encoding per the
/// known 401 context (value / 0.1, never a °C ↔ °F conversion), ranges, and the RTU frames of the planned experiment.
/// What the real sensor does to Reg 223 when 401 changes is NOT covered here: PENDING PHYSICAL VERIFICATION.
/// The expected frames below were computed independently (offline CRC16) and cross-checked with frames recorded
/// from the real sensor where available (e.g. 01 06 01 90 00 00 88 1B, 01 03 02 00 00 B8 44).
/// </summary>
public class Reg223FahrenheitSoftwareTests
{
    private static readonly DeviceProfile A6V = A6V12258321Profile.Profile;
    private static readonly IReadOnlyDictionary<int, ushort> Celsius = new Dictionary<int, ushort> { [401] = 0 };
    private static readonly IReadOnlyDictionary<int, ushort> Fahrenheit = new Dictionary<int, ushort> { [401] = 1 };

    private static IReadOnlyDictionary<int, ushort> Context(ushort systemUnit) => systemUnit == 0 ? Celsius : Fahrenheit;

    private static byte[] Bytes(string hex) => [.. hex.Split(' ').Select(b => Convert.ToByte(b, 16))];

    // A: engineering value / 0.1 → raw in the current unit; the tester never converts between °C and °F
    [Theory]
    [InlineData(0, "1.0", 10, "000A", "°C")]
    [InlineData(1, "1.0", 10, "000A", "°F")]    // +1.0 °F is raw 10 – not 18 (that would be a conversion)
    [InlineData(1, "1.8", 18, "0012", "°F")]    // +1.8 °F is raw 18 – not 10
    [InlineData(0, "1.8", 18, "0012", "°C")]
    [InlineData(1, "-1.0", 65526, "FFF6", "°F")]
    public void Encode_ValueOverScale_NoUnitConversion(ushort systemUnit, string input, int raw, string hex, string unit)
    {
        ProfileWriteResult r = ProfileWriteService.Prepare(A6V, 1, 223, AddressBase.OneBased, input, Context(systemUnit));
        Assert.Equal(ProfileWriteError.None, r.Error);
        Assert.Equal((ushort)raw, r.Request!.Values[0]);
        Assert.Equal(hex, r.Request.Values[0].ToString("X4"));
        Assert.Equal(unit, r.Definition!.Unit);
        Assert.Equal(0.1, r.Definition.Scale);
    }

    // B: both ranges, inclusive boundaries, both sides rejected outside
    [Theory]
    [InlineData(0, "-100", ProfileWriteError.None)]
    [InlineData(0, "100", ProfileWriteError.None)]
    [InlineData(0, "-100.1", ProfileWriteError.OutOfRange)]
    [InlineData(0, "100.1", ProfileWriteError.OutOfRange)]
    [InlineData(0, "180", ProfileWriteError.OutOfRange)]      // a °F-only value is refused in °C
    [InlineData(1, "-180", ProfileWriteError.None)]
    [InlineData(1, "180", ProfileWriteError.None)]
    [InlineData(1, "-180.1", ProfileWriteError.OutOfRange)]
    [InlineData(1, "180.1", ProfileWriteError.OutOfRange)]
    public void Range_PerSystemUnit(ushort systemUnit, string input, ProfileWriteError error)
    {
        ProfileWriteResult r = ProfileWriteService.Prepare(A6V, 1, 223, AddressBase.OneBased, input, Context(systemUnit));
        Assert.Equal(error, r.Error);
        Assert.Equal(error == ProfileWriteError.None, r.Request is not null);
    }

    // G: the planned experiment's requests are built exactly as expected …
    [Theory]
    [InlineData(223, "1.0", 0, "01 06 00 DE 00 0A 69 F7", "01 03 00 DE 00 01 E4 30")]
    [InlineData(401, "Fahrenheit", 0, "01 06 01 90 00 01 49 DB", "01 03 01 90 00 01 85 DB")]
    [InlineData(401, "Celsius", 1, "01 06 01 90 00 00 88 1B", "01 03 01 90 00 01 85 DB")]
    [InlineData(223, "0", 0, "01 06 00 DE 00 00 E9 F0", "01 03 00 DE 00 01 E4 30")]
    [InlineData(223, "0", 1, "01 06 00 DE 00 00 E9 F0", "01 03 00 DE 00 01 E4 30")]   // emergency restore in °F context
    public void Frames_Build(int register, string input, ushort systemUnit, string fc06, string verify)
    {
        ModbusWriteRequest request = ProfileWriteService.Prepare(A6V, 1, register, AddressBase.OneBased, input, Context(systemUnit)).Request!;
        Assert.Equal(fc06, HexFormatter.Format(ModbusRtuFrame.Build(1, ModbusPdu.BuildWriteSingleRegister(request.PduAddress, request.Values[0]))));
        Assert.Equal(verify, HexFormatter.Format(ModbusRtuFrame.Build(1, ModbusPdu.BuildReadRequest(ModbusFunction.ReadHoldingRegisters, request.PduAddress, 1))));
    }

    // … and the matching responses parse: CRC, function, echoed address / value, read-back raw
    [Theory]
    [InlineData("01 06 00 DE 00 0A 69 F7", 0x00DE, 10, "01 03 02 00 0A 38 43")]
    [InlineData("01 06 01 90 00 01 49 DB", 0x0190, 1, "01 03 02 00 01 79 84")]
    [InlineData("01 06 01 90 00 00 88 1B", 0x0190, 0, "01 03 02 00 00 B8 44")]
    [InlineData("01 06 00 DE 00 00 E9 F0", 0x00DE, 0, "01 03 02 00 00 B8 44")]
    public void Frames_ParseResponses(string echo, int pdu, int raw, string verifyRx)
    {
        var details = new List<string>();
        byte[] echoPdu = ModbusRtuFrame.Validate(Bytes(echo), 1, details);
        Assert.Equal((byte)ModbusFunction.WriteSingleRegister, echoPdu[0]);
        ModbusPdu.ParseWriteSingleRegisterResponse(echoPdu, (ushort)pdu, (ushort)raw);
        byte[] readPdu = ModbusRtuFrame.Validate(Bytes(verifyRx), 1, details);
        Assert.Equal((byte)ModbusFunction.ReadHoldingRegisters, readPdu[0]);
        Assert.Equal([(ushort)raw], ModbusPdu.ParseReadRegistersResponse(ModbusFunction.ReadHoldingRegisters, readPdu, 1));
        Assert.Equal(2, details.Count(d => d.EndsWith("CRC PASS")));
    }

    [Theory]
    [InlineData("01 06 00 DE 00 0A 69 F6")]   // CRC changed
    [InlineData("01 06 00 DE 00 0B 69 F7")]   // value changed, CRC no longer matches
    public void Frames_CorruptedEcho_IsCrcError(string frame) =>
        Assert.Equal(ResultStatus.CrcError, Assert.Throws<ModbusCommException>(() => ModbusRtuFrame.Validate(Bytes(frame), 1, [])).Status);

    [Fact]
    public void Frames_EchoOfOtherValue_IsRejected() =>
        Assert.ThrowsAny<Exception>(() => ModbusPdu.ParseWriteSingleRegisterResponse(
            ModbusRtuFrame.Validate(Bytes("01 06 00 DE 00 00 E9 F0"), 1, []), 0x00DE, 10));

    // The planned STEP 3 read (Reg 223 × 3) for each candidate sensor answer: the tester only relabels, never converts
    [Theory]
    [InlineData("01 03 06 00 0A 00 00 00 00 B9 74", 10, "1")]
    [InlineData("01 03 06 00 12 00 00 00 00 99 76", 18, "1.8")]
    [InlineData("01 03 06 01 52 00 00 00 00 99 68", 338, "33.8")]
    public void Step3CandidateResponses_DecodeWithoutConversion(string frame, int raw, string valueText)
    {
        ushort[] words = ModbusPdu.ParseReadRegistersResponse(ModbusFunction.ReadHoldingRegisters, ModbusRtuFrame.Validate(Bytes(frame), 1, []), 3);
        Assert.Equal((ushort)raw, words[0]);
        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode(
        [
            new ModbusResult { Request = new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, 223, 3), Status = ResultStatus.Pass, Registers = words },
            new ModbusResult { Request = new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, 401, 1), Status = ResultStatus.Pass, Registers = [1] },
        ], A6V, _ => 1);
        DecodedRegister r223 = rows.Single(r => r.Address == 223);
        Assert.Equal((valueText, "°F"), (r223.ValueText, r223.Unit));
    }

    // C: the context only learns from successful FC03 results (an FC06 timeout gives no read result at all)
    [Fact]
    public async Task Fc06Timeout_NoReadResult_ContextUnchanged()
    {
        var rtu = new FakeRtuSlave { Registers = new ushort[1300] };
        // The slave applies the FC06 but its response is lost (what a real timeout can look like)
        var channel = new FakeSerialChannel
        {
            Responder = f =>
            {
                var response = rtu.Respond(f);
                return f[1] == 0x06 ? [] : response;
            },
        };
        await using var service = new ModbusService(new CommunicationLogger());
        Assert.True(await service.ConnectAsync(new ModbusRtuTransport(new RtuSettings("COM_TEST", TimeoutMs: 200), _ => channel)));
        var context = new ProfileUnitContext();
        context.Record(A6V, [await service.ReadRegistersAsync(new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, 401, 1))]);

        ProfileWriteResult r = await new ProfileWriteService(service).WriteAsync(A6V, 1, 401, AddressBase.OneBased, "Fahrenheit", context.For(1));

        Assert.Equal(ProfileWriteError.CommunicationFailed, r.Error);
        Assert.Equal(ResultStatus.Timeout, r.Transport!.Status);
        Assert.Equal(1, rtu.Registers[400]);                     // the slave did apply it (response lost) …
        Assert.Equal(0, context.For(1)[401]);                    // … but nothing verified it: context still Celsius
    }
}
