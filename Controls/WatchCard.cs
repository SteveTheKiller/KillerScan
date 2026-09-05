using System.ComponentModel;
using System.Windows.Media;
using KillerScan.Services;
using KillerScan.Terminal;

namespace KillerScan.Controls
{
    /// <summary>
    /// One Keep Alive target rendered as a card. Bound rather than replaced, so a reply
    /// pushes only the fields that moved instead of rebuilding the whole visual every
    /// two seconds and losing scroll position with it.
    /// </summary>
    internal sealed class WatchCard : INotifyPropertyChanged
    {
        /// <summary>Samples kept behind the sparkline. Forty at a two second cadence is about eighty seconds.</summary>
        private const int Window = 40;

        /// <summary>Drawing surface for the sparkline, in device independent pixels. Matches the card content width.</summary>
        private const double SparkWidth = 240;
        private const double SparkHeight = 26;

        /// <summary>Never scale below this, so an idle local link does not look like a spike field.</summary>
        private const double FloorMs = 20;

        private readonly Queue<long?> _history = new();

        private string _state = string.Empty;
        private string _latest = "?";
        private string _average = "?";
        private string _loss = "0.0%";
        private string _sent = "0";
        private string _changed = string.Empty;
        private bool _up;
        private bool _known;
        private Brush _stateBrush = Brushes.Gray;
        private Brush _sparkBrush = Brushes.Gray;
        private PointCollection _spark = [];

        internal WatchCard(string address, string waiting)
        {
            Address = address;
            _state = waiting;
            RefreshTheme();
        }

        public string Address { get; }

        /// <summary>Whether the last poll got a reply. Read by the shell's status tooltip.</summary>
        internal bool IsReplying => _known && _up;

        /// <summary>
        /// This target's checks and its own log. Both live on the card rather than in shared
        /// panes, so everything about one address is in one place and several can be compared
        /// side by side instead of one at a time.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<CheckRow> Checks { get; } = [];

        public System.Collections.ObjectModel.ObservableCollection<CardEvent> Events { get; } = [];

        /// <summary>Log lines kept per card. Short: the card is a glance, not an archive.</summary>
        private const int EventWindow = 30;

        internal void LogEvent(DateTimeOffset time, string state, bool up)
        {
            var palette = TerminalPalette.For(TerminalSkin.Default);
            Events.Add(new CardEvent
            {
                Time = time.ToLocalTime().ToString("HH:mm:ss"),
                State = state,
                Up = up,
                StateBrush = StateColor(up ? "WatchUpBrush" : "WatchDownBrush", palette.Ansi[up ? 2 : 1])
            });
            while (Events.Count > EventWindow) Events.RemoveAt(0);
        }

        public string State { get => _state; private set => Set(ref _state, value); }
        public string Latest { get => _latest; private set => Set(ref _latest, value); }
        public string Average { get => _average; private set => Set(ref _average, value); }
        public string Loss { get => _loss; private set => Set(ref _loss, value); }
        public string Sent { get => _sent; private set => Set(ref _sent, value); }
        public string Changed { get => _changed; private set => Set(ref _changed, value); }
        public Brush StateBrush { get => _stateBrush; private set => Set(ref _stateBrush, value); }
        public Brush SparkBrush { get => _sparkBrush; private set => Set(ref _sparkBrush, value); }
        public PointCollection Spark { get => _spark; private set => Set(ref _spark, value); }

        /// <summary>
        /// Folds one poll into the card. <paramref name="sample"/> already carries the running
        /// totals, so this only formats them and extends the sparkline.
        /// </summary>
        internal void Update(ConnectionSample sample, string state, bool up)
        {
            _known = true;
            _up = up;
            State = state;
            Latest = sample.Latest?.ToString() ?? "?";
            Average = sample.Received == 0 ? "?" : sample.Average.ToString("0.0");
            Loss = sample.Loss.ToString("0.0") + "%";
            Sent = sample.Sent.ToString();
            Changed = sample.Changed?.ToLocalTime().ToString("HH:mm:ss") ?? string.Empty;

            _history.Enqueue(sample.Latest);
            while (_history.Count > Window) _history.Dequeue();
            Spark = Plot();
            RefreshTheme();
        }

        /// <summary>
        /// Clears this card's history and counters. The card stays in place so its position
        /// in the wrap panel does not jump while the other targets keep reporting.
        /// </summary>
        internal void Reset(string waiting)
        {
            _history.Clear();
            Events.Clear();
            Checks.Clear();
            _known = false;
            _up = false;
            State = waiting;
            Latest = "?";
            Average = "?";
            Loss = "0.0%";
            Sent = "0";
            Changed = string.Empty;
            Spark = [];
            RefreshTheme();
        }

        /// <summary>
        /// Repoints the semantic colors at the active theme. Red and green come from the
        /// terminal palette because that already carries a tuned pair for all thirteen
        /// themes, which the theme brushes do not.
        /// </summary>
        internal void RefreshTheme()
        {
            var palette = TerminalPalette.For(TerminalSkin.Default);
            string key = !_known ? "WatchIdleBrush" : _up ? "WatchUpBrush" : "WatchDownBrush";
            Color color = !_known ? palette.Ansi[8] : palette.Ansi[_up ? 2 : 1];
            StateBrush = StateColor(key, color);
            SparkBrush = Frozen(_known && !_up ? palette.Ansi[1] : palette.Ansi[6]);
        }

        /// <summary>
        /// Re-labels the card after a language change. The card knows whether it is up, down or
        /// still waiting, so the caller supplies the three words and the right one is chosen here.
        /// Counters, history and the graph are untouched: only the words change.
        /// </summary>
        internal void Relabel(string reply, string noReply, string waiting)
        {
            State = !_known ? waiting : _up ? reply : noReply;

            // The log lines are immutable, so they are rebuilt rather than edited. Rebuilding also
            // gives the list something to notice: editing a property on them would not.
            var rewritten = Events
                .Select(e => new CardEvent { Time = e.Time, Up = e.Up, StateBrush = e.StateBrush,
                                             State = e.Up ? reply : noReply })
                .ToList();
            Events.Clear();
            foreach (var entry in rewritten) Events.Add(entry);
        }

        /// <summary>
        /// The up / down / waiting color for the current theme. The terminal palette carries a
        /// tuned red and green for all thirteen flat themes, so it stays the source for those.
        /// A theme that names its own brush wins: 98SE does, because the VGA green it would
        /// otherwise inherit is too light to read on that theme's white log surface.
        /// </summary>
        private static Brush StateColor(string key, Color fallback)
        {
            if (System.Windows.Application.Current?.TryFindResource(key) is SolidColorBrush b)
                return b;
            return Frozen(fallback);
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// Builds the sparkline against a fixed pixel box rather than stretching the shape,
        /// because a stretched polyline collapses to nothing when every sample is equal.
        /// A lost reply plots at the top so an outage reads as a spike, not a gap.
        /// </summary>
        private PointCollection Plot()
        {
            var samples = _history.ToArray();
            if (samples.Length < 2) return [];

            double ceiling = FloorMs;
            foreach (long? sample in samples)
                if (sample.HasValue && sample.Value > ceiling) ceiling = sample.Value;

            var points = new PointCollection(samples.Length);
            double step = SparkWidth / (samples.Length - 1);
            for (int i = 0; i < samples.Length; i++)
            {
                double value = samples[i] ?? ceiling;
                double y = SparkHeight - (value / ceiling * SparkHeight);
                points.Add(new System.Windows.Point(i * step, Math.Max(1, Math.Min(SparkHeight - 1, y))));
            }
            points.Freeze();
            return points;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
