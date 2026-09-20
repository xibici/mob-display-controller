# remove-btndrv.ps1
# Recovery: run this from Safe Mode if the power button (or anything else) misbehaves
# after installing the btndrv filter. It removes the filter, the service and test signing.

$btnKey = 'HKLM:\SYSTEM\CurrentControlSet\Enum\ACPI\PNP0C0C\2&daba3ff&1'

Write-Host "1) removing UpperFilters from the button device"
Remove-ItemProperty -Path $btnKey -Name UpperFilters -Force -ErrorAction SilentlyContinue
if (Test-Path $btnKey) {
    $v = (Get-ItemProperty $btnKey -ErrorAction SilentlyContinue).UpperFilters
    Write-Host ("   UpperFilters now = '" + ($v -join ',') + "'")
} else {
    Write-Host "   (device key not found)"
}

Write-Host "2) deleting the service"
sc.exe delete btndrv

Write-Host "3) removing the driver file"
Remove-Item 'C:\Windows\System32\drivers\btndrv.sys' -Force -ErrorAction SilentlyContinue

Write-Host "4) turning test signing back off"
bcdedit /set testsigning off

Write-Host "5) optional: remove the self-signed test certificate"
Get-ChildItem 'Cert:\LocalMachine\My' | Where-Object { $_.Subject -eq 'CN=BtnDrvTest' } | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem 'Cert:\LocalMachine\Root' | Where-Object { $_.Subject -eq 'CN=BtnDrvTest' } | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Done. Reboot for the changes to take effect."
