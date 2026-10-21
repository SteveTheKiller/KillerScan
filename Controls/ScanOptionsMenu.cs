using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerScan.Controls
{
    internal static class ScanOptionsMenu
    {
        /// <summary>The tab's adapter choice: read it, write it, and list the adapters to offer.</summary>
        internal sealed record AdapterHooks(Func<string> Read, Action<string> Write, Func<IReadOnlyList<ScanBinding>> List);

        internal static void Attach(Button button, Func<ScanOptions> read, Action<ScanOptions> write, AdapterHooks? adapters = null)
        {
            void Open()
            {
                if (!button.IsEnabled) return;
                // Match other toolbar menus: resolve the current language and palette on opening.
                button.ContextMenu = Create(button, read, write, adapters);
                button.ContextMenu.IsOpen = true;
            }
            button.ContextMenu = Create(button, read, write, adapters);
            button.Click += (_, _) => Open();
            button.ContextMenuOpening += (_, e) => { e.Handled = true; Open(); };
        }

        // Automatic, then every connected adapter with its address. A remembered choice that is
        // not connected right now stays listed, checked and disabled, so it is not lost silently.
        private static MenuItem AdapterMenu(Button button, AdapterHooks hooks)
        {
            var parent = new MenuItem();
            parent.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Options_Adapter");
            void Fill()
            {
                parent.Items.Clear();
                string chosen = hooks.Read();
                var automatic = new MenuItem { IsCheckable = true, IsChecked = chosen.Length == 0 };
                automatic.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Options_AdapterAuto");
                automatic.Click += (_, _) => { if (button.IsEnabled) hooks.Write(string.Empty); Fill(); };
                parent.Items.Add(automatic);
                var available = hooks.List();
                foreach (var binding in available.GroupBy(b => b.InterfaceName).Select(g => g.First()))
                {
                    var item = new DetailMenuItem
                    {
                        Header = binding.InterfaceName,
                        InputGestureText = binding.LocalAddress + "/" + binding.PrefixLength,
                        IsCheckable = true,
                        IsChecked = string.Equals(binding.InterfaceName, chosen, StringComparison.OrdinalIgnoreCase)
                    };
                    string name = binding.InterfaceName;
                    item.Click += (_, _) => { if (button.IsEnabled) hooks.Write(name); Fill(); };
                    parent.Items.Add(item);
                }
                if (chosen.Length > 0 && !available.Any(b => string.Equals(b.InterfaceName, chosen, StringComparison.OrdinalIgnoreCase)))
                    parent.Items.Add(new MenuItem { Header = chosen, IsCheckable = true, IsChecked = true, IsEnabled = false });
            }
            parent.SubmenuOpened += (_, _) => Fill();
            Fill();
            return parent;
        }

        private static ContextMenu Create(Button button, Func<ScanOptions> read, Action<ScanOptions> write, AdapterHooks? adapters)
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

            // Off by default and outside Features: it widens deep-scan payloads rather than
            // disabling a check, so it is not part of the "all checks on" defaults.
            var unknownPorts = new MenuItem { IsCheckable = true, StaysOpenOnClick = true };

            void Refresh()
            {
                var options = read();
                foreach (var item in menu.Items.OfType<MenuItem>())
                {
                    if (item.Tag is not string feature) continue;
                    item.IsChecked = options.IsEnabled(feature);
                    item.IsEnabled = options.Ports || feature is not ("http" or "tls" or "ssh");
                }
                unknownPorts.IsChecked = options.ProbeUnknownPorts;
                unknownPorts.IsEnabled = options.Ports && (options.Http || options.Tls);
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
            unknownPorts.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Options_ProbeUnknownPorts");
            unknownPorts.Click += (_, _) =>
            {
                if (button.IsEnabled) write(read() with { ProbeUnknownPorts = unknownPorts.IsChecked });
                Refresh();
            };
            menu.Items.Add(unknownPorts);
            if (adapters != null) menu.Items.Add(AdapterMenu(button, adapters));
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
