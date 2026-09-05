namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        /// <summary>
        /// Opens the terminal and runs a speed test in it.
        ///
        /// The official Ookla CLI gives the number people recognize, but it is a separate binary
        /// under its own license, so it is never fetched silently: if it is not already on PATH the
        /// script says what it would download and from where, and waits for an answer. Decline and
        /// the built-in HTTP throughput test runs instead, which needs nothing installed and is
        /// honest about being indicative rather than an official speedtest.net result.
        /// </summary>
        private void SpeedTestButton_Click(object sender, System.Windows.RoutedEventArgs e) =>
            NewTerminal(title: "Speed test", shellCommand: SpeedTestCommand());

        /// <summary>
        /// Single quotes throughout, because this rides inside the double-quoted -Command that
        /// also carries the prompt. Written for Windows PowerShell 5.1 as well as 7: WebClient and
        /// Expand-Archive rather than anything newer.
        /// </summary>
        private static string SpeedTestCommand() =>
            "$ErrorActionPreference = 'Stop'; " +
            "$e = [char]27; " +
            "Write-Host ''; " +
            "Write-Host ($e + '[36m' + 'KillerScan speed test' + $e + '[0m'); " +
            "Write-Host ''; " +
            "$exe = $null; " +
            "$found = Get-Command speedtest.exe -ErrorAction SilentlyContinue; " +
            "if ($found) { $exe = $found.Source } " +
            "else { " +
              "Write-Host 'The Ookla speedtest CLI is not installed.'; " +
              "Write-Host 'KillerScan can download it from install.speedtest.net (about 4 MB), or run its own HTTP throughput test instead, which installs nothing.'; " +
              "Write-Host ''; " +
              "$answer = Read-Host 'Download the Ookla CLI? (y/N)'; " +
              "if ($answer -match '^(y|yes)$') { " +
                "try { " +
                  "$zip = Join-Path $env:TEMP 'ookla-speedtest.zip'; " +
                  "$dir = Join-Path $env:TEMP 'ookla-speedtest'; " +
                  "Write-Host 'Downloading...'; " +
                  "Invoke-WebRequest -UseBasicParsing -Uri 'https://install.speedtest.net/app/cli/ookla-speedtest-1.2.0-win64.zip' -OutFile $zip; " +
                  "Expand-Archive -Path $zip -DestinationPath $dir -Force; " +
                  "$candidate = Join-Path $dir 'speedtest.exe'; " +
                  "if (Test-Path $candidate) { $exe = $candidate } " +
                "} catch { " +
                  "Write-Host ($e + '[31m' + 'The download failed: ' + $_.Exception.Message + $e + '[0m'); " +
                "} " +
              "} " +
            "} " +
            "if ($exe) { " +
              // jsonl rather than the CLI's own output: one JSON object per line, so the numbers
              // are rendered here in the app's colors, the progress line is ours, and Ookla's
              // stray startup diagnostics are simply not JSON and get dropped.
              "$srv = ''; " +
              "& $exe --accept-license --accept-gdpr --format=jsonl 2>$null | ForEach-Object { " +
                "$o = $null; try { $o = $_ | ConvertFrom-Json } catch { return }; " +
                "switch ($o.type) { " +
                  "'testStart' { " +
                    "$srv = $o.server.name + ' - ' + $o.server.location; " +
                    "Write-Host ('  Server    ' + $e + '[36m' + $srv + $e + '[0m'); " +
                    "Write-Host ('  ISP       ' + $e + '[36m' + $o.isp + $e + '[0m'); " +
                    "Write-Host '' " +
                  "} " +
                  "'ping' { " +
                    "Write-Host -NoNewline ([char]13 + '  Latency   ' + $e + '[33m' + " +
                      "[math]::Round($o.ping.latency, 2) + ' ms' + $e + '[0m   ') " +
                  "} " +
                  "'download' { " +
                    "$m = [math]::Round(($o.download.bandwidth * 8) / 1000000, 2); " +
                    "Write-Host -NoNewline ([char]13 + '  Download  ' + $e + '[32m' + $m + ' Mbps' + $e + '[0m   ') " +
                  "} " +
                  "'upload' { " +
                    "$m = [math]::Round(($o.upload.bandwidth * 8) / 1000000, 2); " +
                    "Write-Host -NoNewline ([char]13 + '  Upload    ' + $e + '[32m' + $m + ' Mbps' + $e + '[0m   ') " +
                  "} " +
                  "'result' { " +
                    "$d = [math]::Round(($o.download.bandwidth * 8) / 1000000, 2); " +
                    "$u = [math]::Round(($o.upload.bandwidth * 8) / 1000000, 2); " +
                    "Write-Host ([char]13 + '                                             '); " +
                    "Write-Host ('  Latency   ' + $e + '[33m' + [math]::Round($o.ping.latency, 2) + ' ms' + $e + '[0m' + " +
                      "$e + '[90m' + '   jitter ' + [math]::Round($o.ping.jitter, 2) + ' ms' + $e + '[0m'); " +
                    "Write-Host ('  Download  ' + $e + '[32m' + $d + ' Mbps' + $e + '[0m'); " +
                    "Write-Host ('  Upload    ' + $e + '[32m' + $u + ' Mbps' + $e + '[0m'); " +
                    "if ($o.packetLoss -ne $null) { " +
                      "Write-Host ('  Loss      ' + $e + '[32m' + $o.packetLoss + ' %' + $e + '[0m') " +
                    "} " +
                    "Write-Host ''; " +
                    "Write-Host ($e + '[90m' + '  ' + $o.result.url + $e + '[0m') " +
                  "} " +
                "} " +
              "} " +
            "} else { " +
              "Write-Host ''; " +
              "Write-Host 'Running the built-in HTTP throughput test. These figures are indicative, not an official speedtest.net result.'; " +
              "Write-Host ''; " +
              "try { " +
                "$ping = New-Object System.Net.NetworkInformation.Ping; " +
                "$reply = $ping.Send('1.1.1.1', 2000); " +
                "if ($reply.Status -eq 'Success') { " +
                  "Write-Host ('  Latency   ' + $e + '[32m' + $reply.RoundtripTime + ' ms' + $e + '[0m') " +
                "} else { Write-Host '  Latency   no reply' } " +
                "$wc = New-Object System.Net.WebClient; " +
                "$down = Measure-Command { $script:payload = $wc.DownloadData('https://speed.cloudflare.com/__down?bytes=25000000') }; " +
                "$mbps = [math]::Round((($script:payload.Length * 8) / $down.TotalSeconds) / 1000000, 2); " +
                "Write-Host ('  Download  ' + $e + '[32m' + $mbps + ' Mbps' + $e + '[0m'); " +
                "$bytes = New-Object byte[] 8000000; " +
                "$up = Measure-Command { $null = $wc.UploadData('https://speed.cloudflare.com/__up', 'POST', $bytes) }; " +
                "$umbps = [math]::Round((($bytes.Length * 8) / $up.TotalSeconds) / 1000000, 2); " +
                "Write-Host ('  Upload    ' + $e + '[32m' + $umbps + ' Mbps' + $e + '[0m'); " +
                "$wc.Dispose(); " +
              "} catch { " +
                "Write-Host ($e + '[31m' + 'The test could not finish: ' + $_.Exception.Message + $e + '[0m'); " +
              "} " +
            "} " +
            "Write-Host ''";
    }
}
