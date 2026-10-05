# Windows 原生烟测

在 Windows 上执行：

```powershell
dotnet run --project .\tests\WindowsSmoke\SpeedBar.WindowsSmoke.csproj -c Release
```

测试创建一个独立桌面，在其中启动 fixture 与 worker 两个独立进程。整个过程不调用 `SwitchDesktop`，不修改用户配置或注册表，不操作用户的 Explorer。测试结束后关闭所有自建进程与桌面；JSON 证据留在输出中指定的临时目录。

fixture 提供真实的 `Shell_TrayWnd`、原生 Button 和普通/最大化/无边框全屏窗口。worker 通过 ProjectReference 使用产品的 `MainWindow`、`TaskbarController`、`FullscreenService`、指标采样和实际 UI Automation。读取配置后仅在测试进程内替换为默认设置，不保存。检查：

- 初始显示、实际 taskbar Button 扫描、普通最大化不误判。
- 无边框全屏隐藏实际 WPF HWND，并清除 TOPMOST。
- 解除全屏后至少等待 800 ms；手动暂停不会被全屏退出取消。
- 全屏与暂停期间各连续 5.5 秒不创建新的布局扫描，也不触发显示激活；真实 MainWindow 指标计时器停止，采样状态、generation 和 CPU baseline 保持不变。
- 销毁并重建模拟任务栏后，主窗口 HWND 存活，重新获取布局后恢复显示。
- 通过实际 `CloseApplication` 关闭路径停止计时器、解除 WinEvent hooks、退出 UIA worker并销毁 HWND。

隔离桌面没有成为输入桌面，不能获得真实前台激活。因此测试在普通、最大化、全屏窗口判断前，都反射预置“曾观察到的全屏窗口”的 HWND/PID/monitor 三个字段，确保实际执行原生几何判断；之后的窗口几何、可见性、UIA 和控制器判断均走产品代码。报告的 `ForegroundAvailable` 与 `Limitation` 会保留这项边界。此测试不能证明前台 WinEvent 投递、真实 Explorer 重启、独占 Direct3D 游戏或多 DPI 交互已经验收。
