param([string]$NodePath = 'node')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot

function Assert-Exit([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw ($Step + ' failed with exit code ' + $LASTEXITCODE) }
}

$helperOutput = Join-Path $projectRoot 'artifacts\helper-tests'
& dotnet publish (Join-Path $projectRoot 'helper\OXP3.PowerWidget.Helper.csproj') -c Release --no-self-contained -o $helperOutput --nologo
Assert-Exit 'Helper build'
& dotnet (Join-Path $helperOutput 'OXP3.PowerWidget.Helper.dll') --self-test
Assert-Exit 'Helper self-tests'

& dotnet build (Join-Path $projectRoot 'desktop\OXP3.GamePower.Desktop.csproj') -c Release --nologo
Assert-Exit 'Desktop build'
$controller = Start-Process -FilePath (Join-Path $projectRoot 'desktop\bin\Release\net8.0-windows\OXP3.GamePower.Desktop.exe') -ArgumentList '--controller-self-test' -PassThru -Wait -WindowStyle Hidden
if ($controller.ExitCode -ne 0) { throw ('Controller self-tests failed with exit code ' + $controller.ExitCode) }
Write-Output 'PASS: controller input self-tests.'

foreach ($test in @('test-live-bridge.cjs', 'test-protocol.cjs')) {
    & $NodePath (Join-Path $projectRoot ('native-bridge\' + $test))
    Assert-Exit $test
}
& (Join-Path $projectRoot 'native-bridge\test-startup-retry.ps1')
& (Join-Path $projectRoot 'installer\Test-Removal.ps1')
& (Join-Path $projectRoot 'Test-GameDiscovery.ps1')
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $projectRoot 'Test-GameDiscovery.ps1')
Assert-Exit 'Windows PowerShell Unicode discovery tests'

# Parse production scripts without executing installation, startup or live tools.
foreach ($folder in @('installer','native-bridge','widget')) {
    foreach ($script in Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -File -Filter '*.ps1') {
        # Only supported source paths, excluding local historical experiments.
        if ($folder -eq 'native-bridge' -and $script.Name -notin @('startup-native.ps1','startup-task-common.ps1','test-startup-retry.ps1')) { continue }
        if ($script.Name -in @('Check-WidgetSource.ps1','Check-WidgetManifest.ps1')) { continue }
        $parseTokens = $null; $parseErrors = $null
        [void][Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$parseTokens, [ref]$parseErrors)
        if ($parseErrors) { throw ($script.Name + ': ' + ($parseErrors.Message -join '; ')) }
    }
}
$modules = Get-Content -LiteralPath (Join-Path $projectRoot 'native-bridge\startup-modules.json') -Raw | ConvertFrom-Json
foreach ($module in $modules.modules) {
    $source = Join-Path $projectRoot ('native-bridge\' + $module.file)
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $module.sha256) { throw ('Startup module hash mismatch: ' + $module.file) }
}
Write-Output 'PASS: supported script syntax and pinned startup-module hashes.'
Write-Output 'All isolated tests passed. No device settings were changed.'
