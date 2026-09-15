using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace KillerScan.Terminal
{
    internal sealed partial class TerminalControl
    {
        private static readonly string FontPreferencePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "terminal-font.json");

        private sealed class FontPreference
        {
            public string Family { get; set; } = string.Empty;
            public double Size { get; set; } = 11;
        }

        private void LoadFontPreference()
        {
            try
            {
                if (!File.Exists(FontPreferencePath)) return;
                var preference = JsonSerializer.Deserialize<FontPreference>(File.ReadAllText(FontPreferencePath));
                if (preference == null || preference.Size < 8 || preference.Size > 28 || double.IsNaN(preference.Size)) return;
                _fontFamily = preference.Family ?? string.Empty;
                _fontSize = preference.Size;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }

        private void ChooseFont()
        {
            var dialog = new Controls.TerminalFontDialog(_fontFamily, _fontSize) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            _fontFamily = dialog.SelectedFont;
            SetFontSize(dialog.SelectedSize);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FontPreferencePath)!);
                File.WriteAllText(FontPreferencePath, JsonSerializer.Serialize(new FontPreference
                { Family = _fontFamily, Size = _fontSize }));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Controls.ConfirmDialog.ShowNotice(ex.Message, Window.GetWindow(this));
            }
            Focus();
        }
    }
}
