using System;
using System.Windows;

namespace KillerScan.Services
{
    // Mirrors KillerPDF's LocaleManager. en-US.xaml is always the base layer so any locale that
    // omits a key falls back to English; the chosen locale's file is layered on top.
    internal enum Locale { EnUS, Es, ZhTW, ZhCN, Bn, TrTR, De, Fr, Ja, CsCZ, PlPL, HuHU, ItIT, KkKZ, RuRU }

    internal static class LocaleManager
    {
        private static Locale _current = Locale.EnUS;
        public static Locale Current => _current;

        /// <summary>Call once at startup (after ThemeManager.Initialize) to restore the saved locale.</summary>
        public static void Initialize()
        {
            var saved = App.GetSetting("Locale");
            _current = Enum.TryParse<Locale>(saved, out var l) ? l : Locale.EnUS;
            ApplyInternal(_current);
        }

        /// <summary>Switch locale, persist the choice, and hot-swap the string ResourceDictionary.</summary>
        public static void Apply(Locale locale)
        {
            _current = locale;
            App.SetSetting("Locale", locale.ToString());
            ApplyInternal(locale);
        }

        private static void ApplyInternal(Locale locale)
        {
            var merged = Application.Current.Resources.MergedDictionaries;

            // [0] theme. [1] en-US BASE - always present so any partial locale falls back to English
            // for keys it doesn't translate. [2] the chosen locale's overrides (absent for English).
            if (merged.Count > 1)
                merged[1] = new ResourceDictionary { Source = new Uri("pack://application:,,,/Strings/en-US.xaml") };

            Uri? overrideUri = locale switch
            {
                Locale.Es   => new Uri("pack://application:,,,/Strings/es.xaml"),
                Locale.Fr   => new Uri("pack://application:,,,/Strings/fr-FR.xaml"),
                Locale.ZhTW => new Uri("pack://application:,,,/Strings/zh-TW.xaml"),
                Locale.ZhCN => new Uri("pack://application:,,,/Strings/zh-CN.xaml"),
                Locale.Bn   => new Uri("pack://application:,,,/Strings/bn.xaml"),
                Locale.TrTR => new Uri("pack://application:,,,/Strings/tr-TR.xaml"),
                Locale.De   => new Uri("pack://application:,,,/Strings/de-DE.xaml"),
                Locale.Ja   => new Uri("pack://application:,,,/Strings/ja-JP.xaml"),
                Locale.CsCZ => new Uri("pack://application:,,,/Strings/cs-CZ.xaml"),
                Locale.PlPL => new Uri("pack://application:,,,/Strings/pl-PL.xaml"),
                Locale.HuHU => new Uri("pack://application:,,,/Strings/hu-HU.xaml"),
                Locale.ItIT => new Uri("pack://application:,,,/Strings/it-IT.xaml"),
                Locale.KkKZ => new Uri("pack://application:,,,/Strings/kk-KZ.xaml"),
                Locale.RuRU => new Uri("pack://application:,,,/Strings/ru-RU.xaml"),
                _           => null,   // English: base only
            };

            if (overrideUri is not null)
            {
                try
                {
                    var ov = new ResourceDictionary { Source = overrideUri };
                    if (merged.Count > 2) merged[2] = ov; else merged.Add(ov);
                }
                catch
                {
                    // Locale file not present yet (or invalid) - stay on the English base instead of crashing.
                    if (merged.Count > 2) merged.RemoveAt(2);
                }
            }
            else if (merged.Count > 2)
            {
                merged.RemoveAt(2);
            }
        }
    }
}
