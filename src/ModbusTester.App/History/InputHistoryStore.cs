using System.IO;
using System.Text.Json;

namespace ModbusTester.App.History;

/// <summary>
/// Persists every field's input history in one JSON file in the user's local app data (outside the
/// build output, so builds never touch it). A missing or unreadable file is treated as an empty history.
/// <code>
/// { "version": 2, "fields": { "tcpHost": ["192.168.137.189", "127.0.0.1"], "quantity": ["6", "1"] } }
/// </code>
/// </summary>
public sealed class InputHistoryStore(string filePath)
{
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModbusTester");

    /// <summary>%LOCALAPPDATA%\ModbusTester\input-history.json</summary>
    public static string DefaultPath { get; } = Path.Combine(DefaultDirectory, "input-history.json");

    /// <summary>Version 1 file (IP + Port pairs) written by the previous TCP-only history.</summary>
    public const string LegacyFileName = "tcp-connection-history.json";

    public string FilePath { get; } = filePath;

    /// <summary>Messages for the Communication Log (load/save problems, migration); cleared on each call.</summary>
    public List<HistoryMessage> Messages { get; } = [];

    public Dictionary<string, List<string>> Load()
    {
        Messages.Clear();
        if (!File.Exists(FilePath))
            return MigrateLegacy();
        try
        {
            HistoryFile? file = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(FilePath), JsonOptions);
            return file?.Fields?.Where(f => f.Value is not null)
                       .ToDictionary(f => f.Key, f => f.Value!.Where(v => v is not null).ToList())
                   ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Messages.Add(new(true, $"Input history could not be loaded ({ex.GetType().Name}: {ex.Message}); starting empty."));
            return [];
        }
    }

    public void Save(IReadOnlyDictionary<string, List<string>> fields)
    {
        Messages.Clear();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string json = JsonSerializer.Serialize(new HistoryFile(CurrentVersion, fields.ToDictionary(f => f.Key, List<string>? (f) => f.Value)), JsonOptions);
            // Write to a temp file and swap it in, so a crash mid-write never leaves a truncated history.
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Messages.Add(new(true, $"Input history could not be saved ({ex.GetType().Name}: {ex.Message})."));
        }
    }

    /// <summary>
    /// Import of the old TCP history next to this file: its hosts become the "tcpHost" history (ports are
    /// not imported). Loading never writes: the new file is created by the next history change, and from then
    /// on the old file (left in place) is no longer read.
    /// </summary>
    private Dictionary<string, List<string>> MigrateLegacy()
    {
        string legacyPath = Path.Combine(Path.GetDirectoryName(FilePath)!, LegacyFileName);
        if (!File.Exists(legacyPath))
            return [];
        try
        {
            LegacyFile? legacy = JsonSerializer.Deserialize<LegacyFile>(File.ReadAllText(legacyPath), JsonOptions);
            List<string> hosts = FieldHistory.Normalize(legacy?.Entries?.Select(e => e?.Host) ?? []);
            Messages.Add(new(false, $"Input history: imported {hosts.Count} IP address(es) from {LegacyFileName}."));
            return new Dictionary<string, List<string>> { [HistoryKeys.TcpHost] = hosts };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Messages.Add(new(true, $"Old {LegacyFileName} could not be imported ({ex.GetType().Name}: {ex.Message}); starting empty."));
            return [];
        }
    }

    private sealed record HistoryFile(int Version, Dictionary<string, List<string>?>? Fields);

    private sealed record LegacyFile(int Version, LegacyEntry?[]? Entries);

    private sealed record LegacyEntry(string? Host, int Port);
}

/// <summary>A note for the Communication Log; <see cref="IsError"/> for load/save failures.</summary>
public sealed record HistoryMessage(bool IsError, string Text);

/// <summary>JSON keys of the eight fields that keep a history.</summary>
public static class HistoryKeys
{
    public const string TcpHost = "tcpHost";
    public const string TcpUnitId = "tcpUnitId";
    public const string TcpTimeout = "tcpTimeout";
    public const string RtuSlaveId = "rtuSlaveId";
    public const string RtuTimeout = "rtuTimeout";
    public const string Address = "address";
    public const string Quantity = "quantity";
    public const string DefaultScale = "defaultScale";
}
