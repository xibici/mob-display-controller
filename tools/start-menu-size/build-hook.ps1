chcp 65001 | Out-Null
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

# Builds the Start-menu size hook and deploys it where the app looks for it:
#   C:\ProgramData\MobDisplayController\start_menu_size_hook_<time>.dll
#
# The app itself does the injection (tray menu -> 开始菜单尺寸 -> 把钩子注入开始菜单进程), so this script
# never touches another process. It deploys under a new file name every time, because the shell keeps the
# library it already loaded until it restarts - the same reason the button driver alternates btndrv.sys and
# btndrv_alt.sys. The newest file in that folder is the one the app injects.

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # ...\mob-display-controller
$source = Join-Path $PSScriptRoot 'start_menu_size_hook.cpp'
$deployDir = 'C:\ProgramData\MobDisplayController'
$vcvars = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
$cppwinrt = 'C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\cppwinrt'

foreach ($path in @($source, $vcvars, $cppwinrt)) {
    if (-not (Test-Path $path)) { throw "not found: $path" }
}

$stamp = Get-Date -Format HHmmss
$intermediate = Join-Path $env:TEMP "smshook-$stamp"
$dll = Join-Path $deployDir "start_menu_size_hook_$stamp.dll"
New-Item -ItemType Directory -Path $intermediate -Force | Out-Null

'===== build ====='
# The intermediate files go to a scratch folder: cl writes the .obj next to the current directory by default,
# which would drop a stray .obj into the repo. The paths are given as complete file names - a /Fo path that
# ends in a backslash loses its closing quote when cmd parses the line.
$command = "call `"$vcvars`" >nul && cl /nologo /LD /O2 /GS- /W3 /std:c++17 /EHsc /DUNICODE /D_UNICODE " +
           "/I`"$cppwinrt`" /Fo`"$intermediate\start_menu_size_hook.obj`" /Fe`"$intermediate\start_menu_size_hook.dll`" `"$source`" " +
           "/link /DLL windowsapp.lib runtimeobject.lib kernel32.lib advapi32.lib user32.lib"
cmd /c $command

$built = Join-Path $intermediate 'start_menu_size_hook.dll'
if (-not (Test-Path $built)) { throw 'the compiler did not produce a DLL' }
"built {0} bytes" -f (Get-Item $built).Length

'===== deploy ====='
New-Item -ItemType Directory -Path $deployDir -Force | Out-Null
Copy-Item $built $dll -Force

# SearchHost runs in an AppContainer and its LoadLibraryW silently returns 0 for a library it may not read,
# and the hook writes its log in this folder too.
icacls $deployDir /grant '*S-1-15-2-1:(OI)(CI)(M)' | Out-Null

"deployed $dll"
''
'Now use the tray menu: 开始菜单尺寸 -> 把钩子注入开始菜单进程 (or stop and start the app).'
'The sizes themselves are in the menu too; it writes C:\ProgramData\MobDisplayController\start-menu-size.ini,'
'which the hook reads every time the Start menu is opened.'
