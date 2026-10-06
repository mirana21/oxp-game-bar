$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot 'desktop\bin\Release\net8.0-windows\OXP3.GamePower.Desktop.exe'
if (-not (Test-Path -LiteralPath $app)) { throw 'Build the desktop test app first.' }
# This is the visible interactive test app; its helper remains hidden.
Start-Process -FilePath $app
