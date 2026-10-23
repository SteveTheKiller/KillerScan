$ErrorActionPreference = 'Stop'
$previousLocation = $env:KS_LOCATION_ID
$env:KS_LOCATION_ID = $null
Import-Module (Join-Path $PSScriptRoot '../Modules/KillerScan/KillerScan.psm1') -Force
$module = Get-Module KillerScan
& $module {
    # Mock the executable boundary. No child process or network traffic is started.
    Set-Item function:script:Invoke-KsJson -Value {
        param([string[]]$Arguments)
        $script:CapturedArguments = $Arguments
        @()
    }
    scan '192.0.2.0/24' -Quick -Skip dns,mdns
    if (($script:CapturedArguments -join '|') -ne '/scan|192.0.2.0/24|/quick|/skip|dns,mdns|/json') {
        throw 'scan did not forward the choices'
    }
    probe '192.0.2.10' -Skip ports,tls -Timeout 3
    if (($script:CapturedArguments -join '|') -ne '/probe|192.0.2.10|/skip|ports,tls|/timeout|3|/json') {
        throw 'probe did not forward the choices'
    }
    try { scan '192.0.2.10' -Skip typo; throw 'Unknown choice was accepted' }
    catch { if ($_.FullyQualifiedErrorId -notlike 'ParameterArgumentValidationError*') { throw } }
    scan '192.0.2.0/24' -InterfaceId 'fixture-adapter' -SourceIp '192.0.2.20'
    if (($script:CapturedArguments -join '|') -ne '/scan|192.0.2.0/24|/interface|fixture-adapter|/source-ip|192.0.2.20|/json') {
        throw 'scan did not forward its interface and source'
    }
    probe '192.0.2.10' -InterfaceId 'fixture-adapter' -SourceIp '192.0.2.20'
    if (($script:CapturedArguments -join '|') -ne '/probe|192.0.2.10|/interface|fixture-adapter|/source-ip|192.0.2.20|/json') {
        throw 'probe did not forward its interface and source'
    }
}
& $module {
    scan '192.0.2.10' -LocationId '11111111111111111111111111111111'
    if (($script:CapturedArguments -join '|') -ne '/scan|192.0.2.10|/location|11111111111111111111111111111111|/json') { throw 'scan did not forward location identity' }
    probe '192.0.2.10' -LocationId '22222222222222222222222222222222'
    if (($script:CapturedArguments -join '|') -ne '/probe|192.0.2.10|/location|22222222222222222222222222222222|/json') { throw 'probe did not forward location identity' }
    $evidence = [pscustomobject]@{Confidence='Low'; Evidence=@([pscustomobject]@{Source='OUI'; Value='Fixture'})}
    $device = ConvertTo-KsDevice ([pscustomobject]@{LocationId='fixture'; LocationName='Fixture A'; Identification=$evidence; Access=@([pscustomobject]@{Protocol='https'; Port=9187; Authentication='Required'})})
    if ($device.Access[0].Port -ne 9187 -or $device.Access[0].Authentication -ne 'Required') { throw 'device projection lost access metadata' }
    if ($device.LocationId -ne 'fixture' -or $device.LocationName -ne 'Fixture A') { throw 'device projection lost location metadata' }
    if ($device.Identification.Confidence -ne 'Low' -or $device.Identification.Evidence[0].Source -ne 'OUI') { throw 'device projection lost recognition evidence' }
    $env:KS_LOCATION_ID = '33333333333333333333333333333333'
    scan '192.0.2.10'
    if (($script:CapturedArguments -join '|') -ne '/scan|192.0.2.10|/location|33333333333333333333333333333333|/json') { throw 'module did not inherit the GUI terminal location' }
}
$env:KS_LOCATION_ID = $previousLocation
Write-Output 'PASS: scan/probe forward choices; unknown names fail before the executable boundary.'
