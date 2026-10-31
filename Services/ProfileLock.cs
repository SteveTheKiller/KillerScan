using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace KillerScan.Services
{
    /// <summary>
    /// Serializes profile transactions across Windows sessions using a stable sidecar in the
    /// profile directory. Its permissions follow that directory, rather than a public object name.
    /// Callers must dispose synchronously on the acquiring thread without pumping the dispatcher.
    /// </summary>
    internal static class ProfileLock
    {
        private static readonly ConcurrentDictionary<string, Ownership> Owners = new(StringComparer.OrdinalIgnoreCase);

        internal static IDisposable Acquire(string kind, string path, TimeSpan timeout, string busyMessage)
        {
            string sidecar = Path.GetFullPath(path) + "." + kind + ".lock";
            var owner = Owners.GetOrAdd(sidecar, _ => new Ownership());
            var elapsed = Stopwatch.StartNew();
            if (!Monitor.TryEnter(owner, timeout)) throw new IOException(busyMessage);
            try
            {
                if (owner.Depth == 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
                    while (true)
                    {
                        try
                        {
                            // Never truncate or unlink this file: replacing its identity would let
                            // a second process acquire a different handle to the same transaction.
                            owner.Stream = new FileStream(sidecar, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                            break;
                        }
                        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
                        {
                            var remaining = timeout - elapsed.Elapsed;
                            if (remaining <= TimeSpan.Zero) throw new IOException(busyMessage, error);
                            Thread.Sleep((int)Math.Min(25, Math.Max(1, remaining.TotalMilliseconds)));
                        }
                    }
                }
                owner.Depth++;
                return new Lease(owner);
            }
            catch { Monitor.Exit(owner); throw; }
        }

        private sealed class Ownership
        {
            internal FileStream? Stream;
            internal int Depth;
        }

        private sealed class Lease(Ownership owner) : IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                try
                {
                    if (--owner.Depth == 0) { owner.Stream!.Dispose(); owner.Stream = null; }
                    _disposed = true;
                }
                finally { Monitor.Exit(owner); }
            }
        }
    }
}
