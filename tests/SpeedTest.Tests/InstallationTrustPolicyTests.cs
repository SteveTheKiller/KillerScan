using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using KillerScan.Services;

internal static class InstallationTrustPolicyTests
{
    private const string Administrators = "S-1-5-32-544";
    private const string UntrustedCreator = "S-1-5-21-1001-1002-1003-1107";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const string ProtectedRoot = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

    internal static void Run()
    {
        var cases = Matrix();
        var failures = new List<string>();
        foreach (var item in cases)
        {
            try
            {
                bool accepted;
                var security = Descriptor(item.Sddl);
                try
                {
                    TrustedUpdate.ValidateMachineOwner(security, new SecurityIdentifier(item.CreatorOwner));
                    accepted = true;
                }
                catch (SecurityException) { accepted = false; }
                if (accepted != item.Expected)
                    failures.Add("Compiled policy: " + item.Name + ", expected " + item.Expected + ", accepted " + accepted + ".");
            }
            catch (Exception error) { failures.Add("Compiled policy fixture: " + item.Name + ": " + error); }
        }

        // A non-creating preflight can defer creator proof. The staging matrix always
        // supplies the actual creator SID to both policies.
        try { TrustedUpdate.ValidateMachineOwner(Descriptor(ProtectedRoot + "(A;OICIIO;GA;;;CO)")); }
        catch (Exception error) { failures.Add("Compiled policy rejected the creator-owner preflight without a staging owner: " + error); }

        // Run the second policy even when the compiled policy is red, so one bounded
        // invocation retains both boundaries' actual decisions.
        try { RunGeneratedPolicy(cases); }
        catch (Exception error) { failures.Add("Generated policy: " + error); }
        if (failures.Count != 0)
            throw new InvalidOperationException("Installation trust policy matrix failed:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS " + cases.Count + " shared installation trust cases and one compiled preflight control.");
    }

    private static List<PolicyCase> Matrix()
    {
        var cases = new List<PolicyCase>();
        // These literal flags and rights describe the security invariant independently
        // of either implementation's access-mask and inheritance calculations.
        var flags = new[]
        {
            "OIIO", "CIIO", "OICIIO", "OINPIO", "CINPIO", "OICINPIO",
            "OIIOID", "CIIOID", "OICIIOID", "OINPIOID", "CINPIOID", "OICINPIOID",
            "", "OI", "CI", "OICI", "ID", "OIID", "CIID", "OICIID"
        };
        foreach (string rights in new[] { "FA", "GA", "GW" })
            foreach (string inheritance in flags)
                cases.Add(new PolicyCase("Users write " + rights + " " + Label(inheritance),
                    ProtectedRoot + "(A;" + inheritance + ";" + rights + ";;;BU)", Administrators, false));
        foreach (string inheritance in flags)
            cases.Add(new PolicyCase("Users read/execute GRGX " + Label(inheritance),
                ProtectedRoot + "(A;" + inheritance + ";GRGX;;;BU)", Administrators, true));

        foreach (string trustee in new[] { "SY", "BA", TrustedInstaller })
            foreach (string rights in new[] { "FA", "GA", "GW" })
                foreach (string inheritance in new[] { "OICI", "OICIIO" })
                    cases.Add(new PolicyCase("Trusted writer " + trustee + " " + rights + " " + inheritance,
                        ProtectedRoot + "(A;" + inheritance + ";" + rights + ";;;" + trustee + ")", Administrators, true));

        cases.Add(new PolicyCase("SYSTEM root owner", "O:SYG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", Administrators, true));
        cases.Add(new PolicyCase("TrustedInstaller root owner", "O:" + TrustedInstaller + "G:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", Administrators, true));
        cases.Add(new PolicyCase("Untrusted root owner", "O:" + UntrustedCreator + "G:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", Administrators, false));
        cases.Add(new PolicyCase("Missing/null DACL grants access", "O:BAG:BA", Administrators, false));
        cases.Add(new PolicyCase("Present empty DACL denies access", "O:BAG:BAD:", Administrators, true));
        foreach (string rights in new[] { "0x00000002", "0x00000004", "0x00000040", "0x00010000", "0x00040000", "0x00080000" })
            cases.Add(new PolicyCase("Users effective specific write " + rights,
                ProtectedRoot + "(A;;" + rights + ";;;BU)", Administrators, false));

        foreach (string inheritance in new[] { "OIIO", "CIIO", "OICIIO", "OINPIO", "CINPIO", "OICINPIO", "OICIIOID" })
        {
            cases.Add(new PolicyCase("CREATOR_OWNER with trusted creator " + inheritance,
                ProtectedRoot + "(A;" + inheritance + ";GA;;;CO)", Administrators, true));
            cases.Add(new PolicyCase("CREATOR_OWNER with untrusted creator " + inheritance,
                ProtectedRoot + "(A;" + inheritance + ";GA;;;CO)", UntrustedCreator, false));
        }
        cases.Add(new PolicyCase("Untrusted staging owner without CREATOR_OWNER ACE", ProtectedRoot, UntrustedCreator, false));
        return cases;
    }

    private static DirectorySecurity Descriptor(string sddl)
    {
        // Binary replacement preserves the distinction between a missing DACL and
        // a present empty DACL instead of merging a partial SDDL into default ACLs.
        var raw = new RawSecurityDescriptor(sddl);
        var bytes = new byte[raw.BinaryLength];
        raw.GetBinaryForm(bytes, 0);
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.All);
        return security;
    }

    private static void RunGeneratedPolicy(List<PolicyCase> cases)
    {
        var script = new StringBuilder("$ErrorActionPreference='Stop';$failed=0;\n");
        script.Append("$policyText=").Append(Data(UninstallCompletion.MachineDirectoryPolicy)).AppendLine(";");
        script.AppendLine("$tokens=$null;$errors=$null;[void][Management.Automation.Language.Parser]::ParseInput($policyText,[ref]$tokens,[ref]$errors);");
        script.AppendLine("if($errors.Count -ne 0){$errors|ForEach-Object{[Console]::Error.WriteLine($_.Message)};exit 3};$policy=[scriptblock]::Create($policyText);");
        script.AppendLine("$cases=@(");
        for (int index = 0; index < cases.Count; index++)
        {
            var item = cases[index];
            script.Append("[pscustomobject]@{Name=").Append(Data(item.Name)).Append(";Sddl=").Append(Data(item.Sddl))
                .Append(";CreatorOwner=").Append(Data(item.CreatorOwner)).Append(";Expected=").Append(item.Expected ? "$true" : "$false")
                .Append(index + 1 == cases.Count ? "}\n" : "},\n");
        }
        script.AppendLine(");foreach($case in $cases){");
        script.AppendLine("try{$raw=New-Object Security.AccessControl.RawSecurityDescriptor($case.Sddl);$bytes=New-Object byte[] $raw.BinaryLength;$raw.GetBinaryForm($bytes,0);$acl=New-Object Security.AccessControl.DirectorySecurity;$acl.SetSecurityDescriptorBinaryForm($bytes,[Security.AccessControl.AccessControlSections]::All)}catch{[Console]::Error.WriteLine('FIXTURE '+$case.Name+': '+$_.Exception.ToString());exit 3};");
        script.AppendLine("$creatorOwner=[string]$case.CreatorOwner;$accepted=$false;try{& $policy;$accepted=$true}catch{};");
        script.AppendLine("if($accepted -ne $case.Expected){$failed++;[Console]::Error.WriteLine('FAIL '+$case.Name+': expected='+$case.Expected+' accepted='+$accepted)}else{[Console]::Out.WriteLine('PASS '+$case.Name)}");
        script.AppendLine("};[Console]::Out.WriteLine('Generated policy cases='+$cases.Count+' failures='+$failed);if($failed -ne 0){exit 1};exit 0;");

        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe"), ImmutablePowerShell.Arguments(script.ToString()))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };
        if (start.Arguments.Length >= 30000) throw new InvalidOperationException("Pure policy matrix command exceeds the bounded Windows command length.");
        using var child = Process.Start(start) ?? throw new IOException("The pure policy matrix process did not start.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        bool timedOut = !child.WaitForExit(20000);
        if (timedOut) { child.Kill(); child.WaitForExit(5000); }
        int? exitCode = child.HasExited ? child.ExitCode : (int?)null;
        bool outputComplete = stdout.Wait(5000), errorComplete = stderr.Wait(5000);
        string output = outputComplete ? stdout.Result : "<stdout incomplete>";
        string error = errorComplete ? stderr.Result : "<stderr incomplete>";
        Console.WriteLine("Pure policy process exit=" + (exitCode?.ToString() ?? "<missing>") + "; timedOut=" + timedOut + "; stdout:\n" + output);
        Console.Error.WriteLine("Pure policy process stderr:\npolicy stderr: " + error.Replace("\n", "\npolicy stderr: "));
        if (timedOut || exitCode != 0 || !outputComplete || !errorComplete)
            throw new InvalidOperationException("Pure policy process failed: exit=" + (exitCode?.ToString() ?? "<missing>")
                + "; timedOut=" + timedOut + ". Captured stdout/stderr are printed above.");
    }

    private static string Data(string value) => "[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('"
        + Convert.ToBase64String(Encoding.Unicode.GetBytes(value)) + "'))";
    private static string Label(string flags) => flags.Length == 0 ? "effective" : flags;

    private sealed class PolicyCase
    {
        internal readonly string Name, Sddl, CreatorOwner;
        internal readonly bool Expected;
        internal PolicyCase(string name, string sddl, string creatorOwner, bool expected)
        { Name = name; Sddl = sddl; CreatorOwner = creatorOwner; Expected = expected; }
    }
}
