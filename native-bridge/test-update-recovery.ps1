$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'startup-task-common.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('Oxp3UpdateFixture-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$runtime = 'C:\Program Files\OXP3 Game Power Bridge\Versions\0.1.0.29'
function Protect-StartupCode { param($ItemPath,$Directory) }
function Export-ScheduledTask { param($TaskName,$TaskPath) if ($TaskName -ne 'OneXConsole') { throw 'Wrong task' }; $script:current }
function Register-ScheduledTask { param($TaskName,$TaskPath,$Xml,[switch]$Force) $script:registrations++; $script:current = $Xml }
function Vendor-Xml([string]$Setting = '4') {
    '<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Principals><Principal><UserId>' + $sid + '</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><Priority>' + $Setting + '</Priority></Settings><Actions><Exec><Command>wscript.exe</Command><Arguments>"C:\Program Files\OneXConsole\resources\resources\tools\OneXConsoleStartup.vbs"</Arguments></Exec></Actions></Task>'
}
try {
    $script:registrations = 0
    $script:current = Vendor-Xml
    $fresh = Get-Oxp3StartupInstallation $root $sid
    Set-Oxp3StartupInstallation $fresh $root $runtime
    if ($registrations -ne 1 -or -not $current.Contains($runtime)) { throw 'Fresh startup connection was not installed.' }
    $installed = $current
    $normal = Get-Oxp3StartupInstallation $root $sid
    Set-Oxp3StartupInstallation $normal $root $runtime
    if ($registrations -ne 1 -or $normal.VendorReset) { throw 'Unchanged startup must not register again or create an event loop.' }
    $script:current = Vendor-Xml '6'
    $updatedVendor = $current
    $reset = Get-Oxp3StartupInstallation $root $sid
    if (-not $reset.VendorReset -or $reset.Original -ne $updatedVendor) { throw 'Vendor update was not recognized.' }
    Set-Oxp3StartupInstallation $reset $root $runtime
    if ($registrations -ne 2 -or -not $current.Contains('<Priority>6</Priority>') -or -not $current.Contains($runtime)) { throw 'Repair did not preserve updated vendor settings.' }
    if ((Get-Content (Join-Path $root 'OneXConsole.original.xml') -Raw) -ne $updatedVendor) { throw 'Removal would restore the old vendor definition.' }
    $repaired = Get-Oxp3StartupInstallation $root $sid
    if ($repaired.VendorReset) { throw 'Recovery receipt does not describe the repaired task.' }
    $script:current = (Vendor-Xml).Replace('wscript.exe','different.exe')
    $refused = $false
    try { Get-Oxp3StartupInstallation $root $sid | Out-Null } catch { $refused = $true }
    if (-not $refused -or $registrations -ne 2) { throw 'Unknown startup action must stay untouched.' }
} finally {
    # One generated fixture directory, never a workspace or product path.
    if ([IO.Path]::GetFullPath($root).StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force }
}
Write-Output 'PASS: vendor task replacement repairs once, retains new vendor settings/backup, and refuses unknown startup changes.'
