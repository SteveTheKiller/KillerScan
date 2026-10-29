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

        if (!ReportLogoColors.Variants.TryGetValue("light-yellow", out var yellow) || yellow[4] == "none"
            || ReportLogoColors.Variants.Where(v => v.Key != "light-yellow").Any(v => v.Value[4] != "none"))
            throw new InvalidOperationException("Only light-yellow carries the outline layer.");
        return Task.CompletedTask;
    }
}
