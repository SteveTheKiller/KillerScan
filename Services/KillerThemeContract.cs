using System;
using System.Windows;

/// <summary>
/// Applies the shared KillerUI skin after an app's feature-specific palette and before
/// its optional accent overlay. This is deliberately namespace-free so the same source
/// compiles into every standalone Killer app without adding a runtime dependency.
/// </summary>
internal static class KillerThemeContract
{
    internal static void Apply(ResourceDictionary target, string theme)
    {
        var shared = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/KillerUI/{theme}.xaml")
        };
        foreach (object key in shared.Keys)
            target[key] = shared[key];
    }
}
