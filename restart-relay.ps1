param([ValidateRange(0,3600)][int]$DelaySeconds = 0)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$assembly = [IO.Path]::GetFullPath((Join-Path $root 'bin\Debug\net8.0\CodexRelay.dll'))
$healthFile = Join-Path $root 'logs\health.txt'
$logFile = Join-Path $root 'logs\restart-relay.log'
$taskName = 'CodexRelay'

function Write-RestartLog([string]$message) {
    Add-Content -LiteralPath $logFile -Encoding UTF8 -Value ((Get-Date -Format o) + ' ' + $message)
}
function Get-RelayProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
        Where-Object {
            $command = $_.CommandLine
            $command -and $command.IndexOf($assembly, [StringComparison]::OrdinalIgnoreCase) -ge 0
        })
}

if (!(Test-Path -LiteralPath $assembly)) { throw "Relay DLL not found: $assembly" }
if (!(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) {
    throw "Scheduled task not found: $taskName"
}
New-Item -ItemType Directory -Path (Split-Path -Parent $logFile) -Force | Out-Null
Write-RestartLog "Restart requested; delay $DelaySeconds seconds."
if ($DelaySeconds -gt 0) { Start-Sleep -Seconds $DelaySeconds }
$oldPid = if (Test-Path -LiteralPath $healthFile) {
    (Get-Content -LiteralPath $healthFile -Raw).Trim().Split('|')[0]
} else { '' }
$disabled = $false
try {
    Disable-ScheduledTask -TaskName $taskName | Out-Null
    $disabled = $true
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $processes = @(Get-RelayProcesses)
        if ($processes.Count -eq 0) { break }
        foreach ($process in $processes) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Seconds 1
    }
    if (@(Get-RelayProcesses).Count -ne 0) { throw 'Old Relay process is still running.' }
    Enable-ScheduledTask -TaskName $taskName | Out-Null
    $disabled = $false
    Start-ScheduledTask -TaskName $taskName
    for ($attempt = 0; $attempt -lt 45; $attempt++) {
        Start-Sleep -Seconds 2
        if (!(Test-Path -LiteralPath $healthFile)) { continue }
        $health = (Get-Content -LiteralPath $healthFile -Raw).Trim().Split('|')
        if ($health.Length -ge 2 -and $health[0] -ne $oldPid -and $health[1] -eq 'ready' -and
            @(Get-RelayProcesses | Where-Object { $_.ProcessId.ToString() -eq $health[0] }).Count -gt 0) {
            Write-RestartLog "Ready, PID $($health[0])."
            Write-Output "Relay ready, PID $($health[0])"
            exit 0
        }
    }
    throw 'Relay did not become ready within 90 seconds.'
}
catch {
    Write-RestartLog ('Restart failed: ' + $_.Exception.Message)
    throw
}
finally {
    if ($disabled) {
        Enable-ScheduledTask -TaskName $taskName | Out-Null
        if (@(Get-RelayProcesses).Count -eq 0) {
            Start-ScheduledTask -TaskName $taskName
            Write-RestartLog 'Recovery start requested.'
        }
    }
}