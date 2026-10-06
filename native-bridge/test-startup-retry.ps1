$ErrorActionPreference = 'Stop'
# Extract only the preparation function. Never execute the production launcher.
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'startup-native.ps1'), [ref]$tokens, [ref]$errors)
if ($errors) { throw ($errors.Message -join '; ') }
$function = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-StartupPreparation'}, $false)
Invoke-Expression $function.Extent.Text
$script:events = @(); $script:waits = 0; $script:attempts = 0
function Write-StartupEvent($Phase, $Detail) { $script:events += $Phase }
function Start-Sleep($Seconds) { if ($Seconds -ne 3) { throw 'Unexpected retry delay' }; $script:waits++ }
Invoke-StartupPreparation { $script:attempts++; if ($script:attempts -lt 3) { throw 'Cold Windows service is not ready' } }
if ($script:attempts -ne 3 -or $script:waits -ne 2 -or ($script:events -join ',') -ne 'preflight-failed,preflight-failed,preflight-ready') { throw 'Transient startup did not recover' }
$script:attempts = 0; $script:waits = 0; $script:events = @(); $rejected = $false
try { Invoke-StartupPreparation { $script:attempts++; throw 'Integrity check failed' } }
catch { if ($_.Exception.Message -ne 'Integrity check failed') { throw }; $rejected = $true }
if (-not $rejected -or $script:attempts -ne 3 -or $script:waits -ne 2 -or $script:events -contains 'preflight-ready') { throw 'Persistent failure was accepted or retries were unbounded' }
Write-Output 'PASS: cold-start recovery, bounded persistent-failure rejection, and diagnostic phases. No task, native app, token, or wattage changed.'
