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

首次使用请点击托盘图标 → "设置…",在下拉框里选择你的便携屏(列表包含当前连接的和已断开的所有显示器),保存后程序会记住该显示器的适配器/目标 ID,以后开机后能自动重新识别到它(即使 GDI 设备号 `\\.\DISPLAYx` 发生变化)。也可以只填"名称匹配关键字"(默认是 `DP`),按显示器友好名称模糊匹配。

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
