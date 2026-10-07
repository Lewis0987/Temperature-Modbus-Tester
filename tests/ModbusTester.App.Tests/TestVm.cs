using System.IO;
using ModbusTester.App.History;

namespace ModbusTester.App.Tests;

/// <summary>Test helpers: every ViewModel under test gets its own temp history file, never the user's real one.</summary>
internal static class TestVm
{
    public static string TempHistoryPath() =>
        Path.Combine(Path.GetTempPath(), "ModbusTester.App.Tests", Guid.NewGuid().ToString("N"), "input-history.json");

    public static InputHistoryStore TempStore() => new(TempHistoryPath());
}
