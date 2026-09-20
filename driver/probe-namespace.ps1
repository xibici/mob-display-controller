# Enumerates the object-manager namespace around \BaseNamedObjects so we can see whether the
# driver's event object really exists, and where.  ASCII only.
$ErrorActionPreference = 'Stop'

Add-Type -Namespace NtApi -Name Dir -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)]
public struct UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
[StructLayout(LayoutKind.Sequential)]
public struct OBJECT_ATTRIBUTES {
    public int Length; public IntPtr RootDirectory; public IntPtr ObjectName;
    public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService;
}
[StructLayout(LayoutKind.Sequential)]
public struct OBJDIRINFO { public UNICODE_STRING Name; public UNICODE_STRING TypeName; }
[DllImport("ntdll.dll")] public static extern int NtOpenDirectoryObject(out IntPtr h, uint access, ref OBJECT_ATTRIBUTES oa);
[DllImport("ntdll.dll")] public static extern int NtQueryDirectoryObject(IntPtr h, IntPtr buf, uint len, bool single, bool restart, ref uint ctx, out uint ret);
[DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
'@

function Get-ObjectChildren {
    param([string]$Path)

    $name = [System.Runtime.InteropServices.Marshal]::StringToHGlobalUni($Path)
    $us = New-Object NtApi.Dir+UNICODE_STRING
    $us.Length = [uint16]($Path.Length * 2)
    $us.MaximumLength = [uint16]($Path.Length * 2 + 2)
    $us.Buffer = $name
    $usPtr = [System.Runtime.InteropServices.Marshal]::AllocHGlobal(16)
    [System.Runtime.InteropServices.Marshal]::StructureToPtr($us, $usPtr, $false)

    $oa = New-Object NtApi.Dir+OBJECT_ATTRIBUTES
    $oa.Length = 48
    $oa.ObjectName = $usPtr
    $oa.Attributes = 0x40   # OBJ_CASE_INSENSITIVE

    $handle = [IntPtr]::Zero
    # DIRECTORY_QUERY | DIRECTORY_TRAVERSE
    $st = [NtApi.Dir]::NtOpenDirectoryObject([ref]$handle, 0x3, [ref]$oa)
    if ($st -ne 0) { "   cannot open '$Path' - status 0x{0:X8}" -f $st; return }

    $bufSize = 65536
    $buf = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($bufSize)
    $ctx = [uint32]0
    $returned = [uint32]0
    $result = New-Object System.Collections.Generic.List[string]

    while ($true) {
        $st = [NtApi.Dir]::NtQueryDirectoryObject($handle, $buf, $bufSize, $true, $false, [ref]$ctx, [ref]$returned)
        if ($st -eq 0x8000001A -or $returned -eq 0) { break }   # STATUS_NO_MORE_ENTRIES
        if ($st -ne 0) { "   query failed - status 0x{0:X8}" -f $st; break }

        $info = [System.Runtime.InteropServices.Marshal]::PtrToStructure($buf, [type][NtApi.Dir+OBJDIRINFO])
        $entryName = [System.Runtime.InteropServices.Marshal]::PtrToStringUni($info.Name.Buffer, $info.Name.Length / 2)
        $entryType = [System.Runtime.InteropServices.Marshal]::PtrToStringUni($info.TypeName.Buffer, $info.TypeName.Length / 2)
        $result.Add(("{0,-34} {1}" -f $entryName, $entryType))
    }

    [System.Runtime.InteropServices.Marshal]::FreeHGlobal($buf)
    [NtApi.Dir]::CloseHandle($handle) | Out-Null
    [System.Runtime.InteropServices.Marshal]::FreeHGlobal($usPtr)
    [System.Runtime.InteropServices.Marshal]::FreeHGlobal($name)
    return $result
}

"--- \BaseNamedObjects (matches for BtnDrv/Global) ---"
Get-ObjectChildren '\BaseNamedObjects' | Where-Object { $_ -match 'BtnDrv|Global|GoDisplay' }
"--- \BaseNamedObjects\Global (matches for BtnDrv) ---"
Get-ObjectChildren '\BaseNamedObjects\Global' | Where-Object { $_ -match 'BtnDrv' }
"--- \Sessions\1\BaseNamedObjects (matches for BtnDrv) ---"
Get-ObjectChildren '\Sessions\1\BaseNamedObjects' | Where-Object { $_ -match 'BtnDrv' }
