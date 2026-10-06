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
    Invoke-StartupPreparation {
        # Capture native stderr as ordinary output: Windows PowerShell otherwise
        # turns it into a terminating NativeCommandError before exit inspection.
        $previousErrorPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $preflightOutput = & $nodePath (Join-Path $PSScriptRoot 'preflight-native.cjs') 2>&1
            $preflightExit = $LASTEXITCODE
        } finally { $ErrorActionPreference = $previousErrorPreference }
        if ($preflightExit -ne 0) { throw ('Native preflight unavailable: ' + ($preflightOutput -join ' ')) }
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
    & $nodePath (Join-Path $PSScriptRoot 'launch-native.cjs') 2>$null
    $launcherExitCode = $LASTEXITCODE
} catch {
    $launcherExitCode = $LASTEXITCODE
}
# Exit 20 means prelaunch/OS-spawn failure and no native process/owned handles.
# Every native process that was created exits as 0 or 31; never relaunch those.
if ($launcherExitCode -eq 20) { Write-StartupEvent 'vendor-fallback' 'Native launcher exited before creating ONEXConsole.'; Start-VendorFallback }
exit 0
