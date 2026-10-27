using System;
using System.IO;
using System.Text.RegularExpressions;
using KillerScan.Services;

internal static class UpdateHandoffTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerScan-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string payload = Path.Combine(root, "verified.exe");
        File.WriteAllText(payload, "abc");
        UpdateImagePublication.RememberVerified(payload, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
            new string('0', 40));
        string? ownedBatch = null;
        bool mutableElevatedCode = false;
        Exception? rejection = null;
        int starts = 0;
        try
        {
            try
            {
                UpdateService.StartSwap(payload, "9.9.9", new UpdateService.Platform
                {
                    CanWrite = _ => false,
                    Start = start =>
                    {
                        starts++;
                        if (string.Equals(Path.GetFileName(start.FileName), "cmd.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            var name = Regex.Match(start.Arguments, "^/d /v:off /c \"(KillerScan_update_[a-fA-F0-9]{32}\\.bat)\"$");
                            if (name.Success) ownedBatch = Path.Combine(start.WorkingDirectory, name.Groups[1].Value);
                        }
                        mutableElevatedCode = string.Equals(start.Verb, "runas", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(Path.GetFileName(start.FileName), "cmd.exe", StringComparison.OrdinalIgnoreCase);
                        return null;
                    }
                });
            }
            catch (Exception error) { rejection = error; }
            if (mutableElevatedCode)
                throw new InvalidOperationException("The updater attempted to elevate a mutable temporary command script.");
            if (starts != 0 || rejection is not System.Security.SecurityException)
                throw new InvalidOperationException("An untrusted elevated update target was not rejected at the trust boundary.", rejection);
            if (File.ReadAllText(payload) != "abc")
                throw new InvalidOperationException("A rejected elevated handoff changed its retained download.");
            Console.WriteLine("PASS an untrusted target never elevates a mutable temporary script.");
            string target = Path.Combine(root, "KillerScan.exe");
            var publication = TrustedUpdate.Publication(payload, target, "9.9.9", new string('0', 64), 1234, 5678, 4321, 8765);
            UninstallCommandTests.Parse(UninstallCommandTests.Decode(publication.Arguments));
            Console.WriteLine("PASS Windows PowerShell parses the immutable protected publication command.");
        }
        finally
        {
            if (ownedBatch != null) File.Delete(ownedBatch);
            File.Delete(payload);
            File.Delete(payload + ".recovery.txt");
            Directory.Delete(root, false);
        }
    }
}
