<#
.SYNOPSIS
    Shows whether modbus_tcp_server.py is running and listening.
.EXAMPLE
    .\Status_ModbusTCP.ps1
#>
param(
    [int]$Port = 5020
)

. (Join-Path $PSScriptRoot 'ModbusTCP_Common.ps1')

$null = Show-ServerStatus $Port
Write-Host "Log           : $LogFile"
