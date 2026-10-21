using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerScan.Controls
{
    internal static class ScanOptionsMenu
    {
        internal static void Attach(Button button, Func<ScanOptions> read, Action<ScanOptions> write)
        {
            void Open()
            {
                if (!button.IsEnabled) return;
                // Match other toolbar menus: resolve the current language and palette on opening.
                button.ContextMenu = Create(button, read, write);
                button.ContextMenu.IsOpen = true;
            }
            button.ContextMenu = Create(button, read, write);
            button.Click += (_, _) => Open();
            button.ContextMenuOpening += (_, e) => { e.Handled = true; Open(); };
        }

        private static ContextMenu Create(Button button, Func<ScanOptions> read, Action<ScanOptions> write)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = button,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 4
            };
            var detail = new TextBlock { MaxWidth = 340, TextWrapping = TextWrapping.Wrap };
            detail.SetResourceReference(TextBlock.TextProperty, "Str_Options_Detail");
            menu.Items.Add(new MenuItem { Header = detail, IsEnabled = false });
            menu.Items.Add(new Separator());

            void Refresh()
            {
                var options = read();
                foreach (var item in menu.Items.OfType<MenuItem>())
                {
                    if (item.Tag is not string feature) continue;
                    item.IsChecked = options.IsEnabled(feature);
                    item.IsEnabled = options.Ports || feature is not ("http" or "tls" or "ssh");
                }
            }

            foreach (string feature in ScanOptions.Features)
            {
                var item = new MenuItem { Tag = feature, IsCheckable = true, StaysOpenOnClick = true };
                item.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Options_" + feature);
                item.Click += (_, _) =>
                {
                    if (button.IsEnabled) write(read().WithFeature(feature, item.IsChecked));
                    Refresh();
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var reset = new MenuItem { StaysOpenOnClick = true };
            reset.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Options_Defaults");
            reset.Click += (_, _) =>
            {
                if (button.IsEnabled) write(new ScanOptions());
                Refresh();
            };
            menu.Items.Add(reset);
            menu.Opened += (_, _) => Refresh();
            Refresh();
            return menu;
        }
    }
}
