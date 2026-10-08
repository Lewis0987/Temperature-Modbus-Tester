using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Services;

namespace ModbusTester.Tests;

/// <summary>
/// Phase 3D.1: software version per Siemens A6V12045847 – Major 1 byte, Minor 1 byte, Build 2 bytes,
/// example [2.01.33] = 0x02010021 (register 1286 = 0x0201, register 1287 = 0x0021).
/// </summary>
public class SoftwareVersionTests
{
    private static readonly DeviceProfile A6V = A6V12258321Profile.Profile;

    // Tests 1–4
    [Theory]
    [InlineData(0x0201, 2, 1)]
    [InlineData(0x0102, 1, 2)]
    [InlineData(0x0000, 0, 0)]
    [InlineData(0xFFFF, 255, 255)]
    public void MajorMinor_FromWord(int word, int major, int minor)
    {
        Assert.Equal(major, SoftwareVersion.MajorOf((ushort)word));
        Assert.Equal(minor, SoftwareVersion.MinorOf((ushort)word));
    }

    // Tests 5–6
    [Theory]
    [InlineData(0x0021, 33)]
    [InlineData(0x007B, 123)]
    public void Build_FromWord(int word, int build) => Assert.Equal(build, SoftwareVersion.FromRegisters(0, (ushort)word).Build);

    // Tests 7–9: document example, simulator data, two-digit Minor
    [Theory]
    [InlineData(0x0201, 0x0021, "2.01.33")]   // A6V12045847 example [2.01.33] = 0x02010021
    [InlineData(258, 123, "1.02.123")]        // simulator defaults
    [InlineData(0x0000, 0x0000, "0.00.0")]
    [InlineData(0x0A0C, 0x0001, "10.12.1")]
    [InlineData(0xFFFF, 0xFFFF, "255.255.65535")]
    public void Version_Text(int majorMinor, int build, string text)
    {
        SoftwareVersion v = SoftwareVersion.FromRegisters((ushort)majorMinor, (ushort)build);
        Assert.Equal(text, v.ToString());
        Assert.NotEqual("2.1.33", SoftwareVersion.FromRegisters(0x0201, 0x0021).ToString());
    }

    [Theory]
    [InlineData(0x0201, "2.01")]
    [InlineData(0x0102, "1.02")]
    [InlineData(0x0109, "1.09")]
    [InlineData(0x010A, "1.10")]
    public void MajorMinorText_TwoDigitMinor(int word, string text) => Assert.Equal(text, SoftwareVersion.MajorMinorText((ushort)word));

    [Fact]
    public void DocumentExample_ThroughProfileDecode()
    {
        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode(Result(1286, 0x0201, 0x0021), A6V, _ => 1);

        Assert.Equal(["2.01", "33"], rows.Select(r => r.ValueText));
        Assert.Equal(new SoftwareVersion(2, 1, 33), SoftwareVersion.Find(rows));
        Assert.Equal("2.01.33", SoftwareVersion.Find(rows).ToString());
    }

    // Test 10: Generic keeps raw registers (no version meaning)
    [Fact]
    public void Generic_NoVersionDecode()
    {
        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode(Result(1286, 513, 33), DeviceProfile.Generic, _ => 1);

        Assert.Equal(["513", "33"], rows.Select(r => r.ValueText));
        Assert.All(rows, r => Assert.Null(r.Definition));
        Assert.Null(SoftwareVersion.Find(rows));
    }

    [Fact]
    public void OnlyOneVersionWordRead_NoFullVersion()
    {
        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode(Result(1286, 0x0201), A6V, _ => 1);
        Assert.Equal("2.01", rows.Single().ValueText);
        Assert.Null(SoftwareVersion.Find(rows));
    }

    // Test 12: 0-based Address 1285 is the same register 1286
    [Fact]
    public void ZeroBased_SameDecode()
    {
        var result = new ModbusResult
        {
            Request = new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, 1285, 2, AddressBase.ZeroBased),
            Status = ResultStatus.Pass,
            Registers = [0x0201, 0x0021],
        };
        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode(result, A6V, _ => 1);
        Assert.Equal([1285, 1286], rows.Select(r => r.Address));
        Assert.Equal([(ushort)0x0505, (ushort)0x0506], rows.Select(r => r.PduAddress));
        Assert.Equal("2.01.33", SoftwareVersion.Find(rows).ToString());
    }

    // Version registers stay read-only for the write path
    [Theory]
    [InlineData(1286)]
    [InlineData(1287)]
    public void VersionRegisters_StillReadOnly(int register) =>
        Assert.Equal(ProfileWriteError.ReadOnly, RegisterWriteEncoder.Encode(A6V.Find((ushort)(register - 1))!, "1").Error);

    // Tests 14–16: document reconciliation keeps the earlier write rules and 401 as they were
    [Fact]
    public void Reconciliation_KeepsWriteRules()
    {
        RegisterDefinition r764 = A6V.Find(763)!;
        Assert.Equal("Reserved for on-event addressing", r764.ExcludedValues[246]);
        Assert.Equal(1, r764.Min);
        Assert.Equal(247, r764.Max);
        Assert.Equal(1, r764.DefaultValue);
        Assert.Equal([764, 765, 766], A6V.Registers.Where(r => r.WriteHoldReason is not null).Select(r => r.RegisterNumber));
        Assert.Equal(0, A6V.Find(764)!.DefaultValue);                                  // 765 default 0 = Auto
        Assert.Equal("Auto", A6V.Find(764)!.States[0].Text);
        Assert.Equal(0, A6V.Find(765)!.DefaultValue);                                  // 766 default 0 = 1-8-E-1
        Assert.Equal(ModbusFunction.ReadHoldingRegisters, A6V.RequiredReadFunction);
        Assert.Equal(ModbusFunction.WriteSingleRegister, ProfileWriteService.WriteFunction);

        RegisterDefinition r401 = A6V.Find(400)!;
        Assert.Equal(["Celsius", "Fahrenheit"], r401.States.OrderBy(s => s.Key).Select(s => s.Value.Text));
        Assert.Equal("°C", A6V.Find(0)!.Unit);                                        // Phase 3E still pending: no °F handling
        Assert.Equal("°C", A6V.Find(222)!.Unit);
        Assert.Equal(RegisterValueEncoding.Plain, r401.ValueEncoding);
    }

    [Fact]
    public void OnlyVersionRegistersHaveAnEncoding() =>
        Assert.Equal([1286, 1287], A6V.Registers.Where(r => r.ValueEncoding != RegisterValueEncoding.Plain).Select(r => r.RegisterNumber));

    private static ModbusResult Result(int address, params ushort[] registers) => new()
    {
        Request = new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, address, (ushort)registers.Length),
        Status = ResultStatus.Pass,
        Registers = registers,
    };
}
