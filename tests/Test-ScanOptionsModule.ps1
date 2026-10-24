$ErrorActionPreference = 'Stop'
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
    if (($script:CapturedArguments -join '|') -ne '/scan|192.0.2.0/24|/quick|/skip|dns,mdns|/json|/quiet') {
        throw 'scan did not forward the choices'
    }
    probe '192.0.2.10' -Skip ports,tls -Timeout 3
    if (($script:CapturedArguments -join '|') -ne '/probe|192.0.2.10|/skip|ports,tls|/timeout|3|/json|/quiet') {
        throw 'probe did not forward the choices'
    }
    try { scan '192.0.2.10' -Skip typo; throw 'Unknown choice was accepted' }
    catch { if ($_.FullyQualifiedErrorId -notlike 'ParameterArgumentValidationError*') { throw } }
}
Write-Output 'PASS: scan/probe forward choices; unknown names fail before the executable boundary.'
