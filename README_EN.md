# SpeedBar

English | [简体中文](README.md)

SpeedBar is a lightweight network and system monitor for the Windows 11 taskbar. Its main display is embedded into the Explorer taskbar without injecting a DLL into Explorer. If embedding fails, SpeedBar falls back to a non-activating overlay aligned with the taskbar.

## Features

- Real-time upload and download speeds
- CPU and memory usage
- Custom colors for upload, download, CPU, memory, and background
- Optional transparent background and adjustable font size
- 500 ms, 1 second, or 2 second refresh intervals
- Automatic avoidance of taskbar buttons and notification-area icons
- Left-side or right-side docking
- Automatic re-embedding after Explorer or the taskbar restarts
- Tray menu, launch at sign-in, and single-instance operation

## Requirements

- Windows 11 x64
- No .NET installation is required for the self-contained release

> SpeedBar uses a Win32 child-window technique because Windows 11 does not provide a public API for arbitrary custom taskbar controls. Major Windows updates may require compatibility adjustments.

## Install and use

1. Download `SpeedBar.exe` from [Releases](https://github.com/neofai/speedBar/releases).
2. Place it in a permanent folder and run it.
3. Double-click SpeedBar in the taskbar to open Settings, or right-click it for the menu.
4. Enable “Start with Windows” in Settings if desired.

Settings are stored in `%LOCALAPPDATA%\SpeedBar\settings.json`. Enabling launch at sign-in writes to the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registry key.

## Build from source

Windows 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) are required.

```powershell
dotnet build .\SpeedBar.csproj -c Release
dotnet run --project .\SpeedBar.csproj -c Release
```

To publish a self-contained single-file build:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The output is `SpeedBar-optimized\SpeedBar.exe`.

## License

Licensed under the [Apache License 2.0](LICENSE).
