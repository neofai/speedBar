# SpeedBar v2

English | [简体中文](README.md)

SpeedBar is a lightweight network and system monitor for Windows 11. It shows upload, download, CPU, and memory usage in a free area of the primary taskbar. v2 restructures visibility, fullscreen suppression, and background sampling. The display is always an independent, non-activating popup, with no Explorer parent or owner.

## Features and visibility

- Upload and download rates, CPU usage, and memory usage.
- Custom colors, transparent background, font size, and refresh interval.
- Avoids taskbar buttons and the notification area; supports left or right docking. A left-aligned system taskbar forces right docking.
- Hides during fullscreen, session lock/disconnection, suspend, or taskbar auto-hide, and stops starting new metric samples. Fullscreen and unavailable-taskbar states also stop new UI Automation scans.
- Uses foreground events with a 250 ms polling fallback. Restoration requires at least 800 ms of uninterrupted safe state and a valid layout.
- Scans the normal taskbar layout every 5 seconds. Failed scans, cache older than 15 seconds, or insufficient space keep the display hidden while the tray remains available. Low-frequency layout checks continue when space is insufficient so the display can recover.
- Keeps the same main window when Explorer restarts, then locates the rebuilt taskbar. It does not repeatedly recreate the window or force its topmost state.
- Includes a tray pause command for windowed games. Unpause or select the reattach command to reevaluate visibility.
- Single-instance operation, launch at sign-in, and compatibility with existing settings fields.

## Install and use

Requires Windows 11 x64. The self-contained release does not require a separate .NET installation.

1. Download `SpeedBar.exe` from the [v2 release](https://github.com/neofai/speedBar/releases/tag/v2).
2. Exit the old instance, place the executable in a permanent folder, and run it. The product version is `2.0.0` (assembly/file version `2.0.0.0`); the executable remains `SpeedBar.exe`.
3. Double-click the display to open Settings, or right-click for its menu. Settings and Exit remain accessible through the system tray while the display is hidden.
4. For windowed games, enable the tray command “暂停显示（游戏模式）” (Pause display / game mode).
5. Enable launch at sign-in in Settings if desired.

Settings are stored in `%LOCALAPPDATA%\SpeedBar\settings.json`. Launch at sign-in uses the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` key.

## Scope and limitations

v2 targets the primary taskbar (`Shell_TrayWnd`) and does not create one display per monitor. Geometry-based fullscreen checks use the taskbar's monitor. Session-wide Shell states, including exclusive D3D fullscreen and presentation mode, conservatively suppress the display; a game on another monitor may therefore hide SpeedBar on the primary monitor.

Windowed games are not automatically classified by process name; use manual pause. Detection depends on Windows events, Shell state, and window geometry. The 250 ms polling interval is not a guaranteed response-time bound. System calls already in progress are allowed to finish, and obsolete results are discarded.

When layout information is unavailable, SpeedBar keeps its tray icon and hides the display instead of placing it over an unknown taskbar area. Actual game frame times, mixed DPI, auto-hide, and Explorer recovery still require acceptance testing on the target Windows system.

## Build and verify

Requires Windows and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). The application and regression runner have no additional NuGet dependencies.

```powershell
dotnet build .\SpeedBar.csproj -c Release
dotnet run --project .\tests\SpeedBar.RegressionTests.csproj -c Release
dotnet run --project .\tests\WindowsSmoke\SpeedBar.WindowsSmoke.csproj -c Release
dotnet run --project .\SpeedBar.csproj -c Release
```

The regression runner checks placement, fullscreen geometry, restore delay, settings compatibility, and metric edge cases. See the [v2 design and validation notes](docs/v2-design.md) for native-window smoke tests and acceptance scope.

Publish a self-contained single-file executable:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The script runs regression tests before publishing. The single-file output is `SpeedBar-optimized\SpeedBar.exe`. It also creates `SpeedBar.exe`, `SpeedBar-v2-win-x64.zip`, and `SHA256SUMS.txt` in `artifacts\release\`. GitHub Actions runs the same script on pushes, pull requests, and manual dispatch, then uploads the build artifacts.

## Design reference and license

v2 studied fullscreen suppression, change-based layout, and taskbar recovery in [TrafficMonitor at commit 930f175](https://github.com/zhongyang219/TrafficMonitor/tree/930f17533d6098989ebad62f210aa97d75ef174b). The WPF implementation is an independent rewrite; no TrafficMonitor source code was copied. See the [design notes](docs/v2-design.md) for exact references and differences.

SpeedBar is licensed under [Apache License 2.0](LICENSE). TrafficMonitor's separate [Anti 996 license](https://github.com/zhongyang219/TrafficMonitor/blob/930f17533d6098989ebad62f210aa97d75ef174b/LICENSE) remains its own.
