using System.IO;
using System.Text.Json;

namespace KillerScan.Services
{
    /// <summary>The optional test/portable data root must not share the installed app's registry settings.</summary>
    internal static class IsolatedSettings
    {
        private static readonly object Gate = new();
        internal static bool Enabled => Environment.GetEnvironmentVariable("KILLERSCAN_DATA_ROOT") != null;
        private static string FilePath => Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("KILLERSCAN_DATA_ROOT")!), "settings.json");
        private static Dictionary<string, string> Read() => File.Exists(FilePath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Invalid isolated settings.")
            : new();
        internal static string? Get(string name) { lock (Gate) return Read().TryGetValue(name, out string? value) ? value : null; }
        internal static void Set(string name, string? value)
        {
            lock (Gate)
            {
                var values = Read();
                if (value == null) values.Remove(name); else values[name] = value;
                LocationCatalog.WriteAtomic(FilePath, JsonSerializer.Serialize(values));
            }
        }
    }
}
