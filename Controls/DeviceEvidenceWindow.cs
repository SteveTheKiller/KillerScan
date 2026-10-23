using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using KillerScan.Engine;
using KillerScan.Services;

namespace KillerScan.Controls;

public sealed class IdentificationConfidenceConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, CultureInfo culture) =>
        value is DeviceIdentification result ? " · " + IdentificationText.Confidence(result) : "";
    public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

internal sealed class DeviceEvidenceWindow : Window
{
    internal DeviceEvidenceWindow(NetworkDevice device)
    {
        Title = IdentificationText.L("Title"); Width = 820; Height = 600; MinWidth = 500; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "UiFont");
        var panel = new DockPanel { Margin = new Thickness(18) };
        var close = new Button { HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 90, Margin = new Thickness(0, 12, 0, 0) };
        close.SetResourceReference(ContentControl.ContentProperty, "Str_Sys_Close");
        close.SetResourceReference(StyleProperty, "OutlineButton");
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        var body = new TextBox { Name = "EvidenceBody", Text = IdentificationText.Describe(device), IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, BorderThickness = new Thickness(0), Padding = new Thickness(10) };
        body.SetResourceReference(StyleProperty, "DarkTextBox");
        panel.Children.Add(body); Content = panel;
    }
}
