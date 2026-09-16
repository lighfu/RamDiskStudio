#Requires -RunAsAdministrator
param([string] $CleanupProfileFile)
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
$reportPath = Join-Path $workspacePath 'artifacts\driver-setup'
New-Item -ItemType Directory -Path $reportPath -Force | Out-Null
$result = [ordered]@{ Success = $false; TestsExitCode = $null; Error = $null }
Start-Transcript -Path (Join-Path $reportPath 'integration.log') -Force | Out-Null
Push-Location $workspacePath
try {
    if ($CleanupProfileFile) {
        $testRoot = [IO.Path]::GetFullPath((Join-Path $workspacePath 'artifacts\tests')) + '\'
        $cleanupPath = [IO.Path]::GetFullPath($CleanupProfileFile)
        if (-not $cleanupPath.StartsWith($testRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup profile must be inside artifacts/tests.' }
        & 'C:\Program Files\dotnet\dotnet.exe' 'tests/RamDisk.Tests/bin/Release/net10.0-windows/RamDisk.Tests.dll' --cleanup-test-force $cleanupPath
        if ($LASTEXITCODE -ne 0) { throw 'Could not detach the previous test disk.' }
    }
    & 'C:\Program Files\dotnet\dotnet.exe' 'tests/RamDisk.Tests/bin/Release/net10.0-windows/RamDisk.Tests.dll' --integration-standard
    $result.TestsExitCode = $LASTEXITCODE
    if ($LASTEXITCODE -ne 0) { throw 'RAM disk integration tests failed.' }
    $result.Success = $true
}
catch { $result.Error = $_.Exception.Message; Write-Output $_.Exception.ToString() }
finally {
    Pop-Location
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportPath 'integration-result.json') -Encoding UTF8
    Stop-Transcript | Out-Null
}
if (-not $result.Success) { exit 1 }
