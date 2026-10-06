param([Parameter(Mandatory=$true)][string]$PackagePath, [string]$HelperDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = Get-Item -LiteralPath $PackagePath
function Read-PackageManifest([string]$path) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        if (-not $entry) { throw ('Missing package manifest: ' + $path) }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { return [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $zip.Dispose() }
}
$manifest = Read-PackageManifest $package.FullName
$dependencyRoot = Join-Path $package.DirectoryName 'Dependencies\x64'
$available = @{}
foreach ($dependency in Get-ChildItem -LiteralPath $dependencyRoot -File -Filter '*.appx') {
    $dependencyManifest = Read-PackageManifest $dependency.FullName
    $identity = $dependencyManifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    $available[$identity.GetAttribute('Name')] = [pscustomobject]@{
        name=$identity.GetAttribute('Name'); version=[version]$identity.GetAttribute('Version');
        publisher=$identity.GetAttribute('Publisher'); architecture=$identity.GetAttribute('ProcessorArchitecture'); path=$dependency.FullName
    }
}
foreach ($required in $manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']")) {
    $name = $required.GetAttribute('Name')
    $provided = $available[$name]
    if (-not $provided) { throw ('Missing x64 dependency package: ' + $name) }
    $minimum = [version]$required.GetAttribute('MinVersion')
    if ($provided.version -lt $minimum) { throw ($name + ' payload version ' + $provided.version + ' is below required ' + $minimum) }
    if ($provided.publisher -ne $required.GetAttribute('Publisher')) { throw ('Dependency publisher mismatch: ' + $name) }
    if ($provided.architecture -ne 'x64') { throw ('Dependency architecture mismatch: ' + $name) }
    Write-Host ($name + ' ' + $provided.version + ' satisfies minimum ' + $minimum)
}
if (-not $HelperDirectory) { $HelperDirectory = Join-Path $PSScriptRoot '..\artifacts\helper' }
$helperRoot = (Resolve-Path -LiteralPath $HelperDirectory).Path
$zip = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    foreach ($source in Get-ChildItem -LiteralPath $helperRoot -File -Recurse | Where-Object Extension -NE '.pdb') {
        $relative = $source.FullName.Substring($helperRoot.Length + 1).Replace('\', '/')
        $entry = $zip.GetEntry('Helper/' + $relative)
        if (-not $entry) { throw ('Missing helper runtime payload: ' + $relative) }
        $stream = $entry.Open()
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { $packagedHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose(); $stream.Dispose() }
        if ($packagedHash -ne (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash) { throw ('Helper runtime payload differs: ' + $relative) }
    }
} finally { $zip.Dispose() }
Write-Host 'All helper runtime payload hashes match the published helper. PDBs are separate developer symbols.'
Write-Host 'Package dependency identities and versions passed. Signing, installation, and actual activation remain separate checks.'
