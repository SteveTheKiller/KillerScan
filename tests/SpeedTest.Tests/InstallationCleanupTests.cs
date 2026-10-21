using System;
using System.IO;
using KillerScan.Services;

internal static class InstallationCleanupTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerScan-cleanup-" + Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "KillerScan");
        string nested = Path.Combine(install, "reports");
        Directory.CreateDirectory(nested);
        string sentinel = Path.Combine(nested, "report.txt");
        File.WriteAllText(sentinel, "Saved report: 42 devices.");
        File.WriteAllText(Path.Combine(install, "KillerScan.exe"), "owned image");
        try
        {
            InstallationFiles.Remove(install);
            if (!File.Exists(sentinel) || File.ReadAllText(sentinel) != "Saved report: 42 devices.")
                throw new InvalidOperationException("Cleanup destroyed an unrelated nested report.");
            if (File.Exists(Path.Combine(install, "KillerScan.exe")))
                throw new InvalidOperationException("Cleanup did not remove the owned image.");
            Console.WriteLine("PASS cleanup preserves unknown nested reports and removes only its image.");
            File.Delete(sentinel);
            Directory.Delete(nested, false);
            InstallationFiles.Remove(install);
            Require(!Directory.Exists(install), "An empty installation directory was retained.");
            InstallationFiles.Remove(install);
            Console.WriteLine("PASS absent and empty installation cleanup.");

            Directory.CreateDirectory(install);
            string owned = Path.Combine(install, "KillerScan.exe");
            File.WriteAllText(owned, "locked image");
            bool rejected = false;
            using (new FileStream(owned, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { InstallationFiles.Remove(install); }
                catch (IOException) { rejected = true; }
            }
            Require(rejected && File.ReadAllText(owned) == "locked image", "A locked image must fail without changing bytes.");
            InstallationFiles.Remove(install);
            Console.WriteLine("PASS locked images fail without destroying their bytes.");

            Directory.CreateDirectory(install);
            File.WriteAllText(owned, "retryable image");
            bool registered = true;
            int notifications = 0;
            rejected = false;
            try
            {
                RemovalTransaction.Run(install,
                    () => { registered = false; throw new UnauthorizedAccessException("Registry fixture denied."); },
                    () => registered = true, () => notifications++);
            }
            catch (UnauthorizedAccessException) { rejected = true; }
            Require(rejected && registered && File.ReadAllText(owned) == "retryable image",
                "Failed registration cleanup must restore a runnable image and registration.");
            Require(notifications == 1, "Restored PATH registration did not refresh the environment boundary.");
            Require(Directory.GetFiles(install).Length == 1, "A successfully restored removal left a retired image.");
            RemovalTransaction.Run(install, () => registered = false, () => registered = true, () => notifications++);
            Require(!registered && !File.Exists(owned), "Completed removal did not remove both registration and image.");
            Require(notifications == 2, "Completed removal did not notify the environment boundary.");
            InstallationFiles.Remove(install);
            Console.WriteLine("PASS registry failure restores the image; completed removal owns both outcomes.");
            UninstallCommandTests.Run();

            Directory.CreateDirectory(install);
            string relocated = Path.Combine(root, "relocated");
            rejected = false;
            using (InstallationFiles.PinDirectory(install))
            {
                try { Directory.Move(install, relocated); }
                catch (IOException) { rejected = true; }
            }
            Require(rejected && Directory.Exists(install), "A cleanup ancestor could move while its lease was held.");
            InstallationFiles.Remove(install);
            Console.WriteLine("PASS directory leases prevent ancestor replacement during cleanup.");

            string destination = Path.Combine(root, "other-app");
            Directory.CreateDirectory(destination);
            string otherImage = Path.Combine(destination, "KillerScan.exe");
            File.WriteAllText(otherImage, "unrelated linked target");
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                    "/d /c mklink /J \"" + install + "\" \"" + destination + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var process = System.Diagnostics.Process.Start(start)!)
                {
                    string output = process.StandardOutput.ReadToEnd(), errors = process.StandardError.ReadToEnd();
                    Require(process.WaitForExit(10000) && process.ExitCode == 0, "Junction fixture creation failed: " + output + errors);
                }
                rejected = false;
                try { InstallationFiles.Remove(install); }
                catch (UnauthorizedAccessException) { rejected = true; }
                Require(rejected && File.ReadAllText(otherImage) == "unrelated linked target", "Cleanup followed a directory junction.");
                Console.WriteLine("PASS junction cleanup is rejected and its target remains intact.");
            }
            finally
            {
                if (Directory.Exists(install) && (File.GetAttributes(install) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(install, false);
                File.Delete(otherImage);
                Directory.Delete(destination, false);
            }
        }
        finally
        {
            // Only this fixture's explicitly created files are eligible for cleanup.
            if (File.Exists(sentinel)) File.Delete(sentinel);
            if (Directory.Exists(nested)) Directory.Delete(nested, false);
            string image = Path.Combine(install, "KillerScan.exe");
            if (File.Exists(image)) File.Delete(image);
            if (Directory.Exists(install)) Directory.Delete(install, false);
            string relocated = Path.Combine(root, "relocated");
            if (Directory.Exists(relocated)) Directory.Delete(relocated, false);
            if (Directory.Exists(root)) Directory.Delete(root, false);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
