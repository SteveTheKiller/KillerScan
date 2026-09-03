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
        /// <summary>Ports checked when the selected card is not a device we scanned.</summary>
        private static readonly int[] CommonPorts = [22, 80, 443, 445, 3389];

        private CancellationTokenSource? _run;
        private CancellationTokenSource? _checks;
        private readonly ObservableCollection<ResultRow> _rows = [];
        private readonly ObservableCollection<ResultRow> _detail = [];
        private readonly ObservableCollection<WatchCard> _cards = [];
        private readonly Dictionary<string, int[]> _knownPorts = [];
        private readonly Queue<string> _events = new();
        private bool _closed;
        private string L(string key) => TryFindResource(key) as string ?? key;

        internal NetworkToolsWindow(string targets, double scale)
        {
            InitializeComponent();
            Targets.Text = targets;
            TargetLabel.Visibility = Visibility.Collapsed;
            Cards.ItemsSource = _cards;
            ThemeManager.ThemeChanged += OnThemeChanged;
            Heading.SetResourceReference(TextBlock.TextProperty, "Str_View_KeepAlive");
            Hint.SetResourceReference(TextBlock.TextProperty, "Str_Watch_Hint");
            StartButton.SetResourceReference(ContentProperty, "Str_Watch_Start");
            StartButton.SetResourceReference(ToolTipProperty, "Str_Watch_Reset");
            Heading.SetBinding(ToolTipProperty, new Binding("Text") { Source = Hint });
            if (Fonts.SystemFontFamilies.Any(f => f.Source == "ProFont IIx Nerd Font"))
                Events.FontFamily = new FontFamily("ProFont IIx Nerd Font");
            ApplyScale(scale);
            AddColumn("Str_Diag_Check", "Target", 110);
            AddColumn("Str_Diag_Result", "Result", 0);
            Results.ItemsSource = _detail;
        }

        internal void ApplyScale(double scale) => BodyHost.LayoutTransform = new ScaleTransform(scale, scale);

        /// <summary>
        /// Hands the target box and run controls to the shell so they can live on the window's
        /// one toolbar, the same way the scan controls do.
        /// </summary>
        internal FrameworkElement DetachToolbar()
        {
            ((Panel)ToolBar.Parent).Children.Remove(ToolBar);
            ToolBar.Margin = new Thickness(0);
            return ToolBar;
        }

        internal void MatchScanTable(DataGrid reference)
        {
            Results.ColumnHeaderStyle = reference.ColumnHeaderStyle;
            Results.RowStyle = reference.RowStyle;
            Results.AlternationCount = reference.AlternationCount;
            Results.CellStyle = reference.CellStyle;
            Results.ColumnHeaderHeight = reference.ColumnHeaderHeight;
            // Deliberately not RowHeight: a check result wraps, and a fixed height from the
            // scan table would clip the second line of a route or a long DNS name.
            Results.FontFamily = reference.FontFamily;
            Results.FontSize = reference.FontSize;
            Results.BorderThickness = reference.BorderThickness;
            Results.SetResourceReference(BorderBrushProperty, "PaneBorderBrush");
        }

        internal void IncludeTarget(string address)
        {
            if (_run != null) return;
            var targets = Targets.Text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (!targets.Contains(address))
                Targets.Text = string.Join(", ", targets.Concat([address]));
        }

        /// <summary>
        /// Remembers the ports a scan already found open on a device, so running checks from its
        /// card probes what is actually there instead of only the common five.
        /// </summary>
        internal void RememberPorts(string address, IEnumerable<int> ports) =>
            _knownPorts[address] = [.. ports.Where(p => p is > 0 and <= 65535).Distinct().Take(64)];

        private int[] PortsFor(string address) =>
            _knownPorts.TryGetValue(address, out var ports) && ports.Length > 0
                ? [.. ports.Concat(CommonPorts).Distinct().Take(64)] : CommonPorts;

        private void AddColumn(string key, string path, double width)
        {
            var heading = new TextBlock();
            heading.SetResourceReference(TextBlock.TextProperty, key);
            heading.SetResourceReference(ToolTipProperty, key);
            var column = new DataGridTextColumn { Header = heading, Binding = new Binding(path) };
            if (width > 0) column.Width = width;
            else
            {
                column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                column.MinWidth = 90;
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
            try { await WatchAsync(addresses, run.Token); }
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

        /// <summary>
        /// Raised when the details pane needs a target's scan record, so the shell can hand
        /// back the ports it already discovered before the checks run.
        /// </summary>
        internal event Action<string>? DiagnoseRequested;

        // ---- Details pane -------------------------------------------------------------
        // Checks run against whichever card is selected, cancelled and restarted whenever the
        // selection moves, and are entirely separate from the watch loop's own cancellation.

        private string? _detailAddress;

        private void Card_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _checks?.Cancel();
            _detail.Clear();
            _detailAddress = (Cards.SelectedItem as WatchCard)?.Address;
            bool has = _detailAddress != null;
            CheckButton.IsEnabled = has;
            DetailHeading.Text = _detailAddress ?? string.Empty;
            DetailChecked.Text = string.Empty;
            Results.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            DetailHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (!has) return;
            // Ask the shell for this target's scan record before probing, so the checks cover
            // its discovered ports. Guarded on the cache, so the shell calling back into
            // ShowDetails cannot bounce between the two.
            if (!_knownPorts.ContainsKey(_detailAddress!)) DiagnoseRequested?.Invoke(_detailAddress!);
            RunChecks_Click(this, new RoutedEventArgs());
        }

        /// <summary>Selects a target's card, adding it to the run if it is not already watched.</summary>
        internal void ShowDetails(string address, IEnumerable<int> ports)
        {
            RememberPorts(address, ports);
            var card = _cards.FirstOrDefault(c => c.Address == address);
            if (card == null) { IncludeTarget(address); return; }
            Cards.SelectedItem = card;
            Cards.ScrollIntoView(card);
        }

        private void CardDiagnose_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WatchCard card) return;
            Cards.SelectedItem = card;
            RunChecks_Click(sender, e);
        }

        private async void RunChecks_Click(object sender, RoutedEventArgs e)
        {
            if (_detailAddress == null || !IPAddress.TryParse(_detailAddress, out var address)) return;
            _checks?.Cancel();
            _detail.Clear();
            DetailChecked.Text = string.Empty;
            var run = _checks = new CancellationTokenSource();
            try { await InspectAsync(address, run.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (!_closed) _detail.Add(new ResultRow { Target = L("Str_Diag_Error") }); }
        }

        /// <summary>
        /// Every check for one target, laid out before any of them answer. The rows exist up
        /// front reading "Checking", so the table has the same shape the moment a card is
        /// picked and fills in rather than growing a line at a time. The groups run
        /// concurrently because none of them depends on another's answer, which puts a
        /// thirty-hop trace alongside the port sweep instead of after it.
        /// </summary>
        private async Task InspectAsync(IPAddress address, CancellationToken token)
        {
            string working = L("Str_Diag_Working");
            ResultRow Row(string check) { var r = new ResultRow { Target = check, Result = working }; _detail.Add(r); return r; }

            var routeRow = Row(L("Str_Diag_Route"));
            var reverseRow = Row(L("Str_Diag_Dns"));
            var forwardRow = Row("DNS");
            var icmpRow = Row("ICMP");
            int[] ports = PortsFor(address.ToString());
            var portRows = ports.Select(p => Row("TCP " + p)).ToArray();

            // The route comes from the local table, so it is already known.
            var route = ConnectionChecks.Route(address);
            routeRow.Result = route.HasValue
                ? string.Format(L("Str_Diag_RouteValue"), route.Value.Interface,
                    string.IsNullOrEmpty(route.Value.NextHop) ? L("Str_Diag_OnLink") : route.Value.NextHop)
                : L("Str_Diag_Unavailable");

            async Task DnsAsync()
            {
                try
                {
                    var entry = await ConnectionChecks.BoundedAsync(Dns.GetHostEntryAsync(address), token);
                    reverseRow.Result = entry.HostName;
                    var forward = await ConnectionChecks.BoundedAsync(Dns.GetHostAddressesAsync(entry.HostName), token);
                    forwardRow.Result = L(forward.Contains(address) ? "Str_Diag_Match" : "Str_Diag_Mismatch");
                }
                catch (Exception ex) when (ex is SocketException || ex is TimeoutException)
                {
                    reverseRow.Result = L("Str_Diag_Unavailable");
                    forwardRow.Result = L("Str_Diag_Unavailable");
                }
            }

            async Task IcmpAsync()
            {
                long? ping = await ConnectionChecks.PingAsync(address);
                icmpRow.Result = ping.HasValue ? ping + " ms" : L("Str_Watch_NoReply");
            }

            async Task PortsAsync()
            {
                // One at a time: a parallel sweep of every port looks like a port scan to the
                // device and to anything watching the segment, which is not what a single
                // selected card should provoke.
                for (int i = 0; i < ports.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    bool connected = await ConnectionChecks.TcpAsync(address, ports[i], token);
                    portRows[i].Result = L(connected ? "Str_Diag_Open" : "Str_Diag_Failed");
                }
            }

            async Task TraceAsync()
            {
                // TraceAsync reports from a pool thread, so hop rows go back through the
                // dispatcher before they touch the bound collection.
                await ConnectionChecks.TraceAsync(address, 30, hop => Dispatcher.Invoke(() =>
                    _detail.Add(new ResultRow
                    {
                        Target = string.Format(L("Str_Trace_Hop"), hop.Ttl),
                        Result = hop.Address == null ? L("Str_Trace_Timeout")
                            : hop.Address + (hop.Latency.HasValue ? "  " + hop.Latency + " ms" : string.Empty)
                    })), token);
            }

            await Task.WhenAll(DnsAsync(), IcmpAsync(), PortsAsync(), TraceAsync());
            token.ThrowIfCancellationRequested();
            // The timestamp belongs to the run, not to the checks, so it sits in the caption
            // beside the address and leaves the table as nothing but checks and results.
            DetailChecked.Text = L("Str_Diag_Time") + " " + DateTime.Now.ToString("h:mm:ss tt");
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
        public void Dispose()
        {
            _closed = true;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _run?.Cancel();
            _checks?.Cancel();
        }

        /// <summary>
        /// Copies the watch table, then the event log, then whatever the details pane is
        /// currently showing, so a paste into a ticket carries the whole picture.
        /// </summary>
        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine(L("Str_View_KeepAlive"));
            if (!string.IsNullOrEmpty(Status.Text)) text.AppendLine(Status.Text);
            text.AppendLine(string.Join("\t", L("Str_Col_Ip"), L("Str_Diag_Result"), L("Str_Watch_Sent"),
                L("Str_Watch_Loss"), L("Str_Watch_Latency"), L("Str_Watch_Changed")));
            foreach (var r in _rows)
                text.AppendLine(string.Join("\t", r.Target, r.Result, r.Sent, r.Loss, r.Latency, r.Changed));
            if (_events.Count > 0)
            {
                text.AppendLine();
                text.AppendLine(L("Str_Watch_Events"));
                foreach (string line in _events) text.AppendLine(line);
            }
            if (_detail.Count > 0)
            {
                text.AppendLine();
                text.AppendLine(DetailHeading.Text);
                foreach (var r in _detail) text.AppendLine(r.Target + "\t" + r.Result);
            }
            try { Clipboard.SetText(text.ToString()); }
            catch (System.Runtime.InteropServices.COMException) { Status.Text = L("Str_Diag_Error"); }
        }
        /// <summary>
        /// Notifies, because the details pane creates its rows reading "Checking" and fills
        /// each one in as its answer lands rather than replacing the row.
        /// </summary>
        private sealed class ResultRow : System.ComponentModel.INotifyPropertyChanged
        {
            private string _result = string.Empty;
            public string Target { get; set; } = string.Empty;
            public string Result
            {
                get => _result;
                set
                {
                    if (_result == value) return;
                    _result = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Result)));
                }
            }
            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            public string Sent { get; set; } = string.Empty;
            public string Loss { get; set; } = string.Empty;
            public string Latency { get; set; } = string.Empty;
            public string Changed { get; set; } = string.Empty;
        }
    }
}
