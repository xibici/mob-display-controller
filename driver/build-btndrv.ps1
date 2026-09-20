# build-btndrv.ps1 - builds and signs btndrv.sys.
#
# Deliberately does not use MSBuild: the WDK's driver platform toolset
# (WindowsKernelModeDriver10.0) is installed by the WDK's Visual Studio integration, which only
# attaches to a full Visual Studio install - on a machine with Build Tools alone the project fails
# with MSB8020. cl.exe + link.exe against the WDK's km headers and libs works everywhere.
#
# ASCII only: run with "powershell -ExecutionPolicy Bypass -File build-btndrv.ps1".

param(
    [string]$Source     = (Join-Path $PSScriptRoot 'btndrv.c'),
    [string]$OutDir     = $PSScriptRoot,
    [string]$CertName   = 'BtnDrvTest',
    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'

# --- locate the toolchain -------------------------------------------------------------------
$cl = Get-ChildItem 'C:\Program Files (x86)\Microsoft Visual Studio\2022' -Recurse -Filter cl.exe -ErrorAction SilentlyContinue |
      Where-Object { $_.FullName -match 'Hostx64\\x64\\cl\.exe$' } | Select-Object -First 1 -ExpandProperty FullName
if (-not $cl) { $cl = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\2022' -Recurse -Filter cl.exe -ErrorAction SilentlyContinue |
      Where-Object { $_.FullName -match 'Hostx64\\x64\\cl\.exe$' } | Select-Object -First 1 -ExpandProperty FullName }
if (-not $cl) { throw 'cl.exe not found - install the Visual Studio C++ build tools' }

$link = $cl -replace 'cl\.exe$', 'link.exe'
$msvcRoot = $cl -replace '\\bin\\Hostx64\\x64\\cl\.exe$', ''

$kits = 'C:\Program Files (x86)\Windows Kits\10'
$version = (Get-ChildItem "$kits\Include" -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'km') } |
            Sort-Object Name -Descending | Select-Object -First 1).Name
if (-not $version) { throw 'WDK kernel headers not found - install the Windows Driver Kit' }

$inc = Join-Path $kits "Include\$version"
$lib = Join-Path $kits "Lib\$version\km\x64"
Write-Host "toolchain: $cl"
Write-Host "wdk      : $version"

# --- compile + link -------------------------------------------------------------------------
$obj = Join-Path $OutDir 'btndrv.obj'
$sys = Join-Path $OutDir 'btndrv.sys'
Remove-Item $obj, $sys -Force -ErrorAction SilentlyContinue

& $cl /nologo /c /kernel /GS- /W4 /O2 /D _AMD64_ /D AMD64 `
      /D NTDDI_VERSION=0x0A00000C /D _WIN32_WINNT=0x0A00 `
      "/I$inc\km" "/I$inc\km\crt" "/I$inc\shared" "/I$inc\ucrt" "/I$msvcRoot\include" `
      "/Fo$obj" $Source
if (-not (Test-Path $obj)) { throw "compile failed - see the compiler output above (expected $obj)" }

& $link /nologo /DRIVER /SUBSYSTEM:NATIVE /MACHINE:X64 /ENTRY:DriverEntry /OUT:$sys `
       "/LIBPATH:$lib" $obj ntoskrnl.lib hal.lib libcntpr.lib
if (-not (Test-Path $sys)) { throw 'link failed' }
Remove-Item $obj -Force -ErrorAction SilentlyContinue

# --- sign (test signing) --------------------------------------------------------------------
if (-not $SkipSign) {
    $signtool = Get-ChildItem "$kits\bin\$version\x64\signtool.exe" -ErrorAction SilentlyContinue |
                Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw 'signtool.exe not found' }

    $cert = Get-ChildItem 'Cert:\LocalMachine\My' | Where-Object { $_.Subject -eq "CN=$CertName" } | Select-Object -First 1
    if (-not $cert) {
        Write-Host "creating self-signed code signing certificate CN=$CertName"
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=$CertName" `
                 -CertStoreLocation 'Cert:\LocalMachine\My' -KeyUsage DigitalSignature `
                 -NotAfter (Get-Date).AddYears(5)
        $cer = Join-Path $OutDir "$CertName.cer"
        Export-Certificate -Cert $cert -FilePath $cer | Out-Null
        Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    }

    & $signtool sign /fd sha256 /sha1 $cert.Thumbprint /s My /sm $sys
    & $signtool verify /pa $sys
}

Get-Item $sys | Select-Object FullName, Length, LastWriteTime | Format-List
Write-Host 'done - next: install-btndrv.ps1'
