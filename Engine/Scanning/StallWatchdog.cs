namespace KillerScan.Engine
{
    /// <summary>
    /// Fires once when no new step is reported within the limit. A command-line scan once sat
    /// for minutes before printing anything, with no CPU and no sockets; with this it ends with a
    /// message that names the step it stalled in, instead of hanging an RMM job forever.
    /// </summary>
    public sealed class StallWatchdog : IDisposable
    {
        private readonly TimeSpan _limit;
        private readonly Action<string> _onStall;
        private readonly Timer _timer;
        private readonly object _gate = new();
        private string _step = "starting";
        private bool _done;

        public StallWatchdog(TimeSpan limit, Action<string> onStall)
        {
            _limit = limit;
            _onStall = onStall ?? throw new ArgumentNullException(nameof(onStall));
            _timer = new Timer(_ => Fire(), null, limit, Timeout.InfiniteTimeSpan);
        }

        /// <summary>Names the step now running and restarts the clock.</summary>
        public void Step(string name)
        {
            lock (_gate)
            {
                if (_done) return;
                _step = name;
                _timer.Change(_limit, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>Stops the watchdog for good; the work it guarded has started.</summary>
        public void Disarm()
        {
            lock (_gate)
            {
                _done = true;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        private void Fire()
        {
            string step;
            lock (_gate)
            {
                if (_done) return;
                _done = true;
                step = _step;
            }
            _onStall(step);
        }

        public void Dispose()
        {
            Disarm();
            _timer.Dispose();
        }
    }
}
