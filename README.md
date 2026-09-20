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

## 切换显示模式不会动分辨率

**程序不会自动设置分辨率。** `显示模式` 菜单(以及电源键、`Ctrl+Alt+Shift+L`)只切换拓扑,分辨率交给 Windows 自己决定;唯一会设置分辨率的地方是 `分辨率` 子菜单,那是你手动点的。

之前不是这样:切换前会先记下当前分辨率,切完再"放回去",以免复制模式把桌面掉到两块屏共享的分辨率上。但这个"放回去"走的是 `ChangeDisplaySettingsEx` + `CDS_UPDATEREGISTRY` —— **它会把分辨率写进注册表**,而复制模式下记下的那个分辨率是"旋转后的共享源"(宽度 1200),于是每次开机 Windows 都恢复成这个不存在的尺寸 ✗。

现在已经把这个自动设置删掉 ✓。如果你机器上也留下过这种坏配置,可以在 `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration` 下删除 `PrimSurfSize.cx` 为 1200 的那几个键(Windows 会自己重建),删之前先导出备份。日志里 `switch: mode untouched at ...` / `switch: Windows moved the mode ...` 会告诉你切换时 Windows 有没有动分辨率 —— 只读,不改 ✗。
