param([string] $Destination = 'artifacts/release')
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
$assets = Get-Content -LiteralPath (Join-Path $workspacePath 'src/RamDisk.App/obj/project.assets.json') -Raw | ConvertFrom-Json
$cachePath = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
$noticesPath = Join-Path $Destination 'licenses'
New-Item -ItemType Directory -Path $noticesPath -Force | Out-Null
$lines = @('# Third-party packages', '', 'Runtime packages restored from NuGet. Bundled license notices are in licenses/.', '')
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $packagePath = Join-Path $cachePath $library.Value.path
    $manifestFile = Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' | Select-Object -First 1
    [xml] $manifest = Get-Content -LiteralPath $manifestFile.FullName
    $metadata = $manifest.package.metadata
    $lines += "- $($metadata.id) $($metadata.version): $($metadata.license.InnerText) — https://www.nuget.org/packages/$($metadata.id)/$($metadata.version)"
    Get-ChildItem -LiteralPath $packagePath -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD.PARTY.NOTICES)(\.|$)' } | ForEach-Object {
        $name = "$($metadata.id)-$($metadata.version)-$($_.Name)"
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $noticesPath $name) -Force
    }
}
$lines | Set-Content -LiteralPath (Join-Path $Destination 'THIRD-PARTY-NOTICES.md') -Encoding UTF8
Get-ChildItem -LiteralPath (Join-Path $workspacePath 'docs/licenses') -File | Copy-Item -Destination $noticesPath -Force
