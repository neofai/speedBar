# AGENTS.md

用中文回答。

## 项目概览

SpeedBar v2 是面向 Windows 11 x64 的轻量级任务栏网速/系统状态监控工具。主程序是 .NET 8 WPF 应用，托盘和颜色选择器使用 Windows Forms；没有外部 NuGet 依赖，没有解决方案文件。发布标签为 `v2`，程序集/文件版本为 `2.0.0.0`，产品版本为 `2.0.0`，输出名称保持 `SpeedBar.exe`。

窗口始终是贴靠主任务栏的独立、无激活 popup，既不是 Explorer 的 child，也不以 Explorer 为 owner。v2 已删除旧任务栏嵌入和 `TaskbarHitTarget` 路径；不要恢复每轮强制置顶或跨进程 `SetParent`。

## 源码结构

```text
.
├── App.xaml(.cs)                    单实例 Mutex、启动和最终释放
├── MainWindow.xaml(.cs)             指标显示、后台采样调度、设置和托盘
├── SettingsWindow.xaml(.cs)         设置编辑
├── Models/AppSettings.cs            容错加载、Normalize、复制、原子保存
├── Services/
│   ├── TaskbarController.cs         UI 线程显示状态、布局调度、Shell 恢复
│   ├── OverlayVisibilityPolicy.cs   阻止显示/800 ms 稳定恢复的纯逻辑
│   ├── FullscreenService.cs         Shell 状态、全屏几何和前台 owner 链
│   ├── ForegroundWindowWatcher.cs   全局前台事件、前台进程位置事件
│   ├── TaskbarService.cs            原生任务栏快照、DPI、放置及 Z 序
│   ├── TaskbarPlacement.cs          安全空位选择的纯逻辑
│   ├── TaskbarLayoutService.cs      单个后台 MTA 线程上的 UI Automation
│   ├── SystemMetricsService.cs      网络、CPU、内存采样与基线
│   └── StartupService.cs            当前用户开机启动注册表项
├── tests/
│   ├── SpeedBar.RegressionTests.csproj  无测试框架依赖的回归程序
│   ├── Program.cs
│   └── WindowsSmoke/
│       ├── SpeedBar.WindowsSmoke.csproj
│       ├── Program.cs               独立桌面上的原生窗口/控制器烟雾测试
│       └── smoke.manifest
├── Assets/                         图标资源
├── app.manifest                    PerMonitorV2 DPI 声明
├── build.ps1                       win-x64 自包含单文件发布
├── docs/v2-design.md                设计、参考出处和验证边界
└── README.md / README_EN.md         用户使用与构建说明
```

`bin/`、`obj/`、`dist*/`、`artifacts/`、`.runtime-packs/`、`SpeedBar-current/` 和 `SpeedBar-optimized/` 是构建、发布或本地运行时产物；测试目录里的 `bin/`、`obj/` 同样不是业务源码。不要手工编辑或提交生成的 C#、BAML、EXE 和 runtime pack。

## 运行流程与不变量

1. `App.OnStartup` 创建 `Local\SpeedBar.SingleInstance` Mutex；已有实例时退出。加载 `%LOCALAPPDATA%\SpeedBar\settings.json` 并创建托盘。
2. `MainWindow.Loaded` 创建 `TaskbarController`。它统一决定 Show/Hide；底层放置方法不得顺便使用 `SWP_SHOWWINDOW`。
3. 前台事件合并后经 Dispatcher 刷新，250 ms 计时器提供兜底。全屏、会话不可用、任务栏缺失/自动隐藏或手动暂停会阻止显示。恢复需连续安全至少 800 ms 和有效布局，时间判断使用单调时钟。
4. `FullscreenService` 结合 Shell 通知状态、物理像素几何、前台 owner 链以及此前全屏窗口。主屏任务栏是唯一显示目标；会话级 D3D 全屏/演示状态可能使副屏全屏也隐藏主屏显示。
5. 正常 UIA 扫描间隔 5 秒，缓存有效期最多 15 秒。每次传入固定 HWND 快照；generation、任务栏句柄和可见性状态校验后才接纳结果。扫描失败、缓存过期或无安全空位则隐藏；空位不足保留低频布局检查。
6. 全屏、会话不可用和任务栏不可用期间不发起新 UIA 扫描。服务仅使用一个后台 MTA worker、最多一个在途请求；COM 调用卡住时不得不断新建线程。Dispose 不 Join UI 线程。
7. 指标仅在显示有效时发起采样。采样和 Reset 都在后台串行运行，UI 不等待采样锁；失效结果通过采样 generation 丢弃。隐藏时停止新采样，已有系统调用允许完成。恢复首帧重建网络/CPU 基线。
8. 任务栏 HWND、显示器或 DPI 改变时失效布局。Explorer 缺失时隐藏，重建后重新定位；独立主窗口不因此销毁或重建。
9. 位置相同不重复 SetWindowPos；只在隐藏到显示转换时提升 Z 序，不逐轮与 Explorer 争抢置顶。保持 `WS_EX_NOACTIVATE`、`WS_EX_TOOLWINDOW` 和 `SWP_NOACTIVATE`。
10. 双击打开设置、右键打开托盘菜单。普通 Close 被转为手动暂停；最终退出走 `CloseApplication` / `ReleaseResources`，解除计时器、hook、系统事件并释放托盘、图标和 Mutex。

## 修改约定

- 保持 file-scoped namespace、nullable、implicit usings 和现有 WPF code-behind 风格；优先复用现有 service，不为单实现增加接口、工厂或依赖。
- 显示与生命周期改 `TaskbarController`；全屏判断改 `FullscreenService`；原生几何与放置改 `TaskbarService`；空位算法改 `TaskbarPlacement`；UIA 改 `TaskbarLayoutService`。
- WPF 控件只能在 UI 线程访问。原生 callback 只安排合并刷新；UIA/网卡调用不得阻塞 UI。
- 修改字体同时检查固定列宽和 Viewbox 适配，避免大字体指标裁切；保留同值文本不重复更新的行为。
- 配置继续兼容已有字段。新增设置检查默认值、Normalize、Clone、设置窗口和保存；无效数字/颜色不能让显示线程崩溃。
- 网络速度按每块网卡的前后累计字节与单调时间计算；网卡更换、计数回退、暂停恢复都需重建基线。CPU 的 kernel time 包含 idle。
- 所有显示入口都必须经过全屏/任务栏检查；托盘“重新贴靠”不能绕过游戏抑制或强行 Show。
- 保持始终可运行的独立 popup 路径。无法确认安全位置时隐藏显示并保留托盘，不回到旧嵌入方案。
- 参考 TrafficMonitor 只借鉴设计；当前实现独立重写、未复制其源码。新增外部代码前检查其许可证与引用要求。

## 构建与验证

在仓库根目录的 Windows PowerShell 中运行：

```powershell
dotnet build .\SpeedBar.csproj -c Release
dotnet run --project .\tests\SpeedBar.RegressionTests.csproj -c Release
dotnet run --project .\tests\WindowsSmoke\SpeedBar.WindowsSmoke.csproj -c Release
dotnet run --project .\SpeedBar.csproj -c Release
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

`build.ps1` 默认先执行回归，再发布 `SpeedBar-optimized\SpeedBar.exe`，并生成 `artifacts\release\SpeedBar.exe`、`SpeedBar-v2-win-x64.zip` 和 `SHA256SUMS.txt`。只有已明确完成等效检查时才使用 `-SkipTests`。`.github/workflows/build.yml` 在 push、PR 和手动触发时运行同一脚本并上传产物。回归与烟雾程序均不使用额外测试框架/NuGet 包，退出码非零表示失败。

WindowsSmoke 创建独立 Windows desktop，不调用 SwitchDesktop；fixture 和 worker 两个自建进程在其中使用模拟任务栏及真实 WPF/控制器/UIA。非输入桌面不能获取真实前台焦点，因此通过反射预置已观察到的全屏窗口，再验证真实几何与显示路径，未覆盖真实前台 WinEvent 投递。MainWindow 路径可能只读加载已有用户配置，但不保存设置、不修改注册表、不重启用户 Explorer；完成后只清理自建进程和桌面。纯回归程序不读写用户设置。独立桌面不可用时记录限制，不能宣称通过。

发布前分别记录 Release 构建、纯逻辑/采样回归、原生烟雾结果和人工验收结果。真实游戏帧时间、独占 D3D、真实 Explorer 重启、混合 DPI、多屏、任务栏自动隐藏、托盘交互及开机启动需实际 Windows 11 验证；编译或模拟窗口通过不能替代这些验收。具体矩阵见 `docs/v2-design.md`。
