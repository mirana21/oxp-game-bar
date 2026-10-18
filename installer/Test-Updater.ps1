$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UpdateSupport.ps1')
function Release-Fixture([string]$Version, [bool]$Preview = $true, [bool]$Draft = $false) {
    @{ tag_name = 'v' + $Version; prerelease = $Preview; draft = $Draft; assets = @(@{name='OXP3-Game-Power-' + $Version + '-alpha-x64.zip';state='uploaded';size=1024;browser_download_url='https://github.com/mirana21/oxp-game-bar/releases/download/v' + $Version + '/OXP3-Game-Power-' + $Version + '-alpha-x64.zip'}) }
}
$releases = @((Release-Fixture '0.1.0.30'),(Release-Fixture '0.1.0.32' $true $true),(Release-Fixture '0.1.0.31'),(Release-Fixture '0.1.0.29' $false))
$chosen = Get-UpdateCandidate $releases ([version]'0.1.0.29') $true
if ($chosen.Version -ne [version]'0.1.0.31') { throw 'Alpha channel must select the highest published complete version, not API order.' }
if (Get-UpdateCandidate $releases ([version]'0.1.0.29') $false) { throw 'Stable channel must not install alpha releases or reinstall the current version.' }
if (Get-UpdateCandidate $releases ([version]'0.1.0.40') $true) { throw 'Updater must never silently downgrade.' }
if ((Get-UpdateCandidate $releases ([version]'0.1.0.31') $true -RepairCurrent).Version -ne [version]'0.1.0.31') { throw 'A missing or damaged package must be repairable from the current release.' }
$incomplete = Release-Fixture '0.1.0.33'; $incomplete.assets[0].state = 'new'
if (Get-UpdateCandidate @($incomplete) ([version]'0.1.0.29') $true) { throw 'Incomplete publication must not become installable.' }
Assert-UpdateDownloadUri $chosen.Asset.browser_download_url 'mirana21/oxp-game-bar' $chosen.Version.ToString() $chosen.Asset.name
foreach ($url in @('http://github.com/mirana21/oxp-game-bar/releases/download/v0.1.0.31/x.zip','https://example.com/x.zip','https://github.com/other/repo/releases/download/v0.1.0.31/x.zip')) {
    $refused = $false; try { Assert-UpdateDownloadUri $url 'mirana21/oxp-game-bar' '0.1.0.31' 'x.zip' } catch { $refused = $true }
    if (-not $refused) { throw 'Untrusted download address was accepted.' }
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Join-Path ([IO.Path]::GetTempPath()) ('Oxp3UpdaterFixture-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    foreach ($names in @(@('Scripts/Update.ps1','Setup.exe'),@('../escape.txt'),@('C:/escape.txt'),@('file.txt','FILE.TXT'),@('NUL.txt'),@('folder. /file.txt'))) {
        $zipPath = Join-Path $root ([Guid]::NewGuid().ToString('N') + '.zip')
        $zip = [IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
        try { foreach ($name in $names) { $writer = [IO.StreamWriter]::new($zip.CreateEntry($name).Open()); try { $writer.Write('fixture') } finally { $writer.Dispose() } } } finally { $zip.Dispose() }
        $destination = Join-Path $root ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $destination | Out-Null
        $refused = $false; try { Expand-UpdateArchive $zipPath $destination } catch { $refused = $true }
        if ($names[0] -eq 'Scripts/Update.ps1') {
            if ($refused -or -not (Test-Path -LiteralPath (Join-Path $destination 'Setup.exe'))) { throw 'Valid archive did not extract.' }
        } elseif (-not $refused -or @(Get-ChildItem -LiteralPath $destination -Recurse).Count) { throw 'Unsafe archive must fail before extracting any entry.' }
    }
} finally {
    if ([IO.Path]::GetFullPath($root).StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force }
}
function Get-AuthenticodeSignature { param($LiteralPath) @{ Status=$fixtureStatus; SignerCertificate=@{Thumbprint=$fixturePublisher} } }
$fixtureStatus=0; $fixturePublisher='expected'
Assert-UpdatePublisher 'fixture.exe' 'expected'
foreach ($invalid in @(@{Status=1;Publisher='expected'},@{Status=0;Publisher='different'})) {
    $fixtureStatus=$invalid.Status; $fixturePublisher=$invalid.Publisher
    $refused=$false; try { Assert-UpdatePublisher 'fixture.exe' 'expected' } catch { $refused=$true }
    if (-not $refused) { throw 'Unsigned or wrong-publisher update was accepted.' }
}
function Invoke-UpdateSetup { param($Directory,$Mode) $script:calls += $Mode; if ($fixtureTimeout) { throw 'Still running' }; if ($Mode -eq 'Update') { return $fixtureInstall }; return $fixtureRestore }
$script:calls=@(); $fixtureInstall=0; $fixtureRestore=0; $fixtureTimeout=$false
if ((Invoke-UpdateTransaction 'new' 'previous' { $true }) -ne 'updated' -or ($calls -join ',') -ne 'Update') { throw 'Successful update must not restore an older version.' }
$script:calls=@()
if ((Invoke-UpdateTransaction 'new' 'previous' { $true } { $false }) -ne 'restored' -or ($calls -join ',') -ne 'Update,Rollback') { throw 'Installer success must be verified against the registered package.' }
$script:calls=@(); $fixtureInstall=1
if ((Invoke-UpdateTransaction 'new' 'previous' { $true }) -ne 'restored' -or ($calls -join ',') -ne 'Update,Rollback') { throw 'Failed installation must restore the previous signed bundle.' }
$script:calls=@()
if ((Invoke-UpdateTransaction 'new' 'previous' { $false }) -ne 'cancelled' -or ($calls -join ',') -ne 'Update') { throw 'User removal must not trigger reinstallation.' }
$script:calls=@(); $fixtureTimeout=$true
$refused=$false; try { Invoke-UpdateTransaction 'new' 'previous' { $true } | Out-Null } catch { $refused=$true }
if (-not $refused -or ($calls -join ',') -ne 'Update') { throw 'An installer still running must not be killed or raced by rollback.' }
Write-Output 'PASS: release/channel selection, no downgrade, trusted download paths, archive validation before extraction, publisher identity, failed-install restoration and uninstall/timeout guards. No network or installation used.'
