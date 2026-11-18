# Signs the driver catalog (.cat). With DRIVER_CERT_PFX (base64 PFX) it uses your real code-signing
# certificate; otherwise it creates a self-signed TEST certificate (only works with test signing enabled,
# see docs/DRIVER.md) and ships its public part as DualScreenTest.cer.
param([Parameter(Mandatory)] [string] $PackageDir)
$ErrorActionPreference = "Stop"

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe |
    Where-Object { $_.FullName -match "\\x64\\" } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found" }
$cat = Join-Path $PackageDir "DualScreenIdd.cat"
if (-not (Test-Path $cat)) { throw "Catalog not found: $cat" }

if ($env:DRIVER_CERT_PFX) {
    $pfx = Join-Path $env:RUNNER_TEMP "driver.pfx"
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:DRIVER_CERT_PFX))
    & $signtool.FullName sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /f $pfx /p $env:DRIVER_CERT_PASSWORD $cat
    Remove-Item $pfx
    Write-Host "Signed with release certificate. For retail Windows without test mode, submit the package to Microsoft (attestation signing) - see docs/DRIVER.md."
} else {
    Write-Warning "DRIVER_CERT_PFX not set: signing with a self-signed TEST certificate."
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=DualScreen Test Driver" -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
    & $signtool.FullName sign /fd sha256 /sha1 $cert.Thumbprint /s My $cat
    Export-Certificate -Cert $cert -FilePath (Join-Path $PackageDir "DualScreenTest.cer") | Out-Null
}
& $signtool.FullName verify /pa /v $cat 2>&1 | Out-Host
exit 0
