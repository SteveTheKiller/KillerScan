using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KillerFamily.Controls;
using KillerScan.Models;
using KillerScan.Services;

namespace KillerScan.Controls
{
    /// <summary>
    /// The device card: the KillerUI tile card (Controls/ScanCards.cs) on the device grid and topology. A header
    /// names what was right-clicked, or counts a selection; every action is a tile with its shortcut, and the
    /// lists (Access, Set Device Type, the separate window) are flyout tiles. The card is built as it opens, from
    /// the selection, so it is never stale. Every tile runs the handler its old menu row used.
    /// </summary>
    public partial class ScanWorkspace
    {
        // Set by AddWorkspaceDeviceMenus: a workspace tab offers to open a device in a separate window.
        private bool _externalDeviceActions;

        /// <summary>The device types Set Device Type offers: storage key, caption key and glyph.</summary>
        private static readonly (string Type, string Key, string Glyph)[] DeviceTypes =
        [
            ("Router", "Str_Dev_Router", ""), ("Switch/AP", "Str_Dev_SwitchAP", ""),
            ("Network", "Str_Dev_Network", ""), ("Windows", "Str_Dev_Windows", ""),
            ("Windows Server", "Str_Dev_WindowsServer", ""), ("Linux/SSH", "Str_Dev_LinuxSSH", ""),
            ("Server", "Str_Dev_Server", ""), ("Printer", "Str_Dev_Printer", ""),
            ("Smart TV", "Str_Dev_SmartTV", ""), ("Apple TV", "Str_Dev_AppleTV", ""),
            ("Media Streamer", "Str_Dev_MediaStreamer", ""), ("Apple Device", "Str_Dev_AppleDevice", ""),
            ("Mobile", "Str_Dev_Mobile", ""), ("IoT", "Str_Dev_IoT", ""),
            ("NAS", "Str_Dev_NAS", ""), ("Camera", "Str_Dev_Camera", ""),
            ("Hypervisor", "Str_Dev_Hypervisor", ""), ("Home Assistant", "Str_Dev_HomeAssistant", ""),
            ("DNS Server", "Str_Dev_DNSServer", ""), ("Web Device", "Str_Dev_WebDevice", ""),
            ("Other", "Str_Dev_Other", ""),
        ];

        private void ResultsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e) => PrepareDeviceContextMenu();

        /// <summary>Rebuilds the device card from the current selection.</summary>
        internal void PrepareDeviceContextMenu()
        {
            if (DeviceCard == null) return;
            var card = ScanCards.Prepare(DeviceCard);
            int count = ResultsGrid.SelectedItems.Count;
            var device = GetSelectedDevice();
            bool one = device != null;
            card.Items.Add(ScanCards.Header(card, DeviceCardTitle(device, count, out string meta), meta));

            CardTile Tile(string id, string glyph, string caption, string? keys, Action run, bool enabled, string? shown = null) =>
                new(glyph, caption, keys, run, false, enabled) { Id = id, Shown = shown };
            void Run(RoutedEventHandler handler) => handler(this, new RoutedEventArgs());

            var connect = new List<CardTile>
            {
                Tile("Browser", "", Loc("Str_Ctx_OpenBrowser"), "Enter", () => Run(OpenBrowser_Click), one, Loc("Str_Card_Browser")),
                Tile("Ping", "", Loc("Str_Ctx_Ping"), "Ctrl+P", () => Run(PingDevice_Click), one),
                Tile("Diagnose", "", Loc("Str_Sc_Diagnostics"), "F3", () => Run(Diagnose_Click), one, Loc("Str_Card_Diagnose")),
                Tile("Watch", "", Loc("Str_View_KeepAlive"), "F9", () => Run(Watch_Click), one),
                Tile("Rdp", "", Loc("Str_Ctx_Rdp"), "Ctrl+D", () => Run(RdpDevice_Click), one),
                Tile("Ssh", "", Loc("Str_Ctx_Ssh"), "Ctrl+S", () => Run(SshDevice_Click), one),
            };
            var reach = new List<CardTile>
            {
                Tile("SshAs", "", Loc("Str_Ctx_SshAs"), "Ctrl+Shift+S", () => Run(SshAsDevice_Click), one),
                ScanCards.LazyFlyout("", Loc("Str_Access_Menu"), Loc("Str_Access_None"),
                    host => { host.Items.Clear(); foreach (var row in AccessRows()) host.Items.Add(row); }, one, "Access"),
            };
            if (_externalDeviceActions)
                reach.Add(ScanCards.Flyout("", Loc("Str_Workspace_OpenExternal"), null, ExternalRows(), one) with { Id = "External", Mirror = true });
            var copy = new List<CardTile>
            {
                Tile("CopyIp", "", Loc("Str_Ctx_CopyIp"), "Ctrl+C", () => Run(CopyIp_Click), one),
                Tile("CopyMac", "", Loc("Str_Ctx_CopyMac"), "Ctrl+Shift+C", () => Run(CopyMac_Click), one) with { Mirror = true },
                Tile("CopyHost", "", Loc("Str_Ctx_CopyHost"), "Ctrl+Alt+C", () => Run(CopyHostname_Click), one),
            };
            var name = new List<CardTile>
            {
                Tile("Rescan", "", Loc(count > 1 ? "Str_Ctx_RescanMany" : "Str_Ctx_RescanOne"), "Ctrl+R",
                    () => Run(RescanSelected_Click), count > 0 && _rescanCts == null && _active.Cts == null),
                Tile("Rename", "", Loc("Str_Rename_Title"), "F2", () => Run(RenameDevice_Click), one, Loc("Str_Card_Rename")),
                Tile("ClearName", "", Loc("Str_Rename_Clear"), "Ctrl+Alt+N", () => Run(ClearDeviceName_Click), one),
            };
            var type = new List<CardTile>
            {
                Tile("Trust", "", Loc("Str_Trust_Device"), "Ctrl+Shift+B", () => Run(TrustDevice_Click), one)
                    with { Checked = device != null && _location.Devices.IsTrusted(device) },
                ScanCards.Flyout("", Loc("Str_Ctx_SetType"), null, TypeRows(), one) with { Id = "SetType", Mirror = true },
                Tile("ClearOverride", "", Loc("Str_Ctx_ClearOverride"), "Ctrl+Alt+T", () => Run(ClearOverride_Click), one) with { Mirror = true },
            };
            var why = new List<CardTile> { Tile("WhyType", "", Loc("Str_Rec_Menu"), "Ctrl+I", () => Run(WhyType_Click), one) };
            card.Items.Add(ScanCards.Editable(card, "Device", ["Browser", "Ping"], ["External"], connect, reach, copy, name, type, why));
        }

        /// <summary>The card's title: the device's shown name over its address, MAC and vendor,
        /// or "N devices" when several are selected (Rescan and Keep Alive take them all).</summary>
        private string DeviceCardTitle(NetworkDevice? device, int count, out string meta)
        {
            meta = "";
            if (count > 1) return string.Format(Loc("Str_Card_Devices"), count);
            if (device == null) return Loc("Str_View_Devices");
            string title = string.IsNullOrWhiteSpace(device.Hostname) ? device.IpAddress : device.Hostname;
            meta = string.Join("  ·  ", new[] { device.IpAddress, device.MacAddress, device.Vendor }
                .Where(part => !string.IsNullOrWhiteSpace(part) && part != title));
            return title;
        }

        private List<object> TypeRows() => DeviceTypes.Select(t =>
        {
            var row = new MenuItem { Header = Loc(t.Key), Tag = t.Type, Icon = MenuGlyph.Create(char.ConvertToUtf32(t.Glyph, 0)) };
            row.Click += SetType_Click;
            return (object)row;
        }).ToList();

        private List<object> ExternalRows() => new[]
        {
            ("PingExternal", "Str_Ctx_Ping", 0xE704), ("SshExternal", "Str_Ctx_Ssh", 0xE756), ("SshAsExternal", "Str_Ctx_SshAs", 0xE756),
        }.Select(action =>
        {
            var row = new MenuItem { Header = Loc(action.Item2), Icon = MenuGlyph.Create(action.Item3) };
            row.Click += (_, _) => RaiseDeviceAction(action.Item1, false);
            return (object)row;
        }).ToList();

        /// <summary>
        /// The Access rows for the selected device, from what the scan recorded. Building them sends nothing;
        /// clicking an endpoint opens it with the existing browser, SSH or Remote Desktop action, or copies the
        /// address when no app handles the protocol. Copy Address lists each endpoint's address.
        /// </summary>
        internal List<MenuItem> AccessRows()
        {
            var rows = new List<MenuItem>();
            var d = GetSelectedDevice();
            var endpoints = d == null ? [] : DeviceAccess.ForDevice(d);
            if (d == null || endpoints.Count == 0)
            {
                rows.Add(new MenuItem { Header = Loc("Str_Access_None"), IsEnabled = false, Icon = MenuGlyph.Create(0xE946) });
                return rows;
            }
            foreach (var endpoint in endpoints)
            {
                string? action = AccessAction(endpoint);
                var item = new MenuItem { Header = AccessWording.Item(endpoint), ToolTip = AccessWording.Tooltip(endpoint, action != null), Icon = MenuGlyph.Create(AccessGlyph(endpoint)) };
                item.Click += (_, _) => OpenAccess(d, endpoint);
                rows.Add(item);
            }
            var copy = new MenuItem { Header = Loc("Str_Access_CopyMenu"), Icon = MenuGlyph.Create(0xE8C8) };
            foreach (var endpoint in endpoints)
            {
                string? address = DeviceAccess.Address(d.IpAddress, endpoint);
                if (address == null) continue;
                var item = new MenuItem { Header = address, Icon = MenuGlyph.Create(0xE8C8) };
                item.Click += (_, _) => CopyDeviceText(address);
                copy.Items.Add(item);
            }
            rows.Add(copy);
            return rows;
        }
    }
}
