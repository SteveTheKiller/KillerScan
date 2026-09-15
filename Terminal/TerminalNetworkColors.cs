namespace KillerScan.Terminal
{
    internal sealed partial class TerminalControl
    {
        // Installed in this shell only, independently of the editable prompt script.
        private const string NetworkColorSetup = """
            function global:Format-KillerScanPing {
                param([string]$Line)
                $esc = [string][char]27
                $base = '0'
                if ($Line -match '(?i)TTL[=:]|time[=<]\s*\d|temps[=<]\s*\d|Zeit[=<]\s*\d') { $base = '32' }
                if ($Line -match '(?i)timed out|unreachable|transmit failed|general failure|could not find host|timeout|Zeitüberschreitung|nicht erreichbar|délai.*dépassé|injoignable|tiempo de espera agotado|inaccesible') { $base = '31' }
                $pattern = '(?<![\w:])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])|(?<!\w)(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:.%]+|\d+(?:[.,]\d+)?\s*ms\b|\d+(?:[.,]\d+)?%'
                $text = [regex]::Replace($Line, $pattern, {
                    param($match)
                    $color = '36'
                    if ($match.Value -match 'ms$') { $color = '33' }
                    elseif ($match.Value.EndsWith('%')) {
                        if ($match.Value -match '^0(?:[.,]0+)?%$') { $color = '32' } else { $color = '31' }
                    }
                    $esc + '[' + $color + 'm' + $match.Value + $esc + '[' + $base + 'm'
                })
                $esc + '[' + $base + 'm' + $text + $esc + '[0m'
            }
            function global:Invoke-KillerScanPing {
                $plain = $MyInvocation.PipelineLength -gt 1
                & ($env:SystemRoot + '\System32\PING.EXE') @args | ForEach-Object {
                    if ($plain) { $_ } else { Format-KillerScanPing $_ }
                }
                $global:LASTEXITCODE = $LASTEXITCODE
            }
            Set-Alias -Name ping -Value Invoke-KillerScanPing -Scope Global;
            Set-Alias -Name ping.exe -Value Invoke-KillerScanPing -Scope Global;
            """;
    }
}
