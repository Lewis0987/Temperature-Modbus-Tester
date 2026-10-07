using ModbusTester.App.Validation;
using ModbusTester.Core.Models;

namespace ModbusTester.App.Tests;

public class InputValidatorTests
{
    // Tests 1-5
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("5020abc")]
    [InlineData("1.5")]
    [InlineData("+5020")]
    [InlineData("5,020")]
    public void Port_Invalid(string text) => Assert.False(InputValidator.Port(text).IsValid);

    [Theory]
    [InlineData("5020", 5020)]
    [InlineData("502", 502)]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    [InlineData(" 5020 ", 5020)]
    public void Port_Valid(string text, int expected)
    {
        Parsed<int> port = InputValidator.Port(text);
        Assert.True(port.IsValid);
        Assert.Equal(expected, port.Value);
    }

    [Fact]
    public void Port_Empty_SaysRequired() => Assert.Equal("Port is required.", InputValidator.Port("").Error);

    [Fact]
    public void Port_OutOfRange_SaysRange() => Assert.Equal("Port must be an integer 1~65535.", InputValidator.Port("0").Error);

    // Tests 7-8
    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("60001")]
    public void Timeout_Invalid(string text) => Assert.False(InputValidator.TimeoutMs(text).IsValid);

    [Theory]
    [InlineData("1000", 1000)]
    [InlineData("1", 1)]
    public void Timeout_Valid(string text, int expected) => Assert.Equal(expected, InputValidator.TimeoutMs(text).Value);

    // Test 9
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("192.168.137")]
    [InlineData("192.168.137.999")]
    [InlineData("1.2.3.4.5")]
    [InlineData("192.168..1")]
    [InlineData("192.168.137.189 x")]
    [InlineData("::zz")]
    public void Host_Invalid(string text) => Assert.False(InputValidator.Host(text).IsValid);

    [Theory]
    [InlineData("192.168.137.189")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("temperature.local")]
    [InlineData("::1")]
    public void Host_Valid(string text) => Assert.True(InputValidator.Host(text).IsValid);

    [Theory]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("248", false)]
    [InlineData("1", true)]
    [InlineData("247", true)]
    public void SlaveId(string text, bool valid) => Assert.Equal(valid, InputValidator.SlaveId(text).IsValid);

    [Theory]
    [InlineData("0", AddressBase.OneBased, false)]
    [InlineData("1", AddressBase.OneBased, true)]
    [InlineData("0", AddressBase.ZeroBased, true)]
    [InlineData("65536", AddressBase.ZeroBased, false)]
    [InlineData("", AddressBase.OneBased, false)]
    public void Address(string text, AddressBase addressBase, bool valid) =>
        Assert.Equal(valid, InputValidator.Address(text, addressBase).IsValid);

    [Theory]
    [InlineData("0.01", true)]
    [InlineData("1", true)]
    [InlineData("-0.5", true)]
    [InlineData("", false)]
    [InlineData("x", false)]
    public void Scale(string text, bool valid) => Assert.Equal(valid, InputValidator.Scale(text).IsValid);
}
