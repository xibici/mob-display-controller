# wait-event.ps1 - user-mode side of the driver channel.
# Opens the named event the filter driver signals when Windows itself reports that the power
# button was pressed (IOCTL_GET_SYS_BUTTON_EVENT completing with SYS_BUTTON_POWER), then waits.
#
# ASCII only: this file is run via "powershell -File", which reads it with the ANSI codepage.

$name = 'Global\BtnDrvEvent'
Write-Host "opening named event: $name"

try {
    $ev = [System.Threading.EventWaitHandle]::OpenExisting($name)
    Write-Host "opened OK - the driver created it"
}
catch {
    Write-Host ("could not open: " + $_.Exception.Message)
    Write-Host "the driver only creates it on the first button ioctl after a session exists"
    exit 1
}

Write-Host "waiting up to 300s - press the power button now"
$deadline = (Get-Date).AddSeconds(300)
$hits = 0

while ((Get-Date) -lt $deadline) {
    if ($ev.WaitOne(1000)) {
        $hits++
        Write-Host ("[" + (Get-Date -Format 'HH:mm:ss.fff') + "] SIGNALLED #" + $hits + " - driver reports the power button was pressed")
        $ev.Reset()
    }
}

Write-Host ("done - signals received: " + $hits)
