using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerScan.Controls
{
    public partial class TerminalFontDialog : Window
    {
        public string SelectedFont => FontBox.SelectedItem as string ?? "Consolas";
        public double SelectedSize => SizeBox.SelectedItem is int size ? size : 11;

        public TerminalFontDialog(string font, double size)
        {
            InitializeComponent();
            if (TryFindResource("InputDialogPlainTitleVisibility") is Visibility.Visible)
            {
                // Keep the dropdowns and preview on the theme's recessed content surface.
                Resources["PaneBrush"] = FindResource("ListPaneBrush");
                Resources["InputDialogTitleBrush"] = FindResource("TitleBarBrush");
            }
            FontBox.ItemsSource = Fonts.SystemFontFamilies.Where(IsMonospace)
                .Select(f => f.Source).OrderBy(f => f).ToArray();
            FontBox.SelectedItem = font;
            if (FontBox.SelectedIndex < 0) FontBox.SelectedIndex = 0;
            SizeBox.ItemsSource = Enumerable.Range(8, 21).ToArray();
            SizeBox.SelectedItem = (int)size;
            Loaded += (_, _) => Anim.FadeIn(RootBorder);
            UpdatePreview();
        }

        private static bool IsMonospace(FontFamily family)
        {
            try
            {
                var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (!face.TryGetGlyphTypeface(out var glyphs)) return false;
                double width = -1;
                foreach (char c in "MiWl0 .")
                {
                    if (!glyphs.CharacterToGlyphMap.TryGetValue(c, out ushort index)) return false;
                    double advance = glyphs.AdvanceWidths[index];
                    if (width >= 0 && Math.Abs(width - advance) > 0.001) return false;
                    width = advance;
                }
                return width > 0;
            }
            catch (ArgumentException) { return false; }
        }

        private void PreviewChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();
        private void Selector_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var selector = (ComboBox)sender;
            if (e.Delta != 0 && selector.Items.Count > 0)
            {
                int direction = e.Delta > 0 ? 1 : -1;
                if (selector == FontBox) direction = -direction;
                selector.SelectedIndex = Math.Max(0, Math.Min(selector.Items.Count - 1,
                    selector.SelectedIndex + direction));
            }
            e.Handled = true;
        }

        private void DialogSurface_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double radius = RootBorder.CornerRadius.TopLeft;
            DialogSurface.Clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
        }
        private void UpdatePreview()
        {
            if (Preview == null || SizeBox == null) return;
            Preview.FontFamily = new FontFamily(SelectedFont);
            Preview.FontSize = SelectedSize;
        }
        private void OK_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
    }
}
