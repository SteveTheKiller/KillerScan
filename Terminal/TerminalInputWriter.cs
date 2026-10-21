using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace KillerScan.Terminal
{
    /// <summary>
    /// Writes keystrokes and pastes to the pseudoconsole off the UI thread, in order. A child that
    /// is busy and not reading input makes ConPTY accept bytes slowly; measured, a 4 MB paste took
    /// eight seconds to write. Writing on the UI thread froze the window for that long, so input
    /// is queued here instead and the caller returns at once. The queue is capped: input beyond
    /// <see cref="MaxPendingBytes"/> waiting to be written is refused rather than held in memory.
    /// Disposing stops the worker; closing the session's handles unblocks a write in progress.
    /// </summary>
    internal sealed class TerminalInputWriter : IDisposable
    {
        internal const int MaxPendingBytes = 64 * 1024 * 1024;
        private readonly Stream _input;
        private readonly Queue<byte[]> _queue = new();
        private readonly object _gate = new();
        private readonly Thread _worker;
        private int _pending;
        private bool _closed;

        internal TerminalInputWriter(Stream input)
        {
            _input = input;
            _worker = new Thread(Run) { IsBackground = true, Name = "KillerScan terminal input" };
            _worker.Start();
        }

        /// <summary>Queues bytes to write. False when the terminal is closed or the queue is full.</summary>
        internal bool Enqueue(byte[] bytes)
        {
            if (bytes.Length == 0) return true;
            lock (_gate)
            {
                if (_closed || _pending + (long)bytes.Length > MaxPendingBytes) return false;
                _queue.Enqueue(bytes);
                _pending += bytes.Length;
                Monitor.Pulse(_gate);
                return true;
            }
        }

        /// <summary>Bytes queued but not yet written.</summary>
        internal int PendingBytes { get { lock (_gate) return _pending; } }

        private void Run()
        {
            while (true)
            {
                byte[] next;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_closed) Monitor.Wait(_gate);
                    if (_closed) return;
                    next = _queue.Peek();
                }
                try
                {
                    // Large pastes go in slices so a close is noticed between them.
                    for (int offset = 0; offset < next.Length; offset += 64 * 1024)
                    {
                        if (Volatile.Read(ref _closed)) return;
                        _input.Write(next, offset, Math.Min(64 * 1024, next.Length - offset));
                    }
                    _input.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // The shell went away; nothing more can be written.
                    Dispose();
                    return;
                }
                lock (_gate)
                {
                    if (_queue.Count > 0) { _queue.Dequeue(); _pending -= next.Length; }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _closed = true;
                _queue.Clear();
                _pending = 0;
                Monitor.PulseAll(_gate);
            }
        }
    }
}
