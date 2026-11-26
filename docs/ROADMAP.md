# 升级路线图

> 基线：v1.0.0（`fc1d7d9`，2026-09-26 打包）。**以下全部未做。**
> 证据来源：2026-09-26 `kill.log` 复盘（`docs/KNOWN-ISSUES.md`、`killer-analysis/REPORT.md`、`killer-analysis/AUDIT.md`）+ 2026-09-28 本机实测复核。
> **2026-09-28 取舍决定**：不做「三档模式」；整类不做「D · 工程化与自动化」。下方清单已按此定稿为 18 条。
> 执行顺序：A → B → C。每项落地时在 `docs/VERIFY.md` 补一条可复现用例。

## A · 正确性与安全（P0，先做）

1. **keep.json 容错** — 现象：一处语法错误就会让 `user` 白名单**静默清空**（`LoadKeep` 的 `catch { }` 吞掉异常，且 `keepUser` 初始化成空 List 而非 null，`if (keepUser == null)` 的回退永不触发，见 `docs/KNOWN-ISSUES.md:5`）。做法：解析失败回退 `DefaultSystem + DefaultUser`，异常写进日志与界面，新增 `--check-config` 只校验不执行。落点：`LoadKeep`（`killall-back.cs:517`）。
2. **结果三分类** — 现象：日志里 `failed=12`，其中 11 条是「进程当前未运行」（计划生成后自己退了），却和真正的失败混报。做法：拆成「已杀 / 已自退出 / 真失败」三类，`revived:` 另计。落点：杀进程循环（`killall-back.cs:224-270`）+ 结果界面。
3. **单实例锁** — 现象：没有互斥锁，双击两次就有两个实例互相杀对方刚拉起的进程，这是「当前未运行」的来源之一。做法：全局命名 `Mutex`，第二个实例提示后退出。落点：`Main`（`killall-back.cs:153`）。
4. **杀进程树** — 现象：`Kill()` 只结束目标进程本身，vivo / MuMu 会残留 `adb`、`crashpad_handler`、`vivo_remote_service` 一堆子进程。做法：`taskkill /F /T` 或 Job Object 带走子进程（注意别连带已白名单的子进程）。落点：杀进程循环（`killall-back.cs:224-270`）。
5. **三段式补杀** — 现象：`vivocontrol` 之类「拒绝访问」只记一行日志，不解释也不补救。做法：`SeDebugPrivilege` → `taskkill /F /T` → WMI `Terminate`，失败原因分类（PPL 保护 / 拒绝访问 / 已退出）。落点：杀进程循环（`killall-back.cs:224-270`）。
6. **服务停止时序** — 现象：现在先杀进程后停服务，`services` 里的服务（如 Everything）可能在杀完后把进程重新拉活。做法：先停服务并等待 → 再杀进程 → 复查一轮。落点：`StopConfiguredServices`（`killall-back.cs:753`）与 `Main` 的调用顺序。
7. **退出码与帮助** — 现象：无退出码、无 `--help` / `--version`，参数拼错也没有任何提示。做法：约定 0=正常 / 1=有失败 / 2=参数错误；补 `--help`、`--version`；未知参数报错退出。落点：`Main`（`killall-back.cs:153`）。

## B · 网络自检

8. **测「真能上网」** — 现象：现在只做一次 DNS 解析，遇到悬空路由「能解析但打不开网页」时仍报正常。做法：加 HTTP 探测（`www.msftconnecttest.com/connecttest.txt`，正文断言 `Microsoft Connect Test`）或 TCP 443 握手，即 Windows NCSI 口径。落点：`RunNetworkCheck`（`killall-back.cs:699-749`）。
9. **多探测目标表决** — 现象：只测一个微软域名，国内被劫持 / 不可达时容易误判。做法：加 2–3 个国内目标（`baidu.com` 等）做多数表决；解析到 127.0.0.1 / 0.0.0.0 / 保留地址时单独标「疑似劫持」。落点：`RunNetworkCheck`。
10. **修复要验证** — 现象：`netsh` / `ipconfig` 的输出与退出码都没捕获，命令失败也照样显示「已修复」。做法：捕获 stdout / stderr 与退出码，修复后复测通过才打绿标，否则标红并附命令输出。落点：`RunNetworkCheck(doRepair: true)`。
11. **悬空默认路由** — 现象：TAP / Wintun 类虚拟网卡留下的 low metric 默认路由未覆盖，README 只给了「重开一次加速器」的规避办法。做法：识别后先备份当前路由表并提示，`delete route` 默认只提示不执行（有断网风险）。落点：`RunNetworkCheck`。
12. **补检测面** — 现象：WinHTTP 代理只在 DNS 失败时才重置；PAC 自动配置脚本（`AutoConfigURL`）完全没检测。做法：无条件检测 WinHTTP / WinINET 代理与 `AutoConfigURL`，命中失效代理再重置。落点：`RunNetworkCheck`。
13. **复测重试** — 现象：修复后只测一次，抓不住「刚修好还没生效」。做法：间隔 500 ms 复测 2–3 次退避，每次都写日志。落点：`RunNetworkCheck`。

## C · 界面与体验

14. **重开逻辑（明显 bug）** — 现象：按「路径」重开，同路径多进程会启动多个实例（日志里 pscuite 出现 7 个 → 会开 7 次）；还会把 `node` / `cmd` / `adb` / `crashpad_handler` 这类后台子进程无脑拉起来。做法：只重开「有窗口」的程序、按路径去重、从进程树根启动。落点：`Reopen`（`killall-back.cs:2041`）。
15. **主数字口径** — 现象：界面大字 `freed` 用的是计划内存累加值（2026-09-26 那次 2259.7 MB），与物理内存实测差（-1.98 GB）、`sessionWS` 差（-2.82 GB）三个数对不上，容易被当成虚报。做法：大字统一用真实采样 delta（`memBefore.used - memAfter.used`），累加值只留在明细列表并注明口径。落点：`TakeSnapshot`（`killall-back.cs:321`）+ 结果界面。
16. **自保护父链** — 现象：目前只排除自身 PID 与 baseDir 下的进程，启动 killer 的终端 / 父进程链（pwsh、Windows Terminal、DSH…）都可能被一起杀掉。做法：Toolhelp32 反查父链，默认整链保留，`--force` 才动。落点：新函数，接 `GetShellSession`（`killall-back.cs:299` 附近）。
17. **温和回收** — 现象：省内存不一定非要杀进程，现在只有「杀 / 不杀」两种结果。做法：`K32EmptyWorkingSet` 修剪工作集 + standby list 清理（+ 可选文件缓存裁剪），结束后给出「没有杀任何进程，也回收了 X MB」。落点：新模块，接 `TakeSnapshot`。
18. **白名单可视化** — 现象：加白名单要手改 `keep.json`，还得小心 JSON 语法（见 A-1）。做法：预演后在界面里勾选「这些不要杀」，一键写回 `keep.json`（写前备份、写后自动 `--check-config` 复验）。落点：结果界面 + `LoadKeep`。

## 已去掉（2026-09-28 决定）

- **三档模式**（`--level gentle|normal|deep`；gentle 只回收不杀、deep 加 `wsl --shutdown` / Docker / 更多服务、keep.json 增 `levels` 分组）—— 不做。
- **D · 工程化与自动化** —— 整类不做：
  - 版本资源 + 崩溃捕获（`/win32res` 嵌入版本、`AppDomain.UnhandledException` 落盘、顺手删 `BuildResultFormLegacy` 约 300 行死代码）
  - 单元测试（`Keep` / `LoadKeep` 纯函数先做）
  - 托盘常驻 + 热键（Win+Alt+K；内存 > 85% 且空闲 > 10 分钟且非全屏时自动 `--silent --level gentle`）
  - 开机基线（登录 60 秒存 `baseline.json`，之后显示「比刚开机多 N 个进程 / +X MB」，按浏览器 / 加速器 / 开发工具分类聚合）
  - 启动项审计（Run 键 / 启动文件夹 / 计划任务 / 非微软自启服务，只读报告 + 可选禁用，禁写 `.reg` 备份）
  - 报告导出（CSV 导出 + 历史释放曲线）

## 待定（本版未纳入执行清单）

以下两项出现在 2026-09-26 的旧版路线图里，但 2026-09-28 的取舍清单中没有出现，先记为待定，未计入上面的 18 条：

- **快照恢复**：记录窗口布局 / 标题 / 顺序 + `code -r` / Chrome 会话参数，重开按钮升级为「按快照恢复」。落点：`Reopen`（`killall-back.cs:2041`）。
- **自学习白名单**：`Reopen()` 落 `reopen:` 日志，`--suggest-keep` 统计反复重开的程序并给出 keep.json diff。落点：`Reopen` + 新子命令。
