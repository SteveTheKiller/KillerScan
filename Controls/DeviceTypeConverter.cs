using System;
using System.Globalization;
using System.Windows.Data;

namespace KillerScan.Controls
{
    // ═══════════════════════════════════════════════════════════
    //  DEVICE TYPE  -  display name for a stored value
    // ═══════════════════════════════════════════════════════════
    //
    // A device type is TWO things in this app and they were the same string, which is why the
    // grid's busiest column was English on every locale:
    //
    //   the VALUE  - "Router". Written into the override registry (DeviceOverrides), matched by
    //                the Type column's DataTriggers to pick a color, used as a CSS class slug in
    //                the exported HTML report, and accepted by the CLI's /type filter. It is an
    //                identifier and it MUST NOT change with the interface language, or a scan
    //                saved in German would filter and color differently from one saved in English.
    //   the LABEL  - what a person reads in the Type column and in the "Set device type" menu.
    //
    // This converter is the seam between them, and it only ever runs on the way OUT to the screen.
    // Nothing that persists, matches or filters goes through it.
    //
    // Unknown values pass straight through. A type this build has no key for - a newer
    // classification, or one restored from an override written by a later version - shows as
    // itself rather than as a blank cell or a raw Str_ key.
    public sealed class DeviceTypeConverter : IValueConverter
    {
        /// <summary>Turns a stored type into its resource key: "Switch/AP" becomes
        /// Str_Dev_SwitchAP. Only letters and digits survive, so the slash, the space and any
        /// punctuation a future type brings cannot produce an invalid key.</summary>
        public static string KeyFor(string type)
        {
            var sb = new System.Text.StringBuilder("Str_Dev_", type.Length + 8);
            foreach (char c in type)
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        public static string Display(string? type)
        {
            if (string.IsNullOrWhiteSpace(type)) return string.Empty;
            var app = System.Windows.Application.Current;
            return app?.TryFindResource(KeyFor(type!)) as string ?? type!;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => Display(value as string);

        // One way only. The grid never writes a type back through the binding - the override menu
        // sets it from the MenuItem's Tag, which is the English value by design.
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>The Vendor column's display side. Manufacturer names are not translated - they are
    /// company names out of the IEEE registry - but ONE value in that column is ours: the
    /// "(Randomized)" placeholder used when a MAC is locally administered and names no
    /// manufacturer at all. That one is a sentence about the address, so it belongs in the
    /// reader's language.
    ///
    /// Same split as device types: the stored value stays English, because it is written to CSV,
    /// used by the HTML report and matched by the CLI's /vendor-filter.</summary>
    public sealed class VendorConverter : IValueConverter
    {
        public static string Display(string? vendor)
        {
            if (string.IsNullOrEmpty(vendor)) return string.Empty;
            if (vendor != Services.NetworkScanner.VendorRandomized) return vendor!;
            return System.Windows.Application.Current?.TryFindResource("Str_Vendor_Randomized") as string ?? vendor!;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => Display(value as string);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
