# AGENTS.md

## 项目概览

SpeedBar 是一个面向 Windows 11 的轻量级任务栏网速/系统状态监控工具。它是单项目 WPF 应用，目标框架为 `.NET 8`，使用 Win32 API 将主窗口嵌入 `Explorer` 任务栏；嵌入失败时回退为贴靠任务栏的无激活悬浮窗。

- 项目文件：`SpeedBar.csproj`
- 输出类型：`WinExe`
- 目标框架：`net8.0-windows`
- UI：WPF；托盘图标和颜色选择器使用 Windows Forms
- 外部 NuGet 依赖：无
- 构建平台：Windows；代码依赖 `user32.dll`、`kernel32.dll`、`shell32.dll`、Windows UI Automation
- 当前没有测试项目或解决方案文件

## 源码结构

```text
.
├── App.xaml(.cs)                 应用入口、单实例 Mutex、生命周期
├── MainWindow.xaml(.cs)          主监控窗口、刷新、停靠、设置和托盘协调
├── SettingsWindow.xaml(.cs)      设置对话框和设置值编辑
├── Models/
│   └── AppSettings.cs             配置模型、JSON 读写
├── Services/
│   ├── SystemMetricsService.cs    网络、CPU、内存采样
│   ├── TaskbarService.cs          任务栏 HWND 嵌入、定位、回退悬浮窗、DPI 转换
│   ├── TaskbarLayoutService.cs    UI Automation 扫描任务栏已占用区域
│   ├── TaskbarHitTarget.cs         原生透明命中窗口和鼠标消息转发
│   └── StartupService.cs           当前用户开机启动注册表项
├── Assets/                        图标资源
├── build.ps1                      自包含单体 EXE 发布
└── README.md                      用户构建和配置说明
```

`bin/`、`obj/`、`dist*/`、`artifacts/` 和 `.runtime-packs/` 是构建/发布产物或本地 runtime pack，不是业务源码。修改功能时只改源码、XAML、项目文件或构建脚本；不要手工编辑这些目录中的文件。

## 运行流程

1. `App.OnStartup` 创建 `Local\\SpeedBar.SingleInstance` Mutex；已有实例时直接退出。
2. `MainWindow` 加载 `%LOCALAPPDATA%\\SpeedBar\\settings.json`，应用颜色、字体、刷新间隔并创建托盘图标。
3. `Loaded` 时调用 `TaskbarService.PrepareWindow`，然后尝试把窗口重设为任务栏的 child HWND。
4. 成功嵌入后，`TaskbarLayoutService` 通过 UI Automation 查找 Explorer 任务栏按钮，`TaskbarService` 按设置选择左侧或右侧安全空位并处理 DPI 转换；系统任务栏靠左对齐时强制 SpeedBar 靠右。
5. 嵌入失败时，窗口改为无激活、置顶、拥有任务栏的 overlay，并贴靠通知区。
6. `DispatcherTimer` 定期调用 `SystemMetricsService.Sample()`，更新上传、下载、CPU 和内存文本；非嵌入状态每 5 秒重试嵌入，任务栏布局最多每 500 ms 扫描一次。
7. 窗口不可拖动；双击打开设置，右键显示托盘菜单。
8. 退出时必须走 `CloseApplication`，释放命中窗口、托盘图标、系统事件订阅和单实例 Mutex。普通窗口关闭会被拦截并隐藏。

## 常见修改位置

- 修改主显示内容或布局：`MainWindow.xaml`、`MainWindow.xaml.cs`
- 修改设置项：同时检查 `AppSettings`、`SettingsWindow.xaml`、`SettingsWindow.xaml.cs` 和 `MainWindow.ApplySettings`
- 修改任务栏位置/嵌入行为：优先改 `TaskbarService`；布局识别改 `TaskbarLayoutService`；鼠标命中改 `TaskbarHitTarget`；停靠方向还要检查 `AppSettings` 和设置窗口
- 修改指标计算：`SystemMetricsService`。网络速度是相邻采样的累计字节差，CPU 使用 `GetSystemTimes`，内存使用 `GlobalMemoryStatusEx`
- 修改开机启动：`StartupService`。写入当前用户 `HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run`
- 修改图标或应用资源：`Assets/` 和 `SpeedBar.csproj` 中的资源声明

## 开发约定

- 保持现有的 file-scoped namespace、nullable reference types、implicit usings 和 WPF code-behind 风格。
- 优先复用现有 service；不要为单一实现添加接口、工厂、配置层或新依赖。
- WPF 控件和窗口状态只能在 UI 线程访问；后台扫描完成后通过 Dispatcher 回到 UI 线程。
- 修改 Win32 代码时同时检查 HWND 样式、父子关系、窗口坐标、DPI 和 `SWP_NOACTIVATE`，不能只验证普通桌面窗口路径。
- 保持“任务栏嵌入失败可运行”的 overlay 回退路径。
- 对配置文件解析保持容错，并兼容已有 `%LOCALAPPDATA%\\SpeedBar\\settings.json` 字段；新增设置要补齐默认值、复制逻辑和保存逻辑。
- 修改系统事件、计时器、托盘图标或原生窗口时，确保在关闭路径解除订阅/释放资源。
- 不要把生成的 `.g.cs`、BAML、EXE、runtime pack 或发布目录提交/编辑为源码。

## 构建与验证

在 Windows PowerShell 中运行：

```powershell
# 快速编译
 dotnet build .\SpeedBar.csproj -c Release

# 本地运行
 dotnet run --project .\SpeedBar.csproj -c Release

# 发布无需安装运行时的单体 EXE
 .\build.ps1
```

项目脚本的发布文件位于 `SpeedBar-optimized\\SpeedBar.exe`。当前没有自动化测试；涉及任务栏嵌入、Explorer 重启、多 DPI、停靠方向、托盘和开机启动的修改，至少应在实际 Windows 11 环境手工验证。非 UI 的纯逻辑修改可先用 `dotnet build -c Release --no-restore` 做最小回归检查。

## 变更检查清单

- 是否只修改了实现该功能所需的最少文件？
- 是否同时覆盖了靠左/靠右、系统任务栏左对齐强制靠右、嵌入失败回退、Explorer/任务栏重建和窗口关闭路径？
- 是否检查了设置加载失败、无网络适配器、无任务栏窗口和 DPI 缩放等边界情况？
- 是否执行了 Release 构建，并记录实际未验证的 Windows UI 行为？
