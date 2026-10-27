namespace KillerScan.Services
{
    /// <summary>
    /// The icons a Location can carry (New and Rename Location, the Locations tree, the
    /// title-bar Location button). Segoe MDL2 glyphs, like the rest of the UI. The key is what a
    /// Location stores, so a glyph can change without touching saved data; an empty or unknown
    /// key draws the pin.
    /// </summary>
    internal static class LocationIcons
    {
        internal const string Default = "pin";

        internal static readonly (string Key, string Glyph, string NameKey)[] All =
        [
            ("pin",    "", "Str_LocIcon_Pin"),
            ("home",   "", "Str_LocIcon_Home"),
            ("office", "", "Str_LocIcon_Office"),
            ("shop",   "", "Str_LocIcon_Shop"),
            ("school", "", "Str_LocIcon_School"),
            ("server", "", "Str_LocIcon_Server"),
            ("cloud",  "", "Str_LocIcon_Cloud"),
            ("wifi",   "", "Str_LocIcon_Wifi"),
        ];

        internal static bool IsKnown(string? key) => key != null && System.Array.Exists(All, icon => icon.Key == key);

        internal static string Glyph(string? key)
        {
            foreach (var icon in All) if (icon.Key == key) return icon.Glyph;
            return All[0].Glyph;
        }
    }
}
