$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Environment.ps1')
# Run production startup validation with only the Windows identity adapter
# replaced. Synthetic accounts need not exist on the test machine.
$tokens=$null; $errors=$null
$startupAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\native-bridge\startup-task-common.ps1'),[ref]$tokens,[ref]$errors)
if($errors){throw ($errors.Message -join '; ')}
$review=$startupAst.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ReviewedStartupXml'},$true)
$identityAdapter='[Security.Principal.WindowsIdentity]::GetCurrent().User.Value'
if(-not $review -or -not $review.Extent.Text.Contains($identityAdapter)){throw 'Production startup identity adapter changed.'}
. ([scriptblock]::Create($review.Extent.Text.Replace($identityAdapter,'$fixtureSid')))
$setupAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'SetupEngine.ps1'),[ref]$tokens,[ref]$errors)
if($errors){throw ($errors.Message -join '; ')}
foreach($name in @('Get-StartupInstallation','Assert-Compatibility')){
    $function=$setupAst.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    if(-not $function){throw ('Production installer function missing: '+$name)}
    $functionText=$function.Extent.Text
    if($name -eq 'Assert-Compatibility'){
        # Hosted CI runs Windows Server. Replace only OS identity reads;
        # the production Windows 11 condition itself still runs unchanged.
        $osBuildAdapter='[Environment]::OSVersion.Version.Build'
        $bitnessAdapter='[Environment]::Is64BitOperatingSystem'
        if(-not $functionText.Contains($osBuildAdapter) -or -not $functionText.Contains($bitnessAdapter)){throw 'Production OS identity adapters changed.'}
        $functionText=$functionText.Replace($osBuildAdapter,'$fixture.WindowsBuild').Replace($bitnessAdapter,'$fixture.Is64Bit')
    }
    . ([scriptblock]::Create($functionText))
}
$codeRoot='C:\PortabilityFixture\Bridge'
$bundleRoot='C:\PortabilityFixture\ExtractedSetup'
$release=@{nodeSha256='runtime-fixture-hash';vendorFiles=@(@{path='C:\Program Files\OneXConsole\OneXConsole.exe';sha256='vendor-exe-fixture-hash'},@{path='C:\Program Files\OneXConsole\resources\app.asar';sha256='vendor-asar-fixture-hash'})}
$fixture=@{}
function Assert-Oxp3AuthPath { param($ItemPath) }
function Protect-Oxp3TokenPath { param($ItemPath,$Directory) $fixture.Protected += $ItemPath }
function New-Item { param($ItemType,$Path,[switch]$Force) $fixture.Created += $Path }
function Test-Path { param($LiteralPath) -not $LiteralPath.EndsWith('startup-receipt.json') }
function Export-ScheduledTask { param($TaskName,$TaskPath) if($TaskName -ne 'OneXConsole' -or $TaskPath -ne '\'){throw 'Wrong vendor task'}; $fixture.TaskXml }
function Get-CimInstance {
    param($ClassName)
    if($ClassName -eq 'Win32_ComputerSystem'){return @{Model='ONEXPLAYER 3'}}
    if($ClassName -eq 'Win32_Processor'){return @{Manufacturer='GenuineIntel'}}
    throw 'Unexpected system query'
}
function Startup-Hash {
    param($ItemPath)
    if($fixture.BadVendor -and $ItemPath.EndsWith('app.asar')){return 'different-vendor-hash'}
    if($ItemPath.EndsWith('node.exe')){return $release.nodeSha256}
    foreach($file in $release.vendorFiles){if($file.path -eq $ItemPath){return $file.sha256}}
    throw 'Unexpected file hash'
}
function Get-AuthenticodeSignature { param($LiteralPath) @{Status=0} }
function Get-AppxPackage { param($Name) if($Name -ne 'Microsoft.XboxGamingOverlay'){throw 'Wrong package query'}; @{Name=$Name} }
function Task-Xml([string]$sid) {
    '<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Principals><Principal><UserId>'+ $sid +'</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Actions><Exec><Command>wscript.exe</Command><Arguments>"C:\Program Files\OneXConsole\resources\resources\tools\OneXConsoleStartup.vbs"</Arguments></Exec></Actions></Task>'
}
foreach($account in @(
    @{sid='S-1-5-21-111-222-333-1001';local='C:\Users\PlayerAlpha\AppData\Local'},
    @{sid='S-1-5-21-444-555-666-1007';local='C:\Profiles\PlayerBeta\AppData\Local'})){
    $fixtureSid=$account.sid
    $fixture=@{TaskXml=(Task-Xml $fixtureSid);Created=@();Protected=@();WindowsBuild=26100;Is64Bit=$true}
    $startup=Get-StartupInstallation
    if($startup.Existing -or $startup.Original -ne $fixture.TaskXml -or $startup.Current -ne $fixture.TaskXml){throw 'Fresh install did not use the destination account startup task.'}
    $paths=Initialize-InstallerData $account.local
    foreach($path in @($paths.SourceRoot,$paths.MirrorRoot)){
        if(-not $path.StartsWith($account.local+'\',[StringComparison]::OrdinalIgnoreCase) -or $path -notin $fixture.Created -or $path -notin $fixture.Protected){throw 'Settings did not follow destination LocalAppData.'}
    }
    Assert-Compatibility
    $fixture.WindowsBuild=20348
    $refused=$false
    try{Assert-Compatibility}catch{if($_.Exception.Message -ne 'This release requires Windows 11 x64.'){throw};$refused=$true}
    if(-not $refused){throw 'Unsupported Windows build was accepted.'}
    $fixture.WindowsBuild=26100
    $fixture.Is64Bit=$false
    $refused=$false
    try{Assert-Compatibility}catch{if($_.Exception.Message -ne 'This release requires Windows 11 x64.'){throw};$refused=$true}
    if(-not $refused){throw 'Unsupported OS architecture was accepted.'}
    $fixture.Is64Bit=$true
    $fixture.TaskXml=Task-Xml 'S-1-5-21-999-888-777-1009'
    $refused=$false
    try{Get-StartupInstallation | Out-Null}catch{if($_.Exception.Message -ne 'Native startup belongs to a different Windows user.'){throw};$refused=$true}
    if(-not $refused){throw 'A different account startup task was accepted.'}
    $fixture.BadVendor=$true
    $refused=$false
    try{Assert-Compatibility}catch{if($_.Exception.Message -notlike 'This ONEXConsole build is not supported*'){throw};$refused=$true}
    if(-not $refused){throw 'An incompatible vendor build was accepted.'}
}
Write-Output 'PASS: production fresh-install validation uses each destination account/task and LocalAppData path, accepts supported Intel OXP3 fixtures and rejects wrong-account tasks, unsupported Windows/architecture and incompatible vendor files. No installation performed.'
