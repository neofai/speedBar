# SpeedBar

[English](README_EN.md) | 简体中文

SpeedBar 是一款面向 Windows 11 的轻量级任务栏网速与系统状态监控工具。它使用贴靠任务栏的无激活悬浮窗显示数据，不向 Explorer 注入 DLL，避免透明窗口嵌入任务栏后出现空白。

## 功能

- 实时显示上传、下载速度
- 显示 CPU 和内存使用率
- 上传、下载、CPU、内存和背景颜色设置
- 可选透明背景和字体大小
- 500 ms、1 秒或 2 秒刷新间隔
- 自动避让任务栏按钮和通知区图标
- 可选择靠左或靠右停靠
- Explorer/任务栏重建后自动恢复显示和贴靠
- 托盘菜单、开机启动和单实例运行

## 系统要求

- Windows 11 x64
- 使用自包含版本时无需安装 .NET

> SpeedBar 保持显示窗口独立，并跟随任务栏位置和按钮布局；不会在启动后切换成 Explorer 的透明子窗口。

## 安装与使用

1. 从 [Releases](https://github.com/neofai/speedBar/releases) 下载 `SpeedBar.exe`。
2. 将文件放到固定目录后直接运行。
3. 双击任务栏中的 SpeedBar 打开设置，右键打开菜单。
4. 如需开机启动，请在设置中启用“登录 Windows 后自动启动”。

设置保存在 `%LOCALAPPDATA%\SpeedBar\settings.json`。启用开机启动时，应用会写入当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 注册表项。

## 从源码构建

需要 Windows 11 和 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
dotnet build .\SpeedBar.csproj -c Release
dotnet run --project .\SpeedBar.csproj -c Release
```

生成无需安装 .NET 的自包含单文件版：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

输出文件为 `SpeedBar-optimized\SpeedBar.exe`。

## 许可证

本项目采用 [Apache License 2.0](LICENSE) 许可。
