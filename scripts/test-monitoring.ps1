#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
$reportPath = Join-Path $workspacePath 'artifacts\monitoring-setup'
New-Item -ItemType Directory -Path $reportPath -Force | Out-Null
$result = [ordered]@{ Success = $false; TestsExitCode = $null; Error = $null }
Start-Transcript -Path (Join-Path $reportPath 'integration.log') -Force | Out-Null
Push-Location $workspacePath
try {
    & 'C:\Program Files\dotnet\dotnet.exe' 'tests/RamDisk.Tests/bin/Release/net10.0-windows/RamDisk.Tests.dll' --monitoring-integration
    $result.TestsExitCode = $LASTEXITCODE
    if ($LASTEXITCODE -ne 0) { throw 'RAM disk monitoring integration tests failed.' }
    $result.Success = $true
}
catch { $result.Error = $_.Exception.Message; Write-Output $_.Exception.ToString() }
finally {
    Pop-Location
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportPath 'integration-result.json') -Encoding UTF8
    Stop-Transcript | Out-Null
}
if (-not $result.Success) { exit 1 }
