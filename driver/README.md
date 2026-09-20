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
- 该驱动是**诊断+功能**两用的最小实现:无文件 I/O、无线程、无自旋外分配(除 DriverEntry 的一次非分页池分配)。
- 日志写在**驱动自己的服务键**下(`HKLM\SYSTEM\CurrentControlSet\Services\btndrv\BtnDrvLog`)—— 选这里是因为加载驱动时它必然存在(第一次实现写 `HKLM\SOFTWARE\BtnDrv`,开机早期配置单元可能还没挂上,所以那次什么都没写成 ✗)。
