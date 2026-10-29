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
        /// <summary>Most mDNS service names one device keeps; a host flooding unique names stops here.</summary>
        internal const int ServiceCount = 64;

        /// <summary>A device's mDNS service names, each bounded like a name and at most <see cref="ServiceCount"/>.</summary>
        internal static System.Collections.Generic.List<string> Services(System.Collections.Generic.IEnumerable<string> names)
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (string name in names)
            {
                if (list.Count == ServiceCount) break;
                string clean = Clean(name, NameLength);
                if (clean.Length > 0 && !list.Contains(clean)) list.Add(clean);
            }
            return list;
        }

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
