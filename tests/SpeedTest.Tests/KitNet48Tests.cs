using System;
using System.Linq;
using System.Runtime.InteropServices;
using KillerScan.Controls;

/// <summary>
/// The KillerUI kit runs on net48 with plain framework calls. The motif cache builder is started with five
/// arguments quoted onto one command line (ThemeMotif.QuoteArgument), so each one must come back whole from
/// the child's command line, including a path with spaces and a trailing backslash. Without that the child
/// does not see the builder switch and opens the app instead.
/// </summary>
internal static class KitNet48Tests
{
    public static void Run()
    {
        var expected = new[] { "--build-motif-cache", @"C:\Users\Some One\AppData\Local\KillerScan\Motif\", "1280.5", "720", "1.25", "say \"hi\"", @"a\b", @"tail\\", "" };
        var back = Split(string.Join(" ", expected.Select(ThemeMotif.QuoteArgument)));
        Require(back.Length == expected.Length, $"The command line keeps every argument ({back.Length} of {expected.Length}).");
        for (int i = 0; i < expected.Length; i++)
            Require(back[i] == expected[i], $"Argument {i} comes back as written: [{back[i]}].");

        Require(FolderNameRule.Check("name.", _ => false) == FolderNameProblem.TrailingDotOrSpace, "A trailing dot is refused.");
        Require(FolderNameRule.Check("name ", _ => false) == FolderNameProblem.None, "A trailing space is trimmed first.");
        Require(FolderNameRule.Check("name", _ => false) == FolderNameProblem.None, "A plain name passes.");
        Console.WriteLine("PASS: kit on net48.");
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
