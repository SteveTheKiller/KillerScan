using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace KillerScan.Services
{
    internal static class UpdateImagePublication
    {
        private static readonly ConcurrentDictionary<string, string> VerifiedHashes =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static void RememberVerified(string path, string sha256)
        {
            if (sha256.Length != 64 || Array.Exists(sha256.ToCharArray(), c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("The verified update hash is invalid.");
            VerifiedHashes[Path.GetFullPath(path)] = sha256;
        }

        internal static string VerifiedHash(string download)
        {
            if (!VerifiedHashes.TryGetValue(Path.GetFullPath(download), out string? expected))
                throw new InvalidDataException("The update has no retained verification hash.");
            return expected;
        }

        /// <summary>The publication script. With <paramref name="showReport"/> a failure opens the recovery report.
        /// <paramref name="overrides"/> replaces individual operations and exists for checks only.</summary>
        internal static string Script(string download, string target, bool elevated,
            string releasesUrl, string displayName, string? version = null, string? hive = null,
            string? reopen = null, bool showReport = false, string? overrides = null)
        {
            download = Path.GetFullPath(download);
            target = Path.GetFullPath(target);
            if (!VerifiedHashes.TryGetValue(download, out string? expected))
                throw new InvalidDataException("This update has no retained verification hash. Download retained at: " + download);
            string staged = target + ".new-" + Guid.NewGuid().ToString("N");
            string backup = target + ".old-" + Guid.NewGuid().ToString("N");
            string restore = target + ".restore-" + Guid.NewGuid().ToString("N");
            string report = download + ".recovery.txt";
            File.WriteAllText(report, "Update publication has not been confirmed. If the helper cannot run, retain these files."
                + Environment.NewLine + "Target: " + target + Environment.NewLine + "Download: " + download
                + Environment.NewLine + "Staging path (not yet verified): " + staged
                + Environment.NewLine + "Backup path: " + backup + Environment.NewLine + "Restoration path: " + restore
                + Environment.NewLine + "Expected SHA-256: " + expected + Environment.NewLine + "Manual download: " + releasesUrl);
            var script = new StringBuilder(Function);
            script.AppendLine("$plan = [pscustomobject]@{");
            Value(script, "Download", download); Value(script, "Target", target);
            Value(script, "Staged", staged); Value(script, "Backup", backup);
            Value(script, "Restore", restore);
            Value(script, "Report", report); Value(script, "Hash", expected);
            Value(script, "Releases", releasesUrl); Value(script, "DisplayName", displayName);
            Value(script, "Version", version ?? ""); Value(script, "Hive", hive ?? "");
            Value(script, "Reopen", reopen ?? "");
            Value(script, "Explorer", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
            script.AppendLine("Elevated = " + (elevated ? "$true" : "$false"));
            script.AppendLine("ShowReport = " + (showReport ? "$true" : "$false")).AppendLine("}");
            script.AppendLine(Operations);
            if (overrides is not null) script.AppendLine(overrides);
            // The elevated helper must not open a window as administrator, so instead of showing the
            // report it reopens whatever copy is installed. The user is not left with the app
            // closed and the old version silently still in place.
            script.AppendLine("if (Invoke-UpdateImagePublication $plan $ops) { exit 0 } else { if ($plan.ShowReport) { try { & $ops.ShowReport $plan.Report } catch { } }; "
                + "if ($plan.Elevated) { try { & $ops.Relaunch $plan } catch { } }; exit 1 }");
            return script.ToString();
        }

        private static void Value(StringBuilder script, string name, string value) =>
            script.AppendLine(name + " = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
                + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "'))");

        private const string Function = """
$ErrorActionPreference = 'Stop'
function Invoke-UpdateImagePublication {
    param($plan, $ops)
    $stageVerified = $false
    try {
        $bytes = & $ops.Read $plan.Download
        if ((& $ops.Hash $bytes) -ne $plan.Hash) { throw 'The download no longer matches its verified SHA-256.' }
        & $ops.Stage $plan.Staged $bytes
        if ((& $ops.Hash (& $ops.Read $plan.Staged)) -ne $plan.Hash) { throw 'The staged image failed SHA-256 verification.' }
        $stageVerified = $true
        if (& $ops.Exists $plan.Target) { & $ops.Replace $plan.Staged $plan.Target $plan.Backup }
        else { & $ops.Move $plan.Staged $plan.Target }
        if ((& $ops.Hash (& $ops.Read $plan.Target)) -ne $plan.Hash) { throw 'The published image failed SHA-256 verification.' }
        & $ops.Metadata $plan
        & $ops.Relaunch $plan
    }
    catch {
        $failure = $_.Exception.ToString()
        $restoration = ''
        try {
            if (!(& $ops.Exists $plan.Target) -and (& $ops.Exists $plan.Backup)) {
                & $ops.Restore $plan.Backup $plan.Target $plan.Restore
            }
        }
        catch { $restoration = [Environment]::NewLine + 'Restoration failed: ' + $_.Exception.ToString() }
        $message = 'Update publication failed: ' + $failure + $restoration + [Environment]::NewLine +
            'Target: ' + $plan.Target + [Environment]::NewLine + 'Download: ' + $plan.Download +
            [Environment]::NewLine + 'Staging path (verified: ' + $stageVerified + '): ' + $plan.Staged +
            [Environment]::NewLine + 'Backup: ' + $plan.Backup + [Environment]::NewLine + 'Restoration path: ' + $plan.Restore +
            [Environment]::NewLine + 'Report: ' + $plan.Report + [Environment]::NewLine + 'Manual download: ' + $plan.Releases
        try { & $ops.Report $plan.Report $message } catch { [Console]::Error.WriteLine($message) }
        return $false
    }
    foreach ($path in @($plan.Download, $plan.Staged, $plan.Backup, $plan.Restore, $plan.Report)) {
        try { & $ops.Delete $path } catch { }
    }
    return $true
}

""";
        private const string Operations = """
$ops = @{
    Read = { param($path) ,([IO.File]::ReadAllBytes($path)) }
    Hash = { param($bytes)
        $hash = [Security.Cryptography.SHA256]::Create()
        try { [BitConverter]::ToString($hash.ComputeHash([byte[]]$bytes)).Replace('-', '') }
        finally { $hash.Dispose() }
    }
    Stage = { param($path, $bytes)
        $stream = New-Object IO.FileStream($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
            [IO.FileShare]::None, 65536, [IO.FileOptions]::WriteThrough)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
    }
    Exists = { param($path) [IO.File]::Exists($path) }
    Replace = { param($source, $target, $backup)
        $originalHash = & $ops.Hash (& $ops.Read $target)
        $replacementHash = & $ops.Hash (& $ops.Read $source)
        for ($attempt = 0; $attempt -lt 10; $attempt++) {
            try { [IO.File]::Replace($source, $target, $backup); return }
            catch {
                $code = $_.Exception.GetBaseException().HResult -band 65535
                if ($attempt -ge 9 -or $code -notin @(32, 33)) { throw }
                if (!(& $ops.Exists $source) -or !(& $ops.Exists $target) -or (& $ops.Exists $backup)) { throw }
                if ((& $ops.Hash (& $ops.Read $source)) -ne $replacementHash -or
                    (& $ops.Hash (& $ops.Read $target)) -ne $originalHash) { throw }
                Start-Sleep -Seconds 1
                if (!(& $ops.Exists $source) -or !(& $ops.Exists $target) -or (& $ops.Exists $backup)) { throw }
                if ((& $ops.Hash (& $ops.Read $source)) -ne $replacementHash -or
                    (& $ops.Hash (& $ops.Read $target)) -ne $originalHash) { throw }
            }
        }
    }
    Move = { param($source, $target) [IO.File]::Move($source, $target) }
    Restore = { param($backup, $target, $restore)
        $bytes = & $ops.Read $backup
        $expected = & $ops.Hash $bytes
        & $ops.Stage $restore $bytes
        if ((& $ops.Hash (& $ops.Read $restore)) -ne $expected) { throw 'The restoration image failed SHA-256 verification.' }
        & $ops.Move $restore $target
    }
    Report = { param($path, $message) [IO.File]::WriteAllText($path, $message) }
    ShowReport = { param($path)
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.UseShellExecute = $true
        $start.FileName = Join-Path ([Environment]::SystemDirectory) 'notepad.exe'
        $start.Arguments = '"' + $path + '"'
        $process = [Diagnostics.Process]::Start($start)
        if ($null -eq $process) { throw 'The update recovery report did not open.' }
        $process.Dispose()
    }
    Delete = { param($path) [IO.File]::Delete($path) }
    Metadata = { param($plan)
        if ($plan.Version.Length -eq 0 -or $plan.Hive.Length -eq 0) { return }
        if ($plan.Hive -eq 'HKLM') { $root = [Microsoft.Win32.Registry]::LocalMachine }
        elseif ($plan.Hive -eq 'HKCU') { $root = [Microsoft.Win32.Registry]::CurrentUser }
        else { throw 'The update registry scope is invalid.' }
        foreach ($keyPath in @(('Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $plan.DisplayName),
            ('Software\' + $plan.DisplayName))) {
            $key = $root.CreateSubKey($keyPath)
            try {
                if ($keyPath.Contains('Uninstall')) {
                    $key.SetValue('DisplayVersion', $plan.Version)
                    $key.SetValue('Publisher', 'Steve the Killer')
                }
                else { $key.SetValue('Version', $plan.Version) }
            }
            finally { if ($null -ne $key) { $key.Dispose() } }
        }
    }
    Relaunch = { param($plan)
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.UseShellExecute = $true
        if ($plan.Elevated) { $start.FileName = $plan.Explorer; $start.Arguments = '"' + $plan.Target + '"' }
        else {
            $start.FileName = $plan.Target
            if ($plan.Reopen.Length -gt 0) { $start.Arguments = '"' + $plan.Reopen + '"' }
        }
        $process = [Diagnostics.Process]::Start($start)
        if ($null -eq $process) { throw 'The updated application did not start.' }
        $process.Dispose()
    }
}
""";
    }
}
