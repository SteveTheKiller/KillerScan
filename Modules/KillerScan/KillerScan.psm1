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

function Invoke-KsText {
    param([string[]]$Arguments)

    $exe = Get-KsExe
    $raw = & $exe @Arguments 2>&1
    # 2 is bad usage, which is a real error. 1 is "found nothing" and 3 is "unknown
    # vendor", both of which are answers rather than failures.
    if ($LASTEXITCODE -eq 2) { throw (($raw | Out-String).Trim()) }
    ($raw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | Out-String).Trim()
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
    scan 192.168.8.0/24 -Deep
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
        [int]$Timeout
    )

    $arguments = @('/scan')
    if ($Target)  { $arguments += ($Target -join ',') }
    if ($Quick)   { $arguments += '/quick' }
    if ($Timeout) { $arguments += @('/timeout', $Timeout) }
    $arguments += @('/json', '/quiet')

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

        [int]$Timeout
    )

    $arguments = @('/probe', $Target)
    if ($Timeout) { $arguments += @('/timeout', $Timeout) }
    $arguments += @('/json', '/quiet')

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
        $name = Invoke-KsText @('/vendor', $Mac)
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

Export-ModuleMember -Function scan, probe, vendor, netinfo
