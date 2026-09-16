#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
$packagePath = Join-Path $workspacePath 'artifacts\imdisk-download\package'
$reportPath = Join-Path $workspacePath 'artifacts\driver-setup'
New-Item -ItemType Directory -Path $reportPath -Force | Out-Null
$resultPath = Join-Path $reportPath 'result.json'
$result = [ordered]@{ Success = $false; Installed = $false; TestsExitCode = $null; Error = $null }
Start-Transcript -Path (Join-Path $reportPath 'install.log') -Force | Out-Null
try {
    if (Get-Service -Name ImDisk -ErrorAction SilentlyContinue) {
        throw 'ImDisk is already registered. This first-install script will not replace an existing driver.'
    }
    $signedFiles = @('sys\amd64\imdisk.sys', 'cli\amd64\imdisk.exe', 'cpl\amd64\imdisk.cpl', 'svc\amd64\imdsksvc.exe', 'cli\i386\imdisk.exe', 'cpl\i386\imdisk.cpl')
    foreach ($relativePath in $signedFiles) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $packagePath $relativePath)
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*CN=Microsoft Windows Hardware Compatibility Publisher,*') {
            throw "Signature verification failed: $relativePath"
        }
    }
    $infPath = Join-Path $packagePath 'imdisk.inf'
    $setup = Start-Process -FilePath "$env:WINDIR\System32\rundll32.exe" -ArgumentList @('setupapi.dll,InstallHinfSection', 'DefaultInstall', '132', $infPath) -WorkingDirectory $packagePath -WindowStyle Hidden -Wait -PassThru
    Write-Output "INF installer exit code: $($setup.ExitCode)"
    if (-not (Test-Path -LiteralPath "$env:WINDIR\System32\drivers\imdisk.sys")) { throw 'The driver file was not installed.' }
    Start-Service -Name ImDisk
    Start-Service -Name ImDskSvc
    $result.Installed = $true
    Get-Service -Name ImDisk,ImDskSvc | Format-Table Name,Status
    Push-Location $workspacePath
    try {
        & 'C:\Program Files\dotnet\dotnet.exe' 'tests/RamDisk.Tests/bin/Release/net10.0-windows/RamDisk.Tests.dll' --integration-standard
        $result.TestsExitCode = $LASTEXITCODE
        if ($LASTEXITCODE -ne 0) { throw 'RAM disk integration tests failed. See install.log.' }
    }
    finally { Pop-Location }
    $result.Success = $true
}
catch {
    $result.Error = $_.Exception.Message
    Write-Output $_.Exception.ToString()
}
finally {
    $result | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    Stop-Transcript | Out-Null
}
if (-not $result.Success) { exit 1 }
