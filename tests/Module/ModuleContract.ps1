#Requires -Version 5.1
<#
    Contract checks for the bundled KillerScan PowerShell module. Run under both
    Windows PowerShell 5.1 and PowerShell 7:

        powershell -NoProfile -File tests\Module\ModuleContract.ps1 -Exe bin\Debug\net48\KillerScan.exe
        pwsh -NoProfile -File tests\Module\ModuleContract.ps1 -Exe bin\Debug\net48\KillerScan.exe

    The real executable covers success and usage errors with fabricated /demo data. A
    stand-in script covers runtime failures and exit codes the real app cannot produce
    on demand without touching a network.
#>
param([Parameter(Mandatory = $true)][string]$Exe)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$realExe = (Resolve-Path -LiteralPath $Exe).Path
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('ks-module-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$passed = 0

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:passed++
}

function Get-Failure([scriptblock]$Block) {
    try { & $Block | Out-Null; return $null } catch { return $_ }
}

# Writes a stand-in executable that prints fixed stdout and stderr and exits with a code.
function New-FakeExe([string]$Name, [string]$Stdout, [string]$Stderr, [int]$Code) {
    $path = Join-Path $work "$Name.cmd"
    $lines = @('@echo off')
    if ($Stdout) { $lines += "echo $Stdout" }
    if ($Stderr) { $lines += "echo $Stderr 1>&2" }
    $lines += "exit /b $Code"
    Set-Content -LiteralPath $path -Value $lines -Encoding Ascii
    $path
}

try {
    Import-Module (Join-Path $root 'Modules\KillerScan\KillerScan.psd1') -Force

    $env:KS_EXE = $realExe
    $devices = @(scan -Demo)
    Require ($devices.Count -gt 0) 'scan -Demo returns device objects from the real executable.'
    Require ($devices[0].PSObject.TypeNames[0] -eq 'KillerScan.Device') 'Devices carry the KillerScan.Device type.'
    Require (-not [string]::IsNullOrEmpty($devices[0].Ip)) 'Devices carry their IP address.'
    Require (-not [string]::IsNullOrEmpty($devices[0].Recognition.Confidence)) 'Devices carry their recognition evidence.'

    $env:KS_EXE = New-FakeExe 'evidence' '[{"IpAddress":"192.0.2.9","OpenPorts":[443],"Recognition":{"Type":"Printer","Confidence":"High"},"Access":[{"Protocol":"https","Port":443,"Evidence":"HttpResponse","HttpStatus":401,"Login":"Requested"}]},{"IpAddress":"192.0.2.10","OpenPorts":[]}]' '' 0
    $evidence = @(scan 192.0.2.0/30)
    Require ($evidence[0].Recognition.Confidence -eq 'High' -and $evidence[0].Access.Count -eq 1 -and $evidence[0].Access[0].Login -eq 'Requested') 'Devices carry their access endpoints.'
    Require ($evidence[1].Access.Count -eq 0 -and $null -eq $evidence[1].Recognition) 'A device without evidence has an empty Access list and no Recognition.'
    $env:KS_EXE = $realExe

    $usage = Get-Failure { probe 'not-an-address' }
    Require ($null -ne $usage) 'A usage error from the real executable is a terminating error.'
    Require ($usage.Exception.Data['ExitCode'] -eq 2) 'Usage errors keep exit code 2.'
    Require ($usage.Exception.Message -match 'IPv4') 'Usage errors keep the diagnostic text.'

    $env:KS_EXE = New-FakeExe 'runtime' '' 'Error: Operation timed out after 5 seconds.' 1
    $runtime = Get-Failure { scan 192.0.2.0/30 }
    Require ($null -ne $runtime) 'A runtime failure (exit 1) is not reported as an empty result.'
    Require ($runtime.Exception.Data['ExitCode'] -eq 1) 'Runtime failures keep exit code 1.'
    Require ($runtime.Exception.Message -match 'timed out') 'Runtime failures keep the diagnostic text.'

    $env:KS_EXE = New-FakeExe 'empty' '[]' 'scanning 192.0.2.0/30...' 0
    $empty = Get-Failure { $script:emptyResult = @(scan 192.0.2.0/30) }
    Require ($null -eq $empty -and $script:emptyResult.Count -eq 0) 'A successful empty scan is an empty result, not an error.'

    $env:KS_EXE = New-FakeExe 'unknown-vendor' 'Unknown' '' 3
    Require ($null -eq (vendor 00:11:22:33:44:55)) 'Exit 3 from vendor is the unknown-vendor answer.'

    $env:KS_EXE = New-FakeExe 'listed' '[{"id":"a1","name":"Acme Dental","customer":"Acme","lastTargets":"10.1.0.0/24","lastAdapter":""}]' '' 0
    $sites = @(locations)
    Require ($sites.Count -eq 1 -and $sites[0].Id -eq 'a1' -and $sites[0].Name -eq 'Acme Dental' -and $sites[0].LastTargets -eq '10.1.0.0/24') 'locations returns each Location as an object.'
    $env:KS_EXE = New-FakeExe 'unlisted' '[]' '' 0
    Require (@(locations).Count -eq 0) 'No Locations is an empty result, not an error.'
    $env:KS_EXE = New-FakeExe 'unreadable' '' 'Error: Locations are unavailable' 1
    $unavailable = Get-Failure { locations }
    Require ($null -ne $unavailable -and $unavailable.Exception.Data['ExitCode'] -eq 1) 'An unreadable catalog is an error, not an empty list.'
    $env:KS_EXE = $realExe
    Require ($null -eq (Get-Failure { locations })) 'The real app lists Locations.'

    $located = Join-Path $work 'located.cmd'
    Set-Content -LiteralPath $located -Encoding Ascii -Value @(
        '@echo off',
        'echo %* | findstr /C:"/location" >nul || (echo []& exit /b 0)',
        'echo %* | findstr /C:"Acme Dental" >nul || (echo Error: wrong Location 1>&2& exit /b 2)',
        'echo [{"IpAddress":"192.0.2.7","OpenPorts":[]}]')
    $env:KS_EXE = $located
    $site = @(scan 192.0.2.0/30 -Location 'Acme Dental')
    Require ($site.Count -eq 1 -and $site[0].Ip -eq '192.0.2.7') '-Location passes the Location name, spaces included, to the app.'
    Require (@(scan 192.0.2.0/30).Count -eq 0) 'Without -Location no /location is passed.'

    $skipped = Join-Path $work 'skipped.cmd'
    Set-Content -LiteralPath $skipped -Encoding Ascii -Value @(
        '@echo off',
        'echo %* | findstr /C:"/skip dns,http" >nul || (echo Error: skip not passed 1>&2& exit /b 2)',
        'echo []')
    $env:KS_EXE = $skipped
    Require ($null -eq (Get-Failure { scan 192.0.2.0/30 -Skip dns, http })) '-Skip passes the checks to turn off as /skip.'
    Require ($null -eq (Get-Failure { probe 192.0.2.1 -Skip dns, http })) 'probe -Skip passes them too.'
    Require ($null -ne (Get-Failure { scan 192.0.2.0/30 -Skip bogus })) '-Skip refuses names Scan settings does not have.'
    $adapted = Join-Path $work 'adapted.cmd'
    Set-Content -LiteralPath $adapted -Encoding Ascii -Value @(
        '@echo off',
        'echo %* | findstr /C:"/adapter" >nul || (echo Error: adapter not passed 1>&2& exit /b 2)',
        'echo %* | findstr /C:"Ethernet 2" >nul || (echo Error: adapter name lost 1>&2& exit /b 2)',
        'echo []')
    $env:KS_EXE = $adapted
    Require ($null -eq (Get-Failure { scan 192.0.2.0/30 -Adapter 'Ethernet 2' })) '-Adapter passes the adapter name, spaces included.'
    Require ($null -eq (Get-Failure { probe 192.0.2.1 -Adapter 'Ethernet 2' })) 'probe -Adapter passes it too.'
    $env:KS_EXE = $realExe
    $demoAdapter = Get-Failure { scan -Demo -Adapter 'Ethernet' }
    Require ($null -ne $demoAdapter -and $demoAdapter.Exception.Data['ExitCode'] -eq 2) 'The real app refuses -Adapter with -Demo as a usage error.'

    # The real app with /demo sends nothing; an unknown /skip name is still a usage error.
    $real = Start-Process -FilePath $realExe -ArgumentList '/scan', '/demo', '/skip', 'bogus', '/json' -Wait -PassThru -WindowStyle Hidden
    Require ($real.ExitCode -eq 2) 'The real app refuses an unknown /skip name with exit code 2.'
    $env:KS_EXE = $realExe

    $env:KS_EXE = $realExe
    $demoLocation = Get-Failure { scan -Demo -Location 'Anywhere' }
    Require ($null -ne $demoLocation -and $demoLocation.Exception.Data['ExitCode'] -eq 2) 'The real app refuses -Location with -Demo as a usage error.'

    # Arguments survive the Windows command-line split exactly, trailing backslashes and quotes included.
    if (-not ('KsArgv' -as [type])) {
        Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class KsArgv {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CommandLineToArgvW(string line, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    public static string[] Split(string line) {
        int n; IntPtr a = CommandLineToArgvW(line, out n);
        try { var r = new string[n]; for (int i = 0; i < n; i++) r[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(a, i * IntPtr.Size)); return r; }
        finally { LocalFree(a); }
    }
}
'@
    }
    $module = Get-Module KillerScan
    $samples = @('plain', 'two words', 'C:\some folder\', 'say "hi"', 'x\"y z', 'end\\', '')
    foreach ($sample in $samples) {
        $quoted = & $module { param($v) ConvertTo-KsArgument $v } $sample
        $split = [KsArgv]::Split('app.exe ' + $quoted)
        Require ($split.Count -eq 2 -and $split[1] -ceq $sample) "Argument quoting round-trips [$sample] as $quoted."
    }

    $env:KS_EXE = New-FakeExe 'noisy' '[{"IpAddress":"192.0.2.1","OpenPorts":[]}]' 'Progress: 100%%' 0
    $noisy = @(scan 192.0.2.1)
    Require ($noisy.Count -eq 1 -and $noisy[0].Ip -eq '192.0.2.1') 'Stderr status lines never mix into parsed stdout.'

    Write-Output "PASS: $passed module contract checks on PowerShell $($PSVersionTable.PSVersion)."
}
finally {
    Remove-Module KillerScan -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
