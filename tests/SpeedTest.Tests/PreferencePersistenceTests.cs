using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using KillerScan.Services;

internal static class PreferencePersistenceTests
{
    internal static void Run()
    {
        ProfilePersistenceTests.Run();
        string scratch = Path.Combine(Path.GetTempPath(), "KillerScan-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var type = typeof(KillerScan.Services.DeviceOverrides);
            var pathField = type.GetField("FilePath", BindingFlags.NonPublic | BindingFlags.Static)!;
            object originalPath = pathField.GetValue(null)!;
            string path = Path.Combine(scratch, "overrides.json");
            try
            {
                pathField.SetValue(null, path);
                File.WriteAllText(path, "{unreadable-original");
                KillerScan.Services.DeviceOverrides.Load();
                bool saved = KillerScan.Services.DeviceOverrides.Set("00:11:22:33:44:55", "Printer");
                if (File.ReadAllText(path) != "{unreadable-original")
                    throw new InvalidOperationException("Saving a device override must preserve unreadable original bytes.");
                if (saved || string.IsNullOrEmpty(KillerScan.Services.DeviceOverrides.SaveError) ||
                    KillerScan.Services.DeviceOverrides.Get("00:11:22:33:44:55") != "Printer")
                    throw new InvalidOperationException("An override save failure reports failure while retaining the session edit.");
                File.WriteAllText(path, "{\"AA:BB:CC:DD:EE:FF\":\"Router\"}");
                if (!KillerScan.Services.DeviceOverrides.Save()) throw new InvalidOperationException("Retry succeeds after the storage fault is repaired.");
                KillerScan.Services.DeviceOverrides.Load();
                if (KillerScan.Services.DeviceOverrides.Get("00:11:22:33:44:55") != "Printer" ||
                    KillerScan.Services.DeviceOverrides.Get("AA:BB:CC:DD:EE:FF") != "Router")
                    throw new InvalidOperationException("Retry retains both the pending mutation and another writer's data.");
                byte[] beforeDenied = File.ReadAllBytes(path);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (KillerScan.Services.DeviceOverrides.Set("00:11:22:33:44:55", "Server"))
                        throw new InvalidOperationException("Denied replacement must not report success.");
                    if (!System.Linq.Enumerable.SequenceEqual(beforeDenied, File.ReadAllBytes(path)))
                        throw new InvalidOperationException("Denied replacement preserves prior committed bytes.");
                }
                if (!KillerScan.Services.DeviceOverrides.Save()) throw new InvalidOperationException("A denied replacement can be retried.");
                KillerScan.Services.DeviceOverrides.Load();
                if (KillerScan.Services.DeviceOverrides.Get("00:11:22:33:44:55") != "Server")
                    throw new InvalidOperationException("Retry publishes the full intended change.");
            }
            finally { pathField.SetValue(null, originalPath); }

            var logins = typeof(KillerScan.App).Assembly.GetType("KillerScan.Services.DeviceLogins", true)!;
            var loginPath = logins.GetField("FilePath", BindingFlags.NonPublic | BindingFlags.Static)!;
            object originalLoginPath = loginPath.GetValue(null)!;
            string loginFile = Path.Combine(scratch, "logins.json");
            try
            {
                loginPath.SetValue(null, loginFile);
                File.WriteAllText(loginFile, "{unreadable-logins");
                logins.GetMethod("Load")!.Invoke(null, null);
                logins.GetMethod("Set")!.Invoke(null, new object[] { "00:11:22:33:44:55", "192.0.2.1", "fixture-user" });
                if (File.ReadAllText(loginFile) != "{unreadable-logins")
                    throw new InvalidOperationException("Saving a username must preserve unreadable original bytes.");
            }
            finally { loginPath.SetValue(null, originalLoginPath); }

            var profiles = typeof(KillerScan.App).Assembly.GetType("KillerScan.Services.ScanProfiles", true)!;
            var profilePath = profiles.GetField("FilePath", BindingFlags.NonPublic | BindingFlags.Static)!;
            object originalProfilePath = profilePath.GetValue(null)!;
            string profileFile = Path.Combine(scratch, "profiles.json");
            try
            {
                profilePath.SetValue(null, profileFile);
                File.WriteAllText(profileFile, "{unreadable-profiles");
                profiles.GetMethod("Load")!.Invoke(null, null);
                object profile = Activator.CreateInstance(typeof(KillerScan.App).Assembly.GetType("KillerScan.Services.ScanProfile", true)!)!;
                profile.GetType().GetProperty("Name")!.SetValue(profile, "Fixture profile");
                profiles.GetMethod("Save")!.Invoke(null, new[] { profile });
                if (File.ReadAllText(profileFile) != "{unreadable-profiles")
                    throw new InvalidOperationException("Saving a profile must preserve unreadable original bytes.");
            }
            finally { profilePath.SetValue(null, originalProfilePath); }

            var preferences = typeof(KillerScan.App).Assembly.GetType("KillerScan.Services.DevicePreferences", true)!;
            var preferencesPath = preferences.GetField("FilePath", BindingFlags.NonPublic | BindingFlags.Static)!;
            object originalPreferencesPath = preferencesPath.GetValue(null)!;
            string preferencesFile = Path.Combine(scratch, "devices.json");
            try
            {
                preferencesPath.SetValue(null, preferencesFile);
                File.WriteAllText(preferencesFile, "{unreadable-preferences");
                preferences.GetMethod("Load")!.Invoke(null, null);
                object device = new KillerScan.Engine.NetworkDevice { IpAddress = "192.0.2.1", MacAddress = "00:11:22:33:44:55" };
                preferences.GetMethod("SetName")!.Invoke(null, new[] { device, "Fixture name" });
                if (File.ReadAllText(preferencesFile) != "{unreadable-preferences")
                    throw new InvalidOperationException("Saving a device name must preserve unreadable original bytes.");
                // The refusal names the file, so the retry prompt says what to repair and where.
                string? why = (string?)preferences.GetProperty("SaveError")!.GetValue(null);
                if (why == null || !why.Contains(preferencesFile))
                    throw new InvalidOperationException("A save refused by an unreadable file names that file: " + why);
            }
            finally { preferencesPath.SetValue(null, originalPreferencesPath); }

            string faultFile = Path.Combine(scratch, "partial-write.json");
            File.WriteAllText(faultFile, "{\"committed\":\"original\"}");
            byte[] committed = File.ReadAllBytes(faultFile);
            bool failWrite = true;
            var faultStore = new JsonPreferenceFile<Dictionary<string, string>>(faultFile, () => new(), value => value, (stream, bytes) =>
            {
                if (failWrite)
                {
                    stream.Write(bytes, 0, bytes.Length / 2);
                    throw new IOException("Controlled storage failure after partial output.");
                }
                stream.Write(bytes, 0, bytes.Length);
            });
            faultStore.Load();
            if (faultStore.Change(value => value["pending"] = "retry me") || !File.ReadAllBytes(faultFile).SequenceEqual(committed))
                throw new InvalidOperationException("Partial output failure must preserve the complete original and report failure.");
            if (faultStore.Value["pending"] != "retry me") throw new InvalidOperationException("Partial output preserves the unsaved session change.");
            failWrite = false;
            if (!faultStore.Save()) throw new InvalidOperationException("Partial output can be retried.");
            faultStore.Load();
            if (faultStore.Value["committed"] != "original" || faultStore.Value["pending"] != "retry me")
                throw new InvalidOperationException("Retry publishes the full original plus intended mutation.");
            if (Directory.EnumerateFiles(scratch, "*.tmp").Any()) throw new InvalidOperationException("Failed and successful preference writes clean up temporary siblings.");

            // A Location import on a pool thread and the UI thread write one catalog at the same
            // time. Neither may report the file busy or lose the other's change.
            string shared = Path.Combine(scratch, "catalog.json");
            var importing = new JsonPreferenceFile<Dictionary<string, string>>(shared, () => new(), value => value,
                (stream, bytes) => { System.Threading.Thread.Sleep(5); stream.Write(bytes, 0, bytes.Length); });
            var window = new JsonPreferenceFile<Dictionary<string, string>>(shared, () => new(), value => value);
            importing.Load(); window.Load();
            var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
            void Writer(JsonPreferenceFile<Dictionary<string, string>> store, string prefix)
            {
                for (int i = 0; i < 40; i++)
                {
                    string key = prefix + i;
                    try
                    {
                        // A slow mutation keeps Save replaying the pending list while the other thread adds to it.
                        Action<Dictionary<string, string>> mutation = value => { System.Threading.Thread.Sleep(1); lock (value) value[key] = "x"; };
                        bool saved = i % 2 == 0 ? store.Commit(mutation) : store.Change(mutation);
                        if (!saved) failures.Enqueue(key + ": " + store.Error);
                    }
                    catch (Exception error) { failures.Enqueue(key + ": " + error.GetType().Name); }
                }
            }
            var background = System.Threading.Tasks.Task.Run(() => Writer(importing, "import-"));
            var sameStore = System.Threading.Tasks.Task.Run(() => Writer(importing, "same-store-"));
            Writer(window, "ui-");
            background.Wait(); sameStore.Wait();
            window.Load();
            if (!failures.IsEmpty || window.Value.Count != 120)
                throw new InvalidOperationException("Concurrent in-process saves must all land: " + window.Value.Count + " saved, "
                    + string.Join("; ", failures.Take(3)));
        }
        finally { Directory.Delete(scratch, true); }
        Console.WriteLine("PASS: preference saves preserve unreadable originals and serialize writers in one process.");
    }
}
