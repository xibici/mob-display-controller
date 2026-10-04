# MobDisplayController

Windows 系统托盘小工具,用于管理一台"远程/便携显示器"(例如通过 DisplayPort / USB-C 接的便携屏,在"高级显示器设置"里能看到的那种)。

## 功能

1. **托盘 + 单实例**:启动后常驻系统托盘,重复启动会提示"已在运行"并退出。
2. **右键菜单断开/重新连接显示器**:通过 Windows 显示配置(CCD)API 操作,效果等同于"设置 → 系统 → 显示 → 高级显示设置"里的断开/连接,不需要物理拔插。
3. **开机自动启动**:托盘菜单里的复选框,写入当前用户的注册表 `Run` 项(无需管理员权限)。
4. **分辨率 / 亮度 / 音量调节**:
   - 分辨率通过标准 Win32 显示 API 设置。
   - 亮度、音量通过 **DDC/CI (VESA MCCS)** 协议调节,前提是该显示器 + 连接线 + 显卡驱动都支持 DDC/CI 透传。大多数 DisplayPort / HDMI / USB-C DP Alt Mode 便携屏支持;部分纯 USB(DisplayLink 芯片)便携屏不支持,此时对应菜单会自动置灰并提示。

## 如何识别"DP"显示器

一般不需要手动配置:如果系统里只接了一块外接屏,程序会自动认出它。识别到之后会记住它 EDID 里的厂商码/型号码(烧在显示器固件里),所以重启、换 GDI 设备号 `\\.\DISPLAYx` 都不影响。注意不能用适配器 LUID 来记——Windows 每次开机都会重新分配 LUID,用它记的话每次重启都会失效。

接了多块外接屏时,点击托盘图标 → "设置…" 在下拉框里手动选(列表只含实际接着显示器的接口,已断开的也在),或者填"名称匹配关键字"(默认是 `DP`)按友好名称模糊匹配。友好名称是显示器厂商写在 EDID 里的,不一定和接口一致——比如有的 DP 屏报的名字就叫 `HDMI`。

## 构建 / 运行

需要 .NET 8 SDK(已在本机验证:`dotnet 8.0.419`)。

```powershell
# 构建
dotnet build

# 直接运行(调试)
dotnet run --project src\MobDisplayController

# 发布为单文件可执行程序
dotnet publish src\MobDisplayController -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
# 产物位于 src\MobDisplayController\bin\Release\net8.0-windows\win-x64\publish\MobDisplayController.exe
```

## 项目结构

```
src/MobDisplayController/
  Program.cs              # 入口 + 单实例互斥锁
  TrayAppContext.cs        # 托盘图标 + 右键菜单逻辑
  TrayIcons.cs              # 运行时生成托盘图标(无需外部 .ico 资源)
  Native/NativeMethods.cs   # P/Invoke:CCD 显示配置 API、EnumDisplayDevices、DDC/CI (Dxva2)
  Services/DisplayService.cs        # 枚举显示器、断开/连接、设置分辨率
  Services/MonitorControlService.cs # DDC/CI 亮度/音量读写
  Services/StartupService.cs        # 开机自启(注册表 Run 项)
  Services/AppSettings.cs           # 设置持久化 (%AppData%\MobDisplayController\settings.json)
  UI/SettingsForm.cs        # 设置窗口:选择目标显示器、开机自启
  UI/VcpSliderForm.cs       # 亮度/音量自定义滑块弹窗
```

## 已知限制

- 断开/重新连接使用 Windows CCD API 直接翻转显示路径的 `ACTIVE` 标志,这是社区验证过的可靠做法,但极少数显卡驱动在"重新连接"时可能需要你手动去系统设置里点一次"检测"。
- DDC/CI 依赖硬件支持,如果亮度/音量菜单显示为灰色,说明该便携屏或连接方式不支持这两项。
- 目前面向单显示器场景优化(一台"DP"便携屏);如果你有多台同名显示器,请在设置里通过下拉框精确选择,程序会记住其唯一 ID 而不仅仅是名字。

## 切换时分辨率和缩放保持不变

`显示模式` 菜单、电源键、`Ctrl+Alt+Shift+L` 切换拓扑时,程序会在切换前记下每块屏的**分辨率+刷新率**和**缩放(DPI)**,切完把仍然亮着的那块屏放回去:复制(双屏)是什么,切到仅外屏后外屏就是什么。复制模式下旋转也会把外屏归零。

**分辨率"放回去"只在本次会话生效,绝不写注册表。** 这是有历史教训的:更早的版本用 `ChangeDisplaySettingsEx` + `CDS_UPDATEREGISTRY` 放回分辨率,它**会把分辨率写进注册表**,而复制模式下记下的是"旋转后的共享源"(宽度 1200),于是每次开机 Windows 都恢复成这个不存在的尺寸 ✗。现在 `dwFlags` 固定为 0(`Native/NativeMethods.cs` 里连 `CDS_UPDATEREGISTRY` 常量都没声明,防止误用),并且拒绝横竖方向不一致的还原。**不要把 `CDS_UPDATEREGISTRY` 加回来。**

- 复制模式本身不还原分辨率:复制的模式由两块屏都支持的最高刷新率决定(内屏不支持 120Hz,外屏单独时才能 120Hz),硬要求会被 Windows 悄悄改回去。以复制里的模式为准,切到仅外屏时外屏保持它。
- 缩放用有文档的 `GetDpiForMonitor` 读真实 DPI,再逐档试 CCD 的相对缩放档位直到命中,找不到就退回 Windows 原来选的档位。基线在每次切换前、显示设置变化时、启动时刷新。
- 历史遗留的坏配置在 `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration` 下,把 `PrimSurfSize.cx` 为 1200 的键删掉即可(Windows 会自己重建),删之前先导出备份。日志里 `switch: 分辨率还原…` / `switch: 缩放还原…` / `switch: Windows moved the mode …` 会告诉你每次切换做了什么。
