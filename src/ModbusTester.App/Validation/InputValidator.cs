using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ModbusTester.Core.Models;

namespace ModbusTester.App.Validation;

public interface IParsed
{
    string? Error { get; }
}

/// <summary>Result of parsing one UI field: either a value or an error message, never a stale value.</summary>
public readonly record struct Parsed<T>(T Value, string? Error) : IParsed
{
    public bool IsValid => Error is null;
    public static Parsed<T> Fail(string error) => new(default!, error);
}

/// <summary>
/// Parses the text the user typed into connection/request fields. Pure functions: the UI validates live
/// with them and the Connect/Read commands parse again with them as the final gate.
/// </summary>
public static class InputValidator
{
    public const int MaxTimeoutMs = 60000;

    /// <summary>
    /// IPv4 literal (strict: 4 decimal octets 0-255), IPv6 literal, or a host name. The previous code passed any
    /// string to TcpClient, so host names stay supported, but a bare word ("abc") or something that looks
    /// like a malformed IPv4 address ("192.168.137", "192.168.137.999") is rejected.
    /// Host names must be "localhost" or contain a dot (e.g. "temperature.local").
    /// </summary>
    public static Parsed<string> Host(string? text)
    {
        string host = text?.Trim() ?? "";
        if (host.Length == 0)
            return Parsed<string>.Fail("IP Address is required.");

        if (host.All(c => char.IsAsciiDigit(c) || c == '.'))
            return IsStrictIPv4(host) ? new(host, null) : Parsed<string>.Fail($"'{host}' is not a valid IPv4 address (e.g. 192.168.137.189).");

        if (host.Contains(':'))
            return IPAddress.TryParse(host, out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
                ? new(host, null)
                : Parsed<string>.Fail($"'{host}' is not a valid IP address.");

        bool validHostName = Uri.CheckHostName(host) == UriHostNameType.Dns
            && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Contains('.'));
        return validHostName
            ? new(host, null)
            : Parsed<string>.Fail($"'{host}' is not a valid IPv4 address or host name (e.g. 192.168.137.189).");
    }

    public static Parsed<int> Port(string? text) => Integer(text, "Port", 1, 65535);
    public static Parsed<int> TimeoutMs(string? text) => Integer(text, "Timeout", 1, MaxTimeoutMs);
    public static Parsed<int> UnitId(string? text) => Integer(text, "Unit ID", 0, 255);
    public static Parsed<int> SlaveId(string? text) => Integer(text, "Slave ID", 1, 247);
    public static Parsed<int> Quantity(string? text) => Integer(text, "Quantity", 1, 125);

    public static Parsed<int> Address(string? text, AddressBase addressBase) => addressBase == AddressBase.OneBased
        ? Integer(text, "Address", 1, 65536)
        : Integer(text, "Address", 0, 65535);

    public static Parsed<double> Scale(string? text)
    {
        string value = text?.Trim() ?? "";
        if (value.Length == 0)
            return Parsed<double>.Fail("Scale is required.");
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) && double.IsFinite(scale)
            ? new(scale, null)
            : Parsed<double>.Fail($"Scale '{value}' is not a number (e.g. 0.01).");
    }

    /// <summary>Plain decimal digits only (no sign, decimal point, exponent or thousands separator).</summary>
    private static Parsed<int> Integer(string? text, string name, int min, int max)
    {
        string value = text?.Trim() ?? "";
        if (value.Length == 0)
            return Parsed<int>.Fail($"{name} is required.");
        if (!value.All(char.IsAsciiDigit) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            || number < min || number > max)
            return Parsed<int>.Fail($"{name} must be an integer {min}~{max}.");
        return new(number, null);
    }

    private static bool IsStrictIPv4(string text)
    {
        string[] parts = text.Split('.');
        return parts.Length == 4 && parts.All(p =>
            p.Length is >= 1 and <= 3 && int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int octet) && octet <= 255);
    }
}
