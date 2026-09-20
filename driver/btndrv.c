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
NTSYSAPI NTSTATUS NTAPI ZwSetEvent(_In_ HANDLE EventHandle, _Out_opt_ PLONG PreviousState);

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
} BTN_BLOB, *PBTN_BLOB;

static PBTN_BLOB        g_Blob;
static KSPIN_LOCK       g_Lock;
static HANDLE           g_Key;
static HANDLE           g_ServiceKey;
static HANDLE           g_Event;            /* named event signalled when the button is reported */
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
        if ((stamp.QuadPart - g_LastWrite.QuadPart) / 10000 > BTN_MIN_WRITE) {
            g_LastWrite = stamp;
            if (g_Key == NULL)
                BtnCreateKey();     /* retry: the first attempt can be too early in boot */
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
    if (g_Event != NULL)
        ZwSetEvent(g_Event, NULL);
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

/* The named event has to exist before user mode can open it, and it can only be created from a
   context where object calls are legal - the first request after a session exists is as good a
   moment as any, and the call is a cheap no-op once it has succeeded. */
static VOID BtnCreateObjects(VOID)
{
    UNICODE_STRING name;
    OBJECT_ATTRIBUTES attributes;

    if (g_Event != NULL || KeGetCurrentIrql() != PASSIVE_LEVEL)
        return;

    RtlInitUnicodeString(&name, L"\\BaseNamedObjects\\Global\\BtnDrvEvent");
    InitializeObjectAttributes(&attributes, &name,
                               OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);
    if (!NT_SUCCESS(ZwCreateEvent(&g_Event, EVENT_ALL_ACCESS, &attributes,
                                  NotificationEvent, FALSE))) {
        g_Event = NULL;
    }
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

    if (ioctl == BTN_IOCTL_EVENT) {
        /* Take a copy of the stack location so the request can be followed to its completion -
           the answer is what says the button was pressed. Everything else stays a memcpy-free
           skip; only this one ioctl pays for the extra plumbing. */
        BtnCreateObjects();
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
    g_Blob->Version = 2;
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
    if (KeGetCurrentIrql() == PASSIVE_LEVEL)
        BtnCreateKey();

    BtnFlush();

    DbgPrint("btndrv: loaded\n");
    return STATUS_SUCCESS;
}
