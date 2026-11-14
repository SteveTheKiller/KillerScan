param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = [IO.Path]::GetFullPath($Executable)
$start.Arguments = '/watch 127.0.0.1 /count 20 /interval 10 /max-seconds 1 /json'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = New-Object Diagnostics.Process
$process.StartInfo = $start
try {
    [void]$process.Start()
    $outputTask = $process.StandardOutput.ReadToEndAsync()
    $errorTask = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(10000)) { $process.Kill(); throw 'The bounded watch did not finish.' }
    $code = $process.ExitCode
    $output = $outputTask.GetAwaiter().GetResult()
    $errorText = $errorTask.GetAwaiter().GetResult()
}
finally { $process.Dispose() }
if ($code -ne 0 -and $code -ne 3) { throw ('Bounded watch failed: ' + $errorText) }
$result = $output | ConvertFrom-Json
if (!$result.stoppedEarly -or @($result.samples).Count -lt 1 -or @($result.samples).Count -ge 20) { throw 'The deadline discarded collected watch samples or did not report the early stop.' }
Write-Output 'PASS watch deadline retains samples and reports stoppedEarly'
