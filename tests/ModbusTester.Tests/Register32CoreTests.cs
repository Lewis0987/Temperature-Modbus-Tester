using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Services;
using ModbusTester.Core.Transport;

namespace ModbusTester.Tests;

/// <summary>Phase 5A: 32-bit core read – UInt32 / Int32 / Float32 over two registers in the four byte arrangements.</summary>
public class Register32DecoderTests
{
    public static readonly Register32Order[] Orders = [Register32Order.ABCD, Register32Order.BADC, Register32Order.CDAB, Register32Order.DCBA];

    /// <summary>
    /// Independent of the decoder: the two wire words carrying the 32-bit pattern <paramref name="value"/> (A = most
    /// significant byte) in <paramref name="order"/> (letters = bytes in wire order).
    /// </summary>
    public static ushort[] Wire(uint value, Register32Order order)
    {
        var bytes = new Dictionary<char, byte> { ['A'] = (byte)(value >> 24), ['B'] = (byte)(value >> 16), ['C'] = (byte)(value >> 8), ['D'] = (byte)value };
        string letters = order.ToString();
        return [(ushort)(bytes[letters[0]] << 8 | bytes[letters[1]]), (ushort)(bytes[letters[2]] << 8 | bytes[letters[3]])];
    }

    private static uint FloatBits(float f) => BitConverter.SingleToUInt32Bits(f);

    // RegisterCount
    [Theory]
    [InlineData(DataType.UInt16, 1)]
    [InlineData(DataType.Int16, 1)]
    [InlineData(DataType.Status, 1)]
    [InlineData(DataType.UInt32, 2)]
    [InlineData(DataType.Int32, 2)]
    [InlineData(DataType.Float32, 2)]
    public void RegisterCount_ByType(DataType type, int count) => Assert.Equal(count, RegisterDecoder.RegisterCount(type));

    // The four arrangements of 0x12345678 written out literally
    [Theory]
    [InlineData(Register32Order.ABCD, 0x1234, 0x5678)]
    [InlineData(Register32Order.BADC, 0x3412, 0x7856)]
    [InlineData(Register32Order.CDAB, 0x5678, 0x1234)]
    [InlineData(Register32Order.DCBA, 0x7856, 0x3412)]
    public void Combine32_Literal(Register32Order order, int first, int second)
    {
        Assert.Equal(0x12345678u, RegisterDecoder.Combine32((ushort)first, (ushort)second, order));
        Assert.Equal([(ushort)first, (ushort)second], Wire(0x12345678, order));
        Assert.Equal(305419896d, RegisterDecoder.Decode([(ushort)first, (ushort)second], DataType.UInt32, order));
    }

    // UInt32: boundaries in every order
    [Theory]
    [InlineData(0x00000000u, 0d)]
    [InlineData(0x00000001u, 1d)]
    [InlineData(0x12345678u, 305419896d)]
    [InlineData(0x80000000u, 2147483648d)]
    [InlineData(0xFFFFFFFFu, 4294967295d)]
    public void UInt32_AllOrders(uint bits, double expected)
    {
        foreach (Register32Order order in Orders)
            Assert.Equal(expected, RegisterDecoder.Decode(Wire(bits, order), DataType.UInt32, order));
    }

    // Int32: two's complement in every order
    [Theory]
    [InlineData(0x00000000u, 0)]
    [InlineData(0x00000001u, 1)]
    [InlineData(0xFFFFFFFFu, -1)]
    [InlineData(0x80000000u, int.MinValue)]
    [InlineData(0x7FFFFFFFu, int.MaxValue)]
    [InlineData(0xFFFFFF85u, -123)]
    public void Int32_AllOrders(uint bits, int expected)
    {
        foreach (Register32Order order in Orders)
            Assert.Equal(expected, RegisterDecoder.Decode(Wire(bits, order), DataType.Int32, order));
    }

    // Float32: IEEE-754 single (not a numeric reinterpretation of the integer) in every order
    [Theory]
    [InlineData(0x3F800000u, 1.0)]
    [InlineData(0xBF800000u, -1.0)]
    [InlineData(0x00000000u, 0.0)]
    [InlineData(0x3FC00000u, 1.5)]
    [InlineData(0x41200000u, 10.0)]
    public void Float32_AllOrders(uint bits, double expected)
    {
        foreach (Register32Order order in Orders)
            Assert.Equal(expected, RegisterDecoder.Decode(Wire(bits, order), DataType.Float32, order));
        if (bits != 0)   // the float is the IEEE-754 meaning of the bits, not the integer (0x3F800000 = 1065353216 as UInt32)
            Assert.NotEqual(expected, RegisterDecoder.Decode(Wire(bits, Register32Order.ABCD), DataType.UInt32, Register32Order.ABCD));
    }

    [Theory]
    [InlineData(0x7FC00000u, "NaN")]
    [InlineData(0x7F800000u, "+Infinity")]
    [InlineData(0xFF800000u, "-Infinity")]
    public void Float32_NonFinite_AllOrders(uint bits, string text)
    {
        foreach (Register32Order order in Orders)
        {
            double value = RegisterDecoder.Decode(Wire(bits, order), DataType.Float32, order);
            Assert.Equal(text, RegisterDecoder.NonFiniteText(value));
        }
    }

    [Fact]
    public void Float32_NegativeZero_IsAnOrdinaryZero() =>
        Assert.Null(RegisterDecoder.NonFiniteText(RegisterDecoder.Decode([0x8000, 0x0000], DataType.Float32, Register32Order.ABCD)));

    // A wrong order really changes the value (the order is not ignored)
    [Fact]
    public void WrongOrder_GivesAnotherValue() =>
        Assert.NotEqual(1.0, RegisterDecoder.Decode(Wire(0x3F800000, Register32Order.ABCD), DataType.Float32, Register32Order.CDAB));

    // Insufficient data: reported, never padded
    [Theory]
    [InlineData(DataType.UInt32)]
    [InlineData(DataType.Int32)]
    [InlineData(DataType.Float32)]
    public void OneWord_For32Bit_Throws(DataType type)
    {
        Assert.Throws<ArgumentException>(() => RegisterDecoder.Decode([0x1234], type, Register32Order.ABCD));
        Assert.Throws<ArgumentException>(() => RegisterDecoder.Decode([], type, Register32Order.ABCD));
    }

    // Compatibility: the existing 16-bit ByteOrder behaviour and the old overload for 32-bit types
    [Fact]
    public void LegacyByteOrder_Compatible()
    {
        Assert.Equal(0x3412, RegisterDecoder.Decode([0x1234], DataType.UInt16, ByteOrder.ByteSwap));
        Assert.Equal(0x3412, RegisterDecoder.Decode([0x1234], DataType.UInt16, ByteOrder.LittleEndian));
        Assert.Equal(0x1234, RegisterDecoder.Decode([0x1234], DataType.UInt16, ByteOrder.WordSwap));
        Assert.Equal(-2, RegisterDecoder.Decode([0xFFFE], DataType.Int16));
        Assert.Equal(305419896d, RegisterDecoder.Decode([0x5678, 0x1234], DataType.UInt32, ByteOrder.WordSwap));   // = CDAB
        Assert.Equal(Register32Order.ABCD, RegisterDecoder.ToRegister32Order(ByteOrder.BigEndian));
        Assert.Equal(Register32Order.DCBA, RegisterDecoder.ToRegister32Order(ByteOrder.LittleEndian));
        Assert.Equal(Register32Order.CDAB, RegisterDecoder.ToRegister32Order(ByteOrder.WordSwap));
        Assert.Equal(Register32Order.BADC, RegisterDecoder.ToRegister32Order(ByteOrder.ByteSwap));
    }

    // Scale (Float32 allowed): engineering = decoded × scale, without float noise and without losing small values
    [Theory]
    [InlineData(DataType.Float32, 1.5, 0.1, 0.15)]
    [InlineData(DataType.Float32, 1.1, 1, 1.1)]          // 1.1f is 1.100000023841858 as double: shown as 1.1
    [InlineData(DataType.Float32, 1e-7, 1, 1e-7)]        // not rounded to 0
    [InlineData(DataType.Float32, 16777216, 1, 16777216)]
    [InlineData(DataType.UInt32, 305419896, 0.01, 3054198.96)]
    [InlineData(DataType.Int32, -123, 0.1, -12.3)]
    [InlineData(DataType.Int16, 2534, 0.01, 25.34)]      // 16-bit unchanged
    public void ScaleValue(DataType type, double decoded, double scale, double expected) =>
        Assert.Equal(expected, RegisterDecoder.ScaleValue(type, type == DataType.Float32 ? (float)decoded : decoded, scale));
}

/// <summary>Phase 5A: decoded 32-bit rows – profile definition and generic.</summary>
public class Register32DecodedRegisterTests
{
    private static RegisterDefinition Def(DataType type, Register32Order order = Register32Order.ABCD, double? scale = null,
        double? min = null, double? max = null, int number = 100) => new()
        {
            RegisterNumber = number, Description = $"{type} test", DataType = type, Order32 = order, Scale = scale, Min = min, Max = max, Unit = "u",
        };

    [Fact]
    public void Definition_SpanDerivedFromType()
    {
        Assert.Equal((1, 100), (Def(DataType.UInt16).RegisterCount, Def(DataType.UInt16).LastRegisterNumber));
        Assert.Equal((2, 101), (Def(DataType.Float32).RegisterCount, Def(DataType.Float32).LastRegisterNumber));
        Assert.Equal((2, 101), (Def(DataType.UInt32).RegisterCount, Def(DataType.UInt32).LastRegisterNumber));
        Assert.Equal(Register32Order.ABCD, new RegisterDefinition { RegisterNumber = 1, Description = "d", DataType = DataType.Int32 }.Order32);
    }

    [Fact]
    public void UInt32Row_ModelAndFormatting()
    {
        DecodedRegister r = RegisterDecoder.DecodeEngineering(100, 99, [0x1234, 0x5678], Def(DataType.UInt32));
        Assert.Equal((0x1234, (ushort?)0x5678, 0x12345678u), (r.Raw, r.SecondWord, r.Bits32!.Value));
        Assert.Equal([(ushort)0x1234, (ushort)0x5678], r.Words);
        Assert.Equal(2, r.RegisterCount);
        Assert.Equal("1234 5678", r.RawWordsText);
        Assert.Equal("12345678", r.Hex);
        Assert.Equal("0001 0010 0011 0100 0101 0110 0111 1000", r.Binary);
        Assert.Equal(305419896L, r.Integer);
        Assert.Equal(("305419896", RegisterQuality.Ok), (r.ValueText, r.Quality));
        Assert.Equal("u", r.Unit);
    }

    [Theory]
    [InlineData(Register32Order.CDAB, 0x5678, 0x1234)]
    [InlineData(Register32Order.DCBA, 0x7856, 0x3412)]
    public void OrderedRow_HexIsTheValue_RawWordsAsRead(Register32Order order, int first, int second)
    {
        DecodedRegister r = RegisterDecoder.DecodeEngineering(100, 99, [(ushort)first, (ushort)second], Def(DataType.UInt32, order));
        Assert.Equal("12345678", r.Hex);
        Assert.Equal($"{first:X4} {second:X4}", r.RawWordsText);
    }

    [Fact]
    public void Int32Row_Signed()
    {
        DecodedRegister r = RegisterDecoder.DecodeEngineering(100, 99, [0xFFFF, 0xFFFF], Def(DataType.Int32, scale: 0.5));
        Assert.Equal((-1L, -0.5, "FFFFFFFF"), (r.Integer, r.Value!.Value, r.Hex));
    }

    [Fact]
    public void Float32Row_ScaleAndRange()
    {
        DecodedRegister ok = RegisterDecoder.DecodeEngineering(100, 99, [0x3FC0, 0x0000], Def(DataType.Float32, scale: 0.1, min: 0, max: 1));
        Assert.Equal((0.15, "0.15", RegisterQuality.Ok, 0x3FC00000L), (ok.Value!.Value, ok.ValueText, ok.Quality, ok.Integer));
        DecodedRegister high = RegisterDecoder.DecodeEngineering(100, 99, [0x4120, 0x0000], Def(DataType.Float32, min: 0, max: 5));
        Assert.Equal((10.0, RegisterQuality.OutOfRange, "10 (Out of range)"), (high.Value!.Value, high.Quality, high.DisplayValue));
    }

    // Profile: NaN / ±Infinity are not engineering values and never pass the range check
    [Theory]
    [InlineData(0x7FC0, 0x0000, "NaN")]
    [InlineData(0x7F80, 0x0000, "+Infinity")]
    [InlineData(0xFF80, 0x0000, "-Infinity")]
    public void Float32Row_NonFinite_IsInvalid(int first, int second, string text)
    {
        DecodedRegister r = RegisterDecoder.DecodeEngineering(100, 99, [(ushort)first, (ushort)second], Def(DataType.Float32, scale: 0.1, min: -1e9, max: 1e9));
        Assert.Equal(RegisterQuality.InvalidValue, r.Quality);
        Assert.Null(r.Value);
        Assert.Equal(text, r.ValueText);
        Assert.Equal(text, r.DisplayValue);
    }

    // Insufficient words: no value, never a guess
    [Theory]
    [InlineData(DataType.UInt32)]
    [InlineData(DataType.Int32)]
    [InlineData(DataType.Float32)]
    public void ProfileRow_OneWord_IsIncomplete(DataType type)
    {
        DecodedRegister r = RegisterDecoder.DecodeEngineering(100, 99, [0x3F80], Def(type));
        Assert.Equal((RegisterQuality.Incomplete, "Incomplete"), (r.Quality, r.ValueText));
        Assert.Null(r.Value);
        Assert.Null(r.Bits32);
        Assert.Equal(1, r.RegisterCount);
    }

    // Generic core API (for Phase 5B): any type, manual scale, NaN text, incomplete values
    [Theory]
    [InlineData(DataType.UInt32, 0x1234, 0x5678, "305419896")]
    [InlineData(DataType.Int32, 0xFFFF, 0xFFFF, "-1")]
    [InlineData(DataType.Float32, 0x3F80, 0x0000, "1")]
    [InlineData(DataType.Float32, 0x7FC0, 0x0000, "NaN")]
    [InlineData(DataType.Float32, 0x7F80, 0x0000, "+Infinity")]
    [InlineData(DataType.Float32, 0xFF80, 0x0000, "-Infinity")]
    public void Generic32_Values(DataType type, int first, int second, string text)
    {
        DecodedRegister r = RegisterDecoder.DecodeGeneric(100, 99, [(ushort)first, (ushort)second], type, Register32Order.ABCD, 1);
        Assert.Equal(text, r.ValueText);
        Assert.Null(r.Definition);
        Assert.Equal(text is "NaN" or "+Infinity" or "-Infinity" ? RegisterQuality.InvalidValue : RegisterQuality.Ok, r.Quality);
    }

    [Fact]
    public void Generic32_OddWord_Incomplete_And16BitUnchanged()
    {
        DecodedRegister last = RegisterDecoder.DecodeGeneric(109, 108, [0x3F80], DataType.Float32, Register32Order.ABCD, 1);
        Assert.Equal((RegisterQuality.Incomplete, "Incomplete"), (last.Quality, last.ValueText));
        Assert.Null(last.Value);
        Assert.Equal(RegisterDecoder.DecodeGeneric(1, 0, 2534, 0.01), RegisterDecoder.DecodeGeneric(1, 0, [2534], DataType.UInt16, Register32Order.ABCD, 0.01));
        Assert.Equal("-2", RegisterDecoder.DecodeGeneric(1, 0, [0xFFFE], DataType.Int16, Register32Order.ABCD, 1).ValueText);
        Assert.Throws<ArgumentException>(() => RegisterDecoder.DecodeGeneric(1, 0, [1], DataType.Status, Register32Order.ABCD, 1));
    }

    // 16-bit rows are exactly what they were (record equality, no second word, 4-digit hex)
    [Fact]
    public void SixteenBitRow_Unchanged()
    {
        RegisterDefinition d = A6V12258321Profile.Profile.Find(0)!;
        DecodedRegister viaRaw = RegisterDecoder.DecodeEngineering(1, 0, 2534, d);
        DecodedRegister viaSpan = RegisterDecoder.DecodeEngineering(1, 0, [2534], d);
        Assert.Equal(viaRaw, viaSpan);
        Assert.Null(viaRaw.SecondWord);
        Assert.Null(viaRaw.Bits32);
        Assert.Equal(("09E6", "09E6", 1), (viaRaw.Hex, viaRaw.RawWordsText, viaRaw.RegisterCount));
    }
}

/// <summary>Phase 5A: profile with 32-bit definitions – overlap check, planner spans, 125 limit, target boundary, multi-word decode.</summary>
public class Register32ProfileTests
{
    private static RegisterDefinition D(int number, DataType type, Register32Order order = Register32Order.ABCD, double? scale = null) =>
        new() { RegisterNumber = number, Description = $"R{number}", DataType = type, Order32 = order, Scale = scale };

    /// <summary>Test profile (never the A6V profile): Float32 at 100–101, UInt16 102, Int32 (CDAB) 110–111, UInt32 125–126.</summary>
    private static readonly DeviceProfile Test32 = new("Test32", "32-bit test profile",
        [D(100, DataType.Float32), D(102, DataType.UInt16), D(110, DataType.Int32, Register32Order.CDAB), D(125, DataType.UInt32)]);

    private static string Groups(IReadOnlyList<ModbusRequest> plan) => string.Join(" | ", plan.Select(r => $"{r.StartAddress}x{r.Quantity}"));

    private static ModbusResult Result(int address, params ushort[] registers) => new()
    {
        Request = new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 1, address, (ushort)registers.Length),
        Status = ResultStatus.Pass,
        Registers = registers,
    };

    [Fact]
    public void Profile_OverlappingSpans_Rejected()
    {
        Assert.Throws<ArgumentException>(() => new DeviceProfile("Bad", "", [D(100, DataType.Float32), D(101, DataType.UInt16)]));
        Assert.Throws<ArgumentException>(() => new DeviceProfile("Bad", "", [D(100, DataType.UInt32), D(101, DataType.Int32)]));
        _ = new DeviceProfile("Ok", "", [D(100, DataType.Float32), D(102, DataType.Int32)]);
    }

    [Fact]
    public void FindCovering_BothWords()
    {
        Assert.Equal(100, Test32.FindCovering(99)!.RegisterNumber);
        Assert.Equal(100, Test32.FindCovering(100)!.RegisterNumber);   // second word
        Assert.Null(Test32.Find(100));                                  // Find is still "starts here"
        Assert.Null(Test32.FindCovering(102));                          // register 103: nothing
    }

    // Target boundary: never half a value, never past the target
    [Theory]
    [InlineData(99, "")]
    [InlineData(100, "")]                 // Float32 100–101 not complete → not included
    [InlineData(101, "100x2")]
    [InlineData(102, "100x3")]            // 100–101 + 102 joined into one request
    [InlineData(110, "100x3")]
    [InlineData(111, "100x3 | 110x2")]
    [InlineData(125, "100x3 | 110x2")]
    [InlineData(126, "100x3 | 110x2 | 125x2")]   // 32-bit at 125: quantity 2
    [InlineData(500, "100x3 | 110x2 | 125x2")]
    public void Plan_Spans_OneBased(int target, string groups)
    {
        IReadOnlyList<ModbusRequest> plan = ProfileReadPlanner.Plan(Test32, target, AddressBase.OneBased, 1);
        Assert.Equal(groups, Groups(plan));
        Assert.All(plan, r => Assert.True(r.Quantity <= ModbusPdu.MaxReadRegisters));
    }

    [Fact]
    public void Select_UsesLastRegister()
    {
        Assert.Equal([102], ProfileReadPlanner.Select(new DeviceProfile("p", "", [D(100, DataType.Float32), D(102, DataType.UInt16)]), 102).Skip(1).Select(r => r.RegisterNumber));
        Assert.Empty(ProfileReadPlanner.Select(Test32, 100));
        Assert.Equal([100], ProfileReadPlanner.Select(Test32, 101).Select(r => r.RegisterNumber));
    }

    [Theory]
    [InlineData(99, "")]                  // 0-based 99 = register 100: Float32 not complete
    [InlineData(100, "99x2")]             // 0-based 100 = register 101: included, request starts at 0-based 99
    [InlineData(101, "99x3")]
    public void Plan_Spans_ZeroBased(int target, string groups)
    {
        IReadOnlyList<ModbusRequest> plan = ProfileReadPlanner.Plan(Test32, target, AddressBase.ZeroBased, 1);
        Assert.Equal(groups, Groups(plan));
        Assert.All(plan, r => Assert.Equal(AddressBase.ZeroBased, r.AddressBase));
        if (plan.Count > 0)
            Assert.Equal(99, plan[0].PduAddress);
    }

    // 125-register limit: a 32-bit value is never split over two requests
    [Fact]
    public void Group_32BitValueAtLimit_StartsNextRequest()
    {
        // 1..124 UInt16 + UInt32 at 125–126: 125 would split it → first request ends at 124
        DeviceProfile p = new("Limit", "", [.. Enumerable.Range(1, 124).Select(n => D(n, DataType.UInt16)), D(125, DataType.UInt32)]);
        Assert.Equal("1x124 | 125x2", Groups(ProfileReadPlanner.Plan(p, 126, AddressBase.OneBased, 1)));
    }

    [Fact]
    public void Group_32BitValueEndingAtLimit_StaysInOneRequest()
    {
        // 1..123 UInt16 + Float32 at 124–125: exactly 125 registers
        DeviceProfile p = new("Limit", "", [.. Enumerable.Range(1, 123).Select(n => D(n, DataType.UInt16)), D(124, DataType.Float32)]);
        Assert.Equal("1x125", Groups(ProfileReadPlanner.Plan(p, 125, AddressBase.OneBased, 1)));
    }

    [Fact]
    public void Group_Only32BitValues_NeverSplit()
    {
        // 70 consecutive Float32 = 140 registers: 62 values (124 registers) then 8 values (16 registers)
        DeviceProfile p = new("Floats", "", Enumerable.Range(0, 70).Select(i => D(1 + 2 * i, DataType.Float32)));
        IReadOnlyList<ModbusRequest> plan = ProfileReadPlanner.Plan(p, 1000, AddressBase.OneBased, 1);
        Assert.Equal("1x124 | 125x16", Groups(plan));
        Assert.All(plan, r => Assert.Equal(0, r.Quantity % 2));
    }

    [Fact]
    public void Group_IntOverload_Unchanged()
    {
        Assert.Equal([new RegisterGroup(1, 125), new RegisterGroup(126, 125), new RegisterGroup(251, 50)], ProfileReadPlanner.Group(Enumerable.Range(1, 300)));
        Assert.Equal([new RegisterGroup(1, 2), new RegisterGroup(5, 1)], ProfileReadPlanner.Group([5, 2, 1, 2]));
        Assert.Equal([new RegisterGroup(100, 3)], ProfileReadPlanner.Group([new RegisterGroup(102, 1), new RegisterGroup(100, 2)]));
    }

    // Multi-word decode: one row per value, both words to one decoder
    [Fact]
    public void Decode_OneRowPerValue()
    {
        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode([Result(100, 0x3F80, 0x0000, 7), Result(110, 0x0001, 0xFFFF)], Test32, _ => 1);
        Assert.Equal([100, 102, 110], rows.Select(r => r.Address));
        Assert.Equal(["1", "7", "-65535"], rows.Select(r => r.ValueText));   // 110 CDAB: words 0001 FFFF → 0xFFFF0001
        Assert.Equal([2, 1, 2], rows.Select(r => r.RegisterCount));
    }

    [Fact]
    public void Decode_WordsInTwoRequests_StillOneRow()
    {
        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode([Result(101, 0x0000), Result(100, 0x3F80)], Test32, _ => 1);
        DecodedRegister row = Assert.Single(rows);
        Assert.Equal((100, "1", RegisterQuality.Ok), (row.Address, row.ValueText, row.Quality));
    }

    [Fact]
    public void Decode_HalfRead_Incomplete()
    {
        DecodedRegister firstOnly = Assert.Single(RegisterService.Decode(Result(100, 0x3F80), Test32, _ => 1));
        Assert.Equal((100, RegisterQuality.Incomplete), (firstOnly.Address, firstOnly.Quality));
        DecodedRegister secondOnly = Assert.Single(RegisterService.Decode(Result(101, 0x0000), Test32, _ => 1));
        Assert.Equal((101, RegisterQuality.Incomplete, 100), (secondOnly.Address, secondOnly.Quality, secondOnly.Definition!.RegisterNumber));
        Assert.Null(firstOnly.Value);
        Assert.Null(secondOnly.Value);
    }

    [Fact]
    public void Decode_GenericProfile_StaysPerWord()
    {
        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode(Result(100, 0x3F80, 0x0000), DeviceProfile.Generic, _ => 1);
        Assert.Equal(["16256", "0"], rows.Select(r => r.ValueText));
    }

    // End to end through the planner, reader and a slave
    [Fact]
    public async Task ReadThroughService_ThreeRequests_FourRows()
    {
        var registers = new ushort[200];
        new ushort[] { 0x3FC0, 0x0000, 42 }.CopyTo(registers, 99);   // Float32 1.5, UInt16 42
        new ushort[] { 0xFF85, 0xFFFF }.CopyTo(registers, 109);      // Int32 CDAB → 0xFFFFFF85 = -123
        new ushort[] { 0x1234, 0x5678 }.CopyTo(registers, 124);      // UInt32 ABCD
        await using var slave = new FakeModbusTcpSlave { Registers = registers };
        await using var service = new ModbusService(new CommunicationLogger());
        Assert.True(await service.ConnectAsync(new ModbusTcpTransport(new TcpSettings("127.0.0.1", slave.Port))));

        ProfileReadResult read = await ProfileReader.ReadAsync(service, ProfileReadPlanner.Plan(Test32, 126, AddressBase.OneBased, 1));

        Assert.True(read.IsSuccess);
        Assert.Equal(["03 00 63 00 03", "03 00 6D 00 02", "03 00 7C 00 02"], slave.ReceivedAdus.Select(a => HexFormatter.Format(a[7..])));
        IReadOnlyList<DecodedRegister> rows = RegisterService.Decode(read.Results, Test32, _ => 1);
        Assert.Equal(["1.5", "42", "-123", "305419896"], rows.Select(r => r.ValueText));
        Assert.Equal(["3FC00000", "002A", "FFFFFF85", "12345678"], rows.Select(r => r.Hex));
    }
}

/// <summary>Phase 5A: the A6V12258321 profile is entirely 16-bit and keeps its exact behaviour.</summary>
public class Register32A6VRegressionTests
{
    private static readonly DeviceProfile A6V = A6V12258321Profile.Profile;

    [Fact]
    public void AllDefinitions_16Bit_SpanEqualsStart()
    {
        Assert.All(A6V.Registers, d => Assert.Equal((1, d.RegisterNumber), (d.RegisterCount, d.LastRegisterNumber)));
        Assert.All(A6V.Registers, d => Assert.Same(d, A6V.FindCovering(d.PduAddress)));
    }

    [Theory]
    [InlineData(1287, "1x6 | 223x3 | 401x1 | 764x3 | 1286x2", 15)]
    [InlineData(225, "1x6 | 223x3", 9)]
    [InlineData(6, "1x6", 6)]
    [InlineData(1, "1x1", 1)]
    public void Plan_Unchanged(int target, string groups, int rows)
    {
        Assert.Equal(groups, string.Join(" | ", ProfileReadPlanner.Plan(A6V, target, AddressBase.OneBased, 1).Select(r => $"{r.StartAddress}x{r.Quantity}")));
        Assert.Equal(rows, ProfileReadPlanner.Select(A6V, target).Count);
    }

    [Fact]
    public void Write_32BitStillUnsupported() =>
        Assert.Equal(ProfileWriteError.UnsupportedDataType,
            RegisterWriteEncoder.Encode(new RegisterDefinition { RegisterNumber = 1, Description = "f", DataType = DataType.Float32, Access = RegisterAccess.ReadWrite }, 1.0).Error);
}
