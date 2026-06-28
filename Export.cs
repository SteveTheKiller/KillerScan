using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace KillerScan
{
    // Scanner core: export the discovered devices to CSV or a styled, interactive HTML report.
    // Column headers stay English (machine-readable) per project convention.
    public partial class MainWindow
    {
        // Where the report pulls optional brand assets from. The report still works fully
        // without them (solid colour + text wordmark fallbacks), so these can 404 safely.
        private const string AssetBase = "https://scan.killertools.net/assets/";
        private const string SiteUrl   = "https://scan.killertools.net";

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveDevices.Count == 0 || ExportButton.ContextMenu is null) return;
            ExportButton.ContextMenu.PlacementTarget = ExportButton;
            ExportButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            ExportButton.ContextMenu.IsOpen = true;
        }

        private void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "CSV File|*.csv", FileName = $"KillerScan_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("IP Address,Hostname,MAC Address,Vendor,Type,Open Ports");
                foreach (var d in ActiveDevices.OrderBy(d => d.IpSortKey))
                    sb.AppendLine($"\"{d.IpAddress}\",\"{d.Hostname}\",\"{d.MacAddress}\",\"{d.Vendor}\",\"{d.DeviceType}\",\"{d.OpenPortsDisplay}\"");
                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                StatusText.Text = $"Exported to {Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            { MessageBox.Show($"Export error: {ex.Message}", "KillerScan", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ExportHtml_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "HTML Report|*.html", FileName = $"KillerScan_{DateTime.Now:yyyyMMdd_HHmmss}.html" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string current = Services.ThemeManager.Current.ToString().ToLowerInvariant();
                var sb = new StringBuilder();

                sb.AppendLine("<!DOCTYPE html>");
                sb.AppendLine($"<html lang='en' class='theme-{current}'><head><meta charset='utf-8'/>");
                sb.AppendLine("<meta name='viewport' content='width=device-width, initial-scale=1'/>");
                sb.AppendLine("<title>KillerScan Report</title>");
                sb.AppendLine("<style>");

                // Per-theme variable blocks (built without string interpolation to avoid brace escaping).
                foreach (var t in ReportThemes)
                {
                    var ty = TypeMapFor(t.Key);
                    var b = new StringBuilder();
                    b.Append("html.theme-").Append(t.Key).Append('{')
                     .Append("--bg:").Append(t.Bg).Append(';')
                     .Append("--surface:").Append(t.Surface).Append(';')
                     .Append("--pane:").Append(t.Pane).Append(';')
                     .Append("--accent:").Append(t.Accent).Append(';')
                     .Append("--text:").Append(t.Text).Append(';')
                     .Append("--muted:").Append(t.Muted).Append(';')
                     .Append("--border:").Append(t.Border).Append(';')
                     .Append("--hover:").Append(t.Hover).Append(';');
                    foreach (var slug in TypeSlugs)
                        b.Append("--t-").Append(slug).Append(':').Append(ty[slug]).Append(';');
                    b.Append('}');
                    sb.AppendLine(b.ToString());
                }

                sb.AppendLine("*{box-sizing:border-box}");
                sb.AppendLine("body{margin:0;background:var(--bg);color:var(--text);font-family:-apple-system,'Segoe UI',Roboto,sans-serif;padding:24px;line-height:1.5}");
                sb.AppendLine(".wrap{max-width:1100px;margin:0 auto}");
                sb.AppendLine(".topbar{display:flex;justify-content:space-between;align-items:center;gap:16px;flex-wrap:wrap;margin-bottom:8px}");
                sb.AppendLine(".brand{display:flex;align-items:center;gap:10px;min-height:38px}");
                sb.AppendLine(".logo{height:36px;display:block}");
                sb.AppendLine(".wordmark{font-family:Consolas,'Courier New',monospace;font-size:27px;font-weight:700;letter-spacing:.5px}");
                sb.AppendLine(".wordmark .k{color:var(--text)}.wordmark .s{color:var(--accent)}");
                sb.AppendLine(".switchers{display:flex;flex-direction:column;gap:6px;align-items:flex-end}");
                sb.AppendLine(".swrow{display:flex;align-items:center;gap:8px}");
                sb.AppendLine(".swlabel{color:var(--muted);font-size:10px;font-family:Consolas,monospace;letter-spacing:.5px;text-transform:uppercase}");
                sb.AppendLine(".themesw{display:flex;gap:7px;align-items:center}");
                sb.AppendLine(".themesw button{width:18px;height:18px;border-radius:50%;border:2px solid var(--border);cursor:pointer;padding:0;outline:none;transition:transform .1s}");
                sb.AppendLine(".themesw button:hover{transform:scale(1.15)}.themesw button.active{border-color:var(--text)}");
                sb.AppendLine(".meta{color:var(--muted);font-size:13px;margin:0 0 18px}.meta b{color:var(--text)}");
                sb.AppendLine($".tablewrap{{overflow-x:auto;border:1px solid var(--border);border-radius:8px;background:var(--pane) url('{AssetBase}killerscan-grain.png');box-shadow:0 10px 30px rgba(0,0,0,.45)}}");
                sb.AppendLine("table{border-collapse:collapse;width:100%;min-width:760px}");
                sb.AppendLine("th{background:var(--surface);color:var(--muted);text-align:left;padding:10px 14px;font-size:12px;font-weight:600;font-family:Consolas,monospace;letter-spacing:.3px;position:sticky;top:0;white-space:nowrap;cursor:pointer;user-select:none}");
                sb.AppendLine("th:hover{color:var(--text)}th .arrow{display:inline-block;width:10px;font-size:10px;opacity:.7}");
                sb.AppendLine("td{border-top:1px solid var(--border);padding:9px 14px;font-size:13px;white-space:nowrap}");
                sb.AppendLine("td.vendor{white-space:normal}tbody tr:hover td{background:var(--hover)}");
                sb.AppendLine(".mono{font-family:Consolas,monospace}");
                sb.AppendLine(".type{font-weight:600;color:var(--accent)}");
                foreach (var slug in TypeSlugs)
                    sb.AppendLine($".type.t-{slug}{{color:var(--t-{slug})}}");
                sb.AppendLine(".footer{color:var(--muted);font-size:11px;margin-top:18px;opacity:.85}");
                sb.AppendLine(".footer a{color:var(--accent);text-decoration:none}.footer a:hover{text-decoration:underline}");
                sb.AppendLine("@media(max-width:640px){body{padding:12px}.wordmark{font-size:22px}th,td{padding:8px 10px;font-size:12px}}");
                sb.AppendLine("</style></head><body><div class='wrap'>");

                // Header: logo (if hosted) falls back to a Consolas wordmark - Killer in text colour, Scan in accent.
                sb.AppendLine("<div class='topbar'><div class='brand'>");
                sb.AppendLine($"<img class='logo' src='{AssetBase}killerscan-wordmark.png' alt='' onload=\"document.getElementById('wm').style.display='none'\" onerror=\"this.style.display='none'\"/>");
                sb.AppendLine("<span id='wm' class='wordmark'><span class='k'>Killer</span><span class='s'>Scan</span></span>");
                sb.AppendLine("</div><div class='switchers'>");
                sb.AppendLine("<div class='swrow'><span class='swlabel'>Theme</span><div class='themesw' id='themesw'></div></div>");
                sb.AppendLine("<div class='swrow'><span class='swlabel'>Accent</span><div class='themesw' id='accentsw'></div></div>");
                sb.AppendLine("</div></div>");

                sb.AppendLine($"<p class='meta'>Subnet <b>{Esc(SubnetInput.Text)}</b> &middot; Scanned <b>{DateTime.Now:yyyy-MM-dd HH:mm}</b> &middot; <b>{ActiveDevices.Count}</b> devices</p>");

                sb.AppendLine("<div class='tablewrap'><table id='tbl'><thead><tr>");
                sb.AppendLine("<th data-type='num'>IP Address <span class='arrow'></span></th>");
                sb.AppendLine("<th data-type='text'>Hostname <span class='arrow'></span></th>");
                sb.AppendLine("<th data-type='text'>MAC Address <span class='arrow'></span></th>");
                sb.AppendLine("<th data-type='text'>Vendor <span class='arrow'></span></th>");
                sb.AppendLine("<th data-type='text'>Type <span class='arrow'></span></th>");
                sb.AppendLine("<th data-type='text'>Open Ports <span class='arrow'></span></th>");
                sb.AppendLine("</tr></thead><tbody>");
                foreach (var d in ActiveDevices.OrderBy(d => d.IpSortKey))
                {
                    string slug = TypeSlug(d.DeviceType);
                    string cls = slug.Length == 0 ? "type" : $"type t-{slug}";
                    sb.AppendLine(
                        $"<tr><td class='mono' data-sort='{d.IpSortKey}'>{Esc(d.IpAddress)}</td>" +
                        $"<td>{Esc(d.Hostname)}</td>" +
                        $"<td class='mono'>{Esc(d.MacAddress)}</td>" +
                        $"<td class='vendor'>{Esc(d.Vendor)}</td>" +
                        $"<td class='{cls}'>{Esc(d.DeviceType)}</td>" +
                        $"<td class='mono'>{Esc(d.OpenPortsDisplay)}</td></tr>");
                }
                sb.AppendLine("</tbody></table></div>");

                sb.AppendLine($"<p class='footer'>Generated by <a href='{SiteUrl}' target='_blank' rel='noopener'>KillerScan</a> &middot; &copy; 2026 Steve the Killer</p>");
                sb.AppendLine("</div>");

                // Interactivity: theme switcher (persisted) + click-to-sort columns.
                sb.AppendLine("<script>");
                sb.AppendLine("var THEMES=[['dark','Dark','#3a3a3a'],['light','Light','#e8e8e8'],['black','Black','#000000'],['blood','Blood','#4a1f20'],['greed','Greed','#0a5234'],['cyanotic','Cyanotic','#0a4a6e']];");
                sb.AppendLine("var sw=document.getElementById('themesw');");
                sb.AppendLine("function setTheme(t){document.documentElement.className='theme-'+t;try{localStorage.setItem('ksTheme',t)}catch(e){}var k=sw.children;for(var i=0;i<k.length;i++)k[i].className=(k[i].getAttribute('data-t')===t)?'active':'';}");
                sb.AppendLine("THEMES.forEach(function(a){var b=document.createElement('button');b.title=a[1];b.setAttribute('data-t',a[0]);b.style.background=a[2];b.onclick=function(){setTheme(a[0])};sw.appendChild(b);});");
                sb.AppendLine("var saved=null;try{saved=localStorage.getItem('ksTheme')}catch(e){}");
                sb.AppendLine($"setTheme(saved||'{current}');");
                // Accent picker (ROYGBIV, matching the app) - overrides --accent and persists.
                sb.AppendLine("var ACCENTS=[['#DD504B','Red'],['#E8962C','Orange'],['#1ea54c','Green'],['#1FB8A8','Teal'],['#50AEE8','Blue'],['#B982E3','Purple']];");
                sb.AppendLine("var asw=document.getElementById('accentsw');");
                sb.AppendLine("function setAccent(c){if(c){document.documentElement.style.setProperty('--accent',c);try{localStorage.setItem('ksAccent',c)}catch(e){}}var k=asw.children;for(var i=0;i<k.length;i++)k[i].className=(k[i].getAttribute('data-c')===c)?'active':'';}");
                sb.AppendLine("ACCENTS.forEach(function(a){var b=document.createElement('button');b.title=a[1];b.setAttribute('data-c',a[0]);b.style.background=a[0];b.onclick=function(){setAccent(a[0])};asw.appendChild(b);});");
                sb.AppendLine("var savedA=null;try{savedA=localStorage.getItem('ksAccent')}catch(e){}");
                sb.AppendLine("if(savedA)setAccent(savedA);");
                sb.AppendLine("var tbl=document.getElementById('tbl'),tb=tbl.tBodies[0],sortCol=-1,sortDir=1;");
                sb.AppendLine("var ths=tbl.tHead.rows[0].cells;");
                sb.AppendLine("for(var c=0;c<ths.length;c++){(function(i){ths[i].onclick=function(){");
                sb.AppendLine("sortDir=(sortCol===i)?-sortDir:1;sortCol=i;var num=ths[i].getAttribute('data-type')==='num';");
                sb.AppendLine("var rows=Array.prototype.slice.call(tb.rows);rows.sort(function(a,b){var x,y;if(num){x=parseFloat(a.cells[i].getAttribute('data-sort'))||0;y=parseFloat(b.cells[i].getAttribute('data-sort'))||0;}else{x=a.cells[i].innerText.toLowerCase();y=b.cells[i].innerText.toLowerCase();}return (x>y?1:x<y?-1:0)*sortDir;});");
                sb.AppendLine("for(var r=0;r<rows.length;r++)tb.appendChild(rows[r]);");
                sb.AppendLine("for(var h=0;h<ths.length;h++)ths[h].getElementsByClassName('arrow')[0].textContent='';");
                sb.AppendLine("ths[i].getElementsByClassName('arrow')[0].textContent=sortDir>0?'\\u25B2':'\\u25BC';};})(c);}");
                sb.AppendLine("</script>");
                sb.AppendLine("</body></html>");

                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                StatusText.Text = $"Exported to {Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            { MessageBox.Show($"Export error: {ex.Message}", "KillerScan", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        // ---- Report palette data (the in-report theme switcher embeds all six) ----
        private readonly struct ReportTheme(string key, string bg, string surface, string pane, string accent,
                                            string text, string muted, string border, string hover)
        {
            public readonly string Key = key, Bg = bg, Surface = surface, Pane = pane, Accent = accent,
                                   Text = text, Muted = muted, Border = border, Hover = hover;
        }

        private static readonly ReportTheme[] ReportThemes =
        [
            new("dark",     "#1c1c1c", "#333333", "#3a3a3a", "#1ea54c", "#e0e0e0", "#a0a0a0", "#2e2e2e", "#404040"),
            new("light",    "#dcdcdc", "#f0f0f0", "#c8c8c8", "#1b5e20", "#1a1a1a", "#555555", "#b0b0b0", "#b2b2b2"),
            new("black",    "#000000", "#0d0d0d", "#161616", "#00ff66", "#ffffff", "#cccccc", "#2a2a2a", "#242424"),
            new("blood",    "#240c0d", "#2c1012", "#321416", "#e8485a", "#fffde8", "#f8c99e", "#401d1d", "#54201f"),
            new("greed",    "#002115", "#002e1c", "#003824", "#3fbf6f", "#fffde8", "#e0d49a", "#0f4a30", "#00593a"),
            new("cyanotic", "#001a28", "#00263a", "#002e48", "#3aa0d8", "#fffde8", "#e0d49a", "#183450", "#0a5478"),
        ];

        private static readonly string[] TypeSlugs =
        ["router","windows","printer","switch","hypervisor","nas","iot","server","linux","dns","ha","mobile","camera","smarttv","media"];

        // Bright set (for the dark-paned themes) and the darkened set (for the light pane).
        private static readonly string[] TypeBright =
        ["#F0A030","#60A0F0","#D080A0","#C0C0C0","#E06060","#40C080","#B060D0","#E08050","#D0C050","#50B0D0","#50D0A0","#A0A0F0","#F0C060","#E0A0E0","#70C0E0"];
        private static readonly string[] TypeDarkened =
        ["#B06000","#1D5FB0","#9C3F66","#5A5A5A","#B02A2A","#157A45","#6A2A9C","#B0481A","#7A6A10","#1A6A8C","#157A4F","#4040A8","#9A6800","#864A86","#256A9C"];

        /// <summary>Per-theme type colour map, mirroring the in-app theme overrides.</summary>
        private static Dictionary<string, string> TypeMapFor(string themeKey)
        {
            string[] basePal = themeKey == "light" ? TypeDarkened : TypeBright;
            var map = new Dictionary<string, string>();
            for (int i = 0; i < TypeSlugs.Length; i++) map[TypeSlugs[i]] = basePal[i];
            // Same-hue-as-pane tweaks the app applies in the RGB themes:
            if (themeKey == "cyanotic") { map["windows"] = "#8FC4FF"; map["iot"] = "#A24FD8"; }
            else if (themeKey == "greed") { map["nas"] = "#7BE06A"; map["ha"] = "#38D6CC"; }
            else if (themeKey == "blood") { map["hypervisor"] = "#F08A5A"; }
            return map;
        }

        /// <summary>Maps a device type to its colour-slug (matches the in-app Type* keys).</summary>
        private static string TypeSlug(string? type) => type switch
        {
            "Router"          => "router",
            "Windows"         => "windows",
            "Windows Server"  => "windows",
            "Printer"         => "printer",
            "Switch/AP"       => "switch",
            "Apple Device"    => "switch",
            "Apple TV"        => "switch",
            "Hypervisor"      => "hypervisor",
            "NAS"             => "nas",
            "IoT"             => "iot",
            "Server"          => "server",
            "Linux/SSH"       => "linux",
            "DNS Server"      => "dns",
            "Home Assistant"  => "ha",
            "Mobile"          => "mobile",
            "Camera"          => "camera",
            "Smart TV"        => "smarttv",
            "Media Streamer"  => "media",
            _                 => ""
        };

        private static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
    }
}
