$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\native-bridge\startup-task-common.ps1')
$parseTokens = $null; $parseErrors = $null
$source = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'SetupEngine.ps1'), [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors) { throw ($parseErrors.Message -join '; ') }
$remove = $source.Find({ param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -eq "`$Mode -eq 'Remove'" }, $true)
if (-not $remove) { throw 'Production removal branch was not found.' }
# Execute the production branch with isolated installer adapters. Its final
# exit is omitted so assertions can inspect the completed removal transaction.
$body = [scriptblock]::Create(($remove.Clauses[0].Item2.Statements | Where-Object { $_ -isnot [Management.Automation.Language.ExitStatementAst] } | ForEach-Object { $_.Extent.Text }) -join "`n")
$codeRoot = 'C:\RemovalTest\Bridge'
$fixture = @{}
function Get-StartupInstallation { @{ Existing = $fixture.Existing; Current = '<bridge/>'; Original = '<vendor/>' } }
function Export-ScheduledTask { param($TaskName, $TaskPath) if ($fixture.Changed) { '<changed/>' } else { '<bridge/>' } }
function Get-Process { throw 'Removal must not depend on or operate on running processes.' }
function Get-ScheduledTask { @{ State = 4 } }
function Stop-Process { throw 'Removal must not stop processes.' }
function Stop-ScheduledTask { throw 'Removal must not stop tasks.' }
function Unregister-ScheduledTask { throw 'Removal must update the existing task.' }
function Register-ScheduledTask {
    param($TaskName, $TaskPath, $Xml, [switch]$Force)
    if ($TaskName -ne 'OneXConsole' -or $TaskPath -ne '\' -or $Xml -ne '<vendor/>' -or -not $Force) { throw 'Incorrect startup restoration.' }
    if ($fixture.FailRestore) { throw 'Fixture: task restoration failed.' }
    $fixture.Restored = $true
}
function Assert-Oxp3AuthPath { param($ItemPath) }
function Protect-Oxp3TokenPath { param($ItemPath, $Directory) }
function Test-Path { param($LiteralPath) $true }
function Copy-Item {
    param($LiteralPath, $Destination, [switch]$Force)
    if (-not $LiteralPath.EndsWith('remembered-watts.json') -or -not $Destination.EndsWith('preferences-backup.json')) { throw 'Incorrect preference backup.' }
    $fixture.BackedUp = $true
}
function Move-Item { param($LiteralPath, $Destination, [switch]$Force) $fixture.ReceiptMoved = $true }
function Get-AppxPackage { param($Name) if ($Name -ne 'OXP3.PowerWidget') { throw 'Incorrect package removal.' }; [pscustomobject]@{ Name = $Name } }
function Remove-AppxPackage { param([Parameter(ValueFromPipeline)]$Package) process { $fixture.PackageRemoved = $true } }
function Remove-Item {
    param($LiteralPath)
    if ($LiteralPath -ne 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OXP3GamePower') { throw 'Removal must retain live bridge files.' }
    $fixture.EntryRemoved = $true
}
foreach ($existing in @($true, $false)) {
    $fixture = @{ Existing = $existing; Changed = $false }
    & $body | Out-Null
    if (-not $fixture.BackedUp -or -not $fixture.PackageRemoved -or -not $fixture.EntryRemoved -or [bool]$fixture.Restored -ne $existing -or [bool]$fixture.ReceiptMoved -ne $existing) { throw 'Removal did not complete correctly.' }
}
$fixture = @{ Existing = $true; Changed = $true }
$refused = $false
try { & $body | Out-Null } catch { if ($_.Exception.Message -notlike 'Startup changed during removal*') { throw }; $refused = $true }
if (-not $refused -or $fixture.Restored -or $fixture.ReceiptMoved -or $fixture.PackageRemoved -or $fixture.EntryRemoved) { throw 'Changed startup was not preserved.' }
$fixture = @{ Existing = $true; FailRestore = $true }
$refused = $false
try { & $body | Out-Null } catch { if ($_.Exception.Message -ne 'Fixture: task restoration failed.') { throw }; $refused = $true }
if (-not $refused -or $fixture.ReceiptMoved -or $fixture.PackageRemoved -or $fixture.EntryRemoved) { throw 'Failed startup restoration did not preserve installation.' }
Write-Output 'PASS: removal preserves running native processes, restores startup, backs up preferences and refuses changed startup.'
