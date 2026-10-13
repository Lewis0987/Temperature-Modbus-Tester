using System.IO;
using System.Text;
using ModbusTester.Core.Models;

namespace ModbusTester.App;

/// <summary>
/// The Communication Log as text (exactly what the log list shows, one entry per line) and saving it to a file
/// chosen by the user. Only on demand – the tester never writes log files by itself.
/// </summary>
internal static class CommunicationLogExport
{
    /// <summary>UTF-8 without a byte order mark.</summary>
    public static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string BuildText(IEnumerable<LogEntry> entries) => string.Join(Environment.NewLine, entries);

    /// <summary>Suggested file name, e.g. "ModbusLog_20261013_142501.txt".</summary>
    public static string DefaultFileName(DateTime now) => $"ModbusLog_{now:yyyyMMdd_HHmmss}.txt";

    /// <summary>Writes <paramref name="text"/> to <paramref name="path"/> (UTF-8). IO errors are thrown to the caller.</summary>
    public static void Save(string path, string text) => File.WriteAllText(path, text, FileEncoding);
}
