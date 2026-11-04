using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using KillerScan.Services;

// The release certificate's SimpleName, read from a signed release with Get-AuthenticodeSignature.
// Every signer check in the updater has to hold to this exact name, not to a fragment of it: the
// elevated helper compared "Stephen Riley" with Equals and refused every signed machine install.
// Ported from Killendar's UpdateTrustRegressionTests after the KillerUI 3ecfda9 take.
internal static class UpdateTrustRegressionTests
{
    private const string ReleasePublisher = "Open Source Developer Stephen Riley";

    internal static void Run()
    {
        ElevatedHelperHoldsTheInstalledCopyToTheFullPublisherName();
        UpdateFromANameThatOnlyContainsThePublisherIsRefused();
        SignedCopyAcceptsANewerReleaseFromTheSamePublisher();
        UninstallTakesTheInstallerPublicationLeftovers();
        OnlyAnOfflineRevocationServerEarnsTheCacheOnlyRetry();
        EveryInstallLeaseIsScopedToItsFolder();
        PerUserHelperRecordsTheInstalledCopyBeforeCheckingTheDownload();
        Console.WriteLine("PASS update trust holds every signer check to the full release publisher name.");
    }

    private static void ElevatedHelperHoldsTheInstalledCopyToTheFullPublisherName()
    {
        string staged = Path.Combine(Path.GetTempPath(), "KillerScan-elevated-signer-" + Guid.NewGuid().ToString("N") + ".exe");
        UpdateImagePublication.RememberVerified(staged, new string('A', 64), new string('B', 40));
        try
        {
            var start = TrustedUpdate.Publication(staged, @"C:\Program Files\KillerScan\KillerScan.exe", "9.0.0",
                new string('C', 64), 1, 0, 2, 0, new string('D', 40));
            string body = Unwrap(start.Arguments);
            Require(PlanValue(body, "Signer") == ReleasePublisher, "The elevated plan holds the full publisher name.");
            int relaunch = body.IndexOf("Start-KillerUpdatedApp (", StringComparison.Ordinal);
            Require(relaunch > 0, "The elevated helper relaunches through the guard.");
            MatchCollection data = Regex.Matches(body.Substring(relaunch), @"Unicode\.GetString\(\[Convert\]::FromBase64String\('([A-Za-z0-9+/=]*)'\)\)");
            Require(data.Count >= 3, "The relaunch passes its target, signer and publisher.");
            Require(Encoding.Unicode.GetString(Convert.FromBase64String(data[2].Groups[1].Value)) == ReleasePublisher,
                "The elevated relaunch holds the recovery image to the full publisher name.");
        }
        finally { if (File.Exists(staged + ".recovery.txt")) File.Delete(staged + ".recovery.txt"); }
    }

    private static void UpdateFromANameThatOnlyContainsThePublisherIsRefused()
    {
        var incoming = new AssemblyName("KillerScan") { Version = new Version(9, 0, 0, 0) };
        try
        {
            ReleaseIdentity.Verify(incoming, "9.0.0", new Version(1, 9, 1),
                (false, "(not signed)"), (true, "Stephen Rileyson"), requireInstalledSignature: false);
        }
        catch (SecurityException error) when (error.Message == "The update is not signed by the publisher.") { return; }
        throw new InvalidOperationException("A signer whose name only contains the publisher must be refused.");
    }

    private static void SignedCopyAcceptsANewerReleaseFromTheSamePublisher()
    {
        var incoming = new AssemblyName("KillerScan") { Version = new Version(9, 0, 0, 0) };
        ReleaseIdentity.Verify(incoming, "9.0.0", new Version(1, 9, 1),
            (true, ReleasePublisher), (true, ReleasePublisher), requireInstalledSignature: true);
    }

    private static void UninstallTakesTheInstallerPublicationLeftovers()
    {
        foreach (string marker in new[] { ".install-", ".previous-", ".failed-" })
        {
            string folder = Path.Combine(Path.GetTempPath(), "KillerScan-leftovers-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string name = "KillerScan.exe" + marker + "0123456789abcdef0123456789abcdef";
                File.WriteAllText(Path.Combine(folder, name), "old image");
                File.WriteAllText(Path.Combine(folder, "KillerScan.exe" + marker + "notaguid"), "unrelated");
                Require(ExecutableDeployment.UninstallLeftovers(folder, "KillerScan.exe").SequenceEqual(new[] { name }),
                    "Uninstall takes the " + marker + " publication leftover and nothing unrelated.");
            }
            finally { Directory.Delete(folder, true); }
        }
    }

    private static void OnlyAnOfflineRevocationServerEarnsTheCacheOnlyRetry()
    {
        foreach (var (first, retry, trusted) in new[]
        {
            (0u, 0u, true), (0x80092013u, 0u, true), (0x80092013u, 0x800B0101u, false),
            (0x800B010Cu, 0u, false), (0x80096010u, 0u, false)
        })
        {
            var calls = new System.Collections.Generic.List<(uint revocation, uint flags)>();
            bool result = CodeSignature.VerifyTrust((revocation, flags) =>
            {
                calls.Add((revocation, flags));
                return calls.Count == 1 ? first : retry;
            });
            Require(result == trusted, $"Trust result for 0x{first:X8} then 0x{retry:X8}.");
            // WTD_REVOKE_WHOLECHAIN first; the retry is WTD_REVOKE_NONE with cache-only retrieval.
            Require(calls[0] == (1u, 0x80u), "The first check asks for whole-chain revocation.");
            Require(calls.Count == (first == 0x80092013u ? 2 : 1), "Only an offline revocation server earns a retry.");
            if (calls.Count == 2) Require(calls[1] == (0u, 0x1010u), "The retry is cache-only with no revocation.");
        }
    }

    private static void EveryInstallLeaseIsScopedToItsFolder()
    {
        // The update helpers lock Global\KillerTools-Install-KillerScan-<folder hash>. An install
        // that takes the bare product lock would not exclude them.
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "KillerScan.csproj"))) root = Path.GetDirectoryName(root);
        Require(root != null, "The repository root is found from the test output.");
        string sep = Path.DirectorySeparatorChar.ToString();
        foreach (string file in Directory.GetFiles(root!, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(sep + "obj" + sep) || file.Contains(sep + "bin" + sep) || file.Contains(sep + "tests" + sep)
                || file.EndsWith("InstallerExitCodes.cs", StringComparison.Ordinal)) continue;
            foreach (Match call in Regex.Matches(File.ReadAllText(file), @"InstallerExitCodes\.Acquire\(([^()]*(\([^()]*\))?[^()]*)\)"))
                Require(call.Groups[1].Value.Contains(","), Path.GetFileName(file) + " takes an unscoped install lease: " + call.Value);
        }
    }

    private static void PerUserHelperRecordsTheInstalledCopyBeforeCheckingTheDownload()
    {
        // The app has already closed when the helper starts. A download that fails its check must
        // still find the installed copy's identity recorded, or recovery refuses to reopen it.
        string path = Path.Combine(Path.GetTempPath(), "KillerScan-capture-order-" + Guid.NewGuid().ToString("N") + ".exe");
        UpdateImagePublication.RememberVerified(path, new string('A', 64), new string('B', 40));
        try
        {
            string script = UpdateImagePublication.Script(path, @"C:\Apps\KillerScan.exe", false,
                "https://example.invalid/releases", "KillerScan", originalHash: new string('C', 64));
            int function = script.IndexOf("function Invoke-UpdateImagePublication", StringComparison.Ordinal);
            int capture = script.IndexOf("& $ops.CaptureOriginal $plan", function, StringComparison.Ordinal);
            int downloadCheck = script.IndexOf("$plan.Download", function, StringComparison.Ordinal);
            Require(function > 0 && capture > 0 && downloadCheck > 0, "The publication function captures and checks.");
            Require(capture < downloadCheck, "The installed copy's identity is captured before the download is opened.");
        }
        finally { if (File.Exists(path + ".recovery.txt")) File.Delete(path + ".recovery.txt"); }
    }

    // Reverses ImmutablePowerShell.Arguments: the script travels gzip-compressed inside Base64.
    private static string Unwrap(string arguments)
    {
        Match payload = Regex.Match(arguments, @"FromBase64String\('([A-Za-z0-9+/=]+)'\)");
        Require(payload.Success, "The helper carries its script as Base64.");
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(payload.Groups[1].Value)), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // Plan values are written as Base64 so no quoting can break out of them.
    private static string PlanValue(string script, string name)
    {
        Match value = Regex.Match(script, "(?m)^" + Regex.Escape(name) + @" = \[Text\.Encoding\]::UTF8\.GetString\(\[Convert\]::FromBase64String\('([A-Za-z0-9+/=]*)'\)\)");
        Require(value.Success, "The plan sets " + name + ".");
        return Encoding.UTF8.GetString(Convert.FromBase64String(value.Groups[1].Value));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
