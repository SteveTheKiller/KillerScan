using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerScan.Controls;
using KillerScan.Engine;

// A MAC address is always shown whole in the device table: the column is never narrower than
// a full address in the cell's own font.
internal static class DeviceTableTests
{
    internal static void Run()
    {
        using var workspace = new ScanWorkspace("192.0.2.0/24", false);
        var window = new Window
        {
            Content = workspace, Width = 1200, Height = 780, Left = -20000, Top = -20000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None
        };
        window.Show();
        try
        {
            var device = new NetworkDevice
            {
                IpAddress = "192.0.2.10", MacAddress = "DC:A6:32:8E:4F:B7", Hostname = "raspberrypi",
                Vendor = "Raspberry Pi Trading Ltd", DeviceType = "Workstation", OpenPorts = [22, 80, 443, 5900]
            };
            workspace.LoadSnapshot("192.0.2.0/24", [device]);
            Pump();
            var grid = (DataGrid)workspace.FindName("ResultsGrid");
            var column = (DataGridTextColumn)workspace.FindName("ColMac");
            grid.ScrollIntoView(device);
            Pump();
            var text = column.GetCellContent(device) as TextBlock
                ?? throw new InvalidOperationException("The MAC cell is rendered.");
            var full = new FormattedText(text.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(text).PixelsPerDip);
            Require(text.ActualWidth + 0.5 >= full.WidthIncludingTrailingWhitespace,
                $"The MAC cell shows the whole address: {text.ActualWidth:0.0} of {full.WidthIncludingTrailingWhitespace:0.0} px.");

            // A saved narrower width from an older version is widened back to fit.
            column.Width = new DataGridLength(60);
            Pump();
            Require(column.ActualWidth + 0.5 >= column.MinWidth && column.MinWidth > 110,
                "The MAC column cannot be dragged or restored narrower than a full address.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS: device table MAC addresses are never cut off.");
    }

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
