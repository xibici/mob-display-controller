# install-btndrv.ps1 - installs btndrv.sys as an upper filter on the ACPI power button device.
#
# The driver does not change how the button behaves: it forwards every request untouched. All it
# adds is a named event (Global\BtnDrvEvent) that it signals when Windows' own button request comes
# back saying the power button was pressed. With that in place the button can be set to "do nothing"
# and the app still learns about the press - no sleep, no blank, no sign-in prompt.
#
# ASCII only. Run from an elevated PowerShell.
# Undo with: remove-btndrv.ps1

param(
    [string]$DriverFile = (Join-Path $PSScriptRoot 'btndrv.sys'),
    [string]$ServiceName = 'btndrv',
    [string]$DeviceKey  = 'HKLM:\SYSTEM\CurrentControlSet\Enum\ACPI\PNP0C0C\2&daba3ff&1'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    Write-Warning 'not running elevated - this will fail'
}
if (-not (Test-Path $DriverFile)) { throw "driver not found: $DriverFile (run build-btndrv.ps1 first)" }
if (-not (Test-Path $DeviceKey)) { throw "the ACPI power button device key was not found: $DeviceKey" }

Write-Host '1) test signing (required for a self-signed kernel driver; Secure Boot must be off)'
bcdedit /set testsigning on | Out-Null
(Get-Content (Join-Path $env:SystemRoot 'System32\config\system') -ErrorAction SilentlyContinue) | Out-Null
Write-Host ('   testsigning now: ' + ((bcdedit /enum '{current}') | Select-String 'testsigning').Line.Trim())

Write-Host '2) copying the driver'
$target = Join-Path $env:SystemRoot 'System32\drivers\btndrv.sys'
Copy-Item $DriverFile $target -Force
Write-Host "   $target"

Write-Host '3) creating the demand-start kernel service'
sc.exe delete $ServiceName 2>$null | Out-Null
sc.exe create $ServiceName binpath= "$target" type= kernel start= demand error= normal

Write-Host '4) attaching it as an upper filter of the button device'
$existing = (Get-ItemProperty $DeviceKey -ErrorAction SilentlyContinue).UpperFilters
if ($existing -notcontains $ServiceName) {
    $new = @($existing | Where-Object { $_ }) + $ServiceName
    New-ItemProperty -Path $DeviceKey -Name UpperFilters -PropertyType MultiString -Value $new -Force | Out-Null
}
Write-Host ('   UpperFilters = ' + ((Get-ItemProperty $DeviceKey).UpperFilters -join ','))

Write-Host ''
Write-Host 'Reboot for the filter to load. After the reboot:'
Write-Host '  - check it is running : sc query btndrv'
Write-Host '  - check it is logging : Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\btndrv BtnDrvLog'
Write-Host '  - check the event     : powershell -File wait-event.ps1   (then press the power button)'
