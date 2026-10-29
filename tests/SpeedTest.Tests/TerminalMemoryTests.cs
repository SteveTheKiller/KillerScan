using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using KillerScan.Terminal;

internal static class TerminalMemoryTests
{
    private const ulong MemoryCap = 384UL * 1024 * 1024;
    internal static void Run()
    {
        BoundedScrollback();
        foreach (string preset in new[] { "20000x80", "50000x80", "25000x160", "control20000x80" })
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception();
            Process? child = null;
            try
            {
                var limit = new ExtendedLimit
                {
                    Basic = new BasicLimit { Flags = 0x100 | 0x200 | 0x2000 },
                    ProcessMemory = new UIntPtr(MemoryCap), JobMemory = new UIntPtr(MemoryCap),
                };
                if (!SetInformationJobObject(job, 9, ref limit, (uint)Marshal.SizeOf<ExtendedLimit>()))
                    throw new Win32Exception();
                child = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule!.FileName,
                    "--terminal-memory-child " + preset)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                }) ?? throw new InvalidOperationException("Memory child did not start.");
                Task<string> output = child.StandardOutput.ReadToEndAsync(), error = child.StandardError.ReadToEndAsync();
                if (!AssignProcessToJobObject(job, child.Handle)) throw new Win32Exception();
                child.StandardInput.WriteLine("START"); child.StandardInput.Close();
                if (!child.WaitForExit(30000)) throw new TimeoutException("Capped memory child exceeded 30 seconds.");
                Console.Write(output.GetAwaiter().GetResult());
                if (child.ExitCode != 0) throw new InvalidOperationException("Capped memory child failed: " + error.GetAwaiter().GetResult());
                if (!QueryInformationJobObject(job, 9, out ExtendedLimit measured, (uint)Marshal.SizeOf<ExtendedLimit>(), IntPtr.Zero))
                    throw new Win32Exception();
                Console.WriteLine("MEMORY " + preset + " jobPeakMiB=" + MiB((long)measured.PeakJobMemory.ToUInt64()) + " capMiB=384");
            }
            finally
            {
                // Closing this private job terminates only its assigned child.
                CloseHandle(job);
                if (child != null)
                {
                    try { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
                    catch (InvalidOperationException) { }
                    child.Dispose();
                }
            }
        }
    }

    internal static void Child(string preset)
    {
        string? handshake = ReadHandshake();
        bool queried = IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out bool inJob);
        if (handshake != "START" || !queried || !inJob)
            throw new InvalidOperationException("Memory workload requires its parent job. Read "
                + (handshake == null ? "nothing" : "\"" + string.Join(" ", Array.ConvertAll(handshake.ToCharArray(), c => ((int)c).ToString("X4"))) + "\"")
                + ", in job: " + (queried ? inJob.ToString() : "query failed") + ".");
        if (preset == "control20000x80")
        {
            Exception? failure = null;
            var thread = new System.Threading.Thread(() => { try { Control(); } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(System.Threading.ApartmentState.STA); thread.Start(); thread.Join();
            if (failure != null) throw failure;
            return;
        }
        int lines, columns;
        switch (preset)
        {
            case "20000x80": lines = 20000; columns = 80; break;
            case "50000x80": lines = 50000; columns = 80; break;
            case "25000x160": lines = 25000; columns = 160; break;
            default: throw new ArgumentException("Unknown bounded memory preset.");
        }
        // A capacity workload: the whole transcript is the measurement, so these buffers are unbounded.
        var buffer = new TerminalBuffer(columns, 25) { ScrollbackLimit = int.MaxValue };
        var parser = new VtParser(buffer);
        var timer = Stopwatch.StartNew();
        for (int row = 0; row < lines; row++)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(Row(row, columns) + "\r\n");
            parser.Feed(bytes, bytes.Length);
        }
        Verify(buffer, lines, columns);
        Report(preset, "retained", timer.ElapsedMilliseconds);
        for (int replacement = 1; replacement <= 3; replacement++)
        {
            timer.Restart();
            buffer = Replace(buffer, columns);
            long pause = timer.ElapsedMilliseconds;
            Verify(buffer, lines, columns);
            Report(preset, "replacement" + replacement, pause);
        }
        buffer.Resize(columns / 2, 20); buffer.Resize(columns, 25);
        parser = new VtParser(buffer);
        byte[] alternate = Encoding.ASCII.GetBytes("\u001b[?1049hfixture\u001b[?1049l");
        parser.Feed(alternate, alternate.Length);
        Verify(buffer, lines, columns);
        Report(preset, "resize-alt-preserved", 0);
        GC.KeepAlive(buffer);
    }

    /// <summary>
    /// The first stdin line, read as bytes. When the console's code page is UTF-8 (65001), the
    /// parent's .NET Framework stdin writer sends a byte order mark as soon as it is created, and
    /// Console.ReadLine would hand that back as part of the line, so a leading mark is skipped.
    /// </summary>
    private static string? ReadHandshake()
    {
        using var input = Console.OpenStandardInput();
        var bytes = new System.Collections.Generic.List<byte>();
        for (int b; bytes.Count < 64 && (b = input.ReadByte()) >= 0 && b != '\n';) bytes.Add((byte)b);
        if (bytes.Count == 0) return null;
        int skip = bytes.Count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.ASCII.GetString(bytes.ToArray(), skip, bytes.Count - skip).TrimEnd('\r');
    }

    private static void Control()
    {
        var terminal = new TerminalControl();
        terminal.Buffer.ScrollbackLimit = int.MaxValue;
        try
        {
            var parser = new VtParser(terminal.Buffer);
            for (int row = 0; row < 20000; row++)
            {
                byte[] bytes = Encoding.ASCII.GetBytes(Row(row, 80) + "\r\n"); parser.Feed(bytes, bytes.Length);
            }
            for (int replacement = 0; replacement <= 3; replacement++)
            {
                var timer = Stopwatch.StartNew();
                if (replacement > 0) terminal = ReplaceControl(terminal);
                long pause = timer.ElapsedMilliseconds;
                Verify(terminal.Buffer, 20000, 80);
                var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(terminal)!;
                var provider = (System.Windows.Automation.Provider.ITextProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Text);
                if (provider.DocumentRange.GetText(-1).Length != 20000 * 82)
                    throw new InvalidOperationException("UIA did not preserve the full measured transcript.");
                Report("control20000x80", "replacement" + replacement + "-with-UIA", pause);
            }
            GC.KeepAlive(terminal);
        }
        finally { terminal.Dispose(); }
    }

    private static TerminalControl ReplaceControl(TerminalControl previous)
    {
        var next = new TerminalControl(); next.Buffer.ScrollbackLimit = int.MaxValue; next.Buffer.ImportHistory(previous.Buffer); previous.Dispose(); return next;
    }

    private static TerminalBuffer Replace(TerminalBuffer previous, int columns)
    {
        var next = new TerminalBuffer(columns, 25) { ScrollbackLimit = int.MaxValue }; next.ImportHistory(previous); return next;
    }
    /// <summary>A session left open (ping -t for days) stops growing at the default limit and keeps
    /// its newest lines, through scrolling and through a terminal replacement.</summary>
    private static void BoundedScrollback()
    {
        var buffer = new TerminalBuffer(80, 25);
        var parser = new VtParser(buffer);
        for (int row = 0; row < 30000; row++)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(Row(row, 80) + "\r\n"); parser.Feed(bytes, bytes.Length);
        }
        int limit = buffer.ScrollbackLimit;
        if (limit > 20000 || buffer.TotalLines > limit + 512 + 25)
            throw new InvalidOperationException("Scrollback grew past its limit: " + buffer.TotalLines + " lines, limit " + limit + ".");
        int lastRow = buffer.TotalLines - 1;
        while (lastRow > 0 && buffer.LineAt(lastRow)[0].Ch == 0) lastRow--;
        var last = buffer.LineAt(lastRow);
        if (new string(Array.ConvertAll(last, cell => (char)cell.Ch), 0, 8) != "00029999")
            throw new InvalidOperationException("The newest lines were not the ones kept.");
        var next = Replace(buffer, 80);
        next.ScrollbackLimit = limit;
        var fresh = new TerminalBuffer(80, 25); fresh.ImportHistory(buffer);
        if (fresh.TotalLines > limit + 512 + 25) throw new InvalidOperationException("A replacement terminal brought back more than the limit.");
        Console.WriteLine("PASS scrollback stays at its limit (" + limit + " lines) and keeps the newest output.");
    }

    private static string Row(int row, int columns) => row.ToString("D8", CultureInfo.InvariantCulture) + new string('x', columns - 8);
    private static void Verify(TerminalBuffer buffer, int count, int columns)
    {
        for (int row = 0; row < count; row++)
        {
            string expected = Row(row, columns); var actual = buffer.LineAt(row);
            for (int col = 0; col < columns; col++)
                if (actual[col].Ch != expected[col]) throw new InvalidOperationException("Transcript changed at row " + row + ", column " + col);
        }
    }
    private static void Report(string preset, string phase, long milliseconds)
    {
        long managed = GC.GetTotalMemory(true);
        using var process = Process.GetCurrentProcess(); process.Refresh();
        Console.WriteLine("MEMORY " + preset + " " + phase + " privateMiB=" + MiB(process.PrivateMemorySize64)
            + " managedMiB=" + MiB(managed) + " pauseMs=" + milliseconds);
    }
    private static string MiB(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        internal BasicLimit Basic;
        internal IoCounters Io;
        internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimit info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int type, out ExtendedLimit info, uint length, IntPtr returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
