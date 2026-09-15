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
        private void InitializeTerminalScanToolbar(TextBox scanTarget)
        {
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
            RegisterViewToolbar("terminal", bar);
        }

        private async void RunTerminalScan(string target)
        {
            if (_terminalControl?.HasShell != true || _terminalControl.HasRunningCommand || _terminalExited || _terminalScanRunning) return;
            var terminal = _terminalControl;
            _terminalScanRunning = true;
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
                    Arguments = "/scan " + QuoteArgument(target.Trim()) + " /json /progress",
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
                    terminal.WriteManaged(presentation.Progress(status));
                string json = await output;
                await Task.Run(() => process.WaitForExit());
                stop.Token.ThrowIfCancellationRequested();
                if (process.ExitCode == 0)
                    terminal.WriteManaged(presentation.Result(JsonSerializer.Deserialize<List<NetworkDevice>>(json) ?? []));
                else terminal.WriteManaged("\r\n");
            }
            catch (OperationCanceledException) { if (connected) terminal.WriteManaged("\r\n"); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (connected) terminal.WriteManaged("\r\n" + ex.Message + "\r\n");
            }
            finally
            {
                terminal.Disposed -= Cancel;
                try { if (connected) await terminal.EndShellManagedSessionAsync(); }
                catch (System.IO.IOException) { }
                catch (ObjectDisposedException) { }
                _terminalScanRunning = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
