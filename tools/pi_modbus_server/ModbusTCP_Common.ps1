# Shared by Start_ModbusTCP.ps1 / Stop_ModbusTCP.ps1 / Status_ModbusTCP.ps1.
# Windows PowerShell 5.1 compatible; ASCII only (5.1 reads BOM-less scripts as ANSI).

$ServerDir    = $PSScriptRoot
$VenvPython   = Join-Path $ServerDir '.venv\Scripts\python.exe'
$ServerScript = 'modbus_tcp_server.py'
$LogFile      = Join-Path $ServerDir 'modbus_tcp_server.log'
$ErrorLogFile = Join-Path $ServerDir 'modbus_tcp_server_error.log'

function Get-ServerProcesses {
    # A server is "this folder's .venv python running modbus_tcp_server.py". On Windows the venv python.exe is a
    # small launcher that starts the real interpreter as a child (same command line, but the base Python's exe),
    # so both the launcher and its child belong to the server. Other python.exe processes are never matched.
    $all = @(Get-CimInstance Win32_Process -Filter "Name='python.exe'")
    $venv = [IO.Path]::GetFullPath($VenvPython)
    $launchers = @($all | Where-Object {
        $_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath) -ieq $venv) -and ($_.CommandLine -like "*$ServerScript*")
    })
    $launcherIds = @($launchers | ForEach-Object { $_.ProcessId })
    $children = @($all | Where-Object { ($launcherIds -contains $_.ParentProcessId) -and ($_.CommandLine -like "*$ServerScript*") })
    return @($launchers) + @($children)
}

function Get-ListenerPids([int]$Port) {
    return @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
}

function Write-ServerLog([string]$Message) {
    $line = '{0} INFO    {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss,fff'), $Message
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
}

function Show-ServerStatus([int]$Port) {
    $procs = Get-ServerProcesses
    $serverIds = @($procs | ForEach-Object { $_.ProcessId })
    $listeners = Get-ListenerPids $Port
    $serverListener = @($listeners | Where-Object { $serverIds -contains $_ })

    if ($serverIds.Count -eq 0) {
        $status = 'Stopped'; $pidText = '-'
    } elseif ($serverListener.Count -gt 0) {
        $status = 'Running'
        $launcher = @($procs | Where-Object { $serverListener -notcontains $_.ProcessId } | ForEach-Object { $_.ProcessId })
        $pidText = ($serverListener -join ', ')
        if ($launcher.Count -gt 0) { $pidText += '  (launcher ' + ($launcher -join ', ') + ')' }
    } else {
        $status = 'Running (not listening)'; $pidText = ($serverIds -join ', ')
    }

    Write-Host ('Server Status : {0}' -f $status)
    Write-Host ('PID           : {0}' -f $pidText)
    Write-Host ('Port          : {0}' -f $Port)
    Write-Host ('Listen        : {0}' -f $(if ($serverListener.Count -gt 0) { 'Yes' } else { 'No' }))

    $others = @($listeners | Where-Object { $serverIds -notcontains $_ })
    foreach ($id in $others) {
        $name = (Get-Process -Id $id -ErrorAction SilentlyContinue).ProcessName
        Write-Host ('Note          : port {0} is used by another process: PID {1} ({2})' -f $Port, $id, $name)
    }
    return [pscustomobject]@{ Status = $status; ServerPids = $serverIds; Listening = ($serverListener.Count -gt 0) }
}
