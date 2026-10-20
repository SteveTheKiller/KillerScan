using System.Text;

namespace KillerScan.Services
{
    internal static class TerminalText
    {
        internal static string Escape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var result = new StringBuilder(value!.Length);
            foreach (char character in value)
                if (character < 0x20 || character is >= (char)0x7f and <= (char)0x9f)
                    result.Append("\\x").Append(((int)character).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                else result.Append(character);
            return result.ToString();
        }
    }
}
