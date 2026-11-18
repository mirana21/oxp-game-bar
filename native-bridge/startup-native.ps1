$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$env:PSModulePath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules'
$nodePath = Join-Path $PSScriptRoot 'node.exe'
$expectedNodeHash = '3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237'
function Write-StartupEvent([string]$Phase, [string]$Detail = '') {
    # Diagnostics contain phases/errors only, never authentication token data.
    try {
        $root = 'C:\Program Files\OXP3 Game Power Bridge'
        $logPath = Join-Path $root 'startup-native.log'
        if ((Test-Path -LiteralPath $logPath) -and ((Get-Item -LiteralPath $logPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { return }
        $event = @{ at = [DateTime]::UtcNow.ToString('o'); phase = $Phase; detail = $Detail } | ConvertTo-Json -Compress
        [IO.File]::AppendAllText($logPath, $event + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    } catch { } # Logging must never prevent the vendor application from starting.
}
function Invoke-StartupPreparation([scriptblock]$Prepare) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            & $Prepare
            Write-StartupEvent 'preflight-ready' ('attempt=' + $attempt)
            return
        } catch {
            Write-StartupEvent 'preflight-failed' ('attempt=' + $attempt + '; ' + $_.Exception.Message)
            if ($attempt -eq 3) { throw }
            Start-Sleep -Seconds 3
        }
    }
}
function Start-VendorFallback {
    # Preserve the vendor's normal launch path. There are no owned CDP handles
    # yet: fallback is allowed only before the private parent is launched.
    if (@(Get-Process -Name OneXConsole -ErrorAction SilentlyContinue).Count -gt 0) { return }
    $scriptPath = 'C:\Program Files\OneXConsole\resources\resources\tools\OneXConsoleStartup.vbs'
    Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\wscript.exe') -ArgumentList ('"' + $scriptPath + '"') -WindowStyle Hidden
}
function Get-StockPowerBridge {
    $vendorDirectory = 'C:\Program Files\OneXConsole'
    $vendor = Join-Path $vendorDirectory 'OneXConsole.exe'
    if ((Get-Item -LiteralPath $vendor).VersionInfo.FileVersion -ne '0.10.4-fix3') { return $null }
    $bridge = Join-Path $PSScriptRoot 'PowerBridge.exe'
    $files = @{
        $vendor = 'Shenzhen One-Netbook'
        (Join-Path $vendorDirectory 'resources\resources\CompatLayerCT.exe') = 'Shenzhen One-Netbook'
        (Join-Path $vendorDirectory 'resources\resources\tools\intel\IntelPowerPlugin.exe') = 'Shenzhen One-Netbook'
        (Join-Path $vendorDirectory 'resources\resources\tools\intel\PowerPlugin.dll') = 'Intel Corporation'
        (Join-Path $vendorDirectory 'resources\resources\PawnIOLib.dll') = 'namazso.eu'
        $bridge = 'CN=OXP3Personal'
    }
    foreach ($file in $files.Keys) {
        $sig = Get-AuthenticodeSignature -LiteralPath $file
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike ('*' + $files[$file] + '*')) {
            throw 'A reviewed power component failed signature verification.'
        }
    }
    return $bridge
}
function Start-EnabledWidgetHelper {
    $key = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\OXP3.PowerWidget_pv4gfha69c7qe\OXP3PowerWidgetHelper'
    $preference = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
    if ($preference.State -in 2,4 -and (Get-AppxPackage -Name OXP3.PowerWidget).PackageFamilyName -eq 'OXP3.PowerWidget_pv4gfha69c7qe') {
        Start-Process -FilePath 'ms-gamebar:activate/OXP3.PowerWidget_pv4gfha69c7qe_App_PowerProfiles' -WindowStyle Hidden
    }
}
function Invoke-StartupNode([string]$Script, [switch]$Capture) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $nodePath
    $info.Arguments = '"' + (Join-Path $PSScriptRoot $Script) + '"'
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $Capture
    $info.RedirectStandardError = $Capture
    try { $process = [Diagnostics.Process]::Start($info) }
    catch { return [pscustomobject]@{ ExitCode = 20; Output = $_.Exception.Message } }
    try {
        if ($Capture) {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
        }
        $process.WaitForExit()
        [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = if ($Capture) { ($stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()).Trim() }
        }
    } finally { $process.Dispose() }
}
try {
    if ($PSScriptRoot -ne 'C:\Program Files\OXP3 Game Power Bridge' -and $PSScriptRoot -notmatch '^C:\\Program Files\\OXP3 Game Power Bridge\\Versions\\\d+\.\d+\.\d+\.\d+$') { throw 'Unreviewed startup-code location.' }
    if ((Get-FileHash -LiteralPath $nodePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedNodeHash) { throw 'Unreviewed Node runtime.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $nodePath
    if ($null -eq $signature -or [int]$signature.Status -ne 0) { throw 'Invalid Node signature.' }
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'startup-modules.json') -Raw | ConvertFrom-Json
    foreach ($item in $manifest.modules) {
        if ([IO.Path]::GetFileName([string]$item.file) -ne $item.file) { throw 'Invalid startup module path.' }
        $hash = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $item.file) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $item.sha256) { throw 'Startup module changed after installation.' }
    }
    Write-StartupEvent 'startup-begin'
    $stockPowerBridge = Get-StockPowerBridge
    if ($stockPowerBridge) {
        Start-VendorFallback
        $brokerProcess = Start-Process -FilePath $stockPowerBridge -WindowStyle Hidden -PassThru
        Write-StartupEvent 'stock-power-bridge-started' ('pid=' + $brokerProcess.Id)
        try { Start-EnabledWidgetHelper }
        catch { Write-StartupEvent 'widget-helper-activation-unavailable' $_.Exception.Message }
        $brokerProcess.WaitForExit()
        Write-StartupEvent 'stock-power-bridge-stopped' ('exit=' + $brokerProcess.ExitCode)
        exit $brokerProcess.ExitCode
    }
    Invoke-StartupPreparation {
        $preflight = Invoke-StartupNode 'preflight-native.cjs' -Capture
        if ($preflight.ExitCode -ne 0) { throw ('Native preflight unavailable: ' + $preflight.Output) }
    }
} catch {
    # Retain an independent protected log even if profile resolution or script
    # module loading itself failed before the normal diagnostics could run.
    try { [IO.File]::AppendAllText('C:\Program Files\OXP3 Game Power Bridge\startup-error.log', [DateTime]::UtcNow.ToString('o') + ' ' + $_.Exception.Message + '; user=' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value + '; local=' + [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData) + '; env=' + $env:LOCALAPPDATA + [Environment]::NewLine) } catch { }
    Write-StartupEvent 'vendor-fallback' $_.Exception.Message
    Start-VendorFallback
    exit 0
}
# Once a private parent might own CDP handles, never fall back by terminating or
# replacing it. Bootstrap errors are logged by the parent while native stays up.
try {
    Write-StartupEvent 'private-launch-requested'
    $launcherExitCode = (Invoke-StartupNode 'launch-native.cjs').ExitCode
} catch {
    Write-StartupEvent 'launcher-unavailable' $_.Exception.Message
    $launcherExitCode = 31
}
# Exit 20 means prelaunch/OS-spawn failure and no native process/owned handles.
# Every native process that was created exits as 0 or 31; never relaunch those.
if ($launcherExitCode -eq 20) { Write-StartupEvent 'vendor-fallback' 'Native launcher exited before creating ONEXConsole.'; Start-VendorFallback }
exit 0
