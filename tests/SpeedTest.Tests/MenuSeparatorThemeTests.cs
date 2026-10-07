using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

internal static class MenuSeparatorThemeTests
{
    public static Task Run()
    {
        string root = FindRepoRoot();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        foreach (string path in Directory.GetFiles(Path.Combine(root, "Themes"), "*.xaml"))
        {
            if (Path.GetFileNameWithoutExtension(path) == "Defaults") continue;
            var resources = XDocument.Load(path).Root!.Elements().ToList();
            var divider = resources.SingleOrDefault(item => (string?)item.Attribute(x + "Key") == "MenuSeparatorBrush");
            if (Path.GetFileNameWithoutExtension(path) == "Delirium")
                Require((string?)divider?.Attribute("Color") == "#666666", "Delirium menu dividers must be neutral gray.");
            else
            {
                if (divider != null)
                {
                    Require(divider.Name == w + "SolidColorBrush", "Declared menu dividers must be solid brushes.");
                    var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                        (string?)divider.Attribute("Color") ?? throw new InvalidOperationException("Menu divider color is missing."));
                    Require(color.A == 255, "Declared menu dividers must be opaque.");
                }
                Require(resources.Any(item => (string?)item.Attribute(x + "Key") == "MenuBorderBrush"),
                    "The existing menu border fallback must resolve.");
            }
        }

        var styles = XDocument.Load(Path.Combine(root, "App.xaml")).Descendants(w + "Style")
            .Where(style => (string?)style.Attribute("TargetType") == "Separator").ToList();
        var menu = styles.Single(style => (string?)style.Attribute(x + "Key") == "{x:Static MenuItem.SeparatorStyleKey}");
        var generic = styles.Single(style => style.Attribute(x + "Key") == null);
        string? Background(XElement style) => (string?)style.Elements(w + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == "Background").Attribute("Value");
        Require(Background(menu) == "{DynamicResource MenuSeparatorBrush}", "Only menu separators use the dedicated token.");
        Require(Background(generic) == "{DynamicResource MenuBorderBrush}", "Generic separators must retain their original brush.");

        string source = File.ReadAllText(Path.Combine(root, "Services", "ThemeManager.cs"));
        const string fallback = "Complete(\"MenuSeparatorBrush\", newDict[\"MenuBorderBrush\"]);";
        Require(source.Contains(fallback), "Menu separators must retain the previous fallback.");
        Require(source.IndexOf(fallback, StringComparison.Ordinal) >
            source.IndexOf("target[key] = accentDict[key];", StringComparison.Ordinal),
            "The fallback must use the fully merged accent palette.");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerScan.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("KillerScan repository root not found.");
    }
}
