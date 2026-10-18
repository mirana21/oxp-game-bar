$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$env:PSModulePath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules'
. (Join-Path $PSScriptRoot 'startup-task-common.ps1')
$codeRoot = 'C:\Program Files\OXP3 Game Power Bridge'
Assert-StartupAdmin
$maintenanceLock = [Threading.Mutex]::new($false, 'Local\OXP3.GamePower.StartupMaintenance')
try { $lockOwned = $maintenanceLock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $lockOwned = $true }
if (-not $lockOwned) { $maintenanceLock.Dispose(); exit 0 }
try {
    # Removal deletes the receipt first; queued recovery must stay inert.
    if (-not (Test-Path -LiteralPath (Join-Path $codeRoot 'startup-receipt.json'))) { exit 0 }
    $startup = Get-Oxp3StartupInstallation $codeRoot ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    Set-Oxp3StartupInstallation $startup $codeRoot $PSScriptRoot
    $native = @(Get-CimInstance Win32_Process -Filter "Name='OneXConsole.exe'")
    if ($native.Count -eq 0) { Start-ScheduledTask -TaskName OneXConsole; exit 0 }
    $parents = @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($codeRoot) -and $_.CommandLine -match 'launch-native\.cjs' })
    if ($parents.Count -eq 0) {
        $instance = $native | Sort-Object CreationDate | Select-Object -First 1
        $local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
        $marker = Join-Path $local 'Packages\OXP3.PowerWidget_pv4gfha69c7qe\LocalCache\Local\OXP3PowerWidget\pending-power-startup.json'
        Assert-Oxp3AuthPath $marker
        if (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($marker))) {
            $pending = @{ nativeProcessId = [int]$instance.ProcessId; nativeStartedUtc = $instance.CreationDate.ToUniversalTime().ToString('o') }
            [IO.File]::WriteAllText($marker, ($pending | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
            Protect-Oxp3TokenPath $marker $false
        }
    }
} catch {
    # Preserve the vendor app and unrecognised task changes. Never restart or
    # terminate a live native process from unattended update recovery.
    try { [IO.File]::AppendAllText((Join-Path $codeRoot 'startup-recovery.log'), [DateTime]::UtcNow.ToString('o') + ' ' + $_.Exception.Message + [Environment]::NewLine) } catch { }
    exit 1
} finally { $maintenanceLock.ReleaseMutex(); $maintenanceLock.Dispose() }
