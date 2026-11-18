# Run as Administrator, ONLY for test-signed builds (see docs/DRIVER.md).
# Trusts DualScreenTest.cer and enables Windows test-signing mode (reboot required).
param([string] $Certificate = ".\DualScreenTest.cer")
$ErrorActionPreference = "Stop"
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script as Administrator."
}
Import-Certificate -FilePath $Certificate -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $Certificate -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
bcdedit /set testsigning on
Write-Host "Test certificate trusted and test signing enabled. Reboot, then install the driver from DualScreen (Configuration > Driver)."
Write-Host "Note: Secure Boot must be disabled for test signing to take effect."
