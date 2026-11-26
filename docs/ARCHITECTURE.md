# killer 架构说明（v1.1.7-dev）

> 本文按当前源码函数名维护，不依赖固定行号。完整版本记录见 `CHANGELOG.md`。

## 运行形态与安全边界

- 便携 WinForms 程序，目标为 Windows 自带的 .NET Framework 4.x；`build.ps1` 使用 Framework `csc.exe` 编译，无 NuGet 依赖。
- exe 清单要求管理员权限。**禁止运行不带 `--dry-run` 的正常清理模式**；它会结束当前交互会话中所有未列入白名单的进程。
- 只允许安全检查：`--version`、`--check-config --silent`、`--scan-only --silent`、`--dry-run --silent`、`--dry-run --screenshot <path>`，以及只使用合成数据的 `--instant-preview` / `--instant-preview-film`。截图、编辑器验收从交互用户会话提权运行。
- `keep.json` 是本地用户配置，`keep.example.json` 记录默认值。实际配置及其历史备份由 Git 忽略。
- 日志与计划文件都写在 exe 同目录：`kill.log`、`killer-plan.json`、`killer-keep-exclude.txt`。

## Main 控制流

1. 解析参数并建立 `baseDir`，纯信息/配置/白名单编辑器/预览/扫描模式按需提前返回。
2. 读取 `keep.json`，缓存祖先进程 PID 保护列表；复用同一份进程快照采集内存和扫描当前登录会话。显存采样独立执行。
3. `--dry-run` 并行运行只读网络检测，再写出候选计划和诊断结果。正常清理路径先显示准备窗口，显存采样在后台完成。
4. 清理路径停止配置的服务，按首轮并行处理、失败分批 `taskkill`、Win32 终止重试、分批复查、温和 Trim 的顺序推进。每个候选单独核验进程身份和协调租约。
5. 收尾时执行只读网络检测和末次内存采样。结果界面可先显示，检测完成后原处更新。网络设置修复由结果页的确认操作单独发起。
6. 写入 `PhaseTrace` 阶段时间和 `killcost:` 进程耗时，再显示结果或保存截图。

## 主要性能实现

| 标记 | 实现 | 边界 |
|---|---|---|
| A0 | `PhaseTrace` 记录主要阶段；`AddKillCost` 记录单进程耗时 | 仅提供观测，不改变执行结果 |
| A1 | 首轮最多 12 个 worker 并行；worker 在操作前复核身份，统计写入使用锁 | 身份不匹配的 PID 跳过 |
| A2 | `TaskKillBatch` 每批最多 16 个已核验的 PID，异步读取两路输出并逐项确认 | 持有目标句柄防止 PID 复用，子进程按独立候选纳入清理 |
| A3 | 提前启用调试权限，区分 `ERROR_NOT_ALL_ASSIGNED` 与调用失败 | 权限不足时记录原因，不假报成功 |
| A4/A5 | Trim 去掉逐进程固定等待；驻留复查走轻量进程枚举 | 复查仍有限于计划内、身份一致的目标 |
| A6 | 保护与认领检查共享同一份父子快照，本次运行缓存已认领的目标身份 | 新的认领只覆盖将操作的 PID 和创建时间，在共享互斥锁下按剩余预算仲裁 |
| A7 | `RunNetworkCheck` 后台运行，有界并行探测、首个成功即收敛，失败时有限重试 | 汇总等待约 2.5 秒上限 |
| A8 | `SnapshotSampler` 约 3 秒采样一次，启动时并行获取显存读数 | 收尾采集物理内存和会话工作集，显存结果显示实际采样的列 |
| A9 | `UiLiveState` 增量更新行；进度回调节流，`UiPump` 单次最多处理 25ms | 不在 UI 线程堆积重复 Sleep |
| A10 | 候选列表按工作集从大到小排列 | 只影响展示顺序 |
| A11 | 清理流程预算 3 秒；耗尽后跳过后续非必要阶段并记录 `degraded=1` | 只对真实清理标记降级，预演不显示该标记 |

## 主要函数与数据

- 启动/配置：`Main`、`VersionText`、`LoadKeep`、`WriteConfigLog`、`ConfigSummary`。
- 白名单页面：清理完成后由主窗口导航打开，复用当前会话的进程快照。行内添加默认用完整 exe 路径，详情可显式选择同名或目录；保存后可撤销。
- 候选与身份：`FindCandidates`、`ExportPlanFiles`、`MatchKeepRule`、`SameProcess`、`StillPlannedProcess`、`CheckBeforeKill`。
- 结束与耗时：Pass 1 worker、`RunTaskKillBatches`、`TaskKillBatch`、`RequestTermination`、`StopConfiguredServices`、`TrimWorkingSets`、`PhaseTrace`、`AddKillCost`。
- 网络与快照：`RunNetworkCheck`、`OnlineProbe`、`WithProcessSnapshot`、`TakeSnapshot`、`SnapshotSampler`。
- 日志/UI：`WriteLog`、`AppendLogBlock`、`TrimLogFile`、`BuildUiShell`、`RunUi`、`SaveUiScreenshot`、`Reopen`。
- 关键数据：`ProcInfo`（PID、进程名、路径、工作集、创建时间）、`MemorySnapshot`、`NetworkReport`、`ServiceReport`、`UiLiveState`。
- 白名单语义：历史字符串规则沿用进程名全等或路径子串匹配。新文件规则以 `exe=` 表示完整路径，新目录规则以 `dir=` 表示目录及其子目录。扫描与规则页面共享匹配器。

## 界面与设计尺寸

- `src/ui.cs` 中由 `Theme`、`ThemedControl`、`HeroCard`、`MemTable`、`ProcList`、`FlatButton`、`CardPanel` 与 `DpiAwareForm` 组成。主流程复用同一 `BuildUiShell`，清理完成后原地填充结果。
- 固定浅色主题：页面底 `#EEF3FD`、白卡、发丝线 `#D8E2F7`、正文 `#16233D`、主按钮 `#002FA7`、首卡克莱因蓝渐变和白色进度环。
- 设计尺寸 1180×900；验收模式可通过 `--ui-width` / `--ui-height` 突破常规最小窗体尺寸。布局断点：`<1000` 合并前后列；`<900` 缩小清单行距；`<820` 隐藏路径列；`<760` 页脚多行；`<560` 首卡与按钮堆叠；`<=390` 紧凑数值排版。
- `Theme.GetFont` 缓存自绘字体；`--instant-preview`、`--instant-preview-film` 使用合成数据，不枚举系统进程、不写运行日志。
- DPI 和界面验证流程见 [验证说明](VERIFY.md)。

## 参数速查

`--dry-run`、`--silent`、`--screenshot <path>`、`--check-config`、`--keep-editor`、`--scan-only`、`--plan-out <path>`、`--keep-exclude <path>`、`--install-shortcut`、`--version`、`--help`、`--force`、`--no-trim`。界面验收参数为 `--auto-reopen`、`--ui-width N`、`--ui-height N`、`--instant-preview`、`--instant-preview-film`。`--auto-reopen` 在真实结果窗会执行重开；与 `--dry-run --screenshot` 一起使用时只展示忙碌态，不启动程序。

## 延伸阅读

- `README.md`：最终用户行为、参数、白名单与界面规范。
- `VERIFY.md`：安全构建、离线回归、预演与截图验收步骤。
- `KNOWN-ISSUES.md`：当前限制与尚未解决的问题。
- `CHANGELOG.md`：版本级实施和验证记录。
