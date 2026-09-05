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
            LocaleManager.LocaleChanged += OnLocaleChanged;
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

        /// <summary>
        /// Enter in the target box starts the run, the way it does in the subnet box. Ignored
        /// while a run is going, since the box is disabled then and Stop is the only action.
        /// </summary>
        private void Targets_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Return || !StartButton.IsEnabled) return;
            e.Handled = true;
            Start_Click(StartButton, new RoutedEventArgs());
        }

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

        /// <summary>
        /// Demo mode only: put these targets in the box and start watching them straight away, so
        /// the view has cards in it the moment it opens and after every re-rolled scan. A run
        /// already going is stopped first, because the addresses it is watching no longer exist.
        /// </summary>
        internal void RestartWith(string targets)
        {
            if (_closed || string.IsNullOrWhiteSpace(targets)) return;
            _run?.Cancel();
            Targets.Text = targets;
            // The cancelled run clears _run in its own finally, which has not happened yet, so the
            // restart is queued behind it rather than being dropped by the guard in Start_Click.
            Dispatcher.BeginInvoke(new Action(() => Start_Click(StartButton, new RoutedEventArgs())),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private readonly Random _demoRng = new();

        /// <summary>
        /// A plausible reply for demo mode. The fabricated addresses answer to nothing, so a real
        /// ping would fill every card with timeouts. Latency wanders around a per-address baseline
        /// and drops a packet now and then, which is what makes the graph and the event log worth
        /// looking at in a screenshot.
        /// </summary>
        private long? DemoReply(IPAddress address)
        {
            int host = address.GetAddressBytes()[3];
            // One target in three is a device that is not answering, so a demo screenshot shows
            // both states: the healthy cards and the red one with its loss climbing.
            if (host % 3 == 2) return _demoRng.Next(100) < 12 ? _demoRng.Next(180, 400) : null;
            int baseline = 1 + (host % 12);
            if (_demoRng.Next(100) < 4) return null;
            return baseline + _demoRng.Next(0, 5);
        }

        private async Task WatchAsync(IPAddress[] addresses, CancellationToken token)
        {
            _targets.Clear();
            foreach (var address in addresses)
            {
                var card = new WatchCard(address.ToString(), L("Str_Watch_Waiting"));
                _cards.Add(card);
                _targets.Add(new WatchTarget(address, card));
            }
            // The checks are their own sweep alongside the ping loop, not a step inside it.
            RunAllChecks();
            while (_targets.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var clock = Stopwatch.StartNew();
                // Snapshot the list: a context-menu removal during the await would otherwise
                // leave the reply array and the live list disagreeing about who is who.
                var polled = _targets.ToArray();
                var replies = DemoData.Enabled
                    ? await Task.WhenAll(polled.Select(t => Task.FromResult(DemoReply(t.Address))))
                    : await Task.WhenAll(polled.Select(t => ConnectionChecks.PingAsync(t.Address)));
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
            sender is FrameworkElement element && element.DataContext is WatchCard card
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
        // Every card shows its checks, so they run for the whole set when a run starts rather
        // than following the selection. They share one cancellation source, separate from the
        // watch loop's, so stopping or restarting the run drops the whole sweep at once.

        /// <summary>
        /// How many targets are inspected at once. Watching one or two devices is the ordinary
        /// case and runs unthrottled either way; the cap is here for the select-a-dozen case,
        /// where an unbounded sweep would put a traceroute and a port walk on every one of them
        /// at the same moment.
        /// </summary>
        private const int MaxConcurrentChecks = 4;

        private SemaphoreSlim? _checkSlots;

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
            RunChecks(card, _checks?.Token ?? CancellationToken.None);
        }

        /// <summary>
        /// Inspects every watched card. Restarting cancels whatever the last sweep still had in
        /// flight, so a stopped run leaves no traceroute walking hops in the background.
        /// </summary>
        private async void RunAllChecks()
        {
            _checks?.Cancel();
            _checks?.Dispose();
            var run = _checks = new CancellationTokenSource();
            _checkSlots = new SemaphoreSlim(MaxConcurrentChecks, MaxConcurrentChecks);
            try { await Task.WhenAll(_cards.ToArray().Select(c => RunChecksAsync(c, run.Token))); }
            catch (OperationCanceledException) { }
        }

        private async void RunChecks(WatchCard card, CancellationToken token)
        {
            try { await RunChecksAsync(card, token); }
            catch (OperationCanceledException) { }
        }

        private async Task RunChecksAsync(WatchCard card, CancellationToken token)
        {
            if (!IPAddress.TryParse(card.Address, out var address)) return;
            // Ask the shell for this target's scan record before probing, so the checks cover
            // its discovered ports. Guarded on the cache, so the shell calling back into
            // ShowDetails cannot bounce between the two.
            if (!_knownPorts.ContainsKey(card.Address)) DiagnoseRequested?.Invoke(card.Address);
            card.Checks.Clear();
            var slots = _checkSlots;
            if (slots != null) await slots.WaitAsync(token);
            try { await InspectAsync(card, address, token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (!_closed) card.Checks.Add(new CheckRow { Check = L("Str_Diag_Error") }); }
            finally { slots?.Release(); }
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

            // Demo mode answers its own checks. The fabricated addresses belong to nobody, so a
            // real sweep would spend half a minute per card on a thirty-hop trace to nowhere and
            // fill every row with failures, which is neither quick nor worth photographing.
            if (DemoData.Enabled)
            {
                FillDemoChecks(address, routeRow, reverseRow, forwardRow, icmpRow, ports, portRows);
                card.Checks.Add(new CheckRow { Check = L("Str_Diag_Time"), Result = DateTime.Now.ToString("h:mm:ss tt") });
                return;
            }

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

        /// <summary>
        /// Re-words the cards after a language change. The state line and the event log are
        /// re-labelled from what each card already knows, and the checks are run again because
        /// their names and results were resolved when they were written and cannot be translated
        /// after the fact. Counters and the latency graph carry on untouched.
        /// </summary>
        private void OnLocaleChanged()
        {
            if (_closed) return;
            string reply = L("Str_Watch_Reply"), noReply = L("Str_Watch_NoReply"), waiting = L("Str_Watch_Waiting");
            foreach (var card in _cards) card.Relabel(reply, noReply, waiting);
            if (Status.Text.Length > 0 && _run == null) Status.Text = L("Str_Watch_Stopped");
            if (_targets.Count > 0) RunAllChecks();
        }

        /// <summary>
        /// The fabricated answers for demo mode, consistent with the card's own ping behaviour: a
        /// target that is answering resolves, replies and has its ports open, and one that is not
        /// fails the same way a real dead host does. Ports come from the demo device's own record,
        /// so the checks agree with what the Devices table shows for that address.
        /// </summary>
        private void FillDemoChecks(IPAddress address, CheckRow route, CheckRow reverse, CheckRow forward,
                                    CheckRow icmp, int[] ports, CheckRow[] portRows)
        {
            var demo = DemoData.Current;
            string text = address.ToString();
            var device = demo?.Devices.FirstOrDefault(d => d.IpAddress == text);
            bool alive = DemoReply(address).HasValue;

            route.Result = string.Format(L("Str_Diag_RouteValue"), "Ethernet", L("Str_Diag_OnLink"));

            if (alive && !string.IsNullOrWhiteSpace(device?.Hostname))
            {
                reverse.Result = device!.Hostname;
                forward.Result = L("Str_Diag_Match");
            }
            else
            {
                reverse.Result = L("Str_Diag_Unavailable");
                forward.Result = L("Str_Diag_Unavailable");
            }

            icmp.Result = alive ? DemoReply(address) + " ms" : L("Str_Watch_NoReply");

            var open = device?.OpenPorts ?? [];
            for (int i = 0; i < portRows.Length; i++)
                portRows[i].Result = L(alive && open.Contains(ports[i]) ? "Str_Diag_Open" : "Str_Diag_Failed");
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            // Both, not just the ping loop. The checks sweep carries a thirty-hop traceroute at two
            // seconds a hop, so cancelling only the pings left the cards filling in for another
            // half minute per target after Stop said it had stopped.
            _run?.Cancel();
            _checks?.Cancel();
            StopButton.IsEnabled = false;
        }
        public void Dispose()
        {
            _closed = true;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            LocaleManager.LocaleChanged -= OnLocaleChanged;
            _run?.Cancel();
            _checks?.Cancel();
        }

        /// <summary>
        /// Walks the cards, so a paste into a ticket carries every target's counters, its
        /// checks, and its log, in the order they are on screen.
        /// </summary>
        /// <summary>
        /// The run as CSV: one row per card, then its checks and its log lines indented under it,
        /// so a spreadsheet keeps the shape the cards have on screen.
        /// </summary>
        internal string BuildCsv()
        {
            static string Cell(string? value) =>
                value is null ? "" : "\"" + value.Replace("\"", "\"\"") + "\"";

            var csv = new System.Text.StringBuilder();
            csv.AppendLine(string.Join(",", Cell(L("Str_Watch_Targets")), Cell(L("Str_Diag_Result")),
                Cell(L("Str_Watch_Latency")), Cell(L("Str_Watch_Loss")), Cell(L("Str_Watch_Sent"))));
            foreach (var card in _cards)
            {
                csv.AppendLine(string.Join(",", Cell(card.Address), Cell(card.State),
                    Cell(card.Latest + " / " + card.Average), Cell(card.Loss), Cell(card.Sent)));
                foreach (var check in card.Checks)
                    csv.AppendLine(string.Join(",", Cell(card.Address), Cell(check.Check), Cell(check.Result), "", ""));
                foreach (var entry in card.Events)
                    csv.AppendLine(string.Join(",", Cell(card.Address), Cell(entry.Time), Cell(entry.State), "", ""));
            }
            return csv.ToString();
        }

        /// <summary>The same run as a self-contained page, one section per card.</summary>
        internal string BuildHtml()
        {
            static string Esc(string? value) => (value ?? string.Empty)
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

            var html = new System.Text.StringBuilder();
            html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>")
                .Append(Esc(L("Str_View_KeepAlive"))).Append("</title>\n<style>\n")
                .Append("  body { font-family: Segoe UI, system-ui, sans-serif; background: #1c1c1c; color: #e0e0e0; margin: 24px; }\n")
                .Append("  h2 { font-family: Consolas, monospace; margin: 28px 0 6px; }\n")
                .Append("  table { border-collapse: collapse; margin-bottom: 8px; }\n")
                .Append("  td { padding: 3px 14px 3px 0; font-size: 13px; vertical-align: top; }\n")
                .Append("  td:first-child { color: #9a9a9a; }\n")
                .Append("</style>\n</head>\n<body>\n<h1>").Append(Esc(L("Str_View_KeepAlive"))).Append("</h1>\n");

            foreach (var card in _cards)
            {
                html.Append("<h2>").Append(Esc(card.Address)).Append(" &mdash; ").Append(Esc(card.State)).Append("</h2>\n<table>\n");
                html.Append("<tr><td>").Append(Esc(L("Str_Watch_Latency"))).Append("</td><td>")
                    .Append(Esc(card.Latest + " / " + card.Average)).Append("</td></tr>\n");
                html.Append("<tr><td>").Append(Esc(L("Str_Watch_Loss"))).Append("</td><td>").Append(Esc(card.Loss)).Append("</td></tr>\n");
                html.Append("<tr><td>").Append(Esc(L("Str_Watch_Sent"))).Append("</td><td>").Append(Esc(card.Sent)).Append("</td></tr>\n");
                foreach (var check in card.Checks)
                    html.Append("<tr><td>").Append(Esc(check.Check)).Append("</td><td>").Append(Esc(check.Result)).Append("</td></tr>\n");
                foreach (var entry in card.Events)
                    html.Append("<tr><td>").Append(Esc(entry.Time)).Append("</td><td>").Append(Esc(entry.State)).Append("</td></tr>\n");
                html.Append("</table>\n");
            }
            return html.Append("</body>\n</html>\n").ToString();
        }

        /// <summary>The cards themselves, for an image export.</summary>
        internal FrameworkElement CardsVisual => Cards;

        /// <summary>Targets watched, and how many of them are currently answering. For the
        /// shell's status tooltip, which reports the run while you are looking at another view.</summary>
        internal (int Total, int Replying) WatchState =>
            (_cards.Count, _cards.Count(card => card.IsReplying));

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
