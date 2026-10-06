param([string]$MSBuildPath, [switch]$SelfContained, [string]$HelperDirectory, [string]$BuildRoot, [string]$BuildReceiptPath)
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
# .NET Native canonicalizes its CodeView symbol path despite DebugType=none.
# Compile only source in a fresh neutral tree, never under the builder's profile.
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $BuildRoot) { $BuildRoot = Join-Path $env:SystemDrive ('OXP3Build\' + [Guid]::NewGuid().ToString('N')) }
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
if ($BuildRoot -match '(?i)[\\/]Users[\\/]|[\\/]home[\\/]' -or (Test-Path -LiteralPath $BuildRoot)) { throw 'Use a new build directory outside user profiles.' }
New-Item -ItemType Directory -Path $BuildRoot | Out-Null
foreach ($folder in @('helper','shared','widget\Oxp3PowerWidget')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $sourceRoot $folder) -File -Recurse) {
        $relative = $file.FullName.Substring($sourceRoot.Length + 1)
        if ($relative -match '(^|[\\/])(bin|obj)([\\/]|$)') { continue }
        if ($file.Extension -notin @('.cs','.csproj','.xaml','.appxmanifest','.png','.xml')) { continue }
        $destination = Join-Path $BuildRoot $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}
Copy-Item -LiteralPath (Join-Path $sourceRoot 'GameDiscovery.psm1') -Destination $BuildRoot
$project = Join-Path $BuildRoot 'widget\Oxp3PowerWidget\Oxp3PowerWidget.csproj'
$helperProject = Join-Path $BuildRoot 'helper\OXP3.PowerWidget.Helper.csproj'
$helperOutput = $HelperDirectory
if (-not $helperOutput) { $helperOutput = Join-Path $PSScriptRoot ('..\artifacts\helper-publish-' + [Guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $helperOutput) { throw 'Helper publish directory must be new and empty.' }
if ($SelfContained) { & dotnet publish $helperProject --configuration Release --runtime win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true --output $helperOutput --nologo }
else { & dotnet publish $helperProject --configuration Release --no-self-contained --output $helperOutput --nologo }
if ($LASTEXITCODE -ne 0) { throw 'Helper publishing failed; widget package was not built.' }
$packageRoot = Join-Path $PSScriptRoot 'packages\uwp-6.2.14'
& $MSBuildPath $project /restore /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:AppxPackageSigningEnabled=false /p:GenerateAppxPackageOnBuild=true (('/p:AppxPackageDir=' + $packageRoot + '\')) (('/p:HelperPublishDirectory=' + [IO.Path]::GetFullPath($helperOutput))) /m:1 /nologo
if ($LASTEXITCODE -ne 0) { throw 'Widget package build failed; inspect the build diagnostics.' }
$package = Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter '*.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $package) { throw 'Build reported success but no x64 MSIX package was found.' }
& (Join-Path $PSScriptRoot 'Check-BuiltPackage.ps1') -PackagePath $package.FullName -HelperDirectory $helperOutput
if ($BuildReceiptPath) {
    $receipt = @{ runtimeConfigPath = Join-Path $BuildRoot 'helper\bin\Release\net8.0-windows\win-x64\OXP3.PowerWidget.Helper.runtimeconfig.json' }
    [IO.File]::WriteAllText($BuildReceiptPath, ($receipt | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}
Write-Host 'Unsigned widget package built. Package signing, trust, installation, and live Game Bar testing are separate steps.'
