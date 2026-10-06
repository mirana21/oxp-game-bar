param([switch]$SkipWidgetBuild, [string]$NodePath, [string]$SignToolPath)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version='0.1.0.27'
$runtimeRoot=Join-Path $projectRoot ('artifacts\release-helper-' + [Guid]::NewGuid().ToString('N'))
$buildReceipt=Join-Path $projectRoot ('artifacts\widget-build-' + [Guid]::NewGuid().ToString('N') + '.json')
if (-not $SkipWidgetBuild) { & (Join-Path $projectRoot 'widget\Build-Widget.ps1') -SelfContained -HelperDirectory $runtimeRoot -BuildReceiptPath $buildReceipt }
$destination=Join-Path $projectRoot ('releases\OXP3-Game-Power-' + $version + '-alpha-x64')
if(Test-Path -LiteralPath $destination){throw 'Release directory already exists. Preserve it or choose a new version before rebuilding.'}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach($dir in @('Scripts','NativeBridge','Runtime','Widget\Dependencies','Licenses')){New-Item -ItemType Directory -Path (Join-Path $destination $dir) -Force | Out-Null}
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses\NightLight.txt') -Destination (Join-Path $destination 'Licenses\NightLight.txt')
$packageRoot=Join-Path $projectRoot ('widget\packages\uwp-6.2.14\Oxp3PowerWidget_' + $version + '_x64_Test')
Copy-Item -LiteralPath (Join-Path $packageRoot ('Oxp3PowerWidget_' + $version + '_x64.msix')) -Destination (Join-Path $destination 'Widget\OXP3.GamePower.msix')
Get-ChildItem -LiteralPath (Join-Path $packageRoot 'Dependencies\x64') -Filter '*.appx' | Copy-Item -Destination (Join-Path $destination 'Widget\Dependencies')
foreach($file in @('SetupEngine.ps1','Environment.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $destination ('Scripts\' + $file))}
$bridgeRoot=Join-Path $projectRoot 'native-bridge'
$modules=Get-Content -LiteralPath (Join-Path $bridgeRoot 'startup-modules.json') -Raw|ConvertFrom-Json
foreach($item in $modules.modules){
    $source=Join-Path $bridgeRoot $item.file
    if((Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() -ne $item.sha256){throw ('Startup source hash mismatch: '+$item.file)}
    Copy-Item -LiteralPath $source -Destination (Join-Path $destination ('NativeBridge\'+$item.file))
}
Copy-Item -LiteralPath (Join-Path $bridgeRoot 'startup-modules.json') -Destination (Join-Path $destination 'NativeBridge\startup-modules.json')
$node=$NodePath
if (-not $node) {
    $runtimeCache=Join-Path $projectRoot 'artifacts\runtime'
    $node=Join-Path $runtimeCache 'node-v24.19.0-win-x64.exe'
    if (-not (Test-Path -LiteralPath $node)) {
        New-Item -ItemType Directory -Path $runtimeCache -Force | Out-Null
        $download=$node+'.download'
        try {
            Invoke-WebRequest -Uri 'https://nodejs.org/dist/v24.19.0/win-x64/node.exe' -OutFile $download -UseBasicParsing
            if ((Get-FileHash -LiteralPath $download).Hash.ToLowerInvariant() -ne $modules.nodeSha256) { throw 'Downloaded Node runtime does not match the reviewed hash.' }
            Move-Item -LiteralPath $download -Destination $node
        } finally {
            if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download }
        }
    }
}
if((Get-FileHash -LiteralPath $node).Hash.ToLowerInvariant() -ne $modules.nodeSha256){throw 'Unreviewed bundled runtime.'}
Copy-Item -LiteralPath $node -Destination (Join-Path $destination 'Runtime\node.exe')
$nodeVersion=(& $node --version).Trim()
if($nodeVersion -notmatch '^v\d+\.\d+\.\d+$'){throw 'Unexpected runtime version'}
Invoke-WebRequest -Uri ('https://raw.githubusercontent.com/nodejs/node/'+$nodeVersion+'/LICENSE') -OutFile (Join-Path $destination 'Licenses\Node.txt')
foreach($name in @('LICENSE.txt','THIRD-PARTY-NOTICES.txt')){if(Test-Path -LiteralPath (Join-Path $runtimeRoot $name)){Copy-Item -LiteralPath (Join-Path $runtimeRoot $name) -Destination (Join-Path $destination ('Licenses\DotNet-'+$name))}}
$runtimeConfigPath=Join-Path $projectRoot 'helper\bin\Release\net8.0-windows\win-x64\OXP3.PowerWidget.Helper.runtimeconfig.json'
if (-not $SkipWidgetBuild) { $runtimeConfigPath=(Get-Content -LiteralPath $buildReceipt -Raw | ConvertFrom-Json).runtimeConfigPath }
$runtimeConfig=Get-Content -LiteralPath $runtimeConfigPath -Raw|ConvertFrom-Json
$netRuntimeVersion=($runtimeConfig.runtimeOptions.includedFrameworks|Where-Object name -EQ 'Microsoft.NETCore.App'|Select-Object -First 1).version
if(-not $netRuntimeVersion){throw 'Bundled .NET runtime version was not found'}
$nugetRoot=$env:NUGET_PACKAGES
if (-not $nugetRoot) { $nugetRoot=Join-Path $env:USERPROFILE '.nuget\packages' }
foreach($pair in @(@('microsoft.netcore.app.runtime.win-x64','LICENSE.TXT','DotNet-license.txt'),@('microsoft.netcore.app.runtime.win-x64','THIRD-PARTY-NOTICES.TXT','DotNet-notices.txt'),@('microsoft.windowsdesktop.app.runtime.win-x64','LICENSE','WindowsDesktop-license.txt'))){
    $source=Join-Path $nugetRoot ($pair[0]+'\'+$netRuntimeVersion+'\'+$pair[1])
    if(-not (Test-Path -LiteralPath $source)){throw ('Runtime notice missing: '+$pair[0]+' '+$pair[1])}
    Copy-Item -LiteralPath $source -Destination (Join-Path $destination ('Licenses\'+$pair[2]))
}
$certificate=Get-ChildItem Cert:\CurrentUser\My | Where-Object {$_.Subject -eq 'CN=OXP3Personal' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date)} | Select-Object -First 1
if(-not $certificate){throw 'A matching publisher signing certificate is required to build a release.'}
Export-Certificate -Cert $certificate -FilePath (Join-Path $destination 'Publisher.cer')|Out-Null
$signTool=$SignToolPath
if (-not $signTool) { $signTool=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe' }
if (-not (Test-Path -LiteralPath $signTool)) { throw 'Windows SDK signing tool missing. Supply -SignToolPath.' }
& $signTool sign /fd SHA256 /s My /sha1 $certificate.Thumbprint (Join-Path $destination 'Widget\OXP3.GamePower.msix')
if($LASTEXITCODE -ne 0){throw 'Release package signing failed'}
$readme=@'
OXP3 Game Power — alpha 0.1.0.27

For Windows 11 x64, Intel ONEXPLAYER 3 and ONEXConsole 0.10.3-fix2.

Per-game TDP, brightness, and Night light in a controller-friendly Game Bar widget.
We built this because ONEXConsole's TDP controls are awkward to use while
playing, and the ONEXPLAYER 3's Xbox fullscreen experience lacks quick
brightness and Night light controls.

This app is in alpha. If you run into trouble, report what happened and any
error message through the project's GitHub Issues page.

Extract the ZIP, open Setup.exe, and choose Install / Repair.
Open Xbox Game Bar → Widgets → OXP3 Game Power.

To update, run Setup.exe and choose Install / Repair.
To remove, choose Remove in Setup or uninstall OXP3 Game Power from Windows Installed apps.
Uninstalling restores normal ONEXConsole startup.
Saved wattages are retained for reinstall.
'@
[IO.File]::WriteAllText((Join-Path $destination 'Read me.txt'),$readme,[Text.UTF8Encoding]::new($false))
$files=@(Get-ChildItem -LiteralPath $destination -File -Recurse | ForEach-Object {@{path=$_.FullName.Substring($destination.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})
$release=@{formatVersion=1;version=$version;packageFamily='OXP3.PowerWidget_pv4gfha69c7qe';certificateThumbprint=$certificate.Thumbprint;nodeSha256=$modules.nodeSha256;files=$files;vendorFiles=@(@{path='C:\Program Files\OneXConsole\OneXConsole.exe';sha256='20610ea185f83d2b2acc709681125f0f4e1c9b1dcb2ce3881e9b43e8c067adc9'},@{path='C:\Program Files\OneXConsole\resources\app.asar';sha256='b232761f04bfffbc7bb77c664d661e1625186b1121c24bc4ba02e7c8b532d5b0'})}
[IO.File]::WriteAllText((Join-Path $destination 'release.json'),($release|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
$manifestHash=(Get-FileHash -LiteralPath (Join-Path $destination 'release.json')).Hash.ToLowerInvariant()
$source=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Setup.cs') -Raw).Replace('@@MANIFESTSHA@@',$manifestHash)
$generated=Join-Path $projectRoot 'artifacts\Setup.generated.cs'
[IO.File]::WriteAllText($generated,$source,[Text.UTF8Encoding]::new($false))
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ ('/win32manifest:'+(Join-Path $PSScriptRoot 'Setup.manifest')) /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:Microsoft.CSharp.dll ('/out:'+(Join-Path $destination 'Setup.exe')) $generated
if($LASTEXITCODE -ne 0){throw 'Setup executable build failed'}
& $signTool sign /fd SHA256 /s My /sha1 $certificate.Thumbprint (Join-Path $destination 'Setup.exe')
if($LASTEXITCODE -ne 0){throw 'Setup signing failed'}
foreach($path in @('Setup.exe','Widget\OXP3.GamePower.msix')){& $signTool verify /pa (Join-Path $destination $path);if($LASTEXITCODE -ne 0){throw 'Release signature validation failed'}}
$expectedFiles=@($release.files.path) + @('release.json','Setup.exe')
$actualFiles=@(Get-ChildItem -LiteralPath $destination -File -Recurse | ForEach-Object { $_.FullName.Substring($destination.Length+1) })
if (Compare-Object $expectedFiles $actualFiles) { throw 'Unexpected file in release staging directory.' }
$zip=$destination+'.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($destination,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
& (Join-Path $PSScriptRoot 'Check-ReleasePrivacy.ps1') -Path $zip
Write-Output ('Alpha distribution bundle created: '+$zip)
