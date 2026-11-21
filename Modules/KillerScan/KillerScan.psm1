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

# Device text comes from the network: names, page titles, banners, certificate subjects, the
# evidence built from them. Control characters in it (ESC, CSI, OSC, C1, line breaks) would act
# on the console instead of printing, so each becomes U+FFFD before anything shows it. Tabs stay.
function Remove-KsControl {
    param($Value)
    if ($Value -is [string]) { return [regex]::Replace($Value, '[\x00-\x08\x0A-\x1F\x7F-\x9F]', [string][char]0xFFFD) }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Value.PSObject.Properties) { $property.Value = Remove-KsControl $property.Value }
        return $Value
    }
    if ($Value -is [System.Collections.IList]) { return , @(foreach ($item in $Value) { Remove-KsControl $item }) }
    return $Value
}

function ConvertTo-KsDevice {
    param($Device)

    $Device = Remove-KsControl $Device

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
        # Asked for as JSON so the answer never depends on the wording of the text output.
        # Exit 3 is an unknown maker, which comes back as $null rather than a word you
        # would have to test for.
        $text = Invoke-KsText @('/vendor', $Mac, '/json') -Answer 0, 3
        $found = $text | ConvertFrom-Json
        if ($found.known) { Remove-KsControl $found.vendor } else { $null }
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

    # A field the app could not read comes back as $null rather than an empty string.
    $net = @(Invoke-KsJson @('/network', '/json'))[0]
    function Get-KsValue($Value) { if ([string]::IsNullOrEmpty($Value)) { $null } else { Remove-KsControl $Value } }

    [pscustomobject]@{
        Interface = Get-KsValue $net.interface
        Adapter   = Get-KsValue $net.adapter
        Address   = Get-KsValue $net.localIp
        Subnet    = Get-KsValue $net.subnet
        Gateway   = Get-KsValue $net.gateway
        Dns       = Get-KsValue $net.dns
        Wireless  = [bool]$net.wireless
        # Bits per second, as Windows reports it. $null when Windows reported none.
        LinkSpeed = if ($net.linkSpeed -gt 0) { [long]$net.linkSpeed } else { $null }
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

<#
.SYNOPSIS
    Saved scans from History, newest first. Reads the history files and changes nothing.
.DESCRIPTION
    With no -Run, lists the saved runs. With -Run, returns that run's devices, or with
    -Compare, what changed since the run before it. -Run takes the Number or the
    ScannedAt a listing gave; ScannedAt stays the same when newer scans are saved.
.EXAMPLE
    scanhistory
.EXAMPLE
    scanhistory -Run 1 | Where-Object Ports -contains 3389
.EXAMPLE
    scanhistory -Location 'Acme Dental' -Run 1 -Compare
#>
function scanhistory {
    [CmdletBinding()]
    param(
        # A number from the listing (1 is the newest) or a run's ScannedAt.
        [Parameter(Position = 0)]
        [object]$Run,

        # Keep to one saved Location, by name or ID.
        [string]$Location,

        # What changed since the run before this one: Added, Removed and Changed devices.
        [switch]$Compare
    )

    if ($Compare -and -not $Run) { throw '-Compare needs -Run.' }
    # The app matches a ScannedAt by the moment it names, so a date goes back in its round-trip
    # form. PowerShell 7 reads the listing's dates as DateTime, and its own text form would not parse.
    if ($Run -is [DateTimeOffset] -or $Run -is [DateTime]) {
        $Run = ([DateTimeOffset]$Run).ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    }
    $arguments = @('/history')
    if ($Run)      { $arguments += [string]$Run }
    if ($Location) { $arguments += @('/location', $Location) }
    if ($Compare)  { $arguments += '/compare' }
    $arguments += '/json'

    if (-not $Run) {
        foreach ($item in (Invoke-KsJson $arguments)) {
            $item = Remove-KsControl $item
            [pscustomobject]@{
                Number     = $item.number
                # A DateTimeOffset on both editions: Windows PowerShell leaves the JSON text a
                # string, PowerShell 7 turns it into a local DateTime.
                ScannedAt  = if ($item.scannedAt -is [DateTime]) { [DateTimeOffset]$item.scannedAt }
                             else { [DateTimeOffset]::Parse($item.scannedAt, [System.Globalization.CultureInfo]::InvariantCulture) }
                Location   = $item.location
                Target     = $item.target
                Name       = $item.name
                Devices    = $item.devices
                Completion = $item.completion
                Adapter    = $item.adapter
            }
        }
        return
    }

    $found = @(Invoke-KsJson $arguments)[0]
    if (-not $Compare) {
        foreach ($device in @($found.devices)) { if ($null -ne $device) { ConvertTo-KsDevice $device } }
        return
    }
    [pscustomobject]@{
        Baseline = if ($null -eq $found.baseline) { $null }
                   elseif ($found.baseline.scannedAt -is [DateTime]) { [DateTimeOffset]$found.baseline.scannedAt }
                   else { [DateTimeOffset]::Parse($found.baseline.scannedAt, [System.Globalization.CultureInfo]::InvariantCulture) }
        Added    = @(foreach ($device in @($found.added)) { if ($null -ne $device) { ConvertTo-KsDevice $device } })
        Removed  = @(foreach ($device in @($found.removed)) { if ($null -ne $device) { ConvertTo-KsDevice $device } })
        Changed  = @(foreach ($device in @($found.changed)) { if ($null -ne $device) { ConvertTo-KsDevice $device } })
    }
}

Export-ModuleMember -Function scan, probe, vendor, netinfo, locations, scanhistory
