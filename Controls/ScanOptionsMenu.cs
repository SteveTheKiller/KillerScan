using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerScan.Controls
{
    internal static class ScanOptionsMenu
    {
        internal static void Attach(Button button, Func<ScanOptions> read, Action<ScanOptions> write)
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            var detail = new TextBlock { MaxWidth = 340, TextWrapping = TextWrapping.Wrap };
            detail.SetResourceReference(TextBlock.TextProperty, "Str_Options_Detail");
            menu.Items.Add(new MenuItem { Header = detail, IsEnabled = false });
            menu.Items.Add(new Separator());
            foreach (string feature in ScanOptions.Features)
            {
                var item = new MenuItem { Tag = feature, IsCheckable = true, StaysOpenOnClick = true };
                item.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Options_" + feature);
                item.Click += (_, _) => write(read().WithFeature(feature, item.IsChecked));
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var reset = new MenuItem();
            reset.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Options_Defaults");
            reset.Click += (_, _) => write(new ScanOptions());
            menu.Items.Add(reset);
            button.ContextMenu = menu;
            menu.Opened += (_, _) =>
            {
                foreach (var item in menu.Items.OfType<MenuItem>())
                    if (item.Tag is string feature) item.IsChecked = read().IsEnabled(feature);
            };
            button.Click += (_, _) => menu.IsOpen = true;
        }
    }
}
