using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KillerScan.Engine;
using KillerScan.Services;

internal static class ReportLogoTests
{
    public static Task Run()
    {
        string html = ReportExport.BuildHtml([new NetworkDevice { IpAddress = "192.0.2.10" }], "192.0.2.0/24", "dark");
        // The logo travels inside the report, so opening one contacts nothing (U006).
        if (html.Contains("killerscan.net/brand") || html.Contains("<img"))
            throw new InvalidOperationException("The report must not load its logo from the network.");
        if (!html.Contains("<span id='logo' class='logo' style='display:none'><svg") || !html.Contains("var(--lg3)"))
            throw new InvalidOperationException("The report carries the logo artwork with its color slots.");

        string[] lines = html.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        string Line(string prefix) => lines.Single(line => line.StartsWith(prefix, StringComparison.Ordinal));

        // Runs the report's own logo script under Windows Script Host against a minimal page.
        string script = string.Join("\r\n",
            "var wm={style:{display:''}};",
            "var logo={className:'logo',style:{display:'none',props:{},setProperty:function(n,v){this.props[n]=v}}};",
            "var document={documentElement:{className:''},getElementById:function(id){return id=='logo'?logo:wm}};",
            Line("var LOGOS="),
            Line("var CUSTOM_ACCENT="),
            Line("var THEMED="),
            Line("function updateLogo(){"),
            "function show(t){document.documentElement.className='theme-'+t;updateLogo();",
            "  return (logo.style.display!=='none')+','+(wm.style.display!=='none')+','+logo.className+','+logo.style.props['--lg1'];}",
            "WScript.Echo(show('blood')+'|'+show('se98')+'|'+show('light')+'|'+show('black'));");
        string path = Path.Combine(Path.GetTempPath(), "KillerScan-ReportLogo-" + Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(path, script);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cscript.exe"),
                "//nologo //E:jscript \"" + path + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            string output = process.StandardOutput.ReadToEnd().Trim();
            string error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("Report logo script failed: " + error);
            // Blood: its own colors with the shadow. 98SE: the text wordmark. Light (default green
            // accent): no shadow. Black: the shadow again. No theme but light-yellow shows the outline.
            string blood = string.Join(",", "true", "false", "logo nooutline", ReportLogoColors.Variants["blood"][1]);
            string se98 = "false,true,logo nooutline," + ReportLogoColors.Variants["blood"][1];
            string light = string.Join(",", "true", "false", "logo noshadow nooutline", ReportLogoColors.Variants["light-green"][1]);
            string black = string.Join(",", "true", "false", "logo nooutline", ReportLogoColors.Variants["black-green"][1]);
            string expected = blood + "|" + se98 + "|" + light + "|" + black;
            if (output != expected)
                throw new InvalidOperationException("The report logo follows the theme. Expected " + expected + ", got " + output);
        }
        finally { File.Delete(path); }

        // A report saved under any app theme opens styled in that theme, and the switcher offers it.
        foreach (Theme theme in Enum.GetValues(typeof(Theme)))
        {
            string key = theme.ToString().ToLowerInvariant();
            string themed = ReportExport.BuildHtml([new NetworkDevice { IpAddress = "192.0.2.10" }], "192.0.2.0/24", key);
            if (!themed.Contains("html.theme-" + key + "{--bg:") || !themed.Contains("['" + key + "','"))
                throw new InvalidOperationException("The report has a palette and a swatch for the " + theme + " theme.");
        }
        // Trash Polka carries the app's window ink: the ink and the accent mask as embedded PNGs, scoped to the theme and
        // left out of print and high contrast, and no other theme gets it.
        var wallpaper = ReportWallpaper.Get() ?? throw new InvalidOperationException("The Trash Polka report ink builds.");
        if (!html.Contains("html.theme-trashpolka{--tpink:url(" + wallpaper.InkUri + ")")
            || !html.Contains("html.theme-trashpolka body::before{background:var(--tpink)")
            || html.IndexOf(wallpaper.InkUri, StringComparison.Ordinal) != html.LastIndexOf(wallpaper.InkUri, StringComparison.Ordinal)
            || !html.Contains("--tpmask:url(" + wallpaper.CoverageUri + ")")
            || html.IndexOf(wallpaper.CoverageUri, StringComparison.Ordinal) != html.LastIndexOf(wallpaper.CoverageUri, StringComparison.Ordinal)
            || !html.Contains("@media print,(forced-colors:active){html.theme-trashpolka body::before"))
            throw new InvalidOperationException("Every report carries the Trash Polka ink under that theme only.");
        if (System.Text.RegularExpressions.Regex.IsMatch(html, @"html\.theme-(?!trashpolka)\w+ body::(before|after)"))
            throw new InvalidOperationException("Only Trash Polka draws the ink behind the report.");
        static byte[] Decode(string uri) => Convert.FromBase64String(uri.Substring(uri.IndexOf(',') + 1));
        using (var inkPng = new MemoryStream(Decode(wallpaper.InkUri)))
        using (var maskPng = new MemoryStream(Decode(wallpaper.CoverageUri)))
        {
            var ink = System.Windows.Media.Imaging.BitmapDecoder.Create(inkPng, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            var mask = System.Windows.Media.Imaging.BitmapDecoder.Create(maskPng, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            if (ink.PixelWidth != ReportWallpaper.Width || mask.PixelHeight != ReportWallpaper.Height)
                throw new InvalidOperationException("The report ink is " + ReportWallpaper.Width + "x" + ReportWallpaper.Height + ".");
            int Covered(System.Windows.Media.Imaging.BitmapSource frame)
            {
                var px = new byte[frame.PixelWidth * frame.PixelHeight * 4];
                new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(px, frame.PixelWidth * 4, 0);
                int n = 0;
                for (int i = 3; i < px.Length; i += 4) if (px[i] > 0) n++;
                return n;
            }
            int inkPixels = Covered(ink), accentPixels = Covered(mask), total = ink.PixelWidth * ink.PixelHeight;
            if (inkPixels < total / 50 || accentPixels < total / 50)
                throw new InvalidOperationException($"The report ink carries both layers: {inkPixels} ink and {accentPixels} accent pixels of {total}.");
        }
        Console.WriteLine($"report html {html.Length / 1024} KB, ink {wallpaper.InkUri.Length / 1024} KB, accent {wallpaper.CoverageUri.Length / 1024} KB");

        // A theme the report does not know opens in Dark rather than unstyled.
        string unknown = ReportExport.BuildHtml([], "192.0.2.0/24", "nosuchtheme");
        if (!unknown.Contains("class='theme-dark'") || !unknown.Contains("setTheme(saved||'dark')"))
            throw new InvalidOperationException("A report in an unknown theme falls back to Dark.");

        if (!ReportLogoColors.Variants.TryGetValue("light-yellow", out var yellow) || yellow[4] == "none"
            || ReportLogoColors.Variants.Where(v => v.Key != "light-yellow").Any(v => v.Value[4] != "none"))
            throw new InvalidOperationException("Only light-yellow carries the outline layer.");
        return Task.CompletedTask;
    }
}
