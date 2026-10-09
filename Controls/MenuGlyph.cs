using System.Windows;
using System.Windows.Controls;

namespace KillerScan.Controls
{
    internal static class MenuGlyph
    {
        internal static TextBlock Create(int codepoint)
        {
            var icon = new TextBlock { Text = char.ConvertFromUtf32(codepoint) };
            icon.SetResourceReference(FrameworkElement.StyleProperty, "MenuIcon");
            return icon;
        }
    }
}
