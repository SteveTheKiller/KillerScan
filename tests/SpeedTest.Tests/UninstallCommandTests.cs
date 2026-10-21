using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using KillerScan.Services;

internal static class UninstallCommandTests
{
    internal static void Run()
    {
        foreach (bool machine in new[] { false, true })
            foreach (bool silent in new[] { false, true })
            {
                var start = UninstallCompletion.StartInfo(machine, silent, 1234, 5678);
                if (start.UseShellExecute || !start.CreateNoWindow || start.Arguments.Length >= 30000)
                    throw new InvalidOperationException("The registered completion command is not bounded and headless.");
                string script = Decode(start.Arguments);
                Parse(script);
                if (machine)
                {
                    var nested = Regex.Match(script, @"-EncodedCommand ([A-Za-z0-9+/=]+)");
                    if (!nested.Success) throw new InvalidOperationException("The elevation wrapper lost its immutable child command.");
                    Parse(Decode("-EncodedCommand " + nested.Groups[1].Value));
                }
            }
        Console.WriteLine("PASS Windows PowerShell parses all user/machine, silent/interactive completion commands.");
    }

    internal static string Decode(string arguments)
    {
        var match = Regex.Match(arguments, @"-EncodedCommand ([A-Za-z0-9+/=]+)$");
        string bootstrap = Encoding.Unicode.GetString(Convert.FromBase64String(match.Groups[1].Value));
        Parse(bootstrap);
        var compressed = Regex.Match(bootstrap, @"FromBase64String\('([A-Za-z0-9+/=]+)'\)");
        using var input = new MemoryStream(Convert.FromBase64String(compressed.Groups[1].Value));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static void Parse(string script)
    {
        // Parse the actual command without executing any installation or registry operation.
        string check = "$text=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('"
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))
            + "'));$tokens=$null;$errors=$null;[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)|Out-Null;"
            + "if($errors.Count -ne 0){$errors|ForEach-Object{[Console]::Error.WriteLine($_.Message)};exit 1};exit 0";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe"), ImmutablePowerShell.Arguments(check))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var child = Process.Start(start) ?? throw new IOException("The Windows PowerShell parser did not start.");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(15000)) { child.Kill(); throw new TimeoutException("The PowerShell parser did not finish."); }
        if (child.ExitCode != 0) throw new InvalidOperationException("Completion command parse failed: " + error.GetAwaiter().GetResult());
        output.GetAwaiter().GetResult();
    }
}
