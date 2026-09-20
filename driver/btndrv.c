/*
 * btndrv.c - diagnostic pass-through upper filter for the ACPI power button device.
 *
 * Purpose: answer one question with evidence, not opinion - does anything at all reach the
 * driver stack of ACPI\PNP0C0C when the physical power button is pressed? The device's
 * IOCTL_GET_SYS_BUTTON_EVENT never reports a press from user mode (measured: 13276 polls
 * across a real press, zero events), so this filter is the last place that could know.
 *
 * It changes no behaviour: every IRP is forwarded untouched, the flags are copied from the
 * device below, and no request is ever completed, failed or delayed by this driver. What it
 * adds is a record of what it saw, written to HKLM\SOFTWARE\BtnDrv as a REG_BINARY blob so
 * user mode can read it without a debugger and without touching the kernel.
 *
 * Install (diagnostic stage):
 *   sc create btndrv binpath= C:\Windows\System32\drivers\btndrv.sys type= kernel start= demand error= normal
 *   UpperFilters (REG_MULTI_SZ) = btndrv   on  HKLM\SYSTEM\CurrentControlSet\Enum\ACPI\PNP0C0C\<instance>
 *   reboot. Remove the UpperFilters entry in Safe Mode if the button device misbehaves.
 */

#include <ntddk.h>

/* Declared here: ntoskrnl exports these about as far back as Windows goes, but the WDK headers only
   pull them in through ntifs.h, which conflicts with ntddk.h. Without this the compiler assumes an
   implicit int return, which is sloppy in a driver even though x64 makes it harmless. */
NTSYSAPI NTSTATUS NTAPI ZwCreateEvent(_Out_ PHANDLE EventHandle, _In_ ACCESS_MASK DesiredAccess,
                                      _In_opt_ POBJECT_ATTRIBUTES ObjectAttributes,
                                      _In_ EVENT_TYPE EventType, _In_ BOOLEAN InitialState);
NTSYSAPI NTSTATUS NTAPI ObReferenceObjectByHandle(_In_ HANDLE Handle, _In_ ACCESS_MASK DesiredAccess,
                                                  _In_opt_ POBJECT_TYPE ObjectType,
                                                  _In_ KPROCESSOR_MODE AccessMode,
                                                  _Out_ PVOID* Object,
                                                  _Out_opt_ POBJECT_HANDLE_INFORMATION HandleInformation);
NTSYSAPI NTSTATUS NTAPI ZwOpenEvent(_Out_ PHANDLE EventHandle, _In_ ACCESS_MASK DesiredAccess,
                                    _In_ POBJECT_ATTRIBUTES ObjectAttributes);
NTSYSAPI NTSTATUS NTAPI ZwWaitForSingleObject(_In_ HANDLE Handle, _In_ BOOLEAN Alertable,
                                              _In_opt_ PLARGE_INTEGER Timeout);

/* Same story for the security descriptor helpers, and for the two types ntddk.h does not bring in.
   The structures below are deliberately not called SECURITY_DESCRIPTOR / SID_IDENTIFIER_AUTHORITY:
   they only have to be layout-identical to the real ones, which they are, and this keeps the file
   self-contained without pulling in ntifs.h (which cannot be combined with ntddk.h). */
typedef struct _BTN_SID_AUTHORITY { UCHAR Value[6]; } BTN_SID_AUTHORITY;

typedef struct _BTN_SECURITY_DESCRIPTOR {
    UCHAR   Revision;
    UCHAR   Sbz1;
    USHORT  Control;
    PSID    Owner;
    PSID    Group;
    PACL    Sacl;
    PACL    Dacl;
} BTN_SECURITY_DESCRIPTOR;

NTSYSAPI NTSTATUS NTAPI RtlInitializeSid(PSID Sid, const BTN_SID_AUTHORITY* IdentifierAuthority,
                                         UCHAR SubAuthorityCount);
NTSYSAPI PULONG   NTAPI RtlSubAuthoritySid(PSID Sid, ULONG SubAuthority);
NTSYSAPI NTSTATUS NTAPI RtlCreateAcl(PACL Acl, ULONG AclLength, ULONG AclRevision);
NTSYSAPI NTSTATUS NTAPI RtlAddAccessAllowedAce(PACL Acl, ULONG AceRevision, ACCESS_MASK AccessMask,
                                               PSID Sid);
NTSYSAPI NTSTATUS NTAPI RtlCreateSecurityDescriptor(PVOID SecurityDescriptor, ULONG Revision);
NTSYSAPI NTSTATUS NTAPI RtlSetOwnerSecurityDescriptor(PVOID SecurityDescriptor, PSID Owner,
                                                      BOOLEAN OwnerDefaulted);
NTSYSAPI NTSTATUS NTAPI RtlSetGroupSecurityDescriptor(PVOID SecurityDescriptor, PSID Group,
                                                      BOOLEAN GroupDefaulted);
NTSYSAPI NTSTATUS NTAPI RtlSetDaclSecurityDescriptor(PVOID SecurityDescriptor, BOOLEAN DaclPresent,
                                                     PACL Dacl, BOOLEAN DaclDefaulted);

#define BTN_MAGIC      0x42544E44UL     /* 'BTND' */
#define BTN_LOG_MAX    64
#define BTN_MAJOR_MAX  0x20
#define BTN_MIN_WRITE  50               /* ms between registry writes */

#define BTN_KEY_PATH   L"\\Registry\\Machine\\SOFTWARE\\BtnDrv"
#define BTN_POOL_TAG   'NTBD'

typedef struct _BTN_ENTRY {
    ULONG Major;
    ULONG Minor;
    ULONG Ioctl;
    ULONG Tick;
} BTN_ENTRY, *PBTN_ENTRY;

typedef struct _BTN_BLOB {
    ULONG     Magic;
    ULONG     Version;
    ULONG     Stage;            /* 1 = DriverEntry done, 2 = AddDevice done, 3 = unloaded */
    ULONG     AddDeviceSeen;
    ULONG     AttachOk;
    ULONG     Count;
    ULONG     Seq;
    ULONG     LastMajor;
    ULONG     LastMinor;
    ULONG     LastIoctl;
    ULONG     LastTick;
    ULONG     MajorMask;
    ULONG     MajorCount[BTN_MAJOR_MAX];
    BTN_ENTRY Log[BTN_LOG_MAX];
    ULONG     ButtonHits;          /* completions of IOCTL_GET_SYS_BUTTON_EVENT that carried SYS_BUTTON_POWER */
    ULONG     LastButtonValue;
    ULONG     QueriesWithData;     /* completions of that ioctl that returned any data at all */
    ULONG     EventStatus;         /* 0x424E0001 simple name, 0x424E0002 full path, else the NTSTATUS of the failure */
} BTN_BLOB, *PBTN_BLOB;

static PBTN_BLOB        g_Blob;
static KSPIN_LOCK       g_Lock;
static HANDLE           g_Key;
static HANDLE           g_ServiceKey;
static HANDLE           g_Event;            /* named event handle */
static PKEVENT          g_EventObject;      /* the same event as an object: KeSetEvent works at any IRQL */
static HANDLE           g_EventThreadHandle;    /* kept open so unload can wait for the thread to leave */
static LONG             g_EventThreadStarted;   /* claimed once, with an interlocked exchange */
static LONG             g_EventThreadStop;      /* set by unload: the thread must return before the image goes */
static WCHAR            g_ServicePathBuffer[256];
static UNICODE_STRING   g_ServicePath;
static LARGE_INTEGER    g_LastWrite;
static PDEVICE_OBJECT   g_Self;
static PDEVICE_OBJECT   g_Lower;
static BOOLEAN          g_AddDeviceSeen;

static ULONG BtnNowMs(VOID)
{
    /* Interrupt time is in 100ns units; this only ever needs to be monotonic. */
    return (ULONG)(KeQueryInterruptTime() / 10000ULL);
}

static VOID BtnFlush(VOID)
{
    UNICODE_STRING name;

    if (g_Blob == NULL)
        return;

    /* The place that always exists: the driver's own service key. The key under SOFTWARE is
       only a convenience copy, and it may be missing if the hive was not mounted yet when this
       driver was started - which is exactly what happened on the first run. */
    if (g_ServicePath.Buffer != NULL) {
        if (g_ServiceKey == NULL) {
            OBJECT_ATTRIBUTES attributes;
            InitializeObjectAttributes(&attributes, &g_ServicePath,
                                       OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);
            if (NT_SUCCESS(ZwOpenKey(&g_ServiceKey, KEY_SET_VALUE | KEY_QUERY_VALUE, &attributes))) {
                DbgPrint("btndrv: service key opened\n");
            } else {
                g_ServiceKey = NULL;
            }
        }

        if (g_ServiceKey != NULL) {
            RtlInitUnicodeString(&name, L"BtnDrvLog");
            ZwSetValueKey(g_ServiceKey, &name, 0, REG_BINARY, g_Blob, sizeof(BTN_BLOB));
        }
    }

    if (g_Key != NULL) {
        RtlInitUnicodeString(&name, L"State");
        ZwSetValueKey(g_Key, &name, 0, REG_BINARY, g_Blob, sizeof(BTN_BLOB));
    }
}

static VOID BtnCreateKey(VOID);
static VOID BtnCreateEventThread(PVOID Context);
static VOID BtnStartEventThread(VOID);
static VOID BtnFlush(VOID);

static VOID BtnRecord(UCHAR Major, UCHAR Minor, ULONG Ioctl)
{
    KIRQL irql;
    ULONG index;
    ULONG now;

    if (g_Blob == NULL)
        return;

    now = BtnNowMs();

    KeAcquireSpinLock(&g_Lock, &irql);

    g_Blob->Seq++;
    g_Blob->Count++;
    g_Blob->LastMajor = Major;
    g_Blob->LastMinor = Minor;
    g_Blob->LastIoctl = Ioctl;
    g_Blob->LastTick = now;
    if (Major < BTN_MAJOR_MAX) {
        g_Blob->MajorCount[Major]++;
        g_Blob->MajorMask |= (1UL << (Major & 31));
    }

    index = g_Blob->Count % BTN_LOG_MAX;
    g_Blob->Log[index].Major = Major;
    g_Blob->Log[index].Minor = Minor;
    g_Blob->Log[index].Ioctl = Ioctl;
    g_Blob->Log[index].Tick = now;

    KeReleaseSpinLock(&g_Lock, irql);

    /* The registry write is the only operation that needs PASSIVE_LEVEL, and it is throttled:
       requests on this device are rare, but a burst should not turn into a burst of writes. */
    if (KeGetCurrentIrql() == PASSIVE_LEVEL) {
        LARGE_INTEGER stamp;
        KeQuerySystemTime(&stamp);
        if (g_Key == NULL)
            BtnCreateKey();     /* retry: the first attempt can be too early in boot */
        BtnStartEventThread();  /* likewise: only does anything if the attempt at load time failed */
        if ((stamp.QuadPart - g_LastWrite.QuadPart) / 10000 > BTN_MIN_WRITE) {
            g_LastWrite = stamp;
            BtnFlush();
        }
    }
}

static VOID BtnCreateKey(VOID)
{
    UNICODE_STRING path;
    OBJECT_ATTRIBUTES attributes;
    NTSTATUS status;
    ULONG disposition = 0;

    if (g_Key != NULL)
        return;

    RtlInitUnicodeString(&path, BTN_KEY_PATH);
    InitializeObjectAttributes(&attributes, &path, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);

    status = ZwCreateKey(&g_Key, KEY_SET_VALUE | KEY_QUERY_VALUE, &attributes, 0, NULL,
                         REG_OPTION_NON_VOLATILE, &disposition);
    if (!NT_SUCCESS(status)) {
        g_Key = NULL;
        DbgPrint("btndrv: ZwCreateKey failed 0x%08X\n", status);
        return;
    }

    DbgPrint("btndrv: log key ready (disposition %u)\n", disposition);
}

/* IOCTL_GET_SYS_BUTTON_EVENT: what Windows itself sends to this device to learn whether the
   power button was pressed, and SYS_BUTTON_POWER is what its answer carries. The device only
   tells a caller that a real press happened - measured: 13276 user-mode polls across a press
   returned zero, while the system's own request was answered. So this filter watches the system's
   request instead of trying to be the caller. */
#define BTN_IOCTL_EVENT   0x00294144UL
#define BTN_SYS_BUTTON_POWER 0x00000001UL

static VOID BtnCreateObjects(VOID);

static IO_COMPLETION_ROUTINE BtnEventCompletion;

static VOID BtnSignal(VOID)
{
    /* KeSetEvent, not ZwSetEvent: this runs from an IRP completion routine, and the button event is
       reported by the ACPI stack - which can complete the pending request at DISPATCH_LEVEL.
       ZwSetEvent is only legal at PASSIVE_LEVEL, so using it would be a bugcheck waiting to happen. */
    if (g_EventObject != NULL)
        KeSetEvent(g_EventObject, IO_NO_INCREMENT, FALSE);
}

static NTSTATUS BtnEventCompletion(PDEVICE_OBJECT DeviceObject, PIRP Irp, PVOID Context)
{
    UNREFERENCED_PARAMETER(DeviceObject);
    UNREFERENCED_PARAMETER(Context);

    if (Irp->IoStatus.Status == STATUS_SUCCESS &&
        Irp->AssociatedIrp.SystemBuffer != NULL &&
        Irp->IoStatus.Information >= sizeof(ULONG)) {
        ULONG value = *(PULONG)Irp->AssociatedIrp.SystemBuffer;
        KIRQL irql;

        if (g_Blob != NULL) {
            KeAcquireSpinLock(&g_Lock, &irql);
            g_Blob->QueriesWithData++;
            g_Blob->LastButtonValue = value;
            if ((value & BTN_SYS_BUTTON_POWER) != 0)
                g_Blob->ButtonHits++;
            KeReleaseSpinLock(&g_Lock, irql);
        }

        if ((value & BTN_SYS_BUTTON_POWER) != 0)
            BtnSignal();
    }

    if (Irp->PendingReturned)
        IoMarkIrpPending(Irp);

    return STATUS_CONTINUE_COMPLETION;
}

/* The event has to be openable by the tray app, and that is the part that turned out to be hard.
   An object created by a kernel call with no security descriptor gets a default one derived from
   the creating token - and because the object's owner then comes out as whoever that was, Windows
   derives the object's default *mandatory label* from the owner as well. Created from a request
   context the owner can be SYSTEM (the power manager polls this device early in boot), which puts a
   System integrity label on the object - and a High-integrity user process asking for
   EVENT_MODIFY_STATE is then doing a "write up", so it is refused with access denied even though
   the object exists and the name resolves perfectly. That is what made this intermittent: it
   depended on which request created the event first.
   So the descriptor is built explicitly: Everyone may signal and wait on it, and the owner is a SID
   that is not SYSTEM so the derived label stays ordinary. The storage is static because the
   descriptor has to remain valid for as long as the object does. */
static ULONG g_EventSidBuffer[16];
static ULONG g_EventAclBuffer[32];
static BTN_SECURITY_DESCRIPTOR g_EventSd;

static VOID BtnBuildEventSecurity(VOID)
{
    static const BTN_SID_AUTHORITY world = { { 0, 0, 0, 0, 0, 1 } };   /* SECURITY_WORLD_SID_AUTHORITY */
    PSID sid = (PSID)g_EventSidBuffer;
    PACL acl = (PACL)g_EventAclBuffer;

    RtlInitializeSid(sid, &world, 1);
    *RtlSubAuthoritySid(sid, 0) = 0;                        /* SECURITY_WORLD_RID => S-1-1-0 */
    RtlCreateAcl(acl, sizeof(g_EventAclBuffer), ACL_REVISION);
    RtlAddAccessAllowedAce(acl, ACL_REVISION, EVENT_ALL_ACCESS, sid);

    RtlCreateSecurityDescriptor(&g_EventSd, SECURITY_DESCRIPTOR_REVISION);
    RtlSetOwnerSecurityDescriptor(&g_EventSd, sid, FALSE);
    RtlSetGroupSecurityDescriptor(&g_EventSd, sid, FALSE);
    RtlSetDaclSecurityDescriptor(&g_EventSd, TRUE, acl, FALSE);
}

/* Spawns the one system thread that owns the named event. Requests can arrive on several CPUs at
   once, so "already started" is claimed with an interlocked exchange and the loser backs off; a
   failed spawn is not fatal either, it is retried from the next request. The thread object is kept
   because BtnUnload has to wait for the thread rather than unmap the code underneath it. */
static VOID BtnStartEventThread(VOID)
{
    HANDLE thread = NULL;

    if (g_EventObject != NULL || g_EventThreadStarted != 0)
        return;
    if (KeGetCurrentIrql() != PASSIVE_LEVEL)
        return;
    if (InterlockedCompareExchange(&g_EventThreadStarted, 1, 0) != 0)
        return;

    if (!NT_SUCCESS(PsCreateSystemThread(&thread, THREAD_ALL_ACCESS, NULL, NULL, NULL,
                                         BtnCreateEventThread, NULL))) {
        g_EventThreadStarted = 0;       /* let a later request try again */
        return;
    }

    /* The handle is kept (not closed) so that unload can wait for the thread: waiting on a handle
       cannot fail the way an object reference can, and there is then no path that leaves the thread
       running in an unmapped image. */
    g_EventThreadHandle = thread;
}

/* The event is created by a system thread rather than from a request's dispatch context, and the
   reason is privilege, not location: assigning an explicit owner to the object needs
   SeRestorePrivilege, which a system thread has (it runs with the system token) and an arbitrary
   caller's context may not. Measured: the object lands in the global namespace either way, so what
   the thread buys is a descriptor that is built identically on every boot. Requests used to race to
   be the one that created the event, and which context won that race decided whether the app could
   open the result. The attempt is retried, since the first one can run before the object namespace
   is ready. */
static VOID BtnCreateEventThread(PVOID Context)
{
    UNICODE_STRING name;
    OBJECT_ATTRIBUTES attributes;
    NTSTATUS status;
    LARGE_INTEGER delay;

    UNREFERENCED_PARAMETER(Context);

    BtnBuildEventSecurity();

    for (;;) {
        BOOLEAN opened = FALSE;

        if (g_EventThreadStop)
            break;                          /* see BtnUnload: this code is about to be unmapped */

        RtlInitUnicodeString(&name, L"\\BaseNamedObjects\\Global\\BtnDrvEvent");
        InitializeObjectAttributes(&attributes, &name,
                                   OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, &g_EventSd);

        /* A collision means the object outlived a previous load - the tray app holding a handle is
           enough to keep it alive - so open it rather than treating that as a failure. The descriptor
           is the one built above either way, since nothing else creates an object under this name. */
        status = ZwCreateEvent(&g_Event, EVENT_ALL_ACCESS, &attributes, NotificationEvent, FALSE);
        if (status == STATUS_OBJECT_NAME_COLLISION) {
            opened = TRUE;
            status = ZwOpenEvent(&g_Event, EVENT_ALL_ACCESS, &attributes);
        }
        if (NT_SUCCESS(status)) {
            status = ObReferenceObjectByHandle(g_Event, EVENT_MODIFY_STATE, *ExEventObjectType,
                                                KernelMode, (PVOID*)&g_EventObject, NULL);
            if (NT_SUCCESS(status)) {
                if (g_Blob != NULL)
                    g_Blob->EventStatus = opened ? 0x424E0004 : 0x424E0003;
                BtnFlush();
                DbgPrint("btndrv: button event ready (%s)\n", opened ? "opened" : "created");
                break;
            }

            ZwClose(g_Event);
            g_Event = NULL;
        }

        if (g_Blob != NULL)
            g_Blob->EventStatus = (ULONG)status;

        delay.QuadPart = -5000000;      /* 500 ms */
        KeDelayExecutionThread(KernelMode, FALSE, &delay);
    }

    PsTerminateSystemThread(STATUS_SUCCESS);
}

static NTSTATUS BtnPassThrough(PDEVICE_OBJECT DeviceObject, PIRP Irp)
{
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);
    ULONG ioctl = 0;

    UNREFERENCED_PARAMETER(DeviceObject);

    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL ||
        stack->MajorFunction == IRP_MJ_INTERNAL_DEVICE_CONTROL) {
        ioctl = stack->Parameters.DeviceIoControl.IoControlCode;
    }

    BtnRecord(stack->MajorFunction, stack->MinorFunction, ioctl);

    if (stack->MajorFunction == IRP_MJ_PNP && stack->MinorFunction == IRP_MN_REMOVE_DEVICE) {
        /* Let the stack below tear down first, then detach and delete our filter object. Without this
           the device can never be stopped and restarted - which is what "restart the device" needs. */
        NTSTATUS status;
        IoSkipCurrentIrpStackLocation(Irp);
        status = IoCallDriver(g_Lower, Irp);
        if (g_Self != NULL) {
            IoDetachDevice(g_Lower);
            IoDeleteDevice(g_Self);
            g_Self = NULL;
            g_Lower = NULL;
        }
        return status;
    }

    if (ioctl == BTN_IOCTL_EVENT) {
        /* Take a copy of the stack location so the request can be followed to its completion -
           the answer is what says the button was pressed. Everything else stays a memcpy-free
           skip; only this one ioctl pays for the extra plumbing. */
        IoCopyCurrentIrpStackLocationToNext(Irp);
        IoSetCompletionRoutine(Irp, BtnEventCompletion, NULL, TRUE, TRUE, TRUE);
        return IoCallDriver(g_Lower, Irp);
    }

    IoSkipCurrentIrpStackLocation(Irp);

    if (stack->MajorFunction == IRP_MJ_POWER)
        return PoCallDriver(g_Lower, Irp);

    return IoCallDriver(g_Lower, Irp);
}

static NTSTATUS BtnAddDevice(PDRIVER_OBJECT DriverObject, PDEVICE_OBJECT PhysicalDeviceObject)
{
    PDEVICE_OBJECT device = NULL;
    KIRQL oldIrql;
    NTSTATUS status;

    g_AddDeviceSeen = TRUE;

    status = IoCreateDevice(DriverObject, 0, NULL, FILE_DEVICE_UNKNOWN, FILE_DEVICE_SECURE_OPEN,
                            FALSE, &device);
    if (!NT_SUCCESS(status)) {
        DbgPrint("btndrv: IoCreateDevice failed 0x%08X\n", status);
        return status;
    }

    /* Inherit exactly the buffering/power behaviour of the device we attach to: the filter must
       never change how requests are described to the driver below. */
    device->Flags |= (PhysicalDeviceObject->Flags &
                      (DO_BUFFERED_IO | DO_DIRECT_IO | DO_POWER_PAGABLE | DO_POWER_INRUSH));

    g_Lower = IoAttachDeviceToDeviceStack(device, PhysicalDeviceObject);
    if (g_Lower == NULL) {
        IoDeleteDevice(device);
        return STATUS_UNSUCCESSFUL;
    }

    g_Self = device;
    device->Flags &= ~DO_DEVICE_INITIALIZING;

    KeAcquireSpinLock(&g_Lock, &oldIrql);
    g_Blob->Stage = 2;
    g_Blob->AddDeviceSeen = 1;
    g_Blob->AttachOk = 1;
    KeReleaseSpinLock(&g_Lock, oldIrql);
    BtnFlush();

    DbgPrint("btndrv: attached to %wZ\n", &PhysicalDeviceObject->DriverObject->DriverName);
    return STATUS_SUCCESS;
}

static VOID BtnUnload(PDRIVER_OBJECT DriverObject)
{
    UNREFERENCED_PARAMETER(DriverObject);

    /* Stop the event thread first, and wait for it to actually leave: it is running this driver's
       code, and once this routine returns the image can be unmapped. Nothing below may be touched
       until it has terminated, which it does at most one retry interval after the flag is set. */
    g_EventThreadStop = 1;
    if (g_EventThreadHandle != NULL) {
        ZwWaitForSingleObject(g_EventThreadHandle, FALSE, NULL);
        ZwClose(g_EventThreadHandle);
        g_EventThreadHandle = NULL;
    }

    if (g_Self != NULL) {
        if (g_Lower != NULL)
            IoDetachDevice(g_Lower);
        IoDeleteDevice(g_Self);
        g_Self = NULL;
        g_Lower = NULL;
    }

    if (g_Key != NULL) {
        BtnFlush();
        ZwClose(g_Key);
        g_Key = NULL;
    }

    if (g_ServiceKey != NULL) {
        BtnFlush();
        ZwClose(g_ServiceKey);
        g_ServiceKey = NULL;
    }

    if (g_EventObject != NULL) {
        g_EventObject = NULL;
    }

    if (g_Event != NULL) {
        ZwClose(g_Event);
        g_Event = NULL;
    }

    if (g_Blob != NULL) {
        ExFreePoolWithTag(g_Blob, BTN_POOL_TAG);
        g_Blob = NULL;
    }

    DbgPrint("btndrv: unloaded\n");
}

NTSTATUS DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath)
{
    ULONG i;

    UNREFERENCED_PARAMETER(RegistryPath);

    KeInitializeSpinLock(&g_Lock);

    g_Blob = (PBTN_BLOB)ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(BTN_BLOB), BTN_POOL_TAG);
    if (g_Blob == NULL)
        return STATUS_INSUFFICIENT_RESOURCES;

    RtlZeroMemory(g_Blob, sizeof(BTN_BLOB));
    g_Blob->Magic = BTN_MAGIC;
    g_Blob->Version = 3;
    g_Blob->Stage = 1;

    if (RegistryPath != NULL && RegistryPath->Length > 0 &&
        RegistryPath->Length < sizeof(g_ServicePathBuffer)) {
        RtlCopyMemory(g_ServicePathBuffer, RegistryPath->Buffer, RegistryPath->Length);
        g_ServicePath.Buffer = g_ServicePathBuffer;
        g_ServicePath.Length = RegistryPath->Length;
        g_ServicePath.MaximumLength = sizeof(g_ServicePathBuffer);
    }

    for (i = 0; i <= IRP_MJ_MAXIMUM_FUNCTION; i++)
        DriverObject->MajorFunction[i] = BtnPassThrough;

    DriverObject->DriverUnload = BtnUnload;
    DriverObject->DriverExtension->AddDevice = BtnAddDevice;

    /* Loaded at boot with no user session around, so the key is created on demand - but only
       from a context where a registry call is legal, and once at most. */
    if (KeGetCurrentIrql() == PASSIVE_LEVEL) {
        BtnCreateKey();
        BtnStartEventThread();
    }

    BtnFlush();

    DbgPrint("btndrv: loaded\n");
    return STATUS_SUCCESS;
}
