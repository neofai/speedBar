# SpeedBar v2

[English](README_EN.md) | 简体中文

SpeedBar 是面向 Windows 11 的轻量级网速与系统状态监控工具，在主屏任务栏的空位显示上传、下载、CPU 和内存。v2 重构了窗口显示、全屏避让和后台采样；窗口始终是独立、无激活的 popup，不依赖 Explorer 的父子或 owner 关系。

## 功能与显示行为

- 上传、下载速度，以及 CPU、内存使用率。
- 自定义各项颜色、透明背景、字号和刷新间隔。
- 自动避让任务栏按钮和通知区，可选靠左或靠右；系统任务栏左对齐时强制靠右。
- 检测到全屏、锁屏、会话断开、休眠或任务栏自动收起时隐藏显示，暂停新的指标采样；全屏及任务栏不可用期间不再发起 UI Automation 布局扫描。
- 通过前台窗口事件及时检查状态，并以 250 ms 计时器兜底。退出抑制状态后，至少连续安全 800 ms 且取得有效布局，才恢复显示。
- 正常布局扫描间隔为 5 秒；缓存超过 15 秒、扫描失败或没有安全空位时保持隐藏，托盘菜单仍可用。空位不足时保留低频布局检查，以便空位出现后恢复。
- Explorer 重启时保留同一个主窗口，等待任务栏重建后重新定位；不会循环创建主窗口或反复抢占置顶。
- 托盘“暂停显示（游戏模式）”适合窗口化游戏；取消暂停或选择“重新贴靠任务栏”可重新评估显示条件。
- 单实例、当前用户开机启动；已有配置字段继续兼容。

## 安装与使用

系统要求：Windows 11 x64。自包含发布版无需另行安装 .NET。

1. 从 [v2 发布页](https://github.com/neofai/speedBar/releases/tag/v2) 下载 `SpeedBar.exe`。
2. 退出旧实例，将新文件放到固定目录后运行。版本号为 `2.0.0`，文件名继续使用 `SpeedBar.exe`。
3. 双击任务栏显示打开设置，右键打开菜单；显示隐藏时，可通过系统托盘打开设置或退出。
4. 对窗口化游戏，可先在托盘菜单勾选“暂停显示（游戏模式）”。
5. 如需开机启动，在设置中启用“登录 Windows 后自动启动”。

设置文件位于 `%LOCALAPPDATA%\SpeedBar\settings.json`。开机启动项写入当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。

## 支持范围与限制

v2 仅在主任务栏（`Shell_TrayWnd`）显示，不在每个显示器创建一份窗口。全屏几何判断围绕该任务栏所在屏幕；Windows 报告的独占 D3D 全屏、演示等会话级状态会保守隐藏显示，因此副屏游戏也可能使主屏 SpeedBar 隐藏。

窗口化游戏不能仅凭“正在运行游戏”自动识别，请使用手动暂停。检测依赖 Windows 事件、Shell 状态和窗口几何，250 ms 是轮询周期，并非所有情况下的响应上限。后台已经开始的系统调用不会被强行中断，其过期结果会被丢弃。

布局服务失败时保留托盘并隐藏显示，避免使用未知空位压住任务栏按钮。这里没有保证所有游戏都不受影响；真实游戏帧时间、多 DPI、自动隐藏和 Explorer 恢复仍需在目标 Windows 环境验收。

## 从源码构建与验证

需要 Windows 和 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)，应用及回归测试不依赖额外 NuGet 包。

```powershell
dotnet build .\SpeedBar.csproj -c Release
dotnet run --project .\tests\SpeedBar.RegressionTests.csproj -c Release
dotnet run --project .\tests\WindowsSmoke\SpeedBar.WindowsSmoke.csproj -c Release
dotnet run --project .\SpeedBar.csproj -c Release
```

回归程序覆盖布局空位、全屏几何、恢复延迟、配置兼容及指标边界。原生窗口烟雾测试和验收范围见 [v2 设计与验证说明](docs/v2-design.md)。

发布自包含单文件版：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

脚本默认先执行回归测试，再发布。单文件输出为 `SpeedBar-optimized\SpeedBar.exe`；`artifacts\release\` 中同时生成 `SpeedBar.exe`、`SpeedBar-v2-win-x64.zip` 和 `SHA256SUMS.txt`。GitHub Actions 在 push、PR 和手动触发时运行同一脚本并上传构建产物。

## 设计参考与许可证

v2 研究了 [TrafficMonitor 固定提交 930f175](https://github.com/zhongyang219/TrafficMonitor/tree/930f17533d6098989ebad62f210aa97d75ef174b) 的全屏避让、布局变更检测和任务栏恢复思路。SpeedBar 的 WPF 实现为独立重写，没有复制该项目代码；具体参考位置和差异见 [设计说明](docs/v2-design.md)。

SpeedBar 采用 [Apache License 2.0](LICENSE)；参考项目的 [Anti 996 许可证](https://github.com/zhongyang219/TrafficMonitor/blob/930f17533d6098989ebad62f210aa97d75ef174b/LICENSE) 不随本项目的许可证改变。
