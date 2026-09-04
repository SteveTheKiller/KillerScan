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
        private readonly ObservableCollection<WatchCard> _cards = [];
        private readonly Dictionary<string, int[]> _knownPorts = [];
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
            ApplyScale(scale);
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

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_run != null) return;
            if (!ConnectionChecks.TryTargets(Targets.Text, out var addresses))
            { Status.Text = L("Str_Watch_Invalid"); return; }
            var run = _run = new CancellationTokenSource();
            StartButton.IsEnabled = Targets.IsEnabled = false;
            StopButton.IsEnabled = true;
            _cards.Clear(); Status.Text = string.Empty;
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
                    if (changed) target.Card.LogEvent(now, state, up);
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
            _targets.Remove(target);
            _cards.Remove(target.Card);
            var remaining = _targets.Select(t => t.Address.ToString());
            Targets.Text = string.Join(", ", remaining);
        }

        /// <summary>
        /// Raised when the details pane needs a target's scan record, so the shell can hand
        /// back the ports it already discovered before the checks run.
        /// </summary>
        internal event Action<string>? DiagnoseRequested;

        // ---- Card details -------------------------------------------------------------
        // Checks run against whichever card is expanded, cancelled and restarted whenever the
        // selection moves, and are entirely separate from the watch loop's own cancellation.

        private void Card_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _checks?.Cancel();
            if (Cards.SelectedItem is not WatchCard card) return;
            // Ask the shell for this target's scan record before probing, so the checks cover
            // its discovered ports. Guarded on the cache, so the shell calling back into
            // ShowDetails cannot bounce between the two.
            if (!_knownPorts.ContainsKey(card.Address)) DiagnoseRequested?.Invoke(card.Address);
            RunChecks(card);
        }

        /// <summary>Expands a target's card, adding it to the run if it is not already watched.</summary>
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
            RunChecks(card);
        }

        private async void RunChecks(WatchCard card)
        {
            if (!IPAddress.TryParse(card.Address, out var address)) return;
            _checks?.Cancel();
            card.Checks.Clear();
            var run = _checks = new CancellationTokenSource();
            try { await InspectAsync(card, address, run.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (!_closed) card.Checks.Add(new CheckRow { Check = L("Str_Diag_Error") }); }
        }

        /// <summary>
        /// Every check for one target, laid out before any of them answer. The rows exist up
        /// front reading "Checking", so the table has the same shape the moment a card is
        /// picked and fills in rather than growing a line at a time. The groups run
        /// concurrently because none of them depends on another's answer, which puts a
        /// thirty-hop trace alongside the port sweep instead of after it.
        /// </summary>
        private async Task InspectAsync(WatchCard card, IPAddress address, CancellationToken token)
        {
            string working = L("Str_Diag_Working");
            CheckRow Row(string check) { var r = new CheckRow { Check = check, Result = working }; card.Checks.Add(r); return r; }

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
                    card.Checks.Add(new CheckRow
                    {
                        Check = string.Format(L("Str_Trace_Hop"), hop.Ttl),
                        Result = hop.Address == null ? L("Str_Trace_Timeout")
                            : hop.Address + (hop.Latency.HasValue ? "  " + hop.Latency + " ms" : string.Empty)
                    })), token);
            }

            await Task.WhenAll(DnsAsync(), IcmpAsync(), PortsAsync(), TraceAsync());
            token.ThrowIfCancellationRequested();
            card.Checks.Add(new CheckRow
            { Check = L("Str_Diag_Time"), Result = DateTime.Now.ToString("h:mm:ss tt") });
        }

        /// <summary>Repaints the cards after a theme swap. Past log lines keep the colors they were written with.</summary>
        private void OnThemeChanged()
        {
            foreach (var card in _cards) card.RefreshTheme();
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
        /// Walks the cards, so a paste into a ticket carries every target's counters, its
        /// checks, and its log, in the order they are on screen.
        /// </summary>
        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine(L("Str_View_KeepAlive"));
            if (!string.IsNullOrEmpty(Status.Text)) text.AppendLine(Status.Text);
            foreach (var card in _cards)
            {
                text.AppendLine();
                text.AppendLine(string.Join("\t", card.Address, card.State,
                    L("Str_Watch_Latency") + " " + card.Latest + " / " + card.Average,
                    L("Str_Watch_Loss") + " " + card.Loss,
                    L("Str_Watch_Sent") + " " + card.Sent));
                foreach (var check in card.Checks) text.AppendLine("\t" + check.Check + "\t" + check.Result);
                foreach (var entry in card.Events) text.AppendLine("\t" + entry.Time + "\t" + entry.State);
            }
            try { Clipboard.SetText(text.ToString()); }
            catch (System.Runtime.InteropServices.COMException) { Status.Text = L("Str_Diag_Error"); }
        }
    }
}
