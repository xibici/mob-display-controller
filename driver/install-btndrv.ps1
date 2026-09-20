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
# A filter driver that is attached to a PnP device cannot be stopped, so the file it was loaded
# from stays locked until the next reboot. Deploying to a second name sidesteps that: whichever of
# the two names is not currently loaded is free, so a new build can always be installed with a
# single reboot. The service name (and therefore UpperFilters) never changes.
$candidates = @('btndrv.sys', 'btndrv_alt.sys')
$target = $null
$lastError = $null
foreach ($name in $candidates) {
    $candidate = Join-Path $env:SystemRoot "System32\drivers\$name"
    try {
        if (Test-Path $candidate) {
            # Read access with no sharing is the test: an image the loader has mapped cannot be opened
            # at all, while read-only avoids reporting a write-protected file as "locked".
            $stream = [System.IO.File]::Open($candidate, 'Open', 'Read', 'None')
            $stream.Close()
        }
        Copy-Item $DriverFile $candidate -Force
        $target = $candidate
        break
    } catch {
        $lastError = $_.Exception.Message
        Write-Host "   $name is in use by the running driver - trying the other name"
        $target = $null
    }
}
if (-not $target) { throw "both driver file names are unusable - reboot first, then run this again (last error: $lastError)" }
Write-Host "   $target"

Write-Host '3) creating the demand-start kernel service'
# The service cannot be deleted while it is loaded, and a failed create would leave binpath pointing
# at the previous file - so the image path is always written explicitly and then read back.
$exists = $null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
if (-not $exists) {
    sc.exe create $ServiceName binpath= "$target" type= kernel start= demand error= normal | Out-Null
}
sc.exe config $ServiceName binpath= "$target" start= demand | Out-Null
$configured = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName").ImagePath
if ($configured -notmatch [regex]::Escape($target)) {
    throw "the service still points at '$configured' instead of '$target'"
}
Write-Host "   ImagePath = $configured"

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
