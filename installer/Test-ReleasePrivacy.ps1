$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$root = Join-Path ([IO.Path]::GetTempPath()) ('oxp3-release-privacy-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
function Make-Zip([string]$path, [string]$name, [byte[]]$bytes) {
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try { $stream = $zip.CreateEntry($name).Open(); try { $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() } } finally { $zip.Dispose() }
}
try {
    $fixtures = @(
        @{name='helper.exe';bytes=[Text.Encoding]::UTF8.GetBytes('RSDS' + ('x'*20) + 'C:\Users\fixture-owner\source\helper.pdb');error='Personal profile path'},
        @{name='widget.dll';bytes=[byte[]](@(1) + [Text.Encoding]::Unicode.GetBytes('C:\Users\fixture-owner\widget.pdb'));error='Personal profile path'},
        @{name='metadata.bin';bytes=[Text.Encoding]::BigEndianUnicode.GetBytes('fixture-private-machine');error='Build-machine identity'},
        @{name='widget.pdb';bytes=[byte[]]@(1);error='Development/private file'},
        @{name='remembered-watts.json';bytes=[Text.Encoding]::UTF8.GetBytes('{}');error='Machine settings'},
        @{name='identity.bin';bytes=[Text.Encoding]::UTF8.GetBytes('S-1-5-21-123-456-789-1001');error='Windows account SID'},
        @{name='boundary.bin';bytes=[Text.Encoding]::UTF8.GetBytes(([string][char]0*(1024*1024+4087)) + 'fixture-private-machine');error='Build-machine identity'}
    )
    $index = 0
    foreach ($fixture in $fixtures) {
        $index++
        $inner = Join-Path $root ('inner' + $index + '.msix')
        $outer = Join-Path $root ('outer' + $index + '.zip')
        Make-Zip $inner $fixture.name $fixture.bytes
        Make-Zip $outer 'Widget/app.msix' ([IO.File]::ReadAllBytes($inner))
        $refused = $false
        try { & (Join-Path $PSScriptRoot 'Check-ReleasePrivacy.ps1') -Path $outer -ForbiddenText 'fixture-private-machine' | Out-Null }
        catch { if ($_.Exception.Message -notlike ('*' + $fixture.error + '*')) { throw }; $refused = $true }
        if (-not $refused) { throw ('Privacy scan missed fixture: ' + $fixture.name) }
    }
    $clean = Join-Path $root 'clean.zip'
    Make-Zip $clean 'widget.dll' ([Text.Encoding]::UTF8.GetBytes('RSDS' + ('x'*20) + '.\Oxp3PowerWidget.pdb'))
    & (Join-Path $PSScriptRoot 'Check-ReleasePrivacy.ps1') -Path $clean -ForbiddenText 'fixture-private-machine' | Out-Null
    Write-Output 'PASS: privacy gate rejects nested binary UTF-8/UTF-16 identity leaks, account SIDs, symbols/settings, and chunk-boundary leaks; accepts a neutral symbol reference.'
} finally {
    # This is a freshly created, explicit fixture directory under the temp root.
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
