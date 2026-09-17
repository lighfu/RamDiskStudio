#Requires -Version 7.0
param([string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
Push-Location $workspacePath
try {
    [xml] $project = Get-Content -LiteralPath src/RamDisk.App/RamDisk.App.csproj
    $version = [string] $project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a major.minor.patch version.' }
    if (!$OutputDirectory) { $OutputDirectory = "artifacts/github-release/v$version" }
    $outputPath = [IO.Path]::GetFullPath($OutputDirectory, $workspacePath)
    if (Test-Path -LiteralPath $outputPath) { throw 'Output directory already exists. Choose a new -OutputDirectory.' }
    New-Item -ItemType Directory -Path $outputPath | Out-Null
    $packageName = "RamDiskStudio-$version-win-x64"
    $packagePath = Join-Path $outputPath $packageName
    $testPath = Join-Path $outputPath 'verification'

    dotnet publish src/RamDisk.App/RamDisk.App.csproj -c Release -r win-x64 --self-contained true -p:UseSharedCompilation=false -p:DebugType=None -p:DebugSymbols=false -m:1 -o $packagePath
    if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
    & (Join-Path $PSScriptRoot 'copy-notices.ps1') -Destination $packagePath

    # Use a separate test host so production startup cannot open user settings or mount drives.
    dotnet publish tests/RamDisk.Tests/RamDisk.Tests.csproj -c Release -r win-x64 --self-contained true -p:UseSharedCompilation=false -p:DebugType=None -p:DebugSymbols=false -m:1 -o $testPath
    if ($LASTEXITCODE -ne 0) { throw 'Test host publish failed.' }
    Get-ChildItem -LiteralPath $packagePath | Copy-Item -Destination $testPath -Recurse -Force
    & (Join-Path $testPath 'RamDisk.Tests.exe') --ui
    if ($LASTEXITCODE -ne 0) { throw 'Packaged application tests failed.' }

    Copy-Item -LiteralPath README.md, CHANGELOG.md -Destination $packagePath
    Copy-Item -LiteralPath docs -Destination (Join-Path $packagePath 'docs') -Recurse
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $packagePath 'RamDiskStudio.runtimeconfig.json') -Raw | ConvertFrom-Json
    $sdkVersion = dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify SDK.' }
    $sourceCommit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Run from a Git checkout.' }
    [ordered]@{
        version = $version
        runtimeIdentifier = 'win-x64'
        selfContained = $true
        configuration = 'Release'
        sourceCommit = $sourceCommit.Trim()
        dotnetSdk = $sdkVersion.Trim()
        frameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packagePath 'BUILD-INFO.json') -Encoding utf8NoBOM

    $zipPath = Join-Path $outputPath "$packageName.zip"
    Compress-Archive -LiteralPath $packagePath -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $packageName.zip" | Set-Content -LiteralPath (Join-Path $outputPath 'SHA256SUMS.txt') -Encoding ascii
    Write-Output "Release package: $zipPath"
}
finally { Pop-Location }
