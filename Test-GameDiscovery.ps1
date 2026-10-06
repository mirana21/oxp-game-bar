$ErrorActionPreference = 'Stop'
$module = Import-Module (Join-Path $PSScriptRoot 'GameDiscovery.psm1') -Force -PassThru
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$fixtureRoot = Join-Path $tempPrefix ('OXP3DiscoveryTest-' + [Guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false)
$witcher = 'The Witcher 3: Wild Hunt ' + [char]0x2014 + ' Remastered'
$international = 'Pok' + [char]0x00E9 + 'mon' + [char]0x2122 + ' ' + [char]0x6E38 + [char]0x620F + ' ' + [char]::ConvertFromUtf32(0x1F3AE)
try {
    $steam = Join-Path $fixtureRoot 'Steam'
    $library = Join-Path $fixtureRoot ('Library-' + [char]0x00FC + [char]0x6E38)
    New-Item -ItemType Directory -Path (Join-Path $steam 'steamapps'), (Join-Path $library 'steamapps') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $steam 'steamapps\libraryfolders.vdf'), ('"libraryfolders" { "1" { "path" "' + $library.Replace('\', '\\') + '" } }'), $utf8)
    $names = @($witcher, $international)
    for ($index = 0; $index -lt $names.Count; $index++) {
        $gameFolder = 'Game-' + $index + '-' + [char]0x00FC + [char]0x6E38
        $install = Join-Path $library ('steamapps\common\' + $gameFolder)
        New-Item -ItemType Directory -Path $install -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $install 'game.exe'), [byte[]]@())
        [IO.File]::WriteAllText((Join-Path $library ('steamapps\appmanifest_' + (100 + $index) + '.acf')), ('"AppState" { "appid" "' + (100 + $index) + '" "name" "' + $names[$index] + '" "installdir" "' + $gameFolder + '" }'), $utf8)
    }
    $games = @(& $module { param($root) Get-SteamGames -SteamRoots @($root) } $steam)
    if ($games.Count -ne 2) { throw 'Unicode Steam library paths were not discovered.' }
    foreach ($index in 0, 1) {
        $game = $games | Where-Object Id -EQ ('steam:' + (100 + $index))
        if ($game.Name -cne $names[$index] -or $game.Candidates.Count -ne 1) { throw 'Steam title or executable path lost Unicode characters.' }
    }
    $epic = Join-Path $fixtureRoot 'Epic'
    $epicInstall = Join-Path $fixtureRoot 'EpicGame'
    New-Item -ItemType Directory -Path $epic, $epicInstall -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $epicInstall 'game.exe'), [byte[]]@())
    $metadata = @{ AppName = 'test'; DisplayName = $international; InstallLocation = $epicInstall; LaunchExecutable = 'game.exe'; bIsApplication = $true } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText((Join-Path $epic 'test.item'), $metadata, $utf8)
    $game = & $module { param($root) Get-EpicGames -ManifestRoot $root } $epic
    if ($game.Name -cne $international) { throw 'Epic title lost Unicode characters.' }
    foreach ($encoding in @($utf8, [Text.Encoding]::Unicode)) {
        $xbox = Join-Path $fixtureRoot 'XboxGame'
        New-Item -ItemType Directory -Path $xbox -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $xbox 'game.exe'), [byte[]]@())
        $xml = '<?xml version="1.0" encoding="' + $encoding.WebName + '"?><Game><ShellVisuals DefaultDisplayName="' + [Security.SecurityElement]::Escape($international) + '"/><Executable Name="game.exe"/></Game>'
        [IO.File]::WriteAllText((Join-Path $xbox 'MicrosoftGame.Config'), $xml, $encoding)
        $game = & $module { param($root) Get-XboxGameFromFolder -InstallPath $root -Id 'xbox:test' } $xbox
        if ($game.Name -cne $international) { throw 'Xbox XML title lost Unicode characters.' }
    }
    Write-Output 'PASS: UTF-8 Steam/Epic titles and library paths, UTF-8/UTF-16 Xbox XML, accents, punctuation, CJK and emoji. No game launched.'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^OXP3DiscoveryTest-[a-f0-9]{32}$') { throw 'Invalid fixture cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    Remove-Module $module
}
