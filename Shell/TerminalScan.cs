using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KillerScan.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private bool _terminalScanRunning;
        private int _terminalScanCount;
        private double _terminalScanProgress;
        private bool _terminalScanHasStatus;
        private string? _terminalScanError;
        private readonly TextBlock _terminalDeviceCount = new() { FontSize = 11, Visibility = Visibility.Collapsed };
        private void InitializeTerminalScanToolbar(TextBox scanTarget)
        {
            _terminalDeviceCount.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            // Same type as the scan view's count beside the status line.
            _terminalDeviceCount.FontSize = 16;
            _terminalDeviceCount.Margin = new Thickness(8, 0, 0, 0);
            _terminalDeviceCount.VerticalAlignment = VerticalAlignment.Center;
            _terminalDeviceCount.SetResourceReference(TextBlock.FontFamilyProperty, "BrandSansFont");
            System.Windows.Media.TextOptions.SetTextFormattingMode(_terminalDeviceCount, System.Windows.Media.TextFormattingMode.Ideal);
            DeviceCountFooter.Children.Add(_terminalDeviceCount);
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            var target = new TextBox
            {
                Style = (Style)FindResource("DarkTextBox"), Width = 220, FontSize = 12,
                Padding = new Thickness(5, 3, 9, 3), Margin = new Thickness(0, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            target.SetBinding(TextBox.TextProperty, new Binding(nameof(TextBox.Text))
            {
                Source = scanTarget, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            var scan = new RoutedCommand();
            CommandBindings.Add(new CommandBinding(scan, (_, _) => RunTerminalScan(target.Text), (_, e) =>
                e.CanExecute = _workspaceView == "terminal" && _terminalControl?.HasShell == true &&
                    !_terminalControl.HasRunningCommand && !_terminalExited && !_terminalScanRunning));
            var button = new Button
            {
                Style = (Style)FindResource("OutlineButton"), Command = scan, FontSize = 12,
                Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 2, 6, 2)
            };
            button.SetResourceReference(ContentControl.ContentProperty, "Str_Btn_Scan");
            target.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || !scan.CanExecute(null, button)) return;
                scan.Execute(null, button);
                e.Handled = true;
            };
            bar.Children.Add(target);
            bar.Children.Add(button);
            _viewToolbarTargets["terminal"] = target;
            RegisterViewToolbar("terminal", bar);
        }

        private async void RunTerminalScan(string target)
        {
            if (_terminalControl?.HasShell != true || _terminalControl.HasRunningCommand || _terminalExited || _terminalScanRunning) return;
            var terminal = _terminalControl;
            _terminalScanRunning = true;
            _terminalScanCount = 0;
            _terminalScanProgress = 0;
            _terminalScanHasStatus = true;
            _terminalScanError = null;
            _terminalStatusKey = "Str_St_Discovering";
            _terminalStatusArgument = target;
            UpdateWorkspaceStatus();
            UpdateScanLight();
            using var stop = new CancellationTokenSource();
            void Cancel() => stop.Cancel();
            terminal.Disposed += Cancel;
            bool connected = false;
            try
            {
                await terminal.BeginShellManagedSessionAsync(stop.Token);
                connected = true;
                terminal.ManagedInput += input => { if (input is "\u001b" or "\u0003") stop.Cancel(); };
                terminal.Focus();
                var presentation = new Terminal.TerminalScanPresentation(Loc, () => terminal.Buffer.Cols);
                // The scan runs in the scan workspace's Location, which records history and the
                // Known list; a Temporary scan passes none and saves nothing.
                var location = ShellLocation;
                string locationArgument = location.Info != null && !location.IsShared && Services.LocationService.LoadError == null
                    ? " /location " + QuoteArgument(location.Info.Id) : string.Empty;
                // The same checks the Scan settings button has turned off.
                string skipped = _scanWorkspace?.Options.DisabledFeatures ?? string.Empty;
                string skipArgument = skipped.Length > 0 ? " /skip " + skipped : string.Empty;
                using var process = new Process { StartInfo = new ProcessStartInfo
                {
                    FileName = Assembly.GetExecutingAssembly().Location,
                    Arguments = "/scan " + QuoteArgument(target.Trim()) + locationArgument + skipArgument + " /json /progress /terminal-progress",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                } };
                process.Start();
                using var cancellation = stop.Token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) { }
                });
                var output = process.StandardOutput.ReadToEndAsync();
                string? status;
                while ((status = await process.StandardError.ReadLineAsync()) != null)
                {
                    if (status.StartsWith("Progress: ") && int.TryParse(status[10..].TrimEnd('%'), out int percent))
                        _terminalScanProgress = percent;
                    else if (status.StartsWith("Devices: ") && int.TryParse(status[9..], out int count))
                        _terminalScanCount = count;
                    else if (status.StartsWith("Str_St_", StringComparison.Ordinal) && status.Contains('|'))
                    {
                        int separator = status.IndexOf('|');
                        _terminalStatusKey = status[..separator];
                        _terminalStatusArgument = status[(separator + 1)..];
                    }
                    else if (status.StartsWith("Error:", StringComparison.Ordinal)) _terminalScanError = status;
                    UpdateWorkspaceStatus();
                    terminal.WriteManaged(presentation.Progress(string.Format(Loc(_terminalStatusKey!), _terminalStatusArgument) +
                        "  " + _terminalScanProgress + "%"));
                }
                string json = await output;
                await Task.Run(() => process.WaitForExit());
                stop.Token.ThrowIfCancellationRequested();
                if (process.ExitCode == 0)
                {
                    var devices = JsonSerializer.Deserialize<List<NetworkDevice>>(json) ?? [];
                    _terminalScanCount = devices.Count;
                    // The command line already reported a new baseline or new devices, if any.
                    if (_terminalStatusKey is not ("Str_St_KnownBaseline" or "Str_St_UnknownDevices"))
                    {
                        _terminalStatusKey = "Str_St_ScanComplete";
                        _terminalStatusArgument = devices.Count;
                    }
                    if (locationArgument.Length > 0)
                    {
                        // Another process wrote history and Known devices; show what it saved.
                        location.Devices.Load();
                        location.History.Load();
                        RefreshHistoryList();
                    }
                    terminal.WriteManaged(presentation.Result(devices));
                }
                else { _terminalScanError ??= Loc("Str_Speed_TransferFailed"); terminal.WriteManaged("\r\n"); }
            }
            catch (OperationCanceledException)
            {
                _terminalStatusKey = "Str_St_ScanCanceled";
                _terminalStatusArgument = null;
                if (connected) terminal.WriteManaged("\r\n");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _terminalScanError = ex.Message;
                if (connected) terminal.WriteManaged("\r\n" + ex.Message + "\r\n");
            }
            finally
            {
                terminal.Disposed -= Cancel;
                try { if (connected) await terminal.EndShellManagedSessionAsync(); }
                catch (System.IO.IOException) { }
                catch (ObjectDisposedException) { }
                _terminalScanRunning = false;
                if (_terminalScanError != null) { _terminalStatusKey = "Str_Err_Scan"; _terminalStatusArgument = _terminalScanError; }
                UpdateWorkspaceStatus();
                UpdateScanLight();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
