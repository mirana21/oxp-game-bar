# Read-only installed-game discovery. No launchers, games, settings, or services are changed.
# Compatible with Windows PowerShell 5.1 and PowerShell 7.
Set-StrictMode -Version 2.0

function Get-VdfValue {
    param([string]$Text, [string]$Key)
    $match = [regex]::Match($Text, '"' + [regex]::Escape($Key) + '"\s+"((?:\\.|[^"\\])*)"')
    if ($match.Success) { return $match.Groups[1].Value.Replace('\\', '\').Replace('\"', '"') }
    return $null
}

function Get-ExecutableCandidates {
    param([string]$InstallPath, [string]$GameName, [string[]]$PreferredPaths = @())
    if (-not (Test-Path -LiteralPath $InstallPath -PathType Container)) { return @() }
    $root = [System.IO.Path]::GetFullPath($InstallPath).TrimEnd('\', '/')
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($root)
    $candidates = New-Object 'System.Collections.Generic.List[object]'
    $skipDirectory = '^(?:_CommonRedist|redist|redistributables|prerequisites|prereq|EasyAntiCheat|EasyAntiCheat_EOS|BattlEye|DirectX|VCRedist|dotnet|crashpad|crashreport|crashreporter)$'
    $helperName = '(?i)^(?:CrashReport.*|CrashPad.*|UnityCrashHandler.*|crs-handler|crs-video|InstallerMessage|SaveRemoverTool|Unins\d*|uninstall|D3D12StateObjectCompiler|d3dconfig|curl|DXSETUP|vc_redist.*|UE[45]PrereqSetup.*)$'
    $gameToken = [regex]::Replace($GameName.ToLowerInvariant(), '[^a-z0-9]', '')
    $directoryToken = [regex]::Replace([System.IO.Path]::GetFileName($root).ToLowerInvariant(), '[^a-z0-9]', '')
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($file in @(Get-ChildItem -LiteralPath $directory -Filter '*.exe' -File -ErrorAction SilentlyContinue)) {
            $stem = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
            if ($stem -match $helperName) { continue }
            $rank = 10
            $reasons = New-Object 'System.Collections.Generic.List[string]'
            $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/')
            if ($file.DirectoryName -eq $root) { $rank += 8; $reasons.Add('At game root') }
            if ($file.Length -gt 10000000) { $rank += 20; $reasons.Add('Game-sized executable') }
            if ($file.Length -gt 50000000) { $rank += 10 }
            if ($relative -match '(?i)\\Binaries\\(?:Win64|WinGDK|Win32)\\') {
                $rank += 30; $reasons.Add('Engine game binary')
            } elseif ($relative -match '(?i)\\(?:x64|x64_dx12|Win64|WinGDK)\\') {
                $rank += 15; $reasons.Add('Platform game binary')
            }
            if ($stem -match '(?i)-Shipping$') { $rank += 25; $reasons.Add('Shipping game binary') }
            $stemToken = [regex]::Replace($stem.ToLowerInvariant(), '[^a-z0-9]', '')
            if ($stemToken -eq $directoryToken -or $stemToken -eq $gameToken) {
                $rank += 30; $reasons.Add('Matches game name')
            }
            if ($stem -match '(?i)(launcher|prelauncher|start_protected|^play|patcher|setup|updater)' -or $relative -match '(?i)(^|\\)(launcher|patcher)(\\|$)') {
                $rank -= 70; $reasons.Add('Launcher or utility; choose the game binary instead')
            }
            if ($relative -match '(?i)(^|\\)(tools?|support|Engine)(\\|$)') {
                $rank -= 40; $reasons.Add('Support executable')
            }
            if (@($PreferredPaths | Where-Object { $_ -ieq $file.FullName }).Count -gt 0) {
                $rank += 45; $reasons.Add('Declared by installed game metadata')
            }
            $candidates.Add([pscustomobject]@{
                Path = $file.FullName
                ProcessName = $stem
                Rank = $rank
                Reason = ($reasons -join '; ')
            })
        }
        foreach ($child in @(Get-ChildItem -LiteralPath $directory -Directory -ErrorAction SilentlyContinue)) {
            # Do not follow junctions or links into unrelated locations.
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            if ($child.Name -match $skipDirectory) { continue }
            $pending.Push($child.FullName)
        }
    }
    return @($candidates | Sort-Object @{ Expression = 'Rank'; Descending = $true }, Path)
}

function Get-SteamGames {
    param([string[]]$SteamRoots)
    $roots = New-Object 'System.Collections.Generic.List[string]'
    if ($PSBoundParameters.ContainsKey('SteamRoots')) {
        foreach ($root in $SteamRoots) { $roots.Add($root) }
    } else {
        $steam = Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue
        if ($steam -and $steam.PSObject.Properties['SteamPath']) { $roots.Add($steam.SteamPath.Replace('/', '\')) }
        $fallback = Join-Path ${env:ProgramFiles(x86)} 'Steam'
        if (Test-Path -LiteralPath $fallback) { $roots.Add($fallback) }
    }
    $libraries = New-Object 'System.Collections.Generic.List[string]'
    foreach ($root in @($roots | Select-Object -Unique)) {
        $libraries.Add($root)
        $libraryFile = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $libraryFile) {
            $text = Get-Content -LiteralPath $libraryFile -Encoding UTF8 -Raw -ErrorAction SilentlyContinue
            foreach ($match in [regex]::Matches($text, '"path"\s+"((?:\\.|[^"\\])*)"')) {
                $libraries.Add($match.Groups[1].Value.Replace('\\', '\'))
            }
        }
    }
    foreach ($library in @($libraries | Select-Object -Unique)) {
        $steamApps = Join-Path $library 'steamapps'
        if (-not (Test-Path -LiteralPath $steamApps -PathType Container)) { continue }
        foreach ($manifest in @(Get-ChildItem -LiteralPath $steamApps -Filter 'appmanifest_*.acf' -File -ErrorAction SilentlyContinue)) {
            $text = Get-Content -LiteralPath $manifest.FullName -Encoding UTF8 -Raw -ErrorAction SilentlyContinue
            $appId = Get-VdfValue $text 'appid'
            $name = Get-VdfValue $text 'name'
            $installDirectory = Get-VdfValue $text 'installdir'
            if (-not $appId -or -not $name -or -not $installDirectory -or $appId -eq '228980') { continue }
            $install = Join-Path (Join-Path $steamApps 'common') $installDirectory
            if (-not (Test-Path -LiteralPath $install -PathType Container)) { continue }
            $art = $null
            foreach ($root in @($roots | Select-Object -Unique)) {
                foreach ($relative in @("appcache\librarycache\$appId\library_600x900.jpg", "appcache\librarycache\${appId}_library_600x900.jpg")) {
                    $file = Join-Path $root $relative
                    if (Test-Path -LiteralPath $file -PathType Leaf) { $art = $file; break }
                }
                if ($art) { break }
            }
            [pscustomobject]@{
                Id = "steam:$appId"
                Name = $name
                Store = 'Steam'
                InstallPath = [System.IO.Path]::GetFullPath($install)
                ArtworkPath = $art
                Candidates = @(Get-ExecutableCandidates -InstallPath $install -GameName $name)
            }
        }
    }
}

function Get-EpicGames {
    param([string]$ManifestRoot = (Join-Path $env:ProgramData 'Epic\EpicGamesLauncher\Data\Manifests'))
    if (-not (Test-Path -LiteralPath $manifestRoot -PathType Container)) { return }
    foreach ($file in @(Get-ChildItem -LiteralPath $manifestRoot -Filter '*.item' -File -ErrorAction SilentlyContinue)) {
        try { $manifest = Get-Content -LiteralPath $file.FullName -Encoding UTF8 -Raw | ConvertFrom-Json } catch { continue }
        if (-not $manifest.PSObject.Properties['InstallLocation'] -or -not $manifest.PSObject.Properties['DisplayName']) { continue }
        if (-not (Test-Path -LiteralPath $manifest.InstallLocation -PathType Container)) { continue }
        if ($manifest.PSObject.Properties['bIsApplication'] -and -not $manifest.bIsApplication) { continue }
        $preferred = @()
        if ($manifest.PSObject.Properties['LaunchExecutable'] -and $manifest.LaunchExecutable) {
            $preferred = @(Join-Path $manifest.InstallLocation $manifest.LaunchExecutable)
        }
        $appName = if ($manifest.PSObject.Properties['AppName']) { $manifest.AppName } else { $file.BaseName }
        [pscustomobject]@{
            Id = "epic:$appName"
            Name = $manifest.DisplayName
            Store = 'Epic'
            InstallPath = $manifest.InstallLocation
            ArtworkPath = $null
            Candidates = @(Get-ExecutableCandidates -InstallPath $manifest.InstallLocation -GameName $manifest.DisplayName -PreferredPaths $preferred)
        }
    }
}

function Get-XboxGameFromFolder {
    param([string]$InstallPath, [string]$Id)
    $configFile = Join-Path $InstallPath 'MicrosoftGame.Config'
    if (-not (Test-Path -LiteralPath $configFile -PathType Leaf)) { return }
    try {
        $config = New-Object System.Xml.XmlDocument
        $config.XmlResolver = $null
        $config.Load($configFile)
    } catch { return }
    $visuals = $config.SelectSingleNode('//*[local-name()="ShellVisuals"]')
    $name = if ($visuals) { $visuals.GetAttribute('DefaultDisplayName') } else { '' }
    if (-not $name -or $name -match '^ms-resource:') {
        $name = [System.IO.Path]::GetFileName($InstallPath.TrimEnd('\', '/'))
        if ($name -eq 'Content') { $name = [System.IO.Path]::GetFileName((Split-Path $InstallPath -Parent)) }
    }
    $preferred = @($config.SelectNodes('//*[local-name()="Executable"]') | ForEach-Object {
        $exe = $_.GetAttribute('Name')
        if ($exe) { Join-Path $InstallPath $exe }
    })
    [pscustomobject]@{
        Id = $Id
        Name = $name
        Store = 'Xbox'
        InstallPath = $InstallPath
        ArtworkPath = $null
        Candidates = @(Get-ExecutableCandidates -InstallPath $InstallPath -GameName $name -PreferredPaths $preferred)
    }
}

function Get-XboxGames {
    $seen = @{}
    foreach ($drive in @(Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Name -match '^[A-Z]$' })) {
        $gameRoots = New-Object 'System.Collections.Generic.List[string]'
        $gameRoots.Add((Join-Path $drive.Root 'XboxGames'))
        # The RGBX v1 root file on this machine stores a UTF-16 relative folder after its 8-byte header.
        $rootMetadata = Join-Path $drive.Root '.GamingRoot'
        if (Test-Path -LiteralPath $rootMetadata -PathType Leaf) {
            try {
                $bytes = [System.IO.File]::ReadAllBytes($rootMetadata)
                if ($bytes.Length -ge 10 -and $bytes.Length -le 4096 -and [System.Text.Encoding]::ASCII.GetString($bytes, 0, 4) -eq 'RGBX' -and [System.BitConverter]::ToUInt32($bytes, 4) -eq 1) {
                    $relativeRoot = [System.Text.Encoding]::Unicode.GetString($bytes, 8, $bytes.Length - 8).TrimEnd([char]0)
                    if ($relativeRoot -and -not [System.IO.Path]::IsPathRooted($relativeRoot) -and $relativeRoot -notmatch '(^|[\\/])\.\.([\\/]|$)') {
                        $resolvedRoot = [System.IO.Path]::GetFullPath((Join-Path $drive.Root $relativeRoot))
                        if ($resolvedRoot.StartsWith($drive.Root, [System.StringComparison]::OrdinalIgnoreCase) -and $resolvedRoot -ne $drive.Root) { $gameRoots.Add($resolvedRoot) }
                    }
                }
            } catch { }
        }
        foreach ($gameRoot in @($gameRoots | Select-Object -Unique)) {
            if (-not (Test-Path -LiteralPath $gameRoot -PathType Container)) { continue }
            foreach ($folder in @(Get-ChildItem -LiteralPath $gameRoot -Directory -ErrorAction SilentlyContinue)) {
                if (($folder.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
                foreach ($install in @((Join-Path $folder.FullName 'Content'), $folder.FullName)) {
                    $game = Get-XboxGameFromFolder -InstallPath $install -Id ('xbox:' + $folder.Name)
                    if ($game) { $seen[$install.ToLowerInvariant()] = $true; $game; break }
                }
            }
        }
    }
    # Older/protected Store installations are discoverable only when their metadata is readable.
    if (Get-Command Get-AppxPackage -ErrorAction SilentlyContinue) {
        foreach ($package in @(Get-AppxPackage -ErrorAction SilentlyContinue)) {
            if (-not $package.InstallLocation -or $seen.ContainsKey($package.InstallLocation.ToLowerInvariant())) { continue }
            $game = Get-XboxGameFromFolder -InstallPath $package.InstallLocation -Id ('xbox:' + $package.PackageFamilyName)
            if ($game) { $seen[$package.InstallLocation.ToLowerInvariant()] = $true; $game }
        }
    }
}

function Get-InstalledGameCatalog {
    [CmdletBinding()]
    param()
    @(Get-SteamGames; Get-EpicGames; Get-XboxGames) | Sort-Object Name, Store, Id -Unique
}

Export-ModuleMember -Function Get-InstalledGameCatalog
