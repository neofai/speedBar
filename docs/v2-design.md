# SpeedBar v2 设计与验证

发布标签：`v2`。产品版本：`2.0.0`。程序集和文件版本：`2.0.0.0`。发布文件：`SpeedBar.exe`。

## 问题与重构范围

旧实现同时由刷新计时器、布局回调和窗口恢复代码控制显示。全屏隐藏后仍可能发起 UIA 扫描和指标更新；全屏检测的瞬时空档又可能触发置顶与 SHOWWINDOW。以 Explorer 为 owner 也把窗口生命周期和 Z 序与 Shell 绑定。

v2 将可见性统一交给 `TaskbarController`，把检测、布局、原生定位和指标采样分开。任务栏显示始终是独立 popup，没有 Explorer owner/parent，不注入 DLL。旧嵌入方法和 `TaskbarHitTarget` 已移除；任务栏消失时保持原主窗口隐藏，等待重建，而不是创建替代主窗口。

这次改动针对可观察到的显示和工作调度问题；不能仅凭代码审查或编译结果认定真实游戏卡顿已经消失。

## 显示闭环

1. 前台激活事件触发合并刷新；位置事件只订阅前台进程，另外每 250 ms 做一次兜底检查。
2. 读取主任务栏快照、会话状态与全屏状态。阻止显示时立即隐藏、取消显示窗口的置顶并停止新指标采样；不发起新 UIA 扫描。
3. 解除阻止后需连续安全至少 800 ms。还必须取得未过期布局和足够安全空位，才允许恢复。
4. 正常情况下每 5 秒扫描任务栏按钮，布局缓存最长使用 15 秒。任务栏 HWND/几何/DPI 改变时缓存失效。扫描失败或缓存过期时隐藏，保留托盘；空位不足时也隐藏，但继续低频布局检测以发现空位。
5. UIA 完成只能更新缓存并排队重评估，不能直接 Show。generation、HWND 和显示状态防止旧结果重新显示窗口。
6. 位置没变不调用 SetWindowPos；放置函数不含 SHOWWINDOW。仅从隐藏切回显示时提升 Z 序，不逐次“修复”被 Shell 降级的窗口。

250 ms 是计时器间隔，不是绝对响应延迟保证。Dispatcher 繁忙、系统事件延迟及已有原生调用会影响时序；手动暂停是窗口化游戏的明确控制入口。

## 检测范围与多屏行为

`FullscreenService` 综合使用 Shell 的用户通知状态和窗口几何：检查前台窗口、有限层数的 owner 链，并跟踪最近的全屏 HWND/进程。排除桌面宿主、最小化/隐藏窗口、cloaked 窗口和本进程；区分普通带边框最大化窗口与真正覆盖显示器的窗口。几何判断使用一致的物理像素坐标并允许很小的边缘误差。

仅主任务栏 `Shell_TrayWnd` 是显示目标，当前不支持在每块屏幕创建窗口或选择副任务栏。几何判断针对主任务栏所在屏幕；独占 D3D 全屏、演示模式等 Shell 状态属于整个会话，副屏全屏也可能使主屏 SpeedBar 保守隐藏。

锁屏、会话断开、休眠与任务栏自动收起会阻止显示。窗口化游戏没有按进程名自动分类，使用托盘“暂停显示（游戏模式）”；选择“重新贴靠任务栏”会解除手动暂停，但仍需通过自动显示条件。

## 工作线程与采样

- `TaskbarLayoutService` 仅有一个后台 MTA 线程和一个在途请求槽。CacheRequest 批量获取 ProcessId、IsOffscreen、BoundingRectangle，避免逐属性跨进程读取。COM 卡住时不新增 worker、不无限排队；Dispose 不在 UI 线程 Join。
- 指标采样由 MainWindow 的单个在途任务执行。网络枚举、采样和 Reset 都放在后台；UI 不获取指标采样锁。隐藏后不启动下一次采样，旧结果按 generation 丢弃。
- 已开始的 UIA 或网卡调用允许自然结束，不能把“暂停新工作”描述成强制终止系统调用。
- 网卡列表有刷新间隔，各网卡分别保留累计字节与单调时间基线；计数回退、网卡变化和暂停恢复重建基线。CPU 首次采样也建立基线，避免恢复时展示整个暂停期间的平均值。
- 文本只有变化时才写入控件。固定指标列随字号调整，Viewbox 适配实际任务栏高度，降低数字变化导致的窗口抖动。

## 原生窗口与恢复

`TaskbarService` 只处理快照、DPI、安全位置和原生操作；`TaskbarPlacement` 是可独立验证的空位算法。不能在找不到空位时退回一个可能覆盖按钮的位置。

主窗口保留 `WS_EX_TOOLWINDOW` 和 `WS_EX_NOACTIVATE`；定位使用 `SWP_NOACTIVATE`，鼠标激活消息返回 MA_NOACTIVATE。PerMonitorV2 由 manifest 声明。系统任务栏左对齐时，即使用户选左侧，显示也改用右侧空位。

`TaskbarCreated`、显示器/DPI/系统偏好变化使布局失效。Explorer 消失不会作为独立窗口的 owner/parent 销毁主窗口；等待任务栏快照和新布局后恢复即可。最终退出释放计时器、hook、系统事件、托盘和图标；普通关闭转为暂停显示。

## TrafficMonitor 参考记录

研究基准为 [TrafficMonitor 提交 930f17533d6098989ebad62f210aa97d75ef174b](https://github.com/zhongyang219/TrafficMonitor/tree/930f17533d6098989ebad62f210aa97d75ef174b)，不依赖其可变的 master 内容。

| 参考点 | 固定源码链接 | v2 的采用方式 |
|---|---|---|
| 全屏隐藏与隐藏时停止置顶 | [TrafficMonitorDlg.cpp 257–274](https://github.com/zhongyang219/TrafficMonitor/blob/930f17533d6098989ebad62f210aa97d75ef174b/TrafficMonitor/TrafficMonitorDlg.cpp#L257-L274)、[1674–1696](https://github.com/zhongyang219/TrafficMonitor/blob/930f17533d6098989ebad62f210aa97d75ef174b/TrafficMonitor/TrafficMonitorDlg.cpp#L1674-L1696) | 集中可见性控制，增加恢复稳定期和工作暂停 |
| 只在布局变化时移动 | [Win11TaskbarDlg.cpp 5–85](https://github.com/zhongyang219/TrafficMonitor/blob/930f17533d6098989ebad62f210aa97d75ef174b/TrafficMonitor/Win11TaskbarDlg.cpp#L5-L85) | 比较实际位置，避免重复写 HWND |
| Explorer 重建通知 | [TrafficMonitorDlg.cpp 2593–2610](https://github.com/zhongyang219/TrafficMonitor/blob/930f17533d6098989ebad62f210aa97d75ef174b/TrafficMonitor/TrafficMonitorDlg.cpp#L2593-L2610) | TaskbarCreated 失效布局并恢复 |
| 原生任务栏嵌入 | [TaskBarDlg.cpp 1010–1017](https://github.com/zhongyang219/TrafficMonitor/blob/930f17533d6098989ebad62f210aa97d75ef174b/TrafficMonitor/TaskBarDlg.cpp#L1010-L1017) | 保留架构差异：WPF 使用独立 popup，不移植 SetParent 实现 |

SpeedBar 独立编写了这些 WPF/C# 实现，没有复制 TrafficMonitor 源码。TrafficMonitor 的 [Anti 996 v1 Draft 许可证](https://github.com/zhongyang219/TrafficMonitor/blob/930f17533d6098989ebad62f210aa97d75ef174b/LICENSE) 与 SpeedBar 的 [Apache-2.0](../LICENSE) 分别适用各自项目。

补充 API 依据：[SHQueryUserNotificationState](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate) 没有全屏开始/结束通知，因此保留轮询兜底；[UIA 缓存](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/caching-in-ui-automation-clients) 支持批量读取属性；[UIA 线程说明](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-threading-issues) 说明独立线程调用的重要性。

## 构建与自动化验证

在仓库根目录的 Windows PowerShell 执行：

```powershell
dotnet build .\SpeedBar.csproj -c Release
dotnet run --project .\tests\SpeedBar.RegressionTests.csproj -c Release
dotnet run --project .\tests\WindowsSmoke\SpeedBar.WindowsSmoke.csproj -c Release
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

两个测试程序均不依赖第三方测试框架或 NuGet 包。回归测试覆盖空位选择、负坐标/分数边界、全屏几何、稳定恢复时序、配置兼容及网络/CPU 边界；不会读写用户的 settings.json。`build.ps1` 默认先运行回归，再发布 `SpeedBar-optimized\SpeedBar.exe`，同时生成 `artifacts\release\` 中的 EXE、`SpeedBar-v2-win-x64.zip` 和 `SHA256SUMS.txt`。GitHub Actions 的 push、PR 和手动构建运行同一脚本；WindowsSmoke 的独立桌面检查需要另行执行，不应把 CI 构建通过当作该项已通过。

WindowsSmoke 使用 CreateDesktop 创建独立桌面，不调用 SwitchDesktop。自建 fixture 提供模拟 Shell_TrayWnd、按钮及普通/最大化/无边框窗口；另一个 worker 运行真实 WPF 窗口、TaskbarController 和 UIA，验证显示转换、暂停、扫描抑制及释放。因为非输入桌面不能获得真实前台焦点，测试通过反射预置 FullscreenService 已观察到的全屏 HWND、PID 和显示器，随后执行真实几何、可见性和控制器路径。MainWindow 路径可能只读加载已有用户配置，但不保存设置、不修改注册表、不重启用户 Explorer。测试完成后只清理自己创建的进程和桌面，证据目录打印在终端。

这个 smoke 环境不是实际 Explorer，也不是 D3D 游戏；未验证真实前台 WinEvent 投递及用户桌面的托盘交互，不能替代相应的人工验收。MainWindow 指标调度等具体覆盖项应以实际报告为准。

## 验证状态与人工验收

2026-10-05 在 Windows 11（build 26200）、.NET SDK 8.0.423 上执行。以下结果与人工验收分开记录。

| 检查 | 状态 |
|---|---|
| Release 构建 | 通过，0 警告、0 错误 |
| 无依赖回归程序 | 68 项通过、0 失败 |
| 独立桌面 WindowsSmoke | 29 项通过；真实 MainWindow/指标/UIA/原生 HWND，预置已观察到的全屏状态 |
| 自包含 SpeedBar.exe 发布及版本检查 | 通过；win-x64 单文件，FileVersion 2.0.0.0，生成 ZIP 与 SHA-256 校验文件 |
| 真实游戏及 Windows 桌面交互 | 待目标环境验收 |

WindowsSmoke 验证了全屏和手动暂停各连续 5.5 秒不发起新 UIA/指标工作、恢复延迟、模拟任务栏销毁重建时保留主窗口 HWND，以及实际退出资源释放。当前锁屏桌面另行启动实际应用，Shell 返回 NOT_PRESENT，窗口保持隐藏、非置顶且无 Explorer owner，进程正常响应。没有解锁桌面或重启用户 Explorer，因此不能把这些结果作为真实前台事件、独占游戏或帧时间验收。

人工验收应记录 Windows 版本、显示器/DPI、游戏显示模式以及前后结果：

- 主屏无边框与独占全屏、F11 视频、Alt+Tab、游戏弹窗和游戏内 overlay；观察隐藏和稳定恢复，比较实际帧时间而不只看平均 FPS。
- 副屏全屏后切换焦点，核对保守隐藏；窗口化游戏手动暂停/恢复。
- 系统任务栏居中/左对齐、两侧停靠、空位不足和新增按钮；确认不覆盖按钮、时钟和通知区。
- 任务栏自动隐藏、锁屏/解锁、休眠恢复、真实 Explorer 重启；观察恢复是否重新采样而不使用过期缓存。
- 主屏切换、负坐标副屏、100%/150%/200% 混合 DPI、字体 10–20；确认尺寸、点击范围和指标无裁切。
- 设置保存、旧配置/损坏配置、无网络/网卡断开、托盘设置与退出、单实例及开机启动。
