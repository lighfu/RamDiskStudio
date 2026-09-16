$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
Push-Location $workspacePath
try {
    dotnet build RamDiskStudio.sln -c Release --nologo -p:UseSharedCompilation=false -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet tests/RamDisk.Tests/bin/Release/net10.0-windows/RamDisk.Tests.dll
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet publish src/RamDisk.App/RamDisk.App.csproj -c Release --no-restore --no-self-contained -p:UseSharedCompilation=false -o artifacts/release
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.md -Destination artifacts/release/README.md
    Copy-Item -LiteralPath docs/validation.md -Destination artifacts/release/VALIDATION.md
    & (Join-Path $PSScriptRoot 'copy-notices.ps1') -Destination artifacts/release
    Compress-Archive -Path artifacts/release/* -DestinationPath artifacts/RamDiskStudio-win-x64.zip -Force
    Write-Output 'Ready: artifacts/release/RamDiskStudio.exe'
}
finally { Pop-Location }
