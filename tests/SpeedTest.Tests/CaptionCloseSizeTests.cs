using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

/// <summary>
/// Win98 sized a caption button from its caption: the caption minus 4 tall and minus 2 wide, so
/// the 20px 98SE dialog band takes an 18x16 X, centered. Every dialog and card caption close binds
/// the kit's band-derived DialogCloseWidth/Height (28x26 on every other theme), the dialogs sit
/// centered in their band, and the kit derives the size from DialogTitleBarHeight and drops the
/// 98SE caption glyph to 8px. Reads the source only.
/// </summary>
internal static class CaptionCloseSizeTests
{
    private static readonly string[] Dialogs =
    {
        "Controls/ConfirmDialog.xaml", "Controls/InputDialog.xaml", "Controls/FileDialog.xaml",
        "Controls/HistorySettingsDialog.xaml", "Controls/TerminalFontDialog.xaml",
    };

    internal static void Run()
    {
        var root = TestRepository.Find().FullName;
        foreach (var file in Dialogs.Append("Shell/MainWindow.xaml"))
        {
            var closes = XDocument.Load(Path.Combine(root, file)).Descendants()
                .Where(e => e.Name.LocalName == "Button"
                    && (string?)e.Attribute("Style") == "{StaticResource OverlayCloseButton}")
                .ToList();
            if (closes.Count == 0) throw new InvalidOperationException(file + ": no caption close found.");
            foreach (var close in closes)
            {
                if ((string?)close.Attribute("Width") != "{DynamicResource DialogCloseWidth}"
                    || (string?)close.Attribute("Height") != "{DynamicResource DialogCloseHeight}")
                    throw new InvalidOperationException(file + ": caption close is not sized by DialogCloseWidth/Height.");
                if (Dialogs.Contains(file) && (string?)close.Attribute("VerticalAlignment") != "Center")
                    throw new InvalidOperationException(file + ": caption close is not centered in its band.");
            }
        }

        var source = File.ReadAllText(Path.Combine(root, "Services", "ThemeManager.cs"));
        foreach (var line in new[]
        {
            "combined[\"CaptionGlyphSize\"] = flat ? 8.0 : 10.0;",
            "SetIfAbsent(\"DialogCloseWidth\", pickerCaption ? bar - 2 : 28.0);",
            "SetIfAbsent(\"DialogCloseHeight\", pickerCaption ? bar - 4 : 26.0);",
        })
            if (!source.Contains(line)) throw new InvalidOperationException("ThemeManager.cs lacks: " + line);

        var controls = XDocument.Load(Path.Combine(root, "Controls", "Controls.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var chrome = controls.Descendants().Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute(x + "Key") == "ChromeButton");
        var size = chrome.Elements().Single(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "FontSize");
        if ((string?)size.Attribute("Value") != "{DynamicResource CaptionGlyphSize}")
            throw new InvalidOperationException("ChromeButton FontSize is not CaptionGlyphSize.");

        Console.WriteLine("PASS: dialog and card caption closes take the band-derived size.");
    }
}
