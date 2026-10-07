<#
.SYNOPSIS
    Starts modbus_tcp_server.py in the background (hidden, independent of this PowerShell window).
.DESCRIPTION
    Uses this folder's .venv python. Does nothing if the server is already running. Waits until the port is
    really listening before reporting success. Log: modbus_tcp_server.log / modbus_tcp_server_error.log.
.EXAMPLE
    .\Start_ModbusTCP.ps1
    .\Start_ModbusTCP.ps1 -Port 5021
#>
param(
    [int]$Port = 5020,
    [int]$WaitSeconds = 10
)

. (Join-Path $PSScriptRoot 'ModbusTCP_Common.ps1')

if (-not (Test-Path $VenvPython)) {
    Write-Host "ERROR: $VenvPython not found. Create the venv first:" -ForegroundColor Red
    Write-Host '  python -m venv .venv; .\.venv\Scripts\pip install -r requirements.txt'
    exit 1
}

$existing = Get-ServerProcesses
if ($existing.Count -gt 0) {
    Write-Host 'modbus_tcp_server.py is already running; not starting a second one.' -ForegroundColor Yellow
    $null = Show-ServerStatus $Port
    exit 0
}

$busy = Get-ListenerPids $Port
if ($busy.Count -gt 0) {
    Write-Host "ERROR: port $Port is already in use (PID $($busy -join ', ')); not starting." -ForegroundColor Red
    exit 1
}

Write-ServerLog "Start_ModbusTCP.ps1: starting modbus_tcp_server.py on port $Port"
$arguments = @('-u', $ServerScript, '--port', $Port, '--log-file', "`"$LogFile`"", '--error-log-file', "`"$ErrorLogFile`"")
# Start-Process gives the server its own hidden console, so closing this PowerShell does not stop it.
$launcher = Start-Process -FilePath $VenvPython -ArgumentList $arguments -WorkingDirectory $ServerDir -WindowStyle Hidden -PassThru

# Success means "listening", not just "process exists".
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$listening = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 300
    $serverIds = @(Get-ServerProcesses | ForEach-Object { $_.ProcessId })
    if (@(Get-ListenerPids $Port | Where-Object { $serverIds -contains $_ }).Count -gt 0) { $listening = $true; break }
    if ($launcher.HasExited) { break }
}

if ($listening) {
    Write-Host 'Modbus TCP server started.' -ForegroundColor Green
    $null = Show-ServerStatus $Port
    Write-Host "Log           : $LogFile"
    exit 0
}

Write-Host "ERROR: server did not start listening on port $Port within $WaitSeconds s." -ForegroundColor Red
if ($launcher.HasExited) { Write-Host "Process exited with code $($launcher.ExitCode)." }
if (Test-Path $ErrorLogFile) {
    Write-Host "Last lines of ${ErrorLogFile}:"
    Get-Content $ErrorLogFile -Tail 15 | ForEach-Object { Write-Host "  $_" }
}
$null = Show-ServerStatus $Port
exit 1
