<#
.SYNOPSIS
    Stops only modbus_tcp_server.py (this folder's .venv python) and waits until the port is released.
.DESCRIPTION
    Processes are selected by executable + command line (see ModbusTCP_Common.ps1); other python.exe
    processes are never touched. Never uses Stop-Process -Name python or taskkill /IM python.exe.
.EXAMPLE
    .\Stop_ModbusTCP.ps1
#>
param(
    [int]$Port = 5020,
    [int]$WaitSeconds = 10
)

. (Join-Path $PSScriptRoot 'ModbusTCP_Common.ps1')

$procs = Get-ServerProcesses
if ($procs.Count -eq 0) {
    Write-Host 'modbus_tcp_server.py is not running.'
    $null = Show-ServerStatus $Port
    exit 0
}

$ids = @($procs | ForEach-Object { $_.ProcessId })
Write-Host "Stopping modbus_tcp_server.py: PID $($ids -join ', ')"
foreach ($id in $ids) {
    Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
}

$deadline = (Get-Date).AddSeconds($WaitSeconds)
do {
    Start-Sleep -Milliseconds 300
    $alive = @($ids | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
    $stillListening = @(Get-ListenerPids $Port | Where-Object { $ids -contains $_ })
} while (($alive.Count -gt 0 -or $stillListening.Count -gt 0) -and (Get-Date) -lt $deadline)

# Written after the processes are gone, so the server no longer holds the log file.
Write-ServerLog "Stop_ModbusTCP.ps1: Server Stop (PID $($ids -join ', '))"

$result = Show-ServerStatus $Port
if ($alive.Count -gt 0 -or $result.Listening) {
    Write-Host "ERROR: server still running or port $Port still listening after $WaitSeconds s." -ForegroundColor Red
    exit 1
}
Write-Host 'Modbus TCP server stopped.' -ForegroundColor Green
exit 0
