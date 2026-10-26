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
        if (SystemTools.Path("ssh.exe", _ => false) != "ssh.exe")
            throw new InvalidOperationException("Without Windows' OpenSSH client, a separately installed client is not kept.");
        if (!string.Equals(SystemTools.WorkingDirectory, system, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Device tools do not start in System32.");
        Console.WriteLine("PASS device actions start mstsc, ping and Windows' ssh from System32.");
    }
}
