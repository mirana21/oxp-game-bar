param([switch]$ValidateOnly, [switch]$Force)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$env:PSModulePath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules'
. (Join-Path $PSScriptRoot 'UpdateSupport.ps1')
$setupRoot = 'C:\Program Files\OXP3 Game Power Setup'
$codeRoot = 'C:\Program Files\OXP3 Game Power Bridge'
$cacheRoot = Join-Path $setupRoot 'UpdateCache'
$statusPath = Join-Path $setupRoot 'update-status.json'
$mutex = [Threading.Mutex]::new($false,'Local\OXP3.GamePower.Update')
try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
if (-not $owned) { $mutex.Dispose(); exit 0 }
$work = $null
function Write-UpdateStatus([string]$Phase, [string]$Version = '', [string]$Detail = '', [int]$RetryHours = 6) {
    $value = @{ phase = $Phase; version = $Version; detail = $Detail; at = [DateTime]::UtcNow.ToString('o'); nextCheckUtc = [DateTime]::UtcNow.AddHours($RetryHours).ToString('o') }
    [IO.File]::WriteAllText($statusPath, ($value | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}
try {
    # The scheduled updater lives independently of the widget/helper. Removal
    # makes queued checks inert, including a check already downloading a ZIP.
    $receipt = Join-Path $codeRoot 'startup-receipt.json'
    if (-not (Test-Path -LiteralPath $receipt)) { exit 0 }
    $release = Get-Content -LiteralPath (Join-Path $setupRoot 'release.json') -Raw | ConvertFrom-Json
    # Also supports a one-time bootstrap from a pre-updater installation.
    $repository = if ($release.updateRepository) { [string]$release.updateRepository } else { 'mirana21/oxp-game-bar' }
    $allowPrerelease = if ($null -ne $release.allowPrereleaseUpdates) { [bool]$release.allowPrereleaseUpdates } else { $true }
    if ($repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Update source is not configured.' }
    if (-not $Force -and -not $ValidateOnly -and (Test-Path -LiteralPath $statusPath)) {
        try { $last = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json; if ([DateTime]::Parse($last.nextCheckUtc).ToUniversalTime() -gt [DateTime]::UtcNow) { exit 0 } } catch { }
    }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $headers = @{ 'User-Agent' = 'OXP3-Game-Power-Updater'; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    # No GitHub credentials are stored in the app or sent with downloads.
    $releases = Invoke-RestMethod -Uri ('https://api.github.com/repos/' + $repository + '/releases?per_page=100') -Headers $headers -TimeoutSec 30
    $installed = [version]$release.version
    $packages = @(Get-AppxPackage -Name OXP3.PowerWidget)
    $repairCurrent = $packages.Count -ne 1 -or $packages[0].Status.ToString() -ne 'Ok'
    if ($packages.Count -eq 1 -and [version]$packages[0].Version -gt $installed) { $installed = [version]$packages[0].Version; $repairCurrent = $true }
    if ($ValidateOnly) { $installed = [version]'0.0.0.0' }
    $candidate = Get-UpdateCandidate $releases $installed $allowPrerelease -RepairCurrent:$repairCurrent
    if (-not $candidate) { Write-UpdateStatus 'current' $release.version; exit 0 }
    $local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (-not $ValidateOnly -and (Test-UpdateBusy $local)) { Write-UpdateStatus 'deferred' $candidate.Version.ToString() '' 1; exit 2 }
    Assert-UpdateDownloadUri $candidate.Asset.browser_download_url $repository $candidate.Version.ToString() $candidate.Asset.name
    New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
    # Bound disk use after failed downloads/installations. Never delete a
    # cached installer that is still completing an earlier installation.
    $runningSetup = @(Get-CimInstance Win32_Process -Filter "Name='Setup.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($cacheRoot + '\',[StringComparison]::OrdinalIgnoreCase) })
    if ($runningSetup.Count) { Write-UpdateStatus 'deferred' $candidate.Version.ToString() '' 1; exit 2 }
    foreach ($oldWork in Get-ChildItem -LiteralPath $cacheRoot -Directory) {
        if ($oldWork.Name -match '^[a-f0-9]{32}$' -and -not ($oldWork.Attributes -band [IO.FileAttributes]::ReparsePoint) -and [IO.Path]::GetFullPath($oldWork.FullName).StartsWith($cacheRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $oldWork.FullName -Recurse -Force }
    }
    $work = Join-Path $cacheRoot ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    $zip = Join-Path $work 'update.zip'
    Invoke-WebRequest -Uri $candidate.Asset.browser_download_url -Headers @{ 'User-Agent' = 'OXP3-Game-Power-Updater' } -OutFile $zip -UseBasicParsing -TimeoutSec 180
    if ((Get-Item -LiteralPath $zip).Length -ne $candidate.Asset.size) { throw 'Update download was incomplete.' }
    if ($candidate.Asset.digest) {
        if ([string]$candidate.Asset.digest -notmatch '^sha256:([a-fA-F0-9]{64})$' -or (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ine $Matches[1]) { throw 'Update download checksum did not match.' }
    }
    $bundle = Join-Path $work 'Bundle'
    New-Item -ItemType Directory -Path $bundle | Out-Null
    Expand-UpdateArchive $zip $bundle
    Assert-UpdatePublisher (Join-Path $bundle 'Setup.exe') $release.certificateThumbprint
    $next = Get-Content -LiteralPath (Join-Path $bundle 'release.json') -Raw | ConvertFrom-Json
    if ([version]$next.version -ne $candidate.Version -or $next.packageFamily -ne $release.packageFamily) { throw 'Update package identity did not match the release.' }
    # Signed Setup embeds the manifest hash and verifies every payload before
    # running any downloaded script. Its Check mode performs no installation.
    if ((Invoke-UpdateSetup $bundle 'Check') -ne 0) { throw 'Update package checks failed.' }
    if ($ValidateOnly) { Write-UpdateStatus 'validated' $next.version; Write-Output ('Validated signed GitHub release ' + $next.version + '. Nothing installed.'); exit 0 }
    if (Test-UpdateBusy $local) { Write-UpdateStatus 'deferred' $next.version '' 1; exit 2 }
    if (-not (Test-Path -LiteralPath $receipt)) { exit 0 }
    $current = Get-Content -LiteralPath (Join-Path $setupRoot 'release.json') -Raw | ConvertFrom-Json
    if ($current.version -ne $release.version) { exit 0 }
    # Keep one complete known-good installer independently of the app package.
    $previous = Join-Path $work 'Previous'
    New-Item -ItemType Directory -Path $previous | Out-Null
    foreach ($relative in @($release.files.path) + @('release.json','Setup.exe')) {
        $source = [IO.Path]::GetFullPath((Join-Path $setupRoot $relative))
        if (-not $source.StartsWith($setupRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid installed bundle path.' }
        $target = Join-Path $previous $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }
    Assert-UpdatePublisher (Join-Path $previous 'Setup.exe') $release.certificateThumbprint
    Write-UpdateStatus 'installing' $next.version
    $outcome = Invoke-UpdateTransaction $bundle $previous { Test-Path -LiteralPath $receipt } {
        $registered = @(Get-AppxPackage -Name OXP3.PowerWidget)
        $recorded = Get-Content -LiteralPath (Join-Path $setupRoot 'release.json') -Raw | ConvertFrom-Json
        $registered.Count -eq 1 -and $registered[0].Status.ToString() -eq 'Ok' -and [version]$registered[0].Version -eq $candidate.Version -and $recorded.version -eq $next.version
    }
    if ($outcome -eq 'cancelled') { exit 0 }
    if ($outcome -eq 'restored') {
        Write-UpdateStatus 'restored' $release.version ('Update ' + $next.version + ' failed; the previous version was restored.') 24
        exit 1
    }
    Write-UpdateStatus 'updated' $next.version
} catch {
    try { Write-UpdateStatus 'failed' '' $_.Exception.Message 1 } catch { }
    Write-Error $_ -ErrorAction Continue
    exit 1
} finally {
    # Retain installers on failure for repair. Successful/current validation
    # leaves no growing archive cache, and never touches a running native parent.
    if ($work -and (Test-Path -LiteralPath $statusPath)) {
        try {
            $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
            if ($status.phase -in @('updated','validated','deferred') -and [IO.Path]::GetFullPath($work).StartsWith($cacheRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $work -Recurse -Force }
        } catch { }
    }
    $mutex.ReleaseMutex(); $mutex.Dispose()
}
