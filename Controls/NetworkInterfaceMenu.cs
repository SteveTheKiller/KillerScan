using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerScan.Controls
{
    internal static class NetworkInterfaceMenu
    {
        internal static void Attach(Button button, Func<string> id, Func<string> address, Action<string, string> select)
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            button.ContextMenu = menu;
            menu.Opened += (_, _) =>
            {
                menu.Items.Clear();
                var automatic = new MenuItem { IsCheckable = true, IsChecked = id().Length == 0 };
                automatic.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Interface_Auto");
                automatic.Click += (_, _) => select("", "");
                menu.Items.Add(automatic); menu.Items.Add(new Separator());
                bool found = id().Length == 0;
                // Demo UI must not reveal the surrounding network's names/addresses in captures.
                var networks = Services.DemoData.Enabled ? Array.Empty<LocalNet>() : LocalNetwork.Enumerate();
                foreach (var net in networks.Where(n => n.InterfaceLabel is "Ethernet" or "Wi-Fi"))
                {
                    bool selected = id() == net.AdapterId && address() == net.LocalIp;
                    found |= selected;
                    var item = new MenuItem
                    {
                        Header = net.InterfaceLabel + " · " + net.AdapterName + " · " + net.LocalIp,
                        IsCheckable = true, IsChecked = selected,
                        IsEnabled = !net.Wireless || net.ConnectionIdentity.Length > 0
                    };
                    if (!item.IsEnabled)
                    {
                        item.SetResourceReference(FrameworkElement.ToolTipProperty, "Str_Interface_WifiIdentity");
                        ToolTipService.SetShowOnDisabled(item, true);
                    }
                    item.Click += (_, _) => select(net.AdapterId, net.LocalIp);
                    menu.Items.Add(item);
                }
                if (!found)
                {
                    var missing = new MenuItem { IsEnabled = false };
                    missing.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Interface_Unavailable");
                    menu.Items.Add(missing);
                }
            };
            button.Click += (_, _) => menu.IsOpen = true;
        }
    }
}
