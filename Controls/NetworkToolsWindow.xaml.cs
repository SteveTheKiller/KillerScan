using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using KillerScan.Terminal;
using System.Windows.Input;
using System.Windows.Media;
using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class NetworkToolsWindow : UserControl, IDisposable
    {
        private readonly bool _diagnostics;
        private readonly int[] _ports;
        private CancellationTokenSource? _run;
        private readonly ObservableCollection<ResultRow> _rows = [];
        private readonly ObservableCollection<WatchCard> _cards = [];
        private readonly Queue<string> _events = new();
        private bool _closed;
        private string L(string key) => TryFindResource(key) as string ?? key;

        internal NetworkToolsWindow(bool diagnostics, string targets, IEnumerable<int> ports, double scale)
        {
            InitializeComponent();
            _diagnostics = diagnostics;
            _ports = [.. ports.Where(p => p is > 0 and <= 65535).Distinct().Take(64)];
            Targets.Text = targets;
            Targets.IsReadOnly = diagnostics;
            TargetLabel.Visibility = Visibility.Collapsed;
            // Diagnostics is a flat list of check and result, so it keeps the table and gives
            // the whole pane to it. Keep Alive gets cards plus a splittable event log.
            Results.Visibility = diagnostics ? Visibility.Visible : Visibility.Collapsed;
            CardHost.Visibility = EventPane.Visibility = EventSplitter.Visibility =
                diagnostics ? Visibility.Collapsed : Visibility.Visible;
            if (diagnostics)
            {
                SplitterRow.Height = new GridLength(0);
                EventRow.Height = new GridLength(0);
                EventRow.MinHeight = 0;
            }
            Cards.ItemsSource = _cards;
            ThemeManager.ThemeChanged += OnThemeChanged;
            Heading.SetResourceReference(TextBlock.TextProperty, diagnostics ? "Str_Diag_Title" : "Str_View_KeepAlive");
            Hint.SetResourceReference(TextBlock.TextProperty, diagnostics ? "Str_Diag_Hint" : "Str_Watch_Hint");
            StartButton.SetResourceReference(ContentProperty, diagnostics ? "Str_Diag_Run" : "Str_Watch_Start");
            StartButton.SetResourceReference(ToolTipProperty, "Str_Watch_Reset");
            Heading.SetBinding(ToolTipProperty, new Binding("Text") { Source = Hint });
            if (Fonts.SystemFontFamilies.Any(f => f.Source == "ProFont IIx Nerd Font"))
                Events.FontFamily = new FontFamily("ProFont IIx Nerd Font");
            ApplyScale(scale);
            AddColumn(diagnostics ? "Str_Diag_Check" : "Str_Col_Ip", "Target", 150);
            AddColumn("Str_Diag_Result", "Result", diagnostics ? 430 : 95);
            if (!diagnostics)
            {
                AddColumn("Str_Watch_Sent", "Sent", 60);
                AddColumn("Str_Watch_Loss", "Loss", 65);
                AddColumn("Str_Watch_Latency", "Latency", 155);
                AddColumn("Str_Watch_Changed", "Changed", 170);
            }
            Results.ItemsSource = _rows;
        }

        internal void ApplyScale(double scale) => BodyHost.LayoutTransform = new ScaleTransform(scale, scale);

        internal void MatchScanTable(DataGrid reference)
        {
            Results.ColumnHeaderStyle = reference.ColumnHeaderStyle;
            Results.RowStyle = reference.RowStyle;
            Results.AlternationCount = reference.AlternationCount;
            Results.CellStyle = reference.CellStyle;
            Results.ColumnHeaderHeight = reference.ColumnHeaderHeight;
            Results.RowHeight = reference.RowHeight;
            Results.FontFamily = reference.FontFamily;
            Results.FontSize = reference.FontSize;
            Results.BorderThickness = reference.BorderThickness;
            Results.SetResourceReference(BorderBrushProperty, "PaneBorderBrush");
        }

        internal void IncludeTarget(string address)
        {
            if (_diagnostics || _run != null) return;
            var targets = Targets.Text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (!targets.Contains(address))
                Targets.Text = string.Join(", ", targets.Concat([address]));
        }

        private void AddColumn(string key, string path, double width)
        {
            var heading = new TextBlock();
            heading.SetResourceReference(TextBlock.TextProperty, key);
            heading.SetResourceReference(ToolTipProperty, key);
            var column = new DataGridTextColumn { Header = heading, Binding = new Binding(path), Width = width };
            if (_diagnostics && path == "Result")
            {
                column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                column.MinWidth = 160;
                column.ElementStyle = new Style(typeof(TextBlock));
                column.ElementStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            }
            Results.Columns.Add(column);
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_run != null) return;
            if (!ConnectionChecks.TryTargets(Targets.Text, out var addresses))
            { Status.Text = L("Str_Watch_Invalid"); return; }
            var run = _run = new CancellationTokenSource();
            StartButton.IsEnabled = Targets.IsEnabled = false;
            StopButton.IsEnabled = true;
            _rows.Clear(); _cards.Clear(); _events.Clear(); Events.Document.Blocks.Clear(); Status.Text = string.Empty;
            try
            {
                if (_diagnostics) await DiagnoseAsync(addresses[0], run.Token);
                else await WatchAsync(addresses, run.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (!_closed) Status.Text = L("Str_Diag_Error"); }
            finally
            {
                _run = null;
                run.Dispose();
                if (!_closed)
                {
                    StartButton.IsEnabled = Targets.IsEnabled = true;
                    StopButton.IsEnabled = false;
                    if (string.IsNullOrEmpty(Status.Text)) Status.Text = L("Str_Watch_Stopped");
                }
            }
        }

        /// <summary>
        /// One watched target. Held in a list rather than a fixed array so the card context
        /// menu can drop a target or reset its counters without restarting the whole run.
        /// </summary>
        private sealed class WatchTarget(IPAddress address, WatchCard card)
        {
            public IPAddress Address { get; } = address;
            public WatchCard Card { get; } = card;
            public ConnectionSample Sample { get; set; } = new ConnectionSample(address.ToString());
        }

        private readonly List<WatchTarget> _targets = [];

        private async Task WatchAsync(IPAddress[] addresses, CancellationToken token)
        {
            _targets.Clear();
            foreach (var address in addresses)
            {
                var card = new WatchCard(address.ToString(), L("Str_Watch_Waiting"));
                _cards.Add(card);
                _rows.Add(new ResultRow { Target = address.ToString(), Result = L("Str_Watch_Waiting") });
                _targets.Add(new WatchTarget(address, card));
            }
            while (_targets.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var clock = Stopwatch.StartNew();
                // Snapshot the list: a context-menu removal during the await would otherwise
                // leave the reply array and the live list disagreeing about who is who.
                var polled = _targets.ToArray();
                var replies = await Task.WhenAll(polled.Select(t => ConnectionChecks.PingAsync(t.Address)));
                token.ThrowIfCancellationRequested();
                var now = DateTimeOffset.Now;
                for (int i = 0; i < polled.Length; i++)
                {
                    var target = polled[i];
                    if (!_targets.Contains(target)) continue;
                    var sample = target.Sample;
                    bool changed = sample.Record(replies[i], now);
                    bool up = replies[i].HasValue;
                    string state = L(up ? "Str_Watch_Reply" : "Str_Watch_NoReply");
                    target.Card.Update(sample, state, up);
                    // The table is hidden in Keep Alive, but Copy still reads it, so it stays current.
                    int row = _cards.IndexOf(target.Card);
                    if (row >= 0 && row < _rows.Count)
                        _rows[row] = new ResultRow
                        {
                            Target = sample.Address, Result = state, Sent = sample.Sent.ToString(),
                            Loss = sample.Loss.ToString("0.0") + "%",
                            Latency = (sample.Latest?.ToString() ?? "?") + " / " + (sample.Received == 0 ? "?" : sample.Average.ToString("0.0")),
                            Changed = sample.Changed?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty
                        };
                    if (changed) LogEvent(now, sample.Address, state, up);
                }
                if (_targets.Count == 0) break;
                await Task.Delay(Math.Max(1, 2000 - (int)clock.ElapsedMilliseconds), token);
            }
        }

        private WatchTarget? TargetFor(object sender) =>
            (sender as FrameworkElement)?.DataContext is WatchCard card
                ? _targets.FirstOrDefault(t => t.Card == card) : null;

        private void CardCopy_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WatchCard card) return;
            try { Clipboard.SetText(card.Address); }
            catch (System.Runtime.InteropServices.COMException) { Status.Text = L("Str_Diag_Error"); }
        }

        /// <summary>Starts this target's counters over without disturbing the others.</summary>
        private void CardReset_Click(object sender, RoutedEventArgs e)
        {
            var target = TargetFor(sender);
            if (target == null) return;
            target.Sample = new ConnectionSample(target.Address.ToString());
            target.Card.Reset(L("Str_Watch_Waiting"));
        }

        private void CardRemove_Click(object sender, RoutedEventArgs e)
        {
            var target = TargetFor(sender);
            if (target == null) return;
            int row = _cards.IndexOf(target.Card);
            _targets.Remove(target);
            _cards.Remove(target.Card);
            if (row >= 0 && row < _rows.Count) _rows.RemoveAt(row);
            var remaining = _targets.Select(t => t.Address.ToString());
            Targets.Text = string.Join(", ", remaining);
        }

        private void CardDiagnose_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is WatchCard card)
                DiagnoseRequested?.Invoke(card.Address);
        }

        /// <summary>Raised when a card asks for diagnostics, so the shell can open that view.</summary>
        internal event Action<string>? DiagnoseRequested;

        private async Task DiagnoseAsync(IPAddress address, CancellationToken token)
        {
            void Add(string check, string result) { token.ThrowIfCancellationRequested(); _rows.Add(new ResultRow { Target = check, Result = result }); }
            var route = ConnectionChecks.Route(address);
            Add(L("Str_Diag_Route"), route.HasValue
                ? string.Format(L("Str_Diag_RouteValue"), route.Value.Interface, string.IsNullOrEmpty(route.Value.NextHop) ? L("Str_Diag_OnLink") : route.Value.NextHop)
                : L("Str_Diag_Unavailable"));
            try
            {
                var entry = await ConnectionChecks.BoundedAsync(Dns.GetHostEntryAsync(address), token);
                Add(L("Str_Diag_Dns"), entry.HostName);
                var forward = await ConnectionChecks.BoundedAsync(Dns.GetHostAddressesAsync(entry.HostName), token);
                Add("DNS", L(forward.Contains(address) ? "Str_Diag_Match" : "Str_Diag_Mismatch"));
            }
            catch (Exception ex) when (ex is SocketException || ex is TimeoutException)
            { Add(L("Str_Diag_Dns"), L("Str_Diag_Unavailable")); }
            long? ping = await ConnectionChecks.PingAsync(address);
            Add("ICMP", ping.HasValue ? ping + " ms" : L("Str_Watch_NoReply"));
            foreach (int port in _ports)
            {
                token.ThrowIfCancellationRequested();
                bool connected = await ConnectionChecks.TcpAsync(address, port, token);
                Add("TCP " + port, L(connected ? "Str_Diag_Open" : "Str_Diag_Failed"));
            }
            Status.Text = L("Str_Diag_Time") + ": " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
        }

        /// <summary>
        /// Appends one state change. Colors come from the terminal palette so red and green
        /// follow the active theme; everything else stays on the theme's own foreground.
        /// </summary>
        private void LogEvent(DateTimeOffset time, string address, string state, bool up)
        {
            _events.Enqueue(time.ToString("yyyy-MM-dd HH:mm:ss zzz") + "  " + string.Format(L("Str_Watch_Event"), address, state));
            while (_events.Count > 200) _events.Dequeue();
            var palette = TerminalPalette.For(TerminalSkin.Default);
            var line = new Paragraph { Margin = new Thickness(0) };
            line.Inlines.Add(new Run(time.ToString("HH:mm:ss") + "  ") { Foreground = new SolidColorBrush(palette.Ansi[8]) });
            line.Inlines.Add(new Run(address.PadRight(16) + " ") { Foreground = new SolidColorBrush(palette.Foreground) });
            line.Inlines.Add(new Run(state) { Foreground = new SolidColorBrush(palette.Ansi[up ? 2 : 1]) });
            Events.Document.Blocks.Add(line);
            while (Events.Document.Blocks.Count > 200) Events.Document.Blocks.Remove(Events.Document.Blocks.FirstBlock);
            Events.ScrollToEnd();
        }

        /// <summary>Repaints the cards after a theme swap. Past log lines keep the colors they were written with.</summary>
        private void OnThemeChanged()
        {
            foreach (var card in _cards) card.RefreshTheme();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _events.Clear();
            Events.Document.Blocks.Clear();
        }

        private void Stop_Click(object sender, RoutedEventArgs e) { _run?.Cancel(); StopButton.IsEnabled = false; }
        public void Dispose() { _closed = true; ThemeManager.ThemeChanged -= OnThemeChanged; _run?.Cancel(); }
        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            string headings = string.Join("\t", Results.Columns.Select(c => (c.Header as TextBlock)?.Text));
            string rows = string.Join(Environment.NewLine, _rows.Select(r => _diagnostics ? r.Target + "\t" + r.Result :
                string.Join("\t", r.Target, r.Result, r.Sent, r.Loss, r.Latency, r.Changed)));
            try { Clipboard.SetText(Heading.Text + Environment.NewLine + Status.Text + Environment.NewLine + headings + Environment.NewLine + rows + Environment.NewLine + string.Join(Environment.NewLine, _events)); }
            catch (System.Runtime.InteropServices.COMException) { Status.Text = L("Str_Diag_Error"); }
        }
        private sealed class ResultRow
        {
            public string Target { get; set; } = string.Empty;
            public string Result { get; set; } = string.Empty;
            public string Sent { get; set; } = string.Empty;
            public string Loss { get; set; } = string.Empty;
            public string Latency { get; set; } = string.Empty;
            public string Changed { get; set; } = string.Empty;
        }
    }
}
