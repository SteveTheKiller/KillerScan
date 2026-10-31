#Requires -Version 5.1
<#
    KillerScan

    Short commands for the KillerScan terminal. Each one calls the same command line
    the app installs, asks it for JSON, and hands back objects rather than text, so a
    scan can be filtered, sorted, exported and piped like anything else in PowerShell.

    Unpacked from inside KillerScan.exe on first use, so it is present on a machine
    where nothing can be installed. Nothing here reaches the network itself: every
    command is the app doing the work.
#>

# Set by KillerScan when it starts a terminal. Falls back to PATH, which is where an
# installed copy puts itself, so the module still works in a shell opened elsewhere.
function Get-KsExe {
    if ($env:KS_EXE -and (Test-Path -LiteralPath $env:KS_EXE)) { return $env:KS_EXE }
    $found = Get-Command 'killerscan.exe' -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    throw 'KillerScan was not found. Open a terminal from KillerScan, or install it so killerscan.exe is on PATH.'
}

# Quotes one argument the way Windows programs split their command line: backslashes are
# literal unless they come before a quote, so a run of them before a quote, or before the
# closing quote, is doubled. Without that, a path ending in a backslash would swallow the
# closing quote and run into the next argument.
function ConvertTo-KsArgument {
    param([AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $quoted = New-Object System.Text.StringBuilder
    [void]$quoted.Append('"')
    $slashes = 0
    foreach ($c in $Value.ToCharArray()) {
        if ($c -eq '\') { $slashes++; continue }
        if ($c -eq '"') { [void]$quoted.Append('\', $slashes * 2 + 1) }
        elseif ($slashes -gt 0) { [void]$quoted.Append('\', $slashes) }
        [void]$quoted.Append($c)
        $slashes = 0
    }
    if ($slashes -gt 0) { [void]$quoted.Append('\', $slashes * 2) }
    $quoted.Append('"').ToString()
}

# Runs the app with stdout and stderr kept apart: stdout is the data, stderr is status
# and diagnostics. Exit codes are 0 success, 1 failure, cancel or timeout, 2 bad usage
# and 3 empty or not found. Codes listed in -Answer are results rather than failures
# (3 from /vendor means an unknown maker); anything else becomes a terminating error
# that keeps the exit code in Exception.Data['ExitCode'] and the app's own message.
function Invoke-KsText {
    param([string[]]$Arguments, [int[]]$Answer = @(0))

    $exe = Get-KsExe
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.Arguments = ($Arguments | ForEach-Object { ConvertTo-KsArgument $_ }) -join ' '
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [System.Text.Encoding]::UTF8

    $process = [System.Diagnostics.Process]::Start($info)
    try {
        # Both streams are drained together so neither can fill and stall the app.
        $errorTask = $process.StandardError.ReadToEndAsync()
        $stdout = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        $stderr = $errorTask.GetAwaiter().GetResult().Trim()
        $code = $process.ExitCode
    }
    finally { $process.Dispose() }

    if ($Answer -notcontains $code) {
        $message = if ($stderr) { $stderr } else { "KillerScan exited with code $code." }
        $exception = New-Object System.InvalidOperationException $message
        $exception.Data['ExitCode'] = $code
        throw (New-Object System.Management.Automation.ErrorRecord $exception, "KillerScanExit$code",
            ([System.Management.Automation.ErrorCategory]::NotSpecified), $Arguments[0])
    }
    if ($stderr) { Write-Verbose $stderr }
    $stdout.Trim()
}

function Invoke-KsJson {
    param([string[]]$Arguments)

    $text = Invoke-KsText $Arguments
    if ([string]::IsNullOrWhiteSpace($text)) { return @() }

    try { $parsed = $text | ConvertFrom-Json } catch { throw $text }
    if ($null -eq $parsed) { return @() }
    return @($parsed)
}

function ConvertTo-KsDevice {
    param($Device)

    # Flattened and renamed for the console: short property names, ports as an int
    # array you can test with -contains, and the fingerprint fields kept but out of
    # the default view so a bare 'scan' stays readable.
    $out = [pscustomobject]@{
        Ip       = $Device.IpAddress
        Hostname = $Device.Hostname
        Mac      = $Device.MacAddress
        Vendor   = $Device.Vendor
        Type     = $Device.DeviceType
        Ports    = @($Device.OpenPorts)
        Ttl      = $Device.Ttl
        Http     = $Device.HttpTitle
        Server   = $Device.HttpServer
        Ssh      = $Device.SshBanner
        Tls      = $Device.TlsSubject
        Smb      = $Device.SmbOs
        Snmp     = $Device.SnmpDescr
        Netbios  = $Device.NetbiosName
        Mdns     = @($Device.MdnsServices)
        Ssdp     = $Device.SsdpServer
        # Why the device has its type (signals, alternatives, confidence), and each endpoint
        # with what it answered. Access is an empty array, not $null, when none was recorded.
        Recognition = $Device.Recognition
        Access   = [object[]]@($Device.Access | Where-Object { $null -ne $_ })
    }
    $out.PSObject.TypeNames.Insert(0, 'KillerScan.Device')
    $out
}

<#
.SYNOPSIS
    Scan the network and return one object per device.
.EXAMPLE
    scan
.EXAMPLE
    scan 192.168.8.0/24 -Location 'Acme Dental'
.EXAMPLE
    scan | Where-Object Type -eq 'Printer' | Format-Table Ip, Hostname, Vendor
#>
function scan {
    [CmdletBinding()]
    param(
        # CIDR blocks, single hosts or ranges. Omit to use the active network.
        [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
        [string[]]$Target,

        # Discovery only: skip fingerprinting and the full port pass. Without it the
        # command line already does the thorough scan, so there is no deep switch.
        [switch]$Quick,

        # Seconds to allow the whole scan.
        [int]$Timeout,

        # A fabricated network for screenshots and tests. Nothing is scanned.
        [switch]$Demo,

        # A saved Location, by name or ID: its device names apply, and the scan is recorded
        # in its history. Without it nothing saved is read or written.
        [string]$Location,

        # Checks to turn off, the same ones Scan settings offers.
        [ValidateSet('neighbor-cache', 'arp', 'icmp', 'tcp-liveness', 'dns', 'mdns', 'ssdp',
            'ports', 'http', 'tls', 'ssh', 'snmp', 'netbios')]
        [string[]]$Skip,

        # Send every probe from this adapter, by Windows name or address. Only its own
        # network can be scanned while bound.
        [string]$Adapter
    )

    $arguments = @('/scan')
    if ($Target)   { $arguments += ($Target -join ',') }
    if ($Quick)    { $arguments += '/quick' }
    if ($Timeout)  { $arguments += @('/timeout', $Timeout) }
    if ($Demo)     { $arguments += '/demo' }
    if ($Location) { $arguments += @('/location', $Location) }
    if ($Skip)     { $arguments += @('/skip', ($Skip -join ',')) }
    if ($Adapter)  { $arguments += @('/adapter', $Adapter) }
    # No /quiet: it suppresses stdout, which is where the JSON is. Status goes to stderr.
    $arguments += '/json'

    Invoke-KsJson $arguments | ForEach-Object { ConvertTo-KsDevice $_ }
}

<#
.SYNOPSIS
    Probe one host in depth and return it as an object.
.EXAMPLE
    probe 192.168.8.20
#>
function probe {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0, Mandatory = $true)]
        [string]$Target,

        [int]$Timeout,

        # A saved Location whose device names and types apply to the result.
        [string]$Location,

        # Checks to turn off, the same ones Scan settings offers.
        [ValidateSet('neighbor-cache', 'arp', 'icmp', 'tcp-liveness', 'dns', 'mdns', 'ssdp',
            'ports', 'http', 'tls', 'ssh', 'snmp', 'netbios')]
        [string[]]$Skip,

        # Send every probe from this adapter, by Windows name or address.
        [string]$Adapter
    )

    $arguments = @('/probe', $Target)
    if ($Timeout)  { $arguments += @('/timeout', $Timeout) }
    if ($Location) { $arguments += @('/location', $Location) }
    if ($Skip)     { $arguments += @('/skip', ($Skip -join ',')) }
    if ($Adapter)  { $arguments += @('/adapter', $Adapter) }
    $arguments += '/json'

    Invoke-KsJson $arguments | ForEach-Object { ConvertTo-KsDevice $_ }
}

<#
.SYNOPSIS
    Look up the maker of a MAC address, offline, from the database inside the app.
.EXAMPLE
    vendor 94:83:C4:A4:78:82
.EXAMPLE
    scan | ForEach-Object { vendor $_.Mac }
#>
function vendor {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0, Mandatory = $true, ValueFromPipeline = $true)]
        [string]$Mac
    )

    process {
        # /vendor accepts no options at all, so it is called bare and its single line
        # of output is the answer. "Unknown" comes back as $null rather than a word
        # you would have to test for.
        $name = Invoke-KsText @('/vendor', $Mac) -Answer 0, 3
        if ($name -eq 'Unknown') { $null } else { $name }
    }
}

<#
.SYNOPSIS
    The active network: address, subnet, gateway and DNS.
.EXAMPLE
    netinfo
.EXAMPLE
    scan (netinfo).Subnet
#>
function netinfo {
    [CmdletBinding()]
    param()

    # /network takes no options, so its labelled lines are parsed into an object here
    # rather than asked for as JSON. A field the app could not read comes back as $null
    # instead of the dash it prints.
    $map = @{}
    foreach ($line in (Invoke-KsText @('/network')) -split "`r?`n") {
        if ($line -match '^(INTERFACE|LOCAL IP|SUBNET|GATEWAY|DNS)\s+(.+)$') {
            $value = $Matches[2].Trim()
            $map[$Matches[1]] = if ($value -eq '-') { $null } else { $value }
        }
    }

    [pscustomobject]@{
        Interface = $map['INTERFACE']
        Address   = $map['LOCAL IP']
        Subnet    = $map['SUBNET']
        Gateway   = $map['GATEWAY']
        Dns       = $map['DNS']
    }
}

<#
.SYNOPSIS
    The saved Locations, with the name or ID that -Location takes. Changes nothing.
.EXAMPLE
    locations
.EXAMPLE
    scan -Location (locations | ? Customer -eq 'Acme').Id
#>
function locations {
    [CmdletBinding()]
    param()

    foreach ($item in (Invoke-KsJson @('/locations', '/json'))) {
        [pscustomobject]@{
            Id          = $item.id
            Name        = $item.name
            Customer    = $item.customer
            LastTargets = $item.lastTargets
            LastAdapter = $item.lastAdapter
        }
    }
}

Export-ModuleMember -Function scan, probe, vendor, netinfo, locations
