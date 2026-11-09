using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

/// <summary>
/// A style that sets FocusVisualStyle to null switches the family keyboard ring off for every control
/// that uses it (the rail, the folder tree, check rows, toolbar buttons and the danger close button)
/// once the style is applied again, as it is on a theme change. WPF only draws FocusVisualStyle for
/// keyboard focus, so leaving it to the family ring keeps clicks ring-free. Reads the XAML only.
/// </summary>
internal static class KeyboardFocusRingStyleTests
{
    internal static void Run()
    {
        var xaml = XDocument.Load(Path.Combine(TestRepository.Find().FullName, "Controls", "Controls.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var off = xaml.Descendants()
            .Where(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "FocusVisualStyle"
                && (string?)e.Attribute("Value") == "{x:Null}")
            .Select(e => (string?)e.Parent?.Attribute(x + "Key") ?? "(implicit)")
            .ToList();
        if (off.Count != 0)
            throw new InvalidOperationException("Styles in Controls.xaml switch the keyboard focus ring off: " + string.Join(", ", off) + ".");
        Console.WriteLine("PASS: no shared style switches the keyboard focus ring off (1 selected).");
    }
}
