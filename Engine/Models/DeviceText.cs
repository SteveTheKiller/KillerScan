namespace KillerScan.Engine
{
    /// <summary>
    /// Bounds the length of text a device sends about itself: host names, page titles, server
    /// headers, banners and descriptions, so one reply cannot grow a device record, saved history
    /// or a report without limit. The characters themselves are kept as the device sent them;
    /// each presentation (terminal, HTML, CSV) escapes them at its own boundary, and JSON keeps
    /// the original network text.
    /// </summary>
    internal static class DeviceText
    {
        internal const int NameLength = 255;
        internal const int TextLength = 1024;

        internal static string Clean(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value!.Length <= max) return value;
            // Never leave half of a surrogate pair at the cut.
            int cut = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
            return value.Substring(0, cut);
        }
    }
}
