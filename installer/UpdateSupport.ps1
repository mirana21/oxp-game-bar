$ErrorActionPreference = 'Stop'

function Get-UpdateCandidate($Releases, [version]$InstalledVersion, [bool]$AllowPrerelease, [switch]$RepairCurrent) {
    $candidates = @()
    foreach ($item in $Releases) {
        if ($item.draft -or ($item.prerelease -and -not $AllowPrerelease)) { continue }
        if ([string]$item.tag_name -notmatch '^v(\d+\.\d+\.\d+\.\d+)$') { continue }
        try { $version = [version]$Matches[1] } catch { continue }
        if ($version -lt $InstalledVersion -or ($version -eq $InstalledVersion -and -not $RepairCurrent)) { continue }
        $name = 'OXP3-Game-Power-' + $version.ToString() + '-alpha-x64.zip'
        $assets = @($item.assets | Where-Object { $_.name -ceq $name -and $_.state -eq 'uploaded' })
        if ($assets.Count -ne 1 -or $assets[0].size -le 0 -or $assets[0].size -gt 350MB) { continue }
        $candidates += [pscustomobject]@{ Version = $version; Asset = $assets[0] }
    }
    return $candidates | Sort-Object Version -Descending | Select-Object -First 1
}

function Assert-UpdateDownloadUri([string]$Url, [string]$Repository, [string]$Version, [string]$Name) {
    $uri = [uri]$Url
    $expected = '/' + $Repository + '/releases/download/v' + $Version + '/' + $Name
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'github.com' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -cne $expected) { throw 'Unexpected update download address.' }
}

function Expand-UpdateArchive([string]$Archive, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $root = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        if ($zip.Entries.Count -gt 512) { throw 'Update archive has too many entries.' }
        $total = [long]0
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        # Validate every entry before extracting any of them.
        foreach ($entry in $zip.Entries) {
            $relative = $entry.FullName.Replace('/','\')
            if (-not $relative -or $relative.Contains(':') -or $relative -match '(^|\\)\.\.?($|\\)' -or $relative -match '[. ]($|\\)' -or $relative -match '(^|\\)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$|\\)' -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Unsafe update archive entry.' }
            $full = [IO.Path]::GetFullPath((Join-Path $Destination $relative))
            if (-not $full.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($full)) { throw 'Unsafe or duplicate update archive path.' }
            $total += $entry.Length
            if ($entry.Length -gt 512MB -or $total -gt 1GB) { throw 'Update archive is too large.' }
        }
        foreach ($entry in $zip.Entries) {
            $full = [IO.Path]::GetFullPath((Join-Path $Destination $entry.FullName.Replace('/','\')))
            if ($entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('\')) { New-Item -ItemType Directory -Path $full -Force | Out-Null; continue }
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($full)) -Force | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$full,$false)
        }
    } finally { $zip.Dispose() }
}

function Assert-UpdatePublisher([string]$SetupPath, [string]$Thumbprint) {
    $signature = Get-AuthenticodeSignature -LiteralPath $SetupPath
    if ($null -eq $signature -or [int]$signature.Status -ne 0 -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ine $Thumbprint) { throw 'Update publisher verification failed.' }
}

function Invoke-UpdateSetup([string]$Directory, [ValidateSet('Check','Update','Rollback')][string]$Mode) {
    $arguments = switch ($Mode) { 'Check' { '--check' }; 'Update' { '--update' }; 'Rollback' { '--update --rollback' } }
    $process = Start-Process -FilePath (Join-Path $Directory 'Setup.exe') -ArgumentList $arguments -PassThru -WindowStyle Hidden
    try {
        # Never kill a partly completed installation on timeout. The updater
        # exits; Setup retains its own maintenance lock until it completes.
        if (-not $process.WaitForExit(480000)) { throw 'Setup is still running; the next check will re-evaluate the installed version.' }
        return $process.ExitCode
    } finally { $process.Dispose() }
}

function Invoke-UpdateTransaction([string]$Bundle, [string]$Previous, [scriptblock]$StillInstalled, [scriptblock]$ConfirmInstallation = { $true }) {
    $result = Invoke-UpdateSetup $Bundle 'Update'
    if ($result -eq 0 -and (& $ConfirmInstallation)) { return 'updated' }
    if (-not (& $StillInstalled)) { return 'cancelled' }
    if ((Invoke-UpdateSetup $Previous 'Rollback') -ne 0) { throw 'Update and restoration both need attention. The previous installer was retained in UpdateCache.' }
    return 'restored'
}

function Test-UpdateBusy([string]$LocalRoot) {
    $hostPath = Join-Path $LocalRoot 'Packages\OXP3.PowerWidget_pv4gfha69c7qe\LocalState\widget-host-state.json'
    if (Test-Path -LiteralPath $hostPath) {
        try {
            $hostState = Get-Content -LiteralPath $hostPath -Raw | ConvertFrom-Json
            if ($hostState.visible -and (Get-Item -LiteralPath $hostPath).LastWriteTimeUtc -gt [DateTime]::UtcNow.AddMinutes(-2)) { return $true }
        } catch { }
    }
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'OXP3.PowerWidget.Bridge.v1', [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(300)
        $bytes = [Text.Encoding]::UTF8.GetBytes('{"command":"getState"}' + "`n")
        $pipe.Write($bytes,0,$bytes.Length)
        $reader = [IO.StreamReader]::new($pipe)
        try {
            $read = $reader.ReadLineAsync()
            if ($read.Wait(2500)) {
                $reply = $read.Result | ConvertFrom-Json
                if ($reply.ok -and ($reply.result.playingGame -or $reply.result.scanning)) { return $true }
            }
        } finally { $reader.Dispose() }
    } catch { } finally { $pipe.Dispose() }
    # A broken helper must not block its own repair. Recognised saved games
    # still defer installation even when the helper cannot answer.
    $preferences = Join-Path $LocalRoot 'Packages\OXP3.PowerWidget_pv4gfha69c7qe\LocalCache\Local\OXP3PowerWidget\remembered-watts.json'
    try {
        if (Test-Path -LiteralPath $preferences) {
            $saved = Get-Content -LiteralPath $preferences -Raw | ConvertFrom-Json
            $names = @($saved.games | ForEach-Object { $_.processName } | Where-Object { $_ })
            if ($names.Count -and @(Get-Process -Name $names -ErrorAction SilentlyContinue).Count) { return $true }
        }
    } catch { }
    return $false
}

function Register-Oxp3UpdateTask([string]$SetupRoot) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $action = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $SetupRoot 'Scripts\Update.ps1') + '"')
    $signIn = New-ScheduledTaskTrigger -AtLogOn -User $sid
    $signIn.Delay = 'PT2M'
    $daily = New-ScheduledTaskTrigger -Daily -At '12:30'
    $principal = New-ScheduledTaskPrincipal -UserId $sid -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval ([TimeSpan]::FromHours(1))
    $settings.AllowHardTerminate = $false
    Register-ScheduledTask -TaskName OXP3GamePowerUpdate -Action $action -Trigger @($signIn,$daily) -Principal $principal -Settings $settings -Force | Out-Null
}
