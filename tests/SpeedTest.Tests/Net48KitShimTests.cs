using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KillerScan.Compat;

/// <summary>
/// The net48 stand-ins for the modern .NET calls the KillerUI kit files make (Services/Net48KitShims.cs).
/// The motif cache builder is started with five arguments through ArgumentList, so those arguments
/// must come back whole from the child's command line, including a path with spaces and a trailing
/// backslash. Without that the child does not see the builder switch and opens the app instead.
/// </summary>
internal static class Net48KitShimTests
{
    public static void Run()
    {
        var expected = new[] { "--build-motif-cache", @"C:\Users\Some One\AppData\Local\KillerScan\Motif\", "1280.5", "720", "1.25", "say \"hi\"", "" };
        var start = new ProcessStartInfo("x.exe");
        foreach (var arg in expected) start.ArgumentList.Add(arg);
        var back = Split(start.Arguments);
        Require(back.Length == expected.Length, $"ArgumentList keeps every argument ({back.Length} of {expected.Length}).");
        for (int i = 0; i < expected.Length; i++)
            Require(back[i] == expected[i], $"Argument {i} comes back as written: [{back[i]}].");

        Require(Math.Clamp(1.5, 0, 1) == 1 && Math.Clamp(-1.0, 0, 1) == 0 && Math.Clamp(0.25, 0, 1) == 0.25, "Math.Clamp holds the value in range.");
        Require(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{1.5:0.##}") == "1.5", "string.Create formats in the given culture.");
        Require(string.Join(",", "a||b".Split('|', StringSplitOptions.RemoveEmptyEntries)) == "a,b", "Split(char, options) drops empty parts.");
        Require("a.".EndsWith('.') && !"".EndsWith('.'), "EndsWith(char) checks the last character.");
        var (key, value) = new KeyValuePair<string, int>("k", 3);
        Require(key == "k" && value == 3, "KeyValuePair deconstructs.");
        Require(!string.IsNullOrEmpty(Environment.ProcessPath), "Environment.ProcessPath names the running exe.");

        var table = new ConditionalWeakTable<object, object>();
        var held = new object();
        table.Add(held, new object());
        table.Clear();
        Require(!table.TryGetValue(held, out _), "ConditionalWeakTable.Clear empties the table.");

        string dir = Path.Combine(Path.GetTempPath(), "KillerScan-ShimTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.tmp"), b = Path.Combine(dir, "b.png");
            File.WriteAllText(a, "new"); File.WriteAllText(b, "old");
            bool refused = false;
            try { File.Move(a, b, overwrite: false); } catch (IOException) { refused = true; }
            Require(refused && File.ReadAllText(b) == "old", "File.Move without overwrite leaves an existing target alone.");
            File.Move(a, b, overwrite: true);
            Require(!File.Exists(a) && File.ReadAllText(b) == "new", "File.Move with overwrite replaces the target.");
        }
        finally { Directory.Delete(dir, true); }

        var done = new System.Threading.Tasks.TaskCompletionSource();
        done.SetResult();
        Require(done.Task.IsCompleted, "The non-generic TaskCompletionSource completes its task.");
        Console.WriteLine("PASS: net48 kit shims.");
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string line, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static string[] Split(string arguments)
    {
        IntPtr list = CommandLineToArgvW("x.exe " + arguments, out int count);
        try
        {
            var result = new string[count - 1];
            for (int i = 1; i < count; i++) result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, i * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(list); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
    }
}
