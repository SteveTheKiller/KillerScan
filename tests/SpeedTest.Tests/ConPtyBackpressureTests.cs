using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KillerScan.Terminal;

// Review item BF-013. Measured: writing a 4 MB paste to a pseudoconsole whose child never reads
// input took about eight seconds, on the UI thread. Input now goes through an ordered background
// writer; queuing must return at once, the bytes must arrive in order, and closing must stop it.
// The child is an inert loopback ping with its output drained, and every wait has a deadline.
internal static class ConPtyBackpressureTests
{
    internal static void Run()
    {
        using (var session = ConPtySession.Start("cmd.exe /d /c ping -n 60 127.0.0.1 >nul", Environment.CurrentDirectory, 120, 30))
        {
            _ = Task.Run(() =>
            {
                var buffer = new byte[16384];
                try { while (session.Output.Read(buffer, 0, buffer.Length) > 0) { } } catch { }
            });
            using var writer = new TerminalInputWriter(session.Input);
            byte[] paste = Encoding.UTF8.GetBytes(new string('x', 16 * 1024 * 1024));
            var watch = Stopwatch.StartNew();
            Require(writer.Enqueue(paste), "A 16 MB paste is accepted.");
            Require(watch.ElapsedMilliseconds < 200, $"Queuing a 16 MB paste returns at once, not after {watch.ElapsedMilliseconds} ms.");
            Require(writer.PendingBytes > 0, "The paste is written in the background while the child is not reading.");
            writer.Dispose();
            Require(!writer.Enqueue([1]), "A closed writer refuses more input.");
        }

        // Order and completeness, against a stream that accepts bytes slowly.
        var slow = new SlowStream();
        using (var writer = new TerminalInputWriter(slow))
        {
            for (int i = 0; i < 50; i++) writer.Enqueue(Encoding.ASCII.GetBytes(i.ToString("D3") + ";"));
            var deadline = Stopwatch.StartNew();
            while (writer.PendingBytes > 0 && deadline.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
            string expected = string.Concat(Enumerable.Range(0, 50).Select(i => i.ToString("D3") + ";"));
            Require(slow.Text == expected, "Queued input arrives complete and in order.");
            Require(!writer.Enqueue(new byte[TerminalInputWriter.MaxPendingBytes + 1]), "Input beyond the queue cap is refused, not held.");
        }
        // Input the queue refuses is reported, not dropped silently. The control needs an STA thread.
        Exception? failure = null;
        var sta = new Thread(() =>
        {
            try
            {
                var terminal = new KillerScan.Terminal.TerminalControl();
                try
                {
                    terminal.Start("cmd.exe /d /c ping -n 60 127.0.0.1 >nul", Environment.CurrentDirectory);
                    int refused = 0;
                    terminal.InputRefused += () => refused++;
                    terminal.Send("dir");
                    Require(refused == 0, "Ordinary input is queued without a refusal.");
                    terminal.Send(new string('x', TerminalInputWriter.MaxPendingBytes + 1));
                    Require(refused == 1, "Input past the queue cap raises InputRefused instead of vanishing.");
                }
                finally { terminal.Dispose(); }
            }
            catch (Exception ex) { failure = ex; }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        if (!sta.Join(TimeSpan.FromSeconds(60))) throw new InvalidOperationException("The refused-input check finishes within a minute.");
        if (failure != null) throw failure;
        Console.WriteLine("PASS: terminal input is written in order off the UI thread, a 16 MB paste queues at once, and refused input is reported.");
    }

    private sealed class SlowStream : Stream
    {
        private readonly MemoryStream _data = new();
        internal string Text { get { lock (_data) return Encoding.ASCII.GetString(_data.ToArray()); } }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(5);
            lock (_data) _data.Write(buffer, offset, count);
        }
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
