using System;
using System.IO;
using KillerScan.Services;

/// <summary>Device actions start Windows tools by full path, never by a name Windows would search for.</summary>
internal static class SystemToolsTests
{
    internal static void Run()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (string tool in new[] { "mstsc.exe", "ping.exe" })
            if (!string.Equals(SystemTools.Path(tool), Path.Combine(system, tool), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(tool + " is not started from System32: " + SystemTools.Path(tool));
        string openSsh = Path.Combine(system, "OpenSSH", "ssh.exe");
        if (SystemTools.Path("ssh.exe", path => path == openSsh) != openSsh)
            throw new InvalidOperationException("Windows' OpenSSH client is not preferred.");
        // Without Windows' client, a separately installed one is found on PATH by full path. The app's
        // own folder and relative PATH entries are never searched, and a bare name never reaches
        // Windows, which would look beside the exe first.
        const string app = @"C:\Users\someone\Downloads";
        string installed = @"C:\Tools\OpenSSH\ssh.exe";
        string path = app + @";.\bin;C:\Tools\OpenSSH;C:\Windows";
        string found = SystemTools.Path("ssh.exe", p => p == installed || p == Path.Combine(app, "ssh.exe") || p == @".\bin\ssh.exe", path, app);
        if (found != installed)
            throw new InvalidOperationException("Without Windows' OpenSSH client, the installed client on PATH is not used by full path: " + found);
        string none = SystemTools.Path("ssh.exe", p => p == Path.Combine(app, "ssh.exe"), app, app);
        if (!Path.IsPathRooted(none) || string.Equals(none, Path.Combine(app, "ssh.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("With no client installed, ssh resolves to a planted copy or a bare name: " + none);
        if (!string.Equals(SystemTools.WorkingDirectory, system, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Device tools do not start in System32.");
        Console.WriteLine("PASS device actions start mstsc, ping and Windows' ssh from System32.");
    }
}
