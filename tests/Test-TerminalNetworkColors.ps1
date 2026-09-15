param([string]$EvidencePath)
$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '../Terminal/TerminalNetworkColors.cs') -Raw
$setup = [regex]::Match($source, '(?s)NetworkColorSetup = """\s*(.*?)\s*""";').Groups[1].Value
. ([scriptblock]::Create($setup))
$esc = [string][char]27
function Assert-Color([string]$Line, [string]$Token, [int]$Color, [hashtable]$State = @{}) {
    $formatted = Format-KillerScanNetworkLine $Line $State
    if (($formatted -replace '\x1b\[[0-9;]*m', '') -cne $Line) { throw ('Changed text: ' + $Line) }
    $at = $Line.IndexOf($Token)
    $plainIndex = 0
    $activeColor = 0
    foreach ($part in [regex]::Matches($formatted, '\x1b\[(\d+)m|[^\x1b]')) {
        if ($part.Groups[1].Success) { $activeColor = [int]$part.Groups[1].Value }
        else {
            if ($plainIndex -ge $at -and $plainIndex -lt $at + $Token.Length -and $activeColor -ne $Color) {
                throw ('Wrong color for ' + $Token + ' in ' + $Line + ': ' + $activeColor)
            }
            $plainIndex++
        }
    }
}
Assert-Color 'Reply from 1.1.1.1: bytes=32 time=12ms TTL=54' '1.1.1.1' 36
Assert-Color 'Reply from ::1: time<1ms' '::1' 36
Assert-Color 'Reply from 1.1.1.1: bytes=32 time=12ms TTL=54' 'Reply' 32
Assert-Color 'Request timed out.' 'Request' 31
Assert-Color '  1   12 ms   13 ms   11 ms  router.example [192.168.8.1]' '12 ms' 33
Assert-Color '  2    *     *     *     Request timed out.' '*' 31
Assert-Color '  0/ 100 =  0%   1/ 100 =  1%  192.168.8.1' '1%' 31
Assert-Color 'Packets: Lost = 0 (0% loss)' '0%' 32
Assert-Color '*** server cannot find missing.example: NXDOMAIN' 'NXDOMAIN' 31
Assert-Color 'Address:  2001:db8::1' '2001:db8::1' 36
Assert-Color 'IPv6 Address. . . : fe80::1234%19' 'fe80::1234%19' 36
Assert-Color '  192.168.8.1  94-83-c4-a4-78-82  dynamic' '94-83-c4-a4-78-82' 33
Assert-Color '  192.168.8.1  94:83:c4:a4:78:82  Stale' '94:83:c4:a4:78:82' 33
Assert-Color ' TCP  192.168.8.10:54321  1.1.1.1:443  ESTABLISHED' '443' 32
Assert-Color ' TCP  192.168.8.10:54321  1.1.1.1:443  ESTABLISHED' 'ESTABLISHED' 0
Assert-Color ' TCP  [::]:443  [::]:0  LISTENING' '443' 32
Assert-Color ' RemotePort : 443' '443' 32
Assert-Color ' TcpTestSucceeded : True' 'True' 32
Assert-Color ' PingSucceeded : False' 'False' 31
Assert-Color '   0.0.0.0          0.0.0.0       192.168.8.1    192.168.8.10    25' '192.168.8.1' 36
Assert-Color '::/0  fe80::1  25' '::' 36
$state = @{}
$null = Format-KillerScanNetworkLine 'LocalAddress LocalPort RemoteAddress RemotePort State' $state
Assert-Color '0.0.0.0      443       0.0.0.0       0          Listen' '443' 32 $state
$original = $esc + '[35mAlready colored' + $esc + '[0m'
if ((Format-KillerScanNetworkLine $original) -cne $original) { throw 'Existing ANSI modified' }

# No traffic leaves the machine. Exercise arguments, exit codes, tags and assignment/pipes.
foreach ($name in @('ping','tracert','pathping','nslookup','ipconfig','arp','netstat','route')) {
    foreach ($alias in @($name, ($name + '.exe'))) {
        if ((Get-Alias $alias).Definition -ne ('Invoke-KillerScan-' + $name)) { throw ('Missing alias: ' + $alias) }
    }
}
$ping = ping -n 1 127.0.0.1
if ($LASTEXITCODE -ne 0 -or -not ($ping -match 'TTL=')) { throw 'Ping invocation failed' }
if (($ping -join '') -match '\x1b' -or $ping[1] -isnot [string]) { throw 'Assignment is not plain text' }
$piped = $ping | Select-String 'TTL='
if ($piped.Count -ne 1) { throw 'String pipeline changed' }
$connections = Get-NetTCPConnection -State Listen | Select-Object -First 2
if ($connections.Count -lt 1 -or $null -eq $connections[0].LocalPort) { throw 'TCP objects lost' }
if (($connections | ConvertTo-Csv -NoTypeInformation) -match '\x1b') { throw 'CSV contains ANSI' }

# Observe the actual Out-Default dispatch, including native strings and CIM objects.
$global:ksColorCalls = 0
$originalFormatter = ${function:Format-KillerScanNetworkLine}
function global:Format-KillerScanNetworkLine {
    param([string]$Line, [hashtable]$State = @{})
    $global:ksColorCalls++
    & $originalFormatter $Line $State
}
$ping | Out-Default
if ($global:ksColorCalls -lt $ping.Count) { throw 'Native display bypassed colors' }
$global:ksColorCalls = 0
$connections | Out-Default
if ($global:ksColorCalls -lt 1) { throw 'CIM display bypassed colors' }
foreach ($typeName in @('Microsoft.DnsClient.Commands.DnsRecord_A',
    'Microsoft.Management.Infrastructure.CimInstance#ROOT/StandardCimv2/MSFT_NetNeighbor',
    'Microsoft.Management.Infrastructure.CimInstance#ROOT/StandardCimv2/MSFT_NetRoute',
    'TestNetConnectionResult')) {
    $record = [pscustomobject]@{ IPAddress = '192.168.8.1'; RemotePort = 443; TcpTestSucceeded = $true }
    $record.PSTypeNames.Insert(0, $typeName)
    $global:ksColorCalls = 0
    $record | Out-Default
    if ($global:ksColorCalls -lt 1) { throw ('Display bypassed colors: ' + $typeName) }
}
$listener = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Loopback), 0
try {
    $listener.Start()
    $result = Test-NetConnection -ComputerName 127.0.0.1 -Port $listener.LocalEndpoint.Port -WarningAction SilentlyContinue
    if (-not $result.TcpTestSucceeded) { throw 'Loopback connection test failed' }
    $global:ksColorCalls = 0
    $result | Out-Default
    if ($global:ksColorCalls -lt 1) { throw 'Real connection result bypassed colors' }
} finally { $listener.Stop() }
$global:ksColorCalls = 0
[pscustomobject]@{Unrelated = 123} | Out-Default
if ($global:ksColorCalls -ne 0) { throw 'Unrelated output was colored' }

if ($EvidencePath) {
    $sample = @(
        'ping 1.1.1.1',
        'Reply from 1.1.1.1: bytes=32 time=12ms TTL=54',
        'Request timed out.',
        'Packets: Sent = 4, Received = 3, Lost = 1 (25% loss),',
        '', 'tracert 1.1.1.1',
        '  1   12 ms   13 ms   11 ms  router.example [192.168.8.1]',
        '  2    *     *     *     Request timed out.',
        '', 'arp -a', '  192.168.8.1    94-83-c4-a4-78-82    dynamic',
        '', 'netstat -n', ' TCP  192.168.8.10:54321  1.1.1.1:443  ESTABLISHED',
        '', 'Test-NetConnection', ' RemoteAddress    : 1.1.1.1',
        ' RemotePort       : 443', ' TcpTestSucceeded : True'
    )
    $colored = ($sample | ForEach-Object { & $originalFormatter $_ @{} }) -join "`r`n"
    [IO.File]::WriteAllText($EvidencePath, $colored)
}
'PASS: network colors, text retention, native aliases, objects, CSV and display dispatch'
