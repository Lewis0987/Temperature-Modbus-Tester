using System.Reflection;

namespace ModbusTester.App;

/// <summary>Product name and version, taken from the assembly (set once, as &lt;Version&gt; in the App project).</summary>
internal static class AppInfo
{
    public const string ProductName = "Modbus Tester";

    /// <summary>"1.0.0": the project's Version (informational version without any build metadata).</summary>
    public static string Version { get; } = ReadVersion(typeof(AppInfo).Assembly);

    /// <summary>Window title, e.g. "Modbus Tester V1.0.0".</summary>
    public static string Title { get; } = $"{ProductName} V{Version}";

    internal static string ReadVersion(Assembly assembly)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0];   // drop "+commit" if a build ever adds it
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
