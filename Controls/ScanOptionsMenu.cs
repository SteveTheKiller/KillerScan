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
        // The rows are built as the card opens, for the Adapter tile's flyout.
        private static List<object> AdapterRows(Button button, AdapterHooks hooks)
        {
            var rows = new List<object>();
            string chosen = hooks.Read();
            var automatic = new MenuItem { IsCheckable = true, IsChecked = chosen.Length == 0 };
            automatic.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Options_AdapterAuto");
            automatic.Click += (_, _) => { if (button.IsEnabled) hooks.Write(string.Empty); };
            rows.Add(automatic);
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
                item.Click += (_, _) => { if (button.IsEnabled) hooks.Write(name); };
                rows.Add(item);
            }
            if (chosen.Length > 0 && !available.Any(b => string.Equals(b.InterfaceName, chosen, StringComparison.OrdinalIgnoreCase)))
                rows.Add(new MenuItem { Header = chosen, IsCheckable = true, IsChecked = true, IsEnabled = false });
            return rows;
        }

        private static string Loc(string key) => Application.Current?.TryFindResource(key) as string ?? key;

        // Chip captions. Protocol names read the same in every language; the rest are keys.
        private static readonly (string Feature, string? Text, string? TextKey)[] FindChips =
        {
            ("neighbor-cache", null, "Str_Options_ChipCache"), ("arp", "ARP", null), ("icmp", "ICMP", null),
            ("tcp-liveness", "TCP", null), ("dns", "DNS", null), ("mdns", "mDNS", null), ("ssdp", "SSDP", null)
        };
        private static readonly (string Feature, string? Text, string? TextKey)[] PortChips =
        {
            ("ports", null, "Str_Options_ChipPorts"), ("http", "HTTP", null), ("tls", "TLS", null), ("ssh", "SSH", null)
        };
        private static readonly (string Feature, string? Text, string? TextKey)[] NameChips =
        {
            ("snmp", "SNMP", null), ("netbios", "NetBIOS", null)
        };

        /// <summary>
        /// The scan settings card: the checks as chips grouped by what they do, a strip of
        /// presets, and the adapter row. Chips stay open on click, since a person usually flips
        /// several; the full description of each check is its tooltip.
        /// </summary>
        private static ContextMenu Create(Button button, Func<ScanOptions> read, Action<ScanOptions> write, AdapterHooks? adapters)
        {
            var menu = ScanCards.Menu();
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.VerticalOffset = 4;
            menu.Items.Add(ScanCards.Header(menu, Loc("Str_Options_Title")));
            var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(4, 0, 4, 2) };
            detail.SetResourceReference(TextBlock.TextProperty, "Str_Options_Detail");
            detail.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            menu.Items.Add(KillerFamily.Controls.CardBuilder.Labeled("", detail));

            var chips = new List<ToggleButton>();
            // Off by default and outside Features: it widens deep-scan payloads rather than
            // disabling a check, so it is not part of the "all checks on" defaults.
            ToggleButton? unknownPorts = null;

            void Refresh()
            {
                var options = read();
                foreach (var chip in chips)
                {
                    if (chip.Tag is not string feature) continue;
                    chip.IsChecked = options.IsEnabled(feature);
                    chip.IsEnabled = button.IsEnabled && (options.Ports || feature is not ("http" or "tls" or "ssh"));
                }
                if (unknownPorts != null)
                {
                    unknownPorts.IsChecked = options.ProbeUnknownPorts;
                    unknownPorts.IsEnabled = button.IsEnabled && options.Ports && (options.Http || options.Tls);
                }
            }

            ToggleButton Chip(string? text, string? textKey, string tipKey, Action<bool> apply, string? feature)
            {
                var chip = new ToggleButton { Tag = feature, Margin = new Thickness(0, 2, 6, 2) };
                chip.SetResourceReference(FrameworkElement.StyleProperty, "CardToggle");
                if (text != null) chip.Content = text; else chip.SetResourceReference(ContentControl.ContentProperty, textKey!);
                chip.SetResourceReference(FrameworkElement.ToolTipProperty, tipKey);
                chip.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, tipKey);
                ToolTipService.SetShowOnDisabled(chip, true);
                chip.Click += (_, _) => { if (button.IsEnabled) apply(chip.IsChecked == true); Refresh(); };
                chips.Add(chip);
                return chip;
            }

            void Group(string labelKey, (string Feature, string? Text, string? TextKey)[] features, ToggleButton? extra = null)
            {
                var wrap = new WrapPanel();
                foreach (var (feature, text, textKey) in features)
                    wrap.Children.Add(Chip(text, textKey, "Str_Options_" + feature,
                        on => write(read().WithFeature(feature, on)), feature));
                if (extra != null) wrap.Children.Add(extra);
                menu.Items.Add(KillerFamily.Controls.CardBuilder.Labeled(Loc(labelKey), wrap));
            }

            Group("Str_Options_GroupFind", FindChips);
            Group("Str_Options_GroupPorts", PortChips);
            Group("Str_Options_GroupNames", NameChips);
            unknownPorts = Chip(null, "Str_Options_ChipUnknown", "Str_Options_ProbeUnknownPorts",
                on => write(read() with { ProbeUnknownPorts = on }), null);
            chips.Remove(unknownPorts);
            Group("Str_Options_GroupDeep", Array.Empty<(string, string?, string?)>(), unknownPorts);

            void Apply(ScanOptions options) { if (button.IsEnabled) write(options); }
            var none = new ScanOptions().Without(string.Join(",", ScanOptions.Features));
            // The presets and the adapter list as tiles under the chips.
            var tiles = new List<KillerFamily.Controls.CardTile>
            {
                new("", Loc("Str_Options_AllOnTip"), null, () => Apply(new ScanOptions { ProbeUnknownPorts = true }), false, button.IsEnabled)
                    { Id = "AllOn", Shown = Loc("Str_Options_AllOn") },
                new("", Loc("Str_Options_DiscoveryTip"), null,
                    () => Apply(none.WithFeature("neighbor-cache", true).WithFeature("arp", true).WithFeature("icmp", true)
                        .WithFeature("tcp-liveness", true)), false, button.IsEnabled)
                    { Id = "Discovery", Shown = Loc("Str_Options_Discovery") },
                new("", Loc("Str_Options_Defaults"), null, () => Apply(new ScanOptions()), false, button.IsEnabled)
                    { Id = "Defaults", Shown = Loc("Str_Card_Reset") },
            };
            if (adapters != null)
                tiles.Add(ScanCards.Flyout("", Loc("Str_Options_Adapter"), null, AdapterRows(button, adapters))
                    with { Id = "Adapter", Mirror = true });
            menu.Items.Add(ScanCards.Grid(menu, tiles));
            menu.Opened += (_, _) => Refresh();
            Refresh();
            return menu;
        }
    }
}
