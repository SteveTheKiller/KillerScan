using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

/// <summary>
/// No shadow anywhere on a button: not under the face, behind the caption or behind an icon. Any
/// DropShadowEffect, BlurEffect or TextStroke inside a Button, ToggleButton, RadioButton, CheckBox or
/// RepeatButton style or template fails here, in the kit's Controls.xaml and in every XAML file the
/// app owns (the kit's ButtonShadowChecks, widened to the app). Panel, card, flyout and dialog
/// shadows are not button styles and stay. Reads the XAML only.
/// </summary>
internal static class ButtonShadowTests
{
    private static readonly string[] ButtonTypes = { "Button", "ToggleButton", "RadioButton", "CheckBox", "RepeatButton" };

    internal static void Run()
    {
        var root = TestRepository.Find().FullName;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var files = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Substring(root.Length).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or "tests" or "Strings" or "Themes"))
            .ToList();
        if (!files.Any(path => path.EndsWith(Path.Combine("Controls", "Controls.xaml"), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Controls/Controls.xaml was not among the scanned files.");
        var shadowed = files.SelectMany(path => XDocument.Load(path).Descendants()
                .Where(e => e.Name.LocalName is "Style" or "ControlTemplate"
                    && ButtonTypes.Contains(((string?)e.Attribute("TargetType") ?? "").Split(':', '}').Where(s => s.Length > 0).LastOrDefault() ?? ""))
                .Where(style => style.Descendants().Any(d =>
                    d.Name.LocalName is "DropShadowEffect" or "BlurEffect"
                    || d.Attributes().Any(a => ((string)a).Contains("TextStroke"))))
                .Select(style => Path.GetFileName(path) + ": " + ((string?)style.Attribute(x + "Key") ?? "(implicit " + (string?)style.Attribute("TargetType") + ")")))
            .Distinct()
            .ToList();
        if (shadowed.Count != 0)
            throw new InvalidOperationException("Button styles that carry a shadow: " + string.Join(", ", shadowed) + ".");
        Console.WriteLine("PASS: no button style casts a shadow (" + files.Count + " XAML files).");
    }
}
