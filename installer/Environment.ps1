function Assert-UnpackagedInstaller {
    if (-not ('Oxp3InstallerIdentity' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Oxp3InstallerIdentity {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    public static extern int GetCurrentPackageFullName(ref int length, IntPtr name);
}
'@
    }
    $length = 0
    if ([Oxp3InstallerIdentity]::GetCurrentPackageFullName([ref]$length, [IntPtr]::Zero) -ne 15700) {
        throw 'Setup must run outside a packaged application. Open Setup.exe from File Explorer.'
    }
}
function Get-InstallerDataPaths([string]$LocalRoot) {
    $mirrorRoot = Join-Path $LocalRoot 'Packages\OXP3.PowerWidget_pv4gfha69c7qe\LocalCache\Local\OXP3PowerWidget'
    return @{ SourceRoot = Join-Path $LocalRoot 'OXP3PowerWidget'; MirrorRoot = $mirrorRoot }
}
function Initialize-InstallerData([string]$LocalRoot) {
    $paths = Get-InstallerDataPaths $LocalRoot
    foreach ($directory in @($paths.SourceRoot,$paths.MirrorRoot)) {
        Assert-Oxp3AuthPath $directory
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        Protect-Oxp3TokenPath $directory $true
    }
    return $paths
}
