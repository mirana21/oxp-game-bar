param([string]$MSBuildPath, [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
if (-not $MSBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $MSBuildPath = & $vswhere -latest -products '*' -version '[17.0,18.0)' -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
    }
}
if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath)) {
    throw 'The native widget needs Visual Studio 2022 UWP build tools and Windows SDK 10.0.26100.0. The .NET 6 SDK alone cannot package this UWP app. No system changes were made.'
}
$project = Join-Path $PSScriptRoot 'Oxp3PowerWidget\Oxp3PowerWidget.csproj'
$helperProject = Join-Path $PSScriptRoot '..\helper\OXP3.PowerWidget.Helper.csproj'
$helperOutput = Join-Path $PSScriptRoot '..\artifacts\helper'
if ($SelfContained) { $helperOutput = Join-Path $PSScriptRoot '..\artifacts\helper-release-singlefile' }
if ($SelfContained) { & dotnet publish $helperProject --configuration Release --runtime win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true --output $helperOutput --nologo }
else { & dotnet publish $helperProject --configuration Release --no-self-contained --output $helperOutput --nologo }
if ($LASTEXITCODE -ne 0) { throw 'Helper publishing failed; widget package was not built.' }
& $MSBuildPath $project /restore /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:AppxPackageSigningEnabled=false /p:GenerateAppxPackageOnBuild=true (('/p:HelperPublishDirectory=' + [IO.Path]::GetFullPath($helperOutput))) /m:1 /nologo
if ($LASTEXITCODE -ne 0) { throw 'Widget package build failed; inspect the build diagnostics.' }
$packageRoot = Join-Path $PSScriptRoot 'packages\uwp-6.2.14'
$package = Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter '*.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $package) { throw 'Build reported success but no x64 MSIX package was found.' }
& (Join-Path $PSScriptRoot 'Check-BuiltPackage.ps1') -PackagePath $package.FullName -HelperDirectory $helperOutput
Write-Host 'Unsigned widget package built. Package signing, trust, installation, and live Game Bar testing are separate steps.'
