using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
            TargetLabel.Visibility = diagnostics ? Visibility.Collapsed : Visibility.Visible;
            Events.Visibility = EventsHeading.Visibility = diagnostics ? Visibility.Collapsed : Visibility.Visible;
            Heading.SetResourceReference(TextBlock.TextProperty, diagnostics ? "Str_Diag_Title" : "Str_View_KeepAlive");
            Hint.SetResourceReference(TextBlock.TextProperty, diagnostics ? "Str_Diag_Hint" : "Str_Watch_Hint");
            StartButton.SetResourceReference(ContentProperty, diagnostics ? "Str_Diag_Run" : "Str_Watch_Start");
            if (!diagnostics) Status.SetResourceReference(TextBlock.TextProperty, "Str_Watch_Reset");
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

        internal void IncludeTarget(string address)
        {
            if (_diagnostics || _run != null) return;
            var targets = Targets.Text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (!targets.Contains(address))
                Targets.Text = string.Join(", ", targets.Concat(new[] { address }));
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
            _rows.Clear(); _events.Clear(); Events.Clear(); Status.Text = string.Empty;
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

        private async Task WatchAsync(IPAddress[] addresses, CancellationToken token)
        {
            var samples = addresses.Select(a => new ConnectionSample(a.ToString())).ToArray();
            foreach (var sample in samples) _rows.Add(new ResultRow { Target = sample.Address, Result = L("Str_Watch_Waiting") });
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var clock = Stopwatch.StartNew();
                var replies = await Task.WhenAll(addresses.Select(ConnectionChecks.PingAsync));
                token.ThrowIfCancellationRequested();
                var now = DateTimeOffset.Now;
                for (int i = 0; i < samples.Length; i++)
                {
                    var sample = samples[i];
                    bool changed = sample.Record(replies[i], now);
                    string state = L(replies[i].HasValue ? "Str_Watch_Reply" : "Str_Watch_NoReply");
                    _rows[i] = new ResultRow
                    {
                        Target = sample.Address, Result = state, Sent = sample.Sent.ToString(),
                        Loss = sample.Loss.ToString("0.0") + "%",
                        Latency = (sample.Latest?.ToString() ?? "?") + " / " + (sample.Received == 0 ? "?" : sample.Average.ToString("0.0")),
                        Changed = sample.Changed?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty
                    };
                    if (changed)
                    {
                        _events.Enqueue(now.ToString("yyyy-MM-dd HH:mm:ss zzz") + "  " + string.Format(L("Str_Watch_Event"), sample.Address, state));
                        while (_events.Count > 200) _events.Dequeue();
                    }
                }
                Events.Text = string.Join(Environment.NewLine, _events.Reverse());
                await Task.Delay(Math.Max(1, 2000 - (int)clock.ElapsedMilliseconds), token);
            }
        }

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

        private void Stop_Click(object sender, RoutedEventArgs e) { _run?.Cancel(); StopButton.IsEnabled = false; }
        public void Dispose() { _closed = true; _run?.Cancel(); }
        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            string headings = string.Join("\t", Results.Columns.Select(c => (c.Header as TextBlock)?.Text));
            string rows = string.Join(Environment.NewLine, _rows.Select(r => _diagnostics ? r.Target + "\t" + r.Result :
                string.Join("\t", r.Target, r.Result, r.Sent, r.Loss, r.Latency, r.Changed)));
            try { Clipboard.SetText(Heading.Text + Environment.NewLine + Status.Text + Environment.NewLine + headings + Environment.NewLine + rows + Environment.NewLine + Events.Text); }
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
