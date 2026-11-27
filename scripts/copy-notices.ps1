param([Parameter(Mandatory)] [string] $Destination)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$manifest = Join-Path $workspace 'Cargo.toml'
$resolved = cargo metadata --locked --manifest-path $manifest --format-version 1 --filter-platform x86_64-pc-windows-msvc
if ($LASTEXITCODE -ne 0) { throw 'Unable to list Cargo dependencies.' }
$metadata = $resolved | ConvertFrom-Json -Depth 20
$licenses = Join-Path $Destination 'licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
$lines = @('# Third-party notices', '')
$activePackages = @{}
foreach ($node in $metadata.resolve.nodes) { $activePackages[$node.id] = $true }
foreach ($package in ($metadata.packages | Sort-Object name,version)) {
    if (-not $activePackages.ContainsKey($package.id) -or $package.name -eq 'ramdisk-studio') { continue }
    $folder = Split-Path -Parent $package.manifest_path
    $lines += "- $($package.name) $($package.version) — $($package.license)"
    $files = @(Get-ChildItem -LiteralPath $folder -File | Where-Object Name -Match '^(LICENSE|LICENCE|COPYING|NOTICE|UNLICENSE)([._-].*|$)')
    if ($package.license_file) {
        $specified = Join-Path $folder $package.license_file
        if (Test-Path -LiteralPath $specified -PathType Leaf) { $files += Get-Item -LiteralPath $specified }
    }
    foreach ($file in ($files | Sort-Object FullName -Unique)) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $licenses "$($package.name)-$($package.version)-$($file.Name)") -Force
    }
}
$sdk = Join-Path (Split-Path -Parent $workspace) 'm3e-sdk'
Copy-Item -LiteralPath (Join-Path $sdk 'NOTICE') -Destination (Join-Path $licenses 'm3e-sdk-NOTICE')
Copy-Item -LiteralPath (Join-Path $sdk 'licenses') -Destination (Join-Path $licenses 'm3e-sdk') -Recurse
$lines | Set-Content -LiteralPath (Join-Path $Destination 'THIRD-PARTY-NOTICES.md') -Encoding utf8
