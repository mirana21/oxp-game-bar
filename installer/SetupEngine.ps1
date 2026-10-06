param([ValidateSet('Check','Install','Remove','Bootstrap')][string]$Mode = 'Check', [switch]$NoActivate)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$env:PSModulePath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules'
$bundleRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$codeRoot = 'C:\Program Files\OXP3 Game Power Bridge'
$setupRoot = 'C:\Program Files\OXP3 Game Power Setup'
. (Join-Path $bundleRoot 'NativeBridge\startup-task-common.ps1')
. (Join-Path $PSScriptRoot 'Environment.ps1')
function Assert-Bundle {
    $release = Get-Content -LiteralPath (Join-Path $bundleRoot 'release.json') -Raw | ConvertFrom-Json
    if ($release.formatVersion -ne 1 -or $release.packageFamily -ne 'OXP3.PowerWidget_pv4gfha69c7qe') { throw 'Unexpected installation bundle.' }
    foreach ($file in $release.files) {
        $full = [IO.Path]::GetFullPath((Join-Path $bundleRoot $file.path))
        if (-not $full.StartsWith($bundleRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid bundle path.' }
        Assert-Oxp3AuthPath $full
        if ((Startup-Hash $full) -ne $file.sha256) { throw ('Installation file changed: ' + $file.path) }
    }
    return $release
}
function Copy-ProtectedBundle {
    Assert-NoStartupReparse $setupRoot
    New-Item -ItemType Directory -Path $setupRoot -Force | Out-Null
    Protect-StartupCode $setupRoot $true
    if ($bundleRoot -ine $setupRoot) {
        foreach ($file in @($release.files.path) + @('release.json','Setup.exe')) {
            $source = Join-Path $bundleRoot $file
            $destination = Join-Path $setupRoot $file
            Assert-Oxp3AuthPath $destination
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination -Force
            Protect-StartupCode $destination $false
        }
    }
}
function Get-StartupInstallation {
    $receiptPath = Join-Path $codeRoot 'startup-receipt.json'
    Assert-Oxp3AuthPath $receiptPath
    if (-not (Test-Path -LiteralPath $receiptPath)) {
        $original = Export-ScheduledTask -TaskName OneXConsole -TaskPath '\'
        [void](Get-ReviewedStartupXml $original)
        return @{ Existing = $false; Original = $original; Current = $original }
    }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $current = Export-ScheduledTask -TaskName OneXConsole -TaskPath '\'
    if ($receipt.userSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or $receipt.taskName -ne 'OneXConsole' -or $receipt.taskPath -ne '\' -or $receipt.formatVersion -ne 1 -or (Startup-TextHash $current) -ne $receipt.installedSha256) { throw 'ONEXConsole startup changed after installation; no task was overwritten.' }
    $original = Get-Content -LiteralPath (Join-Path $codeRoot 'OneXConsole.original.xml') -Raw
    if ((Startup-TextHash $original) -ne $receipt.originalSha256) { throw 'Startup backup verification failed.' }
    [void](Get-ReviewedStartupXml $original)
    return @{ Existing = $true; Original = $original; Current = $current; Receipt = $receipt }
}
function Assert-Compatibility {
    if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Build -lt 22000) { throw 'This release requires Windows 11 x64.' }
    $system = Get-CimInstance Win32_ComputerSystem
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    if ($system.Model -ne 'ONEXPLAYER 3' -or $cpu.Manufacturer -ne 'GenuineIntel') { throw 'This release is tested for the Intel ONEXPLAYER 3 only.' }
    foreach ($file in $release.vendorFiles) {
        if (-not (Test-Path -LiteralPath $file.path) -or (Startup-Hash $file.path) -ne $file.sha256) { throw 'This ONEXConsole build is not supported. Its files were not changed.' }
    }
    $signature = Get-AuthenticodeSignature -LiteralPath 'C:\Program Files\OneXConsole\OneXConsole.exe'
    if ([int]$signature.Status -ne 0) { throw 'ONEXConsole signature verification failed.' }
    $node = Join-Path $bundleRoot 'Runtime\node.exe'
    if ((Startup-Hash $node) -ne $release.nodeSha256 -or [int](Get-AuthenticodeSignature -LiteralPath $node).Status -ne 0) { throw 'Bundled runtime verification failed.' }
    if (-not (Get-AppxPackage -Name Microsoft.XboxGamingOverlay)) { throw 'Install Xbox Game Bar from Microsoft Store first.' }
}
$release = Assert-Bundle
$runtimeRoot = Join-Path $codeRoot ('Versions\' + $release.version)
if ([version]$release.version -ne [version]'0.1.0.26') { throw 'Unexpected release version.' }
Assert-StartupAdmin
if ($Mode -eq 'Bootstrap') {
    # A Task Scheduler action runs outside the launcher's MSIX identity. Stage
    # only verified installer files into an administrator-controlled directory.
    Copy-ProtectedBundle
    $taskName = 'OXP3GamePowerSetup-' + [Guid]::NewGuid().ToString('N')
    $action = New-ScheduledTaskAction -Execute (Join-Path $setupRoot 'Setup.exe') -Argument ('--unpackaged --cleanup-task ' + $taskName)
    $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings | Out-Null
    Start-ScheduledTask -TaskName $taskName
    Write-Output 'Setup reopened outside the packaged launcher.'
    exit 0
}
Assert-UnpackagedInstaller
if ($Mode -eq 'Remove') {
    $startup = Get-StartupInstallation
    $local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $data = Join-Path $local 'OXP3PowerWidget'
    $preferences = Join-Path $local 'Packages\OXP3.PowerWidget_pv4gfha69c7qe\LocalCache\Local\OXP3PowerWidget\remembered-watts.json'
    $backup = Join-Path $data 'preferences-backup.json'
    Assert-Oxp3AuthPath $backup
    Assert-Oxp3AuthPath $preferences
    if (Test-Path -LiteralPath $preferences) { Copy-Item -LiteralPath $preferences -Destination $backup -Force; Protect-Oxp3TokenPath $backup $false }
    if ($startup.Existing) {
        # Updating the task restores future launches without stopping its
        # current instance. Keep the live bridge and its files until native
        # exits normally: closing its owned handles can terminate Electron.
        if ((Startup-TextHash (Export-ScheduledTask -TaskName OneXConsole -TaskPath '\')) -ne (Startup-TextHash $startup.Current)) { throw 'Startup changed during removal; registration was refused.' }
        Register-ScheduledTask -TaskName OneXConsole -TaskPath '\' -Xml $startup.Original -Force | Out-Null
        Move-Item -LiteralPath (Join-Path $codeRoot 'startup-receipt.json') -Destination (Join-Path $codeRoot 'startup-receipt.json.restored') -Force
    }
    Get-AppxPackage -Name OXP3.PowerWidget | Remove-AppxPackage
    $uninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OXP3GamePower'
    if (Test-Path -LiteralPath $uninstallKey) { Remove-Item -LiteralPath $uninstallKey }
    Write-Output 'Widget removed and normal ONEXConsole startup restored. Saved wattages and companion files are retained for reinstall.'
    exit 0
}
Assert-Compatibility
$startup = Get-StartupInstallation
if ($Mode -eq 'Check') {
    Write-Output ('CHECK PASSED: Intel ONEXPLAYER 3, reviewed ONEXConsole, valid bundle/runtime, Game Bar, same-user startup task. Version ' + $release.version + '. No installation settings or keys changed.')
    exit 0
}
# Installing never terminates or restarts ONEXConsole. For a normal running
# instance, prepare the next Windows startup and defer first activation.
$nativeRunning = @(Get-Process -Name OneXConsole -ErrorAction SilentlyContinue).Count -gt 0
$protectedParents = @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($codeRoot) -and $_.CommandLine -match 'launch-native\.cjs' })
$modules = Get-Content -LiteralPath (Join-Path $bundleRoot 'NativeBridge\startup-modules.json') -Raw | ConvertFrom-Json
if ($startup.Existing) {
    $oldRoot = $codeRoot
    if ($startup.Receipt.runtimeRoot) {
        $oldRoot = [IO.Path]::GetFullPath($startup.Receipt.runtimeRoot)
        if (-not $oldRoot.StartsWith($codeRoot + '\Versions\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected prior runtime directory.' }
    }
    $old = Get-Content -LiteralPath (Join-Path $oldRoot 'startup-modules.json') -Raw | ConvertFrom-Json
    foreach ($item in $old.modules) { if ((Startup-Hash (Join-Path $oldRoot $item.file)) -ne $item.sha256) { throw 'Existing bridge files changed outside Setup; no files were replaced.' } }
    if (@($protectedParents | Where-Object { $_.CommandLine.Contains($runtimeRoot) }).Count -gt 0) {
        foreach ($item in $modules.modules) {
            if ((Startup-Hash (Join-Path $runtimeRoot $item.file)) -ne $item.sha256) { throw 'The active release differs from this bundle. ONEXConsole was left running.' }
        }
    }
} elseif (Test-Path -LiteralPath $codeRoot) {
    # Only a verified prior uninstall receipt permits reuse of a nonempty folder.
    if (@(Get-ChildItem -LiteralPath $codeRoot).Count -gt 0) {
        $restoredPath = Join-Path $codeRoot 'startup-receipt.json.restored'
        if (-not (Test-Path -LiteralPath $restoredPath)) { throw 'An unrelated bridge directory already exists.' }
        $restored = Get-Content -LiteralPath $restoredPath -Raw | ConvertFrom-Json
        if ($restored.userSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or (Startup-TextHash $startup.Original) -ne $restored.originalSha256) { throw 'Prior uninstall receipt does not match native startup.' }
    }
}
Write-Output 'Installing the signed widget and bundled runtimes...'
$cer = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $bundleRoot 'Publisher.cer'))
if ($cer.Thumbprint -ne $release.certificateThumbprint -or $cer.Subject -ne 'CN=OXP3Personal') { throw 'Unexpected publisher certificate.' }
Import-Certificate -FilePath (Join-Path $bundleRoot 'Publisher.cer') -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
$package = Join-Path $bundleRoot 'Widget\OXP3.GamePower.msix'
$sig = Get-AuthenticodeSignature -LiteralPath $package
if ([int]$sig.Status -ne 0 -or $sig.SignerCertificate.Thumbprint -ne $cer.Thumbprint) { throw 'Widget signature verification failed.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$needed = @()
foreach ($dependency in Get-ChildItem -LiteralPath (Join-Path $bundleRoot 'Widget\Dependencies') -Filter '*.appx') {
    $zip = [IO.Compression.ZipFile]::OpenRead($dependency.FullName)
    try { $reader = [IO.StreamReader]::new($zip.GetEntry('AppxManifest.xml').Open()); try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() } } finally { $zip.Dispose() }
    $identity = $xml.Package.Identity
    if (-not @(Get-AppxPackage -Name $identity.Name | Where-Object { $_.Publisher -eq $identity.Publisher -and [version]$_.Version -ge [version]$identity.Version -and $_.Architecture.ToString() -in @('X64','Neutral') }).Count) { $needed += $dependency.FullName }
}
if ($needed.Count) { Add-AppxPackage -Path $package -DependencyPath $needed -ForceTargetApplicationShutdown }
else { Add-AppxPackage -Path $package -ForceTargetApplicationShutdown }
$installed = @(Get-AppxPackage -Name OXP3.PowerWidget)
if ($installed.Count -ne 1 -or $installed[0].PackageFamilyName -ne $release.packageFamily -or [version]$installed[0].Version -ne [version]$release.version -or $installed[0].Status.ToString() -ne 'Ok') { throw 'Widget registration verification failed.' }
Write-Output 'Preparing saved settings...'
$localRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$paths = Initialize-InstallerData $localRoot
$preferenceBackup = Join-Path $paths.SourceRoot 'preferences-backup.json'
$preferenceDestination = Join-Path $paths.MirrorRoot 'remembered-watts.json'
Assert-Oxp3AuthPath $preferenceBackup
Assert-Oxp3AuthPath $preferenceDestination
if ((Test-Path -LiteralPath $preferenceBackup) -and -not (Test-Path -LiteralPath $preferenceDestination)) { Copy-Item -LiteralPath $preferenceBackup -Destination $preferenceDestination }
New-Item -ItemType Directory -Path $codeRoot -Force | Out-Null
Protect-StartupCode $codeRoot $true
Assert-Oxp3AuthPath $runtimeRoot
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
Protect-StartupCode $runtimeRoot $true
foreach ($item in $modules.modules) {
    $destination = Join-Path $runtimeRoot $item.file
    Assert-Oxp3AuthPath $destination
    if (-not (Test-Path -LiteralPath $destination) -or (Startup-Hash $destination) -ne $item.sha256) { Copy-Item -LiteralPath (Join-Path $bundleRoot ('NativeBridge\' + $item.file)) -Destination $destination -Force }
    Protect-StartupCode $destination $false
}
foreach ($file in @('startup-modules.json')) { Copy-Item -LiteralPath (Join-Path $bundleRoot ('NativeBridge\' + $file)) -Destination (Join-Path $runtimeRoot $file) -Force; Protect-StartupCode (Join-Path $runtimeRoot $file) $false }
if (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot 'node.exe')) -or (Startup-Hash (Join-Path $runtimeRoot 'node.exe')) -ne $release.nodeSha256) { Copy-Item -LiteralPath (Join-Path $bundleRoot 'Runtime\node.exe') -Destination (Join-Path $runtimeRoot 'node.exe') -Force }
Protect-StartupCode (Join-Path $runtimeRoot 'node.exe') $false
if (-not $startup.Existing) {
    [IO.File]::WriteAllText((Join-Path $codeRoot 'OneXConsole.original.xml'), $startup.Original, [Text.UTF8Encoding]::new($false))
    Protect-StartupCode (Join-Path $codeRoot 'OneXConsole.original.xml') $false
}
$review = Get-ReviewedStartupXml $startup.Original
$review.Action.SelectSingleNode('t:Arguments',$review.Manager).InnerText = '"' + (Join-Path $runtimeRoot 'startup-native.vbs') + '"'
$settings = $review.Xml.SelectSingleNode('/t:Task/t:Settings', $review.Manager)
foreach ($setting in @(@('ExecutionTimeLimit','PT0S'),@('AllowHardTerminate','false'))) {
    $element = $settings.SelectSingleNode('t:' + $setting[0],$review.Manager)
    if (-not $element) { $element = $review.Xml.CreateElement($setting[0], 'http://schemas.microsoft.com/windows/2004/02/mit/task'); [void]$settings.AppendChild($element) }
    $element.InnerText = $setting[1]
}
if ((Startup-TextHash (Export-ScheduledTask -TaskName OneXConsole -TaskPath '\')) -ne (Startup-TextHash $startup.Current)) { throw 'Startup changed during setup; registration was refused.' }
if (-not $startup.Existing -or $startup.Receipt.runtimeRoot -ine $runtimeRoot) { Register-ScheduledTask -TaskName OneXConsole -TaskPath '\' -Xml $review.Xml.OuterXml -Force | Out-Null }
$receipt = @{ formatVersion = 1; runtimeRoot = $runtimeRoot; taskName = 'OneXConsole'; taskPath = '\'; userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value; originalSha256 = Startup-TextHash $startup.Original; installedSha256 = Startup-TextHash (Export-ScheduledTask -TaskName OneXConsole -TaskPath '\'); installedAt = [DateTime]::UtcNow.ToString('o') }
[IO.File]::WriteAllText((Join-Path $codeRoot 'startup-receipt.json'), ($receipt | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Protect-StartupCode (Join-Path $codeRoot 'startup-receipt.json') $false
$pendingPower = Join-Path $paths.MirrorRoot 'pending-power-startup.json'
Assert-Oxp3AuthPath $pendingPower
$nativeInstances = @(Get-CimInstance Win32_Process -Filter "Name='OneXConsole.exe'" | Where-Object { $_.CreationDate -is [DateTime] })
$nativeRunning = $nativeInstances.Count -gt 0
$protectedParents = @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($codeRoot) -and $_.CommandLine -match 'launch-native\.cjs' })
if ($nativeRunning -and $protectedParents.Count -eq 0) {
    $unconnectedNative = $nativeInstances | Sort-Object CreationDate | Select-Object -First 1
    $pending = @{ nativeProcessId = [int]$unconnectedNative.ProcessId; nativeStartedUtc = $unconnectedNative.CreationDate.ToUniversalTime().ToString('o') }
    [IO.File]::WriteAllText($pendingPower, ($pending | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Protect-Oxp3TokenPath $pendingPower $false
} elseif (Test-Path -LiteralPath $pendingPower) {
    Remove-Item -LiteralPath $pendingPower
}
Copy-ProtectedBundle
$uninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OXP3GamePower'
New-Item -Path $uninstallKey -Force | Out-Null
foreach ($pair in @(@('DisplayName','OXP3 Game Power'),@('DisplayVersion',$release.version),@('Publisher','OXP3Personal'),@('InstallLocation',$setupRoot),@('UninstallString',('"' + (Join-Path $setupRoot 'Setup.exe') + '" --remove')))) { New-ItemProperty -LiteralPath $uninstallKey -Name $pair[0] -Value $pair[1] -PropertyType String -Force | Out-Null }
if (-not $NoActivate -and -not $nativeRunning) {
    Write-Output 'Starting ONEXConsole and checking its connection...'
    Start-ScheduledTask -TaskName OneXConsole
    & (Join-Path $runtimeRoot 'node.exe') (Join-Path $runtimeRoot 'runtime-test.cjs') --wait-ready 45
    if ($LASTEXITCODE -ne 0) { throw 'Installed, but the first connection is not ready. Setup did not stop ONEXConsole; diagnostics are in the protected bridge folder.' }
    Start-Process -FilePath 'ms-gamebar:activate/OXP3.PowerWidget_pv4gfha69c7qe_App_PowerProfiles' -WindowStyle Hidden
}
elseif (-not $NoActivate -and $protectedParents.Count -gt 0) {
    # Updating the widget can reconnect to an existing native bridge without
    # restarting it. The helper supports the older bridge during this session.
    Start-Process -FilePath 'ms-gamebar:activate/OXP3.PowerWidget_pv4gfha69c7qe_App_PowerProfiles' -WindowStyle Hidden
}
if ($nativeRunning -and $protectedParents.Count -eq 0) { Write-Output 'Installed. Restart Windows to enable power control.' }
else { Write-Output 'Installed. Open Xbox Game Bar and select OXP3 Game Power from Widgets.' }
