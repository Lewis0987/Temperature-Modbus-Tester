using System.IO.Ports;

namespace ModbusTester.Core.Models;

/// <param name="TimeoutMs">Response timeout per request, measured from the end of the TX write.</param>
public sealed record RtuSettings(
    string PortName,
    int BaudRate = 9600,
    int DataBits = 8,
    Parity Parity = Parity.Even,
    StopBits StopBits = StopBits.One,
    int TimeoutMs = 1000)
{
    /// <summary>e.g. "9600 / 8E1".</summary>
    public string FrameFormat => $"{BaudRate} / {DataBits}{Parity.ToString()[0]}{(StopBits == StopBits.Two ? 2 : StopBits == StopBits.OnePointFive ? 1.5 : 1)}";

    /// <summary>
    /// Modbus RTU inter-frame silence (3.5 character times); fixed 1.75 ms above 19200 baud per the Modbus spec.
    /// </summary>
    public TimeSpan InterFrameDelay
    {
        get
        {
            if (BaudRate > 19200)
                return TimeSpan.FromMilliseconds(1.75);
            int bitsPerChar = 1 + DataBits + (Parity == Parity.None ? 0 : 1) + (StopBits == StopBits.Two ? 2 : 1);
            return TimeSpan.FromMilliseconds(3.5 * bitsPerChar * 1000.0 / BaudRate);
        }
    }
}
