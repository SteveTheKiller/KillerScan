using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
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
                    !_terminalControl.HasRunningCommand && !_terminalExited));
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

        private void RunTerminalScan(string target)
        {
            if (_terminalControl?.HasShell != true || _terminalControl.HasRunningCommand || _terminalExited) return;
            // Quote both the PowerShell literals and the native argument string separately.
            static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
            string arguments = "/scan " + QuoteArgument(target.Trim()) + " /progress";
            string command = "Start-Process -FilePath " + Literal(Assembly.GetExecutingAssembly().Location) +
                " -ArgumentList " + Literal(arguments) + " -NoNewWindow -Wait";
            _terminalControl.Send("\u0015" + command + "\r");
            _terminalControl.Focus();
        }
    }
}
