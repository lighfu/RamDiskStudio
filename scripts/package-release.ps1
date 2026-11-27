#Requires -Version 7.0
param([string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$version = ((Select-String -LiteralPath (Join-Path $workspace 'Cargo.toml') -Pattern '^version\s*=\s*"([^"]+)"' | Select-Object -First 1).Matches[0].Groups[1].Value)
$packageName = "RamDiskStudio-$version-win-x64"
if (-not $OutputDirectory) { $OutputDirectory = "artifacts/github-release/v$version-rust" }
$output = [IO.Path]::GetFullPath($OutputDirectory, $workspace)
if (Test-Path -LiteralPath $output) { throw 'Choose a new output folder.' }
$sdk = Join-Path (Split-Path -Parent $workspace) 'm3e-sdk'
$sourceCommit = git -C $workspace rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Build from a Git checkout.' }
$sourceStatus = git -C $workspace status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'Cannot read source status.' }
$sdkCommit = git -C $sdk rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Build with an m3e SDK Git checkout.' }
$sdkStatus = git -C $sdk status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'Cannot read SDK source status.' }
$rustVersion = rustc --version
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify Rust compiler.' }
$exe = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build.ps1') -SmokeTest |
    Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { throw 'Build or test failed.' }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Build did not return an executable.' }
$package = Join-Path $output $packageName
New-Item -ItemType Directory -Path $package | Out-Null
Copy-Item -LiteralPath $exe,(Join-Path $workspace 'README.md'),(Join-Path $workspace 'CHANGELOG.md') -Destination $package
$docs = Join-Path $package 'docs'
New-Item -ItemType Directory -Path $docs | Out-Null
Copy-Item -LiteralPath (Join-Path $workspace 'docs/validation.md'),(Join-Path $workspace 'docs/releases') -Destination $docs -Recurse
& (Join-Path $PSScriptRoot 'copy-notices.ps1') -Destination $package
[ordered]@{
    version = $version
    target = 'x86_64-pc-windows-msvc'
    profile = 'release'
    sourceCommit = $sourceCommit.Trim()
    sourceModified = [bool]$sourceStatus
    m3eSdkCommit = $sdkCommit.Trim()
    m3eSdkModified = [bool]$sdkStatus
    rustc = $rustVersion.Trim()
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'BUILD-INFO.json') -Encoding utf8
$zip = Join-Path $output "$packageName.zip"
Compress-Archive -LiteralPath $package -DestinationPath $zip
$checksum = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $packageName.zip" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output $zip
