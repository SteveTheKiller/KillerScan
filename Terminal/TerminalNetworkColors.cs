namespace KillerScan.Terminal
{
    internal sealed partial class TerminalControl
    {
        // Installed in this shell only, independently of the editable prompt script.
        private const string NetworkColorSetup = """
            function global:Format-KillerScanNetworkLine {
                param([string]$Line, [hashtable]$State = @{})
                if ($Line.Contains([string][char]27)) { return $Line }
                $colors = New-Object 'int[]' $Line.Length
                $base = 0
                if ($Line -match '(?i)TTL[=:]|time[=<]\s*\d|temps[=<]\s*\d|Zeit[=<]\s*\d') { $base = 32 }
                if ($Line -match '(?i)timed out|unreachable|transmit failed|general failure|could not find host|non-existent domain|NXDOMAIN|SERVFAIL|REFUSED|Zeitüberschreitung|nicht erreichbar|délai.*dépassé|injoignable|tiempo de espera agotado|inaccesible') { $base = 31 }
                if ($Line -match '(?i)media disconnected') { $base = 2 }
                if ($base) { for ($i = 0; $i -lt $colors.Length; $i++) { $colors[$i] = $base } }

                # Table positions are learned from headers, never guessed from arbitrary numbers.
                if ($Line -match '\b(LocalPort|RemotePort)\b' -and $Line -notmatch ':') {
                    $State.PortColumns = @([regex]::Matches($Line, '\S+') | Where-Object { $_.Value -match '^(LocalPort|RemotePort)$' } | ForEach-Object { $_.Index })
                } elseif ([string]::IsNullOrWhiteSpace($Line)) { $State.Remove('PortColumns') }
                foreach ($column in $State.PortColumns) {
                    if ($column -lt $Line.Length) {
                        $part = [regex]::Match($Line.Substring($column), '^\s*\d+\b')
                        if ($part.Success) { for ($i = $column; $i -lt $column + $part.Length; $i++) { $colors[$i] = 32 } }
                    }
                }

                $rules = @(
                    @('(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b', 33),
                    @('(?i)\b\d+(?:[.,]\d+)?\s*ms\b', 33),
                    @('(?i)(?:LocalPort|RemotePort|DestinationPort|Port)\s*[:=]\s*(?<value>\d+)\b', 32),
                    @('(?i)(?:TcpTestSucceeded|PingSucceeded)\s*:\s*(?<value>True)\b', 32),
                    @('(?i)(?:TcpTestSucceeded|PingSucceeded)\s*:\s*(?<value>False)\b', 31),
                    @('(?i)\bReachable\b', 32),
                    @('(?i)\bIncomplete\b', 33),
                    @('(?i)\bStale\b', 2)
                )
                foreach ($rule in $rules) {
                    foreach ($match in [regex]::Matches($Line, $rule[0])) {
                        $span = $match
                        if ($match.Groups['value'].Success) { $span = $match.Groups['value'] }
                        for ($i = $span.Index; $i -lt $span.Index + $span.Length; $i++) { $colors[$i] = $rule[1] }
                    }
                }

                # Parse candidates as addresses so MACs, times and hex identifiers are not IPs.
                foreach ($match in [regex]::Matches($Line, '(?i)(?<![\w.])(?:[a-f0-9]+[.:]|:)[a-f0-9:.]*(?:%\d+)?')) {
                    $candidate = $match.Value.TrimEnd(':', '.')
                    if ($candidate -match '^(?:[0-9a-f]{2}:){5}[0-9a-f]{2}$') { continue }
                    if ($match.Value -match '::$') { $candidate = $match.Value }
                    $address = $null
                    if (($candidate.Contains('.') -or $candidate.Contains(':')) -and [System.Net.IPAddress]::TryParse($candidate, [ref]$address)) {
                        for ($i = $match.Index; $i -lt $match.Index + $candidate.Length; $i++) { $colors[$i] = 36 }
                    }
                }
                foreach ($match in [regex]::Matches($Line, '(?<address>\[[0-9a-fA-F:.%]+\]|(?:\d{1,3}\.){3}\d{1,3}|\*):(?<port>\d+|\*)')) {
                    $span = $match.Groups['address']
                    for ($i = $span.Index; $i -lt $span.Index + $span.Length; $i++) { $colors[$i] = 36 }
                    $span = $match.Groups['port']
                    for ($i = $span.Index; $i -lt $span.Index + $span.Length; $i++) { $colors[$i] = 32 }
                }
                foreach ($match in [regex]::Matches($Line, '(?<![\w:])\d+(?:[.,]\d+)?%(?!\d)')) {
                    $color = 31
                    if ($match.Value -match '^0(?:[.,]0+)?%$') { $color = 32 }
                    for ($i = $match.Index; $i -lt $match.Index + $match.Length; $i++) { $colors[$i] = $color }
                }
                if ($Line -match '^\s*\d+\s+(?:\*\s+){2}\*') {
                    foreach ($match in [regex]::Matches($Line, '\*')) { $colors[$match.Index] = 31 }
                }

                $builder = New-Object System.Text.StringBuilder
                $previous = 0
                for ($i = 0; $i -lt $Line.Length; $i++) {
                    if ($colors[$i] -ne $previous) {
                        [void]$builder.Append([char]27).Append('[').Append($colors[$i]).Append('m')
                        $previous = $colors[$i]
                    }
                    [void]$builder.Append($Line[$i])
                }
                if ($previous) { [void]$builder.Append([char]27).Append('[0m') }
                $builder.ToString()
            }

            # Native commands return plain strings with a display-only type tag. Redirection,
            # assignment, filtering and export never receive ANSI escapes.
            foreach ($name in @('ping', 'tracert', 'pathping', 'nslookup', 'ipconfig', 'arp', 'netstat', 'route')) {
                $body = {
                    & ($env:SystemRoot + '\System32\' + $name + '.exe') @args | ForEach-Object {
                        Add-Member -InputObject $_ -TypeName KillerScan.NetworkText -PassThru
                    }
                    $global:LASTEXITCODE = $LASTEXITCODE
                }.GetNewClosure()
                Set-Item -Path ('Function:global:Invoke-KillerScan-' + $name) -Value $body
                Set-Alias -Name $name -Value ('Invoke-KillerScan-' + $name) -Scope Global
                Set-Alias -Name ($name + '.exe') -Value ('Invoke-KillerScan-' + $name) -Scope Global
            }

            # Only the terminal's final display is formatted. Network cmdlets are not replaced:
            # their original objects, parameters, completion and pipeline behavior remain intact.
            function global:Out-Default {
                [CmdletBinding()]
                param([switch]$Transcript, [Parameter(ValueFromPipeline=$true)][psobject]$InputObject)
                begin { $pipe = $null; $kind = ''; $state = @{} }
                process {
                    $next = 'normal'
                    if ($InputObject.PSTypeNames -contains 'KillerScan.NetworkText') { $next = 'text' }
                    elseif (($InputObject.PSTypeNames -join ' ') -match 'DnsClient|MSFT_Net(?:TCPConnection|Neighbor|Route)|TestNetConnectionResult|NetConnectionResults') { $next = 'network' }
                    if ($Transcript) { $next = 'normal' }
                    if ($next -ne $kind) {
                        if ($null -ne $pipe) { $pipe.End() }
                        $kind = $next
                        $state = @{}
                        if ($kind -eq 'text') {
                            $render = { ForEach-Object { Format-KillerScanNetworkLine ([string]$_) $state } | Microsoft.PowerShell.Core\Out-Default }
                        } elseif ($kind -eq 'network') {
                            $render = { Microsoft.PowerShell.Utility\Out-String -Stream | ForEach-Object { Format-KillerScanNetworkLine $_ $state } | Microsoft.PowerShell.Core\Out-Default }
                        } else {
                            $render = { Microsoft.PowerShell.Core\Out-Default -Transcript:$Transcript }
                        }
                        $pipe = $render.GetSteppablePipeline($MyInvocation.CommandOrigin)
                        $pipe.Begin($PSCmdlet)
                    }
                    $pipe.Process($InputObject)
                }
                end { if ($null -ne $pipe) { $pipe.End() } }
            };
            """;
    }
}
