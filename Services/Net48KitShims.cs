global using KillerScan.Compat;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

// The KillerUI kit files compile against modern .NET APIs that .NET Framework 4.8 lacks. The kit
// says it supports net48, so these members stand in for them here, outside the kit folders, and
// the kit files stay byte identical to KillerUI. Each one keeps the .NET behavior the kit relies on.
namespace KillerScan.Compat
{
    internal static class Net48KitShims
    {
        extension(Math)
        {
            /// <summary>Math.Clamp: the value held between min and max.</summary>
            public static double Clamp(double value, double min, double max) =>
                value < min ? min : value > max ? max : value;

            public static int Clamp(int value, int min, int max) =>
                value < min ? min : value > max ? max : value;
        }

        extension(Environment)
        {
            /// <summary>Environment.ProcessPath: the running executable's full path.</summary>
            public static string? ProcessPath
            {
                get
                {
                    try { using var self = Process.GetCurrentProcess(); return self.MainModule?.FileName; }
                    catch (Exception) { return null; }
                }
            }
        }

        extension(string)
        {
            /// <summary>string.Create(provider, $"..."): the interpolation formatted in that culture.</summary>
            public static string Create(IFormatProvider? provider, FormattableString text) => text.ToString(provider);
        }

        extension(string text)
        {
            public string[] Split(char separator, StringSplitOptions options) => text.Split([separator], options);

            public bool EndsWith(char value) => text.Length > 0 && text[text.Length - 1] == value;

            public bool StartsWith(char value) => text.Length > 0 && text[0] == value;
        }

        extension<TKey, TValue>(KeyValuePair<TKey, TValue> pair)
        {
            public void Deconstruct(out TKey key, out TValue value)
            {
                key = pair.Key;
                value = pair.Value;
            }
        }

        extension(File)
        {
            /// <summary>File.Move with overwrite: true replaces the target in one step through
            /// MoveFileEx, as .NET does; false fails when the target exists.</summary>
            public static void Move(string sourceFileName, string destFileName, bool overwrite)
            {
                if (!overwrite) { File.Move(sourceFileName, destFileName); return; }
                if (!MoveFileEx(Path.GetFullPath(sourceFileName), Path.GetFullPath(destFileName),
                        MoveFileReplaceExisting | MoveFileCopyAllowed))
                    throw new IOException("Could not move the file.", Marshal.GetHRForLastWin32Error());
            }
        }

        extension(Process process)
        {
            /// <summary>Process.WaitForExitAsync: completes when the process exits.</summary>
            public Task WaitForExitAsync(CancellationToken cancellationToken = default)
            {
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => done.TrySetResult(true);
                if (process.HasExited) done.TrySetResult(true);
                if (cancellationToken.CanBeCanceled)
                    cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));
                return done.Task;
            }
        }

        extension(ProcessStartInfo start)
        {
            /// <summary>ProcessStartInfo.ArgumentList: each added argument is quoted onto Arguments
            /// by the CommandLineToArgvW rules, so a path with spaces stays one argument.</summary>
            public IList<string> ArgumentList => new ArgumentAppender(start);
        }

        extension<TKey, TValue>(ConditionalWeakTable<TKey, TValue> table) where TKey : class where TValue : class
        {
            /// <summary>ConditionalWeakTable.Clear: .NET Framework has the method, only internal.</summary>
            public void Clear()
            {
                var clear = typeof(ConditionalWeakTable<TKey, TValue>).GetMethod("Clear",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (clear is not null) { clear.Invoke(table, null); return; }
                var keys = typeof(ConditionalWeakTable<TKey, TValue>).GetProperty("Keys",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(table) as IEnumerable<TKey>;
                foreach (var key in keys?.ToList() ?? []) table.Remove(key);
            }
        }

        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileCopyAllowed = 0x2;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        private sealed class ArgumentAppender(ProcessStartInfo start) : Collection<string>
        {
            protected override void InsertItem(int index, string item)
            {
                // Each ArgumentList read is a new appender, so it adds to what Arguments already holds.
                base.InsertItem(index, item);
                start.Arguments = string.IsNullOrEmpty(start.Arguments) ? Quote(item) : start.Arguments + " " + Quote(item);
            }

            internal static string Quote(string arg)
            {
                if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
                var sb = new StringBuilder("\"");
                int slashes = 0;
                foreach (char c in arg)
                {
                    if (c == '\\') { slashes++; continue; }
                    if (c == '"') sb.Append('\\', slashes * 2 + 1);
                    else sb.Append('\\', slashes);
                    slashes = 0;
                    sb.Append(c);
                }
                sb.Append('\\', slashes * 2).Append('"');
                return sb.ToString();
            }
        }
    }
}

namespace System.Threading.Tasks
{
    /// <summary>The non-generic TaskCompletionSource that .NET 5 added, for the kit's raster builder.</summary>
    internal sealed class TaskCompletionSource
    {
        private readonly TaskCompletionSource<bool> _inner;

        public TaskCompletionSource() => _inner = new TaskCompletionSource<bool>();

        public TaskCompletionSource(TaskCreationOptions options) => _inner = new TaskCompletionSource<bool>(options);

        public Task Task => _inner.Task;

        public void SetResult() => _inner.SetResult(true);

        public bool TrySetResult() => _inner.TrySetResult(true);

        public void SetException(Exception exception) => _inner.SetException(exception);

        public bool TrySetException(Exception exception) => _inner.TrySetException(exception);

        public void SetCanceled() => _inner.SetCanceled();

        public bool TrySetCanceled() => _inner.TrySetCanceled();
    }
}
