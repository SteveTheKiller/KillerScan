using System;
using System.IO;
using System.Reflection;
using KillerScan.Services;

internal static class DemoStoreTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KillerScan-demo-stores-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        bool originalDemo = DemoData.Enabled;
        try
        {
            Check(typeof(DeviceOverrides), "overrides.json", "Printer", "Router");
            Check(typeof(DeviceLogins), "logins.json", "real-user", "demo-user");
        }
        finally { DemoData.Enabled = originalDemo; Directory.Delete(directory, true); }
        Console.WriteLine("PASS: demo overrides and usernames remain in memory and preserve real stores.");

        void Check(Type type, string name, string realValue, string demoValue)
        {
            var field = type.GetField("FilePath", BindingFlags.Static | BindingFlags.NonPublic)!;
            object original = field.GetValue(null)!;
            string path = Path.Combine(directory, name);
            string sentinel = "{\"00:11:22:33:44:55\":\"" + realValue + "\"}";
            File.WriteAllText(path, sentinel);
            try
            {
                field.SetValue(null, path);
                DemoData.Enabled = true;
                Load();
                Require(Get() == null, "Demo mode must not read the real " + name);
                Require(Set(demoValue) && Get() == demoValue, "Demo edits remain available in memory.");
                Require((bool)type.GetMethod("Save")!.Invoke(null, null)!, "Demo save succeeds without disk access.");
                Require(File.ReadAllText(path) == sentinel, "Demo edits preserve real store bytes.");
                Load();
                Require(Get() == null, "Reloading demo mode starts with an empty session.");
                DemoData.Enabled = false;
                Load();
                Require(Get() == realValue, "Normal mode still reads the real value.");
                Require(Set("normal-change"), "Normal mode still persists edits.");
                Load();
                Require(Get() == "normal-change", "Normal edits survive reload.");
            }
            finally { field.SetValue(null, original); DemoData.Enabled = originalDemo; }

            void Load() => type.GetMethod("Load")!.Invoke(null, null);
            string? Get() => (string?)type.GetMethod("Get")!.Invoke(null,
                type == typeof(DeviceOverrides) ? new object[] { "00:11:22:33:44:55" } : new object[] { "00:11:22:33:44:55", "192.0.2.1" });
            bool Set(string value) => (bool)type.GetMethod("Set")!.Invoke(null,
                type == typeof(DeviceOverrides) ? new object[] { "00:11:22:33:44:55", value } : new object[] { "00:11:22:33:44:55", "192.0.2.1", value })!;
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
