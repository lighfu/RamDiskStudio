#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
$packagePath = Join-Path $workspacePath 'artifacts\awealloc-download\package\Win10'
$driverPath = Join-Path $packagePath 'x64\awealloc.sys'
$installedPath = Join-Path $env:WINDIR 'System32\drivers\awealloc.sys'
$reportPath = Join-Path $workspacePath 'artifacts\awealloc-setup'
New-Item -ItemType Directory -Path $reportPath -Force | Out-Null
$result = [ordered]@{ Success = $false; DriverStarted = $false; TestsExitCode = $null; RebootSuggested = $false; Error = $null }
Start-Transcript -Path (Join-Path $reportPath 'integration.log') -Force | Out-Null
try {
    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { throw 'This setup script is for Windows x64.' }
    $expectedHash = '0BAFDB64E2EB2615CE3674B2DADB2ADEF8BE7A726060CF7F170B980E062CD5E5'
    if ((Get-FileHash -LiteralPath $driverPath).Hash -ne $expectedHash) { throw 'The verified AWEAlloc package has changed.' }
    $driverSignature = Get-AuthenticodeSignature -LiteralPath $driverPath
    $catalogSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $packagePath 'phdskmnt.cat')
    # After staging, Windows prefers the Microsoft catalog signature over the embedded Arsenal signature.
    $trustedDriverSigner = $driverSignature.SignerCertificate.Subject -like '*Arsenal Consulting*' -or
        $driverSignature.SignerCertificate.Subject -like '*Microsoft Windows Hardware Compatibility Publisher*'
    if ($driverSignature.Status -ne 'Valid' -or -not $trustedDriverSigner -or
        $catalogSignature.Status -ne 'Valid' -or $catalogSignature.SignerCertificate.Subject -notlike '*Microsoft Windows Hardware Compatibility Publisher*') {
        throw 'Driver/catalog signature verification failed.'
    }
    $existing = Get-Service -Name awealloc -ErrorAction SilentlyContinue
    if ($existing -or (Test-Path -LiteralPath $installedPath)) {
        if (-not $existing -or -not (Test-Path -LiteralPath $installedPath) -or (Get-FileHash -LiteralPath $installedPath).Hash -ne $expectedHash) {
            throw 'An existing different or incomplete AWEAlloc installation was found; it will not be replaced.'
        }
    }
    else {
        # Stage the original signed catalog/package without /install: no AIM adapter or unrelated driver services are created.
        & "$env:WINDIR\System32\pnputil.exe" /add-driver (Join-Path $packagePath 'phdskmnt.inf')
        if ($LASTEXITCODE -notin 0,3010) { throw "Driver package staging failed: $LASTEXITCODE" }
        $result.RebootSuggested = $LASTEXITCODE -eq 3010
        Copy-Item -LiteralPath $driverPath -Destination $installedPath
        # Same service settings as the official AWEAlloc_Service_Inst INF section.
        & "$env:WINDIR\System32\sc.exe" create awealloc type= kernel start= auto error= ignore binPath= 'System32\drivers\awealloc.sys' DisplayName= 'AWE Allocation driver'
        if ($LASTEXITCODE -ne 0) { throw "AWEAlloc service registration failed: $LASTEXITCODE" }
    }
    Start-Service -Name awealloc
    if ((Get-Service -Name awealloc).Status -ne 'Running') { throw 'AWEAlloc did not start.' }
    $result.DriverStarted = $true
    Get-Service -Name awealloc,imdisk,imdsksvc | Format-Table Name,Status
    Push-Location $workspacePath
    try {
        & 'C:\Program Files\dotnet\dotnet.exe' 'tests/RamDisk.Tests/bin/Release/net10.0-windows/RamDisk.Tests.dll' --integration-physical --monitoring-physical
        $result.TestsExitCode = $LASTEXITCODE
        if ($LASTEXITCODE -ne 0) { throw 'AWEAlloc integration tests failed.' }
    }
    finally { Pop-Location }
    $result.Success = $true
}
catch { $result.Error = $_.Exception.Message; Write-Output $_.Exception.ToString() }
finally {
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportPath 'result.json') -Encoding UTF8
    Stop-Transcript | Out-Null
}
if (-not $result.Success) { exit 1 }
