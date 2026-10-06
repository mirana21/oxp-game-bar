param()
$ErrorActionPreference = 'Stop'

# This is an opt-in live check. It uses one harmless, temporary executable and
# a 1 W change through the installed helper, then removes its own mapping.
function Invoke-Widget([hashtable]$Request) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'OXP3.PowerWidget.Bridge.v1', [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(30000)
        $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 4096, $true)
        $reader = [IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
        try {
            $writer.AutoFlush = $true
            $writer.WriteLine(($Request | ConvertTo-Json -Compress))
            $read = $reader.ReadLineAsync()
            if (-not $read.Wait(30000)) { throw 'Widget helper reply timed out.' }
            $reply = $read.Result | ConvertFrom-Json
            if (-not $reply.ok) { throw ('Widget helper rejected request: ' + $reply.error) }
            return $reply.result
        } finally { $reader.Dispose(); $writer.Dispose() }
    } finally { $pipe.Dispose() }
}
function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Wait-State([int]$Watts, [string]$GameId, [int]$Seconds = 15) {
    $end = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $state = Invoke-Widget @{ command = 'getState' }
        if ($state.connected -and $state.nativeWatts -eq $Watts -and $state.activeGameId -eq $GameId) { return $state }
        Start-Sleep -Milliseconds 600
    } while ([DateTime]::UtcNow -lt $end)
    throw ('Expected native target ' + $Watts + ' W and game ' + $GameId + '; last reply: ' + ($state | ConvertTo-Json -Compress))
}

$initial = Invoke-Widget @{ command = 'getState' }
Assert-Check $initial.connected 'Open the installed widget and connect ONEXConsole before running this check.'
Assert-Check (-not $initial.enabled) 'Pause automatic switching before the isolated live check.'
$remembered = @((Invoke-Widget @{ command = 'listGames' }).games | Where-Object { $null -ne $_.savedWatts } | Select-Object -ExpandProperty selectedExePath)
$runningRemembered = @(Get-CimInstance Win32_Process -Property ExecutablePath | Where-Object { $_.ExecutablePath -and $remembered -contains $_.ExecutablePath })
Assert-Check ($runningRemembered.Count -eq 0) 'Close games with remembered wattages before the isolated live check.'
$baseline = [int]$initial.nativeWatts
$testWatts = if ($baseline -lt [int]$initial.maxWatts) { $baseline + 1 } else { $baseline - 1 }
Assert-Check ($testWatts -ge [int]$initial.minWatts) 'There is no adjacent supported wattage to test.'
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('probe-' + [Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$source = Join-Path $fixtureRoot 'OXP3WattageProbe.cs'
$exe = Join-Path $fixtureRoot 'OXP3WattageProbe.exe'
$id = $null
$process = $null
$testError = $null
$cleanupErrors = [Collections.Generic.List[string]]::new()
try {
    [IO.File]::WriteAllText($source, 'using System.Threading; class Probe { static void Main() { Thread.Sleep(12000); } }')
    & 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:winexe /platform:x64 ('/out:' + $exe) $source
    if ($LASTEXITCODE -ne 0) { throw 'The harmless process fixture did not compile.' }
    $added = Invoke-Widget @{ command = 'addExe'; exePath = $exe; name = 'Temporary wattage check' }
    $id = [string]$added.gameId
    $null = Invoke-Widget @{ command = 'saveWatts'; gameId = $id; exePath = $exe; watts = $testWatts }
    $saved = Invoke-Widget @{ command = 'getState' }
    Assert-Check ($saved.nativeWatts -eq $baseline) 'Remembering an offline executable changed current power.'
    $null = Invoke-Widget @{ command = 'setEnabled'; enabled = $true }
    $process = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru
    $null = Wait-State $testWatts $id
    Write-Output ('PASS: exact process launch applied ' + $testWatts + ' W in ONEXConsole.')
    if (-not $process.WaitForExit(20000)) { throw 'Temporary check process did not exit on its timer.' }
    $null = Wait-State $baseline ''
    Write-Output ('PASS: process exit restored ' + $baseline + ' W in ONEXConsole.')
} catch {
    $testError = $_
} finally {
    # Stop future automatic writes before removing the temporary mapping.
    try { $null = Invoke-Widget @{ command = 'setEnabled'; enabled = $false } } catch { $cleanupErrors.Add('Pause: ' + $_.Exception.Message) }
    if ($id) { try { $null = Invoke-Widget @{ command = 'forgetWatts'; gameId = $id } } catch { $cleanupErrors.Add('Forget: ' + $_.Exception.Message) } }
    try {
        $last = Invoke-Widget @{ command = 'getState' }
        if ($last.connected -and $last.nativeWatts -eq $testWatts) {
            $null = Invoke-Widget @{ command = 'applyWatts'; watts = $baseline }
        }
    } catch { $cleanupErrors.Add('Restore: ' + $_.Exception.Message) }
    try { if ($process -and -not $process.HasExited) { $null = $process.WaitForExit(15000) } } catch { $cleanupErrors.Add('Process exit: ' + $_.Exception.Message) }
    $allowedRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    try {
        if (-not $fixtureRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($fixtureRoot) -notmatch '^probe-[a-f0-9]{32}$') { throw 'Unsafe fixture cleanup path.' }
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    } catch { $cleanupErrors.Add('Fixture cleanup: ' + $_.Exception.Message) }
}
if ($cleanupErrors.Count) { Write-Warning ('Live check cleanup needs attention: ' + ($cleanupErrors -join '; ')) }
if ($testError) { throw $testError }
if ($cleanupErrors.Count) { throw 'The live check did not complete every cleanup step.' }
$final = Invoke-Widget @{ command = 'getState' }
Assert-Check ($final.connected -and $final.nativeWatts -eq $baseline -and -not $final.enabled) 'Live check did not finish paused at its original wattage.'
Write-Output 'PASS: offline save, launch, exit, native confirmation, and temporary mapping cleanup.'
