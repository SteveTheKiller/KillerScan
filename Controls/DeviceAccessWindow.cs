using System.Windows;
using System.Windows.Controls;
using KillerScan.Engine;
using KillerScan.Services;
using AccessText = KillerScan.Services.AccessText;

namespace KillerScan.Controls;

internal sealed class DeviceAccessWindow : Window
{
    internal DeviceAccessWindow(NetworkDevice device, Action<AccessEndpoint>? open, Action<string> copy)
    {
        Title = AccessText.L("Title"); Width = 900; Height = 620; MinWidth = 520; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "UiFont");
        var panel = new DockPanel { Margin = new Thickness(18) };
        var close = Button("Str_Sys_Close"); close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = device.IpAddress + "  " + device.Hostname, FontSize = 18 });
        header.Children.Add(new TextBlock { Text = AccessText.L("Note"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var rows = new StackPanel();
        var endpoints = DeviceAccess.ForDevice(device);
        if (endpoints.Count == 0) rows.Children.Add(new TextBlock { Text = AccessText.L("None"), TextWrapping = TextWrapping.Wrap });
        foreach (var endpoint in endpoints)
        {
            string address = DeviceAccess.Address(device.IpAddress, endpoint)!;
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var actions = new WrapPanel();
            var launch = Button("Str_Btn_Open"); launch.Tag = endpoint;
            launch.IsEnabled = open != null && endpoint.Protocol is "http" or "https" or "ssh" or "rdp";
            launch.Opacity = launch.IsEnabled ? 1 : 0.4;
            launch.Click += (_, _) => { if (launch.IsEnabled) open?.Invoke(endpoint); };
            var copyButton = Button("Str_Watch_CopyAddress"); copyButton.Tag = address;
            copyButton.Click += (_, _) => copy(address);
            actions.Children.Add(launch); actions.Children.Add(copyButton);
            var value = new TextBox { Text = address, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), FontSize = 15 };
            value.SetResourceReference(StyleProperty, "DarkTextBox"); row.Children.Add(value);
            row.Children.Add(new TextBlock { Text = AccessText.Describe(endpoint), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) });
            row.Children.Add(actions); rows.Children.Add(row);
        }
        panel.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Content = panel;
    }
    private static Button Button(string key)
    {
        var button = new Button { MinWidth = 90, Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(10, 5, 10, 5) };
        button.SetResourceReference(ContentControl.ContentProperty, key); button.SetResourceReference(StyleProperty, "OutlineButton");
        return button;
    }
}
