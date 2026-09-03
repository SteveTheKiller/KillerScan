using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace KillerScan.Terminal
{
    internal sealed class ConPtySession : IDisposable
    {
        private IntPtr _pc = IntPtr.Zero;          // HPCON
        private IntPtr _process = IntPtr.Zero;
        private IntPtr _thread = IntPtr.Zero;
        private SafeFileHandle? _inWrite;
        private SafeFileHandle? _outRead;
        private int _disposed;
        private bool _watching;
        private readonly object _consoleGate = new();

        public Stream Output { get; private set; } = Stream.Null;

        public Stream Input { get; private set; } = Stream.Null;

        public event Action<int>? Exited;

        public bool HasExited { get; private set; }

        public static ConPtySession Start(string commandLine, string workingDir, short cols, short rows)
        {
            if (cols < 1) cols = 80;
            if (rows < 1) rows = 25;

            var s = new ConPtySession();
            SafeFileHandle? inRead = null, outWrite = null;

            try
            {

                if (!CreatePipe(out inRead, out var inWrite, IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (in)");
                s._inWrite = inWrite;
                if (!CreatePipe(out var outRead, out outWrite, IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (out)");

                s._outRead = outRead;

                var size = new COORD { X = cols, Y = rows };
                int hr = CreatePseudoConsole(size, inRead, outWrite, 0, out s._pc);
                if (hr != 0) throw Marshal.GetExceptionForHR(hr) ?? new Win32Exception(hr);

                inRead.Dispose();   inRead = null;
                outWrite.Dispose(); outWrite = null;

                s.Launch(commandLine, workingDir);

                s.Output = new FileStream(s._outRead, FileAccess.Read, 4096, isAsync: false);
                s.Input  = new FileStream(s._inWrite, FileAccess.Write, 4096, isAsync: false);

                return s;
            }
            catch
            {
                inRead?.Dispose();
                outWrite?.Dispose();
                s.Dispose();
                throw;
            }
        }

        private void Launch(string commandLine, string workingDir)
        {
            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

            IntPtr bytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            si.lpAttributeList = Marshal.AllocHGlobal(bytes.ToInt32());

            try
            {
                if (!InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref bytes))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");

                if (!UpdateProcThreadAttribute(si.lpAttributeList, 0,
                        (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _pc, (IntPtr)IntPtr.Size,
                        IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");

                if (string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir))
                    workingDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                var cmd = new System.Text.StringBuilder(commandLine);

                if (!CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDir,
                        ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess");

                _process = pi.hProcess;
                _thread  = pi.hThread;
            }
            finally
            {
                if (si.lpAttributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(si.lpAttributeList);
                    Marshal.FreeHGlobal(si.lpAttributeList);
                }
            }
        }

        public void WatchForExit()
        {
            if (_watching) return;
            _watching = true;
            IntPtr process = _process;
            var t = new Thread(() =>
            {
                try
                {
                    _ = WaitForSingleObject(process, INFINITE);
                    GetExitCodeProcess(process, out int code);
                    HasExited = true;
                    CloseConsole();
                    try { Exited?.Invoke(code); } catch { }
                }
                finally { CloseHandle(process); }
            }) { IsBackground = true, Name = "ConPTY exit" };
            t.Start();
        }

        private void CloseConsole()
        {
            lock (_consoleGate)
            {
                IntPtr console = Interlocked.Exchange(ref _pc, IntPtr.Zero);
                if (console != IntPtr.Zero) ClosePseudoConsole(console);
            }
        }

        public void Resize(short cols, short rows)
        {
            if (_pc == IntPtr.Zero || _disposed != 0) return;
            if (cols < 1) cols = 1;
            if (rows < 1) rows = 1;
            if (!Monitor.TryEnter(_consoleGate)) return;
            try
            {
                if (_pc != IntPtr.Zero && _disposed == 0)
                    _ = ResizePseudoConsole(_pc, new COORD { X = cols, Y = rows });
            }
            finally { Monitor.Exit(_consoleGate); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            CloseConsole();

            try { Input.Dispose(); }  catch { }
            try { Output.Dispose(); } catch { }
            _inWrite?.Dispose();
            _outRead?.Dispose();

            if (_thread  != IntPtr.Zero) { CloseHandle(_thread);  _thread  = IntPtr.Zero; }
            if (!_watching && _process != IntPtr.Zero) { CloseHandle(_process); _process = IntPtr.Zero; }
        }

        private const int  PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        private const uint EXTENDED_STARTUPINFO_PRESENT        = 0x00080000;
        private const uint INFINITE                            = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe,
                                              IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput,
                                                      uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount,
                                                                     int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
                                                             IntPtr lpValue, IntPtr cbSize,
                                                             IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(string? lpApplicationName, System.Text.StringBuilder lpCommandLine,
                                                 IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
                                                 bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
                                                 string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
                                                 out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
