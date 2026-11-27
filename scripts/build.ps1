#Requires -Version 7.0
param(
    [ValidateSet('dev','release')] [string] $Profile = 'release',
    [switch] $SmokeTest
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path (Split-Path -Parent $workspace) 'm3e-sdk'
if (-not (Test-Path -LiteralPath (Join-Path $sdk 'rust/m3e/Cargo.toml'))) {
    throw 'Place m3e-sdk 1.0.1 next to this repository.'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw 'Install Visual Studio MSVC build tools and the Windows SDK.' }
Import-Module "$vsPath\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$target = 'x86_64-pc-windows-msvc'
$previousEncodedFlags = [Environment]::GetEnvironmentVariable('CARGO_ENCODED_RUSTFLAGS', 'Process')
Push-Location $workspace
try {
    if ($Profile -eq 'release') {
        $cargoHomePath = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $env:USERPROFILE '.cargo' }
        $remappedRoots = [ordered]@{
            ([IO.Path]::GetFullPath($env:USERPROFILE)) = '/build/user'
            ([IO.Path]::GetFullPath($cargoHomePath)) = '/build/cargo'
            ((Resolve-Path -LiteralPath $sdk).ProviderPath) = '/build/m3e-sdk'
            ($workspace) = '/build/ramdisk-studio'
        }
        # The encoded form keeps a path containing spaces in one argument.
        $flags = @('-C', 'target-feature=+crt-static', '-C', 'link-arg=/PDBALTPATH:%_PDB%')
        foreach ($entry in $remappedRoots.GetEnumerator()) {
            foreach ($prefix in @([string]$entry.Key, ('\\?\' + $entry.Key))) {
                $flags += "--remap-path-prefix=$prefix=$($entry.Value)"
                $flags += "--remap-path-prefix=$($prefix.Replace('\','/'))=$($entry.Value)"
            }
        }
        $env:CARGO_ENCODED_RUSTFLAGS = $flags -join [char]0x1F
    }
    cargo test --locked --target $target --profile $Profile
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    cargo build --locked --target $target --profile $Profile
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $profileFolder = if ($Profile -eq 'dev') { 'debug' } else { $Profile }
    $targetRoot = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { 'target' }
    $exe = (Resolve-Path -LiteralPath (Join-Path $targetRoot "$target/$profileFolder/RamDiskStudio.exe")).ProviderPath
    if ($SmokeTest) {
        $process = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -PassThru
        if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'UI smoke test timed out.' }
        if ($process.ExitCode -ne 0) { throw "UI smoke test failed: $($process.ExitCode)" }
    }
    Write-Output $exe
} finally {
    [Environment]::SetEnvironmentVariable('CARGO_ENCODED_RUSTFLAGS', $previousEncodedFlags, 'Process')
    Pop-Location
}
