# btndrv — 电源键过滤器驱动

## 为什么需要它

这台机器(Legion Go 83E1 / Win11 24H2)的**物理电源键无法从用户态读取**。这一条是**实测**结论,不是推测:

| 尝试 | 结果 |
|---|---|
| 用户态轮询 `IOCTL_GET_SYS_BUTTON_EVENT`(`ACPI\PNP0C0C`,GUID_DEVICE_SYS_BUTTON) | 13,276 次轮询 + 真实按键 = **0 事件**,返回值永远是 0 ✗ |
| 把电源键设为"不采取任何操作"后按键 | 系统事件日志里**一条记录都没有**(6 次按键 = 0 条)✗ |
| HID 通道(所有 HID 集合的 usage page/usage) | 没有任何 System Control / 电源用途 ✗ |
| `ACPI\FixedButton` 设备(acpifiltr 挂载点) | 本机**不存在** ✗ |

**但有一条路能走通**:Windows 自己会往按钮设备发 `IOCTL_GET_SYS_BUTTON_EVENT`(0x00294144),**它发的那一次能得到真正的按键事件**(返回值里带 `SYS_BUTTON_POWER = 0x1`)。

所以做法是:**在上层挂一个纯直通过滤器,旁听那个请求的完成结果** ✓ —— 这也就是开源项目 `valinet/acpifiltr` 的原理 ✓(区别是它挂 `ACPI\FixedButton`,而本机要挂 `ACPI\PNP0C0C`)。

## 它做什么 / 不做什么

**做**:记录它看到的每一个 IRP(主功能号/次功能号/IOCTL 码/毫秒时间戳,写进自己的服务键);
对 `IOCTL_GET_SYS_BUTTON_EVENT` 挂完成例程,读返回值,带 `SYS_BUTTON_POWER` 时 **signal 命名事件 `Global\BtnDrvEvent`**。

**不做**:不改任何行为 —— 每个 IRP 原样转发、缓冲方式/电源标志从下层设备继承、不完成/不失败/不延迟任何请求 ✓。

因为按下的瞬间 **不产生任何原生动作**(电源键设为"不采取任何操作"),所以:**不睡眠、不黑屏、不弹解锁** ✓✓

## 安装

```powershell
# 需要:WDK(编译)+ 管理员 + Secure Boot 关闭(自签驱动)
powershell -ExecutionPolicy Bypass -File build-btndrv.ps1     # 编译 + 自签,产出 btndrv.sys
powershell -ExecutionPolicy Bypass -File install-btndrv.ps1   # 部署 + 注册 + 挂 UpperFilters
# 然后重启
```

重启后验证:
```powershell
sc query btndrv                                                        # 应为 RUNNING
Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\btndrv BtnDrvLog   # 有二进制日志 = 驱动已加载
powershell -File wait-event.ps1                                        # 打开并等待事件,然后按电源键
```

日志字段(`BtnDrvLog`,1212 字节,全是 ULONG,按顺序):
`Magic, Version, Stage, AddDeviceSeen, AttachOk, Count, Seq, LastMajor, LastMinor, LastIoctl, LastTick, MajorMask, MajorCount[32]`,偏移 176 起是 64 条 `{Major, Minor, Ioctl, Tick}` 记录,末尾三个是 `ButtonHits, LastButtonValue, QueriesWithData`。

## 卸载 / 出问题怎么办

**如果电源键或其他东西异常**:进**安全模式**运行
```powershell
powershell -ExecutionPolicy Bypass -File remove-btndrv.ps1
```
它会移除 `UpperFilters`、删除服务与驱动文件、关闭测试签名,然后重启即完全恢复 ✓。

## 注意事项

- 需要 **测试签名模式**(`bcdedit /set testsigning on`)因为驱动是自签的;本机 Secure Boot 本来就是关的 ✓。若日后开启 Secure Boot,此驱动将无法加载(届时需用 `valinet/ssde` 之类做可加载签名)。
- **Windows 大版本更新后需要重新验证**(设备实例路径 `ACPI\PNP0C0C\2&daba3ff&1` 可能变化,`install-btndrv.ps1` 里的 `$DeviceKey` 就要跟着改)。
- 该驱动是**诊断+功能**两用的最小实现:无文件 I/O、无自旋外分配(除 DriverEntry 的一次非分页池分配),只有**一个系统线程**专门负责创建那个命名事件(见下一节)。
- 日志写在**驱动自己的服务键**下(`HKLM\SYSTEM\CurrentControlSet\Services\btndrv\BtnDrvLog`)—— 选这里是因为加载驱动时它必然存在(第一次实现写 `HKLM\SOFTWARE\BtnDrv`,开机早期配置单元可能还没挂上,所以那次什么都没写成 ✗)。

## 命名事件为什么必须在系统线程里建(2026-09-20)

原因**不是**会话/命名空间。实测:`probe-namespace.ps1` 枚举对象命名空间,无论从谁的上下文创建,对象都落在 `\BaseNamedObjects`,用户态都按 `Global\BtnDrvEvent` 打开 ✓ —— 真正的问题是**安全描述符**:

- `ZwCreateEvent` 传 `SecurityDescriptor = NULL` 时,Windows 按**创建者令牌**生成默认描述符,而**属主是谁,对象派生的完整性标签(IL)就是谁**。如果某次请求恰好由**电源管理器(SYSTEM)**抢到"第一次创建",对象就带 **System** 标签;程序是 High 完整性,申请 `EVENT_MODIFY_STATE` 属于**"向上写"**,于是**永远返回"访问被拒绝"**,尽管名字解析完全正常 ✗。
- 这正是"时好时坏"的机理:开机后谁先碰这个设备决定属主。电源管理器轮询得早,程序的 poke 晚几秒 —— 所以 v1.2.0 能用是运气 ✓。
- 现在显式构造描述符:**Everyone 允许 `EVENT_ALL_ACCESS`**,并且**属主设为非 SYSTEM 的 SID**,派生标签不再是 System ✓。赋值属主需要 `SeRestorePrivilege`:系统线程带系统令牌所以有,任意调用者上下文不一定有 —— **这才是那个线程存在的真正理由** ✓。
- 副作用:**任何本地进程都能 signal 这个事件**(等于"假装按了一下电源键"→ 触发一次显示模式切换)。这是刻意的取舍:程序以 `asInvoker` 跑在用户会话里,必须打得开它;而这个动作用户用热键本来也能做,不构成提权 ✗。

线程生命周期:线程句柄**一直保持打开**;`BtnUnload` 先置 `g_EventThreadStop = 1`,再 `ZwWaitForSingleObject(句柄)` 等它真正退出,然后才释放内存 —— 否则线程会回到已卸载的代码里执行 ✗。创建用 `InterlockedCompareExchange` 保证只建一次,万一失败就在下一次请求里重试 ✓。

**部署文件名会轮换**:已加载的 filter 驱动的 `.sys` 被锁死,`sc stop` 报 1052 ✗,所以 `install-btndrv.ps1` 会在 `btndrv.sys` / `btndrv_alt.sys` 里挑一个没被加载的来写,再显式设置 `binpath`(对已存在的服务 `sc create` 会静默失败,从而留下旧路径 ✗,所以脚本会回读 `ImagePath` 确认)。服务名始终是 `btndrv`,`UpperFilters` 与程序都不受影响 ✓。也不要指望 `MoveFileEx(..., MOVEFILE_DELAY_UNTIL_REBOOT)`:重命名由 smss 执行,那时 ACPI 子设备已经枚举、旧驱动已经加载 ✗。

**`BtnDrvLog.EventStatus` 的取值**:`0x424E0003` = 系统线程创建成功;`0x424E0004` = 对象已存在(程序还握着句柄,驱动重载)所以改用 `ZwOpenEvent` 打开 ✓;其他值 = 失败的 NTSTATUS ✗。
