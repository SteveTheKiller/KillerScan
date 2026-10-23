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
        private ScanOptions _terminalScanOptions = new();
        private Button? _terminalScanOptionsButton;
        private Button? _terminalInterfaceButton;
        private string _terminalAdapterId = "", _terminalSourceAddress = "";
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
            _terminalScanOptionsButton = new Button
            {
                Style = (Style)FindResource("OutlineButton"), Content = "\uE713",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 2, 6, 2)
            };
            _terminalScanOptionsButton.SetResourceReference(ToolTipProperty, "Str_Options_Title");
            _terminalScanOptionsButton.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "Str_Options_Title");
            Controls.ScanOptionsMenu.Attach(_terminalScanOptionsButton, () => _terminalScanOptions,
                value => { if (!_terminalScanRunning) _terminalScanOptions = value; });
            bar.Children.Add(_terminalScanOptionsButton);
            _terminalInterfaceButton = new Button
            {
                Style = (Style)FindResource("OutlineButton"), Content = "\uE774",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 2, 6, 2)
            };
            _terminalInterfaceButton.SetResourceReference(ToolTipProperty, "Str_Interface_Title");
            _terminalInterfaceButton.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "Str_Interface_Title");
            Controls.NetworkInterfaceMenu.Attach(_terminalInterfaceButton, () => _terminalAdapterId, () => _terminalSourceAddress,
                (id, address) =>
                {
                    if (_terminalScanRunning) return;
                    _terminalAdapterId = id; _terminalSourceAddress = address;
                    var net = LocalNetwork.Enumerate().FirstOrDefault(n => n.AdapterId == id && n.LocalIp == address);
                    if (net != null) target.Text = net.Subnet;
                });
            bar.Children.Add(_terminalInterfaceButton);
            _viewToolbarTargets["terminal"] = target;
            RegisterViewToolbar("terminal", bar);
        }

        private async void RunTerminalScan(string target)
        {
            if (_terminalControl?.HasShell != true || _terminalControl.HasRunningCommand || _terminalExited || _terminalScanRunning) return;
            var terminal = _terminalControl;
            using var locationRun = Services.Locations.BeginRun();
            string locationId = Services.Locations.Current.Id;
            _terminalScanRunning = true;
            var options = _terminalScanOptions;
            string selectedId = _terminalAdapterId, selectedSource = _terminalSourceAddress;
            if (_terminalInterfaceButton != null)
            {
                _terminalInterfaceButton.IsEnabled = false;
                _terminalInterfaceButton.ContextMenu.IsOpen = false;
            }
            if (_terminalScanOptionsButton != null)
            {
                _terminalScanOptionsButton.IsEnabled = false;
                _terminalScanOptionsButton.ContextMenu.IsOpen = false;
            }
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
                using var process = new Process { StartInfo = new ProcessStartInfo
                {
                    FileName = Assembly.GetExecutingAssembly().Location,
                    Arguments = "/scan " + QuoteArgument(target.Trim()) + " /json /progress /terminal-progress /location " + QuoteArgument(locationId) +
                        (options.DisabledFeatures.Length == 0 ? "" : " /skip " + options.DisabledFeatures),
                    // Interface choices are values from the same inventory as the GUI picker.
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                } };
                if (selectedId.Length > 0) process.StartInfo.Arguments += " /interface " + QuoteArgument(selectedId) + " /source-ip " + QuoteArgument(selectedSource);
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
                    if (!Services.DevicePreferences.HasTrustedDevices)
                    {
                        Services.DevicePreferences.TrustAll(devices);
                        _terminalStatusKey = "Str_St_TrustedBaseline";
                        _terminalStatusArgument = devices.Count;
                    }
                    else
                    {
                        int unknown = devices.Count(device => !Services.DevicePreferences.IsTrusted(device));
                        _terminalStatusKey = unknown > 0 ? "Str_St_UnknownDevices" : "Str_St_ScanComplete";
                        _terminalStatusArgument = unknown > 0 ? unknown : devices.Count;
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
                if (_terminalScanOptionsButton != null) _terminalScanOptionsButton.IsEnabled = true;
                if (_terminalInterfaceButton != null) _terminalInterfaceButton.IsEnabled = true;
                if (_terminalScanError != null) { _terminalStatusKey = "Str_Err_Scan"; _terminalStatusArgument = _terminalScanError; }
                UpdateWorkspaceStatus();
                UpdateScanLight();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
