using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KillerScan.Shell
{
    // Layered keyboard-map overlay, ported from KillerNotes' Shell/KeyboardMap.cs so the two
    // apps behave the same way. KillerScan has no per-binding table like KillerNotes' KsTable
    // though - ShortcutRows carries one flat gesture string per row - so the layer and physical
    // key id for each row are parsed here from that gesture string instead of being authored
    // per-row. A gesture whose modifiers do not land on one of the four layers (Shift-only,
    // Ctrl+Alt, ...) simply has no home on the map; it still shows in the list view untouched.
    public partial class MainWindow
    {
        private enum KbLayer { Base, Ctrl, CtrlShift, Alt }

        private KbLayer _kbLayer = KbLayer.Base;
        private bool _kbHooked;
        private readonly Dictionary<string, (Border Cap, TextBlock Act, Rectangle Bar)> _kbKeys = [];
        private readonly Dictionary<KbLayer, Button> _kbLayerBtns = [];

        private static readonly (KbLayer Layer, string Caption)[] KbLayerTabs =
        [
            (KbLayer.Base, "BASE"), (KbLayer.Ctrl, "CTRL"), (KbLayer.CtrlShift, "CTRL+SHIFT"), (KbLayer.Alt, "ALT"),
        ];

        // Modifier keycaps that light up to show which layer is active - the same set
        // KillerNotes lights. Both Alt caps light for the Alt layer even though only the left
        // one is a real Alt on international layouts (AltGr reports as Ctrl+Alt on Windows).
        private static readonly Dictionary<KbLayer, string[]> KbLayerMods = new()
        {
            [KbLayer.Base] = [],
            [KbLayer.Ctrl] = ["Ctrl", "RCtrl"],
            [KbLayer.CtrlShift] = ["Ctrl", "RCtrl", "Shift", "RShift"],
            [KbLayer.Alt] = ["Alt", "RAlt"],
        };

        // The physical keyboard, moved here from Shortcuts.cs since nothing else in that file
        // needs it any more - the list view reads ShortcutRows directly.
        private static readonly (string Id, string Cap, double Width)[][] KeyboardRows =
        [
            [("Esc", "Esc", 1), ("", "", .8), ("F1", "F1", 1), ("F2", "F2", 1), ("F3", "F3", 1),
             ("F4", "F4", 1), ("", "", .6), ("F5", "F5", 1), ("F6", "F6", 1), ("F7", "F7", 1),
             ("F8", "F8", 1), ("", "", .6), ("F9", "F9", 1), ("F10", "F10", 1), ("F11", "F11", 1), ("F12", "F12", 1)],
            [("Grave", "`", 1), ("D1", "1", 1), ("D2", "2", 1), ("D3", "3", 1), ("D4", "4", 1),
             ("D5", "5", 1), ("D6", "6", 1), ("D7", "7", 1), ("D8", "8", 1), ("D9", "9", 1),
             ("D0", "0", 1), ("Minus", "-", 1), ("Equals", "=", 1), ("Back", "Back", 2)],
            [("Tab", "Tab", 1.5), ("Q", "Q", 1), ("W", "W", 1), ("E", "E", 1), ("R", "R", 1),
             ("T", "T", 1), ("Y", "Y", 1), ("U", "U", 1), ("I", "I", 1), ("O", "O", 1),
             ("P", "P", 1), ("LBr", "[", 1), ("RBr", "]", 1), ("BSl", "\\", 1.5)],
            [("Caps", "Caps", 1.8), ("A", "A", 1), ("S", "S", 1), ("D", "D", 1), ("F", "F", 1),
             ("G", "G", 1), ("H", "H", 1), ("J", "J", 1), ("K", "K", 1), ("L", "L", 1),
             ("Semi", ";", 1), ("Quote", "'", 1), ("Enter", "Enter", 2.2)],
            [("Shift", "Shift", 2.3), ("Z", "Z", 1), ("X", "X", 1), ("C", "C", 1), ("V", "V", 1),
             ("B", "B", 1), ("N", "N", 1), ("M", "M", 1), ("Comma", ",", 1), ("Period", ".", 1),
             ("Slash", "/", 1), ("RShift", "Shift", 2.7)],
            [("Ctrl", "Ctrl", 1.5), ("Win", "Win", 1.2), ("Alt", "Alt", 1.5), ("Space", "", 6.8),
             ("RAlt", "Alt", 1.5), ("Menu", "Menu", 1), ("RCtrl", "Ctrl", 1.5)]
        ];

        private const double KeyboardUnit = 42;

        // Bindings grouped by layer then key id, parsed once from ShortcutRows (Shortcuts.cs).
        //
        // Built on first use rather than in a field initializer. ShortcutRows is a static field in
        // the other half of this partial class, and the order initializers run across the files of
        // one partial class is undefined: this ran first and read ShortcutRows as null.
        private static Dictionary<KbLayer, Dictionary<string, List<(string Keys, string Desc, string Cat)>>>? _kbMap;

        private static Dictionary<KbLayer, Dictionary<string, List<(string Keys, string Desc, string Cat)>>> KbMap
            => _kbMap ??= BuildKbMap();

        private static Dictionary<KbLayer, Dictionary<string, List<(string Keys, string Desc, string Cat)>>> BuildKbMap()
        {
            var map = new Dictionary<KbLayer, Dictionary<string, List<(string, string, string)>>>
            {
                [KbLayer.Base] = [], [KbLayer.Ctrl] = [], [KbLayer.CtrlShift] = [], [KbLayer.Alt] = [],
            };
            foreach (var (keys, desc, cat) in ShortcutRows)
            {
                if (!TryParseGesture(keys, out var layer, out var id)) continue;
                if (!map[layer].TryGetValue(id, out var list)) map[layer][id] = list = [];
                list.Add((keys, desc, cat));
            }
            return map;
        }

        // Splits a gesture like "Ctrl + Shift + C" into the layer its modifiers select and the
        // physical key id KeyboardRows uses for the trailing token. False for a modifier
        // combination that is not one of the four layers (Shift alone, Ctrl+Alt together, ...).
        private static bool TryParseGesture(string gesture, out KbLayer layer, out string id)
        {
            layer = KbLayer.Base;
            id = "";
            string keyToken, modsPart;
            // "+" and "-" are themselves gesture keys, so they cannot be found via LastIndexOf('+')
            // the way every other trailing key can.
            if (gesture.EndsWith(" +", System.StringComparison.Ordinal)) { keyToken = "+"; modsPart = gesture[..^2]; }
            else if (gesture.EndsWith(" -", System.StringComparison.Ordinal)) { keyToken = "-"; modsPart = gesture[..^2]; }
            else
            {
                int lastPlus = gesture.LastIndexOf('+');
                keyToken = (lastPlus >= 0 ? gesture[(lastPlus + 1)..] : gesture).Trim();
                modsPart = lastPlus >= 0 ? gesture[..lastPlus] : "";
            }
            bool ctrl = false, shift = false, alt = false;
            // Trimmed by hand: StringSplitOptions.TrimEntries arrived in .NET 5 and this targets
            // net48, where the overload exists but that flag does not.
            foreach (var raw in modsPart.Split(['+'], System.StringSplitOptions.RemoveEmptyEntries))
            {
                switch (raw.Trim())
                {
                    case "Ctrl": ctrl = true; break;
                    case "Shift": shift = true; break;
                    case "Alt": alt = true; break;
                    default: return false;
                }
            }
            if (ctrl && shift && !alt) layer = KbLayer.CtrlShift;
            else if (ctrl && !shift && !alt) layer = KbLayer.Ctrl;
            else if (alt && !ctrl && !shift) layer = KbLayer.Alt;
            else if (!ctrl && !shift && !alt) layer = KbLayer.Base;
            else return false;   // Shift-only, Ctrl+Alt together, etc: no layer owns this gesture
            id = KeyIdFor(keyToken);
            return id.Length > 0;
        }

        private static string KeyIdFor(string key) => key switch
        {
            "+" => "Equals",
            "-" => "Minus",
            "Esc" => "Esc",
            "Enter" => "Enter",
            "Tab" => "Tab",
            "\\" => "BSl",
            _ when key.Length == 1 && char.IsDigit(key[0]) => "D" + key,
            _ when key.Length == 1 => key.ToUpperInvariant(),
            _ when key.StartsWith("F", System.StringComparison.Ordinal) => key,
            _ => "",
        };

        /// <summary>Builds the layer tabs and the board. Called once; SetKbLayer repaints it
        /// afterwards, same split KillerNotes uses.</summary>
        private void BuildKeyboardMap()
        {
            ShortcutMapRows.Children.Clear();
            _kbKeys.Clear();
            _kbLayerBtns.Clear();

            // Independent of Window_PreviewKeyDown/Up in Shortcuts.cs on purpose - this is an
            // extra subscriber, not a change to the existing key handling.
            if (!_kbHooked)
            {
                _kbHooked = true;
                PreviewKeyDown += (_, _) => KbSyncLayerFromModifiers();
                PreviewKeyUp += (_, _) => KbSyncLayerFromModifiers();
            }

            var tabRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            foreach (var (layer, caption) in KbLayerTabs)
            {
                var b = new Button
                {
                    Content = caption, FontFamily = new FontFamily("Consolas"), FontSize = 11,
                    Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0),
                };
                // The family's own outline button rather than WPF's default chrome, which would
                // put a Windows-styled button on a themed card and ignore 98SE entirely.
                b.SetResourceReference(StyleProperty, "OutlineButton");
                var l = layer;
                b.Click += (_, _2) => SetKbLayer(l);
                _kbLayerBtns[layer] = b;
                tabRow.Children.Add(b);
            }
            var hint = new TextBlock
            {
                Text = Loc("Str_KS_HoldHint"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            tabRow.Children.Add(hint);
            ShortcutMapRows.Children.Add(tabRow);

            foreach (var row in KeyboardRows)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                foreach (var (id, cap, width) in row)
                {
                    if (id.Length == 0)
                    {
                        panel.Children.Add(new Border { Width = KeyboardUnit * width });
                        continue;
                    }
                    panel.Children.Add(BuildKeyboardKey(id, cap, width));
                }
                ShortcutMapRows.Children.Add(panel);
            }
            ShortcutsTitle.Text = Loc("Str_Shortcuts_Title");
            SetKbLayer(KbLayer.Base);
        }

        /// <summary>A blank keycap; SetKbLayer fills in the caption, color and dim state for the
        /// active layer. Colored by category, same as before layers existed - the point of the
        /// category colors is that a key's color says what kind of thing it does before the
        /// caption is even read.</summary>
        private Border BuildKeyboardKey(string id, string cap, double width)
        {
            var capText = new TextBlock
            {
                Text = cap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 0)
            };
            capText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var actionText = new TextBlock
            {
                FontSize = 7.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2, 0, 2, 5),
                Visibility = Visibility.Collapsed,
            };
            var bar = new Rectangle
            {
                Height = 3,
                Margin = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed,
            };

            var grid = new Grid();
            grid.Children.Add(capText);
            grid.Children.Add(actionText);
            grid.Children.Add(bar);
            var key = new Border
            {
                Width = KeyboardUnit * width - 4,
                Height = 40,
                Margin = new Thickness(0, 0, 4, 0),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Child = grid
            };
            // KeyCapBrush, not SurfaceBrush directly: the cap tracks SurfaceBrush on every theme
            // that does not declare it, but 98SE paints its caps white so the map does not read
            // as a slab of button-face gray. Same key in KillerNotes and KillerShell.
            key.SetResourceReference(Border.BackgroundProperty, "KeyCapBrush");
            key.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");

            _kbKeys[id] = (key, actionText, bar);
            return key;
        }

        /// <summary>Repaints every key for the layer that is now active: bound keys get their
        /// category color, caption and tooltip; everything else dims. Also updates which layer
        /// tab and which modifier keycaps read as active.</summary>
        private void SetKbLayer(KbLayer layer)
        {
            _kbLayer = layer;
            var map = KbMap[layer];
            foreach (var kv in _kbKeys)
            {
                var (cap, act, bar) = kv.Value;
                if (map.TryGetValue(kv.Key, out var actions))
                {
                    string categoryBrush = "KsCat" + actions[0].Cat;
                    cap.SetResourceReference(Border.BorderBrushProperty, categoryBrush);
                    act.SetResourceReference(TextBlock.ForegroundProperty, categoryBrush);
                    act.Text = ShortcutDescription(actions[0].Keys, actions[0].Desc);
                    act.Visibility = Visibility.Visible;
                    bar.SetResourceReference(Shape.FillProperty, categoryBrush);
                    bar.Visibility = Visibility.Visible;
                    cap.ToolTip = string.Join(System.Environment.NewLine,
                        actions.ConvertAll(a => $"{a.Keys}  {ShortcutDescription(a.Keys, a.Desc)}"));
                }
                else
                {
                    cap.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                    act.Visibility = Visibility.Collapsed;
                    bar.Visibility = Visibility.Collapsed;
                    cap.ToolTip = null;
                }
            }
            // Modifier caps that define the layer glow accent, matching KillerNotes.
            string[] allMods = ["Ctrl", "RCtrl", "Shift", "RShift", "Alt", "RAlt"];
            foreach (var m in allMods)
                if (_kbKeys.TryGetValue(m, out var vis))
                    vis.Cap.SetResourceReference(Border.BorderBrushProperty,
                        System.Array.IndexOf(KbLayerMods[layer], m) >= 0 ? "PrimaryBrush" : "CardBorderBrush");
            foreach (var kv in _kbLayerBtns)
            {
                kv.Value.SetResourceReference(ForegroundProperty, kv.Key == layer ? "PrimaryBrush" : "MutedTextBrush");
                kv.Value.SetResourceReference(Control.BorderBrushProperty, kv.Key == layer ? "PrimaryBrush" : "CardBorderBrush");
            }
        }

        /// <summary>Maps the live modifier state to a layer while the map shows, so holding a
        /// real Ctrl/Shift/Alt previews that layer - called from the extra Preview handlers
        /// this file wires in BuildKeyboardMap.</summary>
        private void KbSyncLayerFromModifiers()
        {
            if (ShortcutMapHost.Visibility != Visibility.Visible) return;
            var m = Keyboard.Modifiers;
            // Ctrl first, so AltGr (which Windows reports as Ctrl+Alt) previews the Ctrl layer
            // rather than the Alt one - matching which layer its keystrokes can actually reach.
            var layer = m.HasFlag(ModifierKeys.Control) && m.HasFlag(ModifierKeys.Shift) ? KbLayer.CtrlShift
                      : m.HasFlag(ModifierKeys.Control) ? KbLayer.Ctrl
                      : m.HasFlag(ModifierKeys.Alt) ? KbLayer.Alt
                      : KbLayer.Base;
            if (layer != _kbLayer) SetKbLayer(layer);
        }
    }
}
