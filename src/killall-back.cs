using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;


namespace KillAllBack
{
    public class KeepConfig
    {
        public List<string> system { get; set; }
        public List<string> user { get; set; }
        public List<string> services { get; set; }
        public List<string> graceful { get; set; }
        public bool? networkRepair { get; set; }
    }

    internal enum NetworkCheckState { NotRun, Pending, Completed, Incomplete }

    public class NetworkReport
    {
        internal NetworkCheckState State;
        internal bool Repairable;
        public bool DnsBefore;
        public bool DnsAfter;
        // DNS 通了不等于能上网：运营商会劫持解析结果，加速器退出后还会留下悬空
        // 默认路由。主判据放在 HTTP / TCP 探测上（Windows NCSI 口径）。
        public bool OnlineBefore;
        public bool OnlineAfter;
        public string OnlineDetail = "";
        public string ProxyState = "未启用";
        public string PacState = "未设置";
        public string RouteState = "";
        public List<string> Actions = new List<string>();
        public List<string> Notes = new List<string>();
    }

    // Result of the configured service stop pass (Everything and friends),
    // reported in the log, the result window and the reopen button.
    public sealed class ServiceFailure
    {
        public readonly string Name;
        public readonly string Reason;
        public readonly bool Skipped;

        public ServiceFailure(string name, string reason, bool skipped = false)
        {
            Name = name;
            Reason = reason;
            Skipped = skipped;
        }

        public override string ToString() { return Name + " (" + Reason + ")"; }
    }

    public class ServiceReport
    {
        public List<string> Planned = new List<string>();
        public List<string> Stopped = new List<string>();
        public List<ServiceFailure> Failed = new List<ServiceFailure>();
        public List<string> NotRunning = new List<string>();
    }

    // Physical memory + interactive-session working set, sampled before and after.
    internal sealed class GpuSample
    {
        internal double UsedMB;
        internal double TotalMB;
        internal long ElapsedMs;
    }

    public class MemorySnapshot
    {
        public double TotalMB;
        public double UsedMB;
        public double AvailMB;
        public double LoadPercent;
        public double CommitMB;
        public double CommitLimitMB;
        public double SessionWsMB;
        public double GpuUsedMB;
        public double GpuTotalMB;
        public DateTime At = DateTime.Now;

        internal void ApplyGpu(GpuSample sample)
        {
            GpuUsedMB = sample.UsedMB;
            GpuTotalMB = sample.TotalMB;
        }

        internal MemorySnapshot WithGpu(GpuSample sample)
        {
            var snapshot = (MemorySnapshot)MemberwiseClone();
            snapshot.ApplyGpu(sample);
            return snapshot;
        }
    }

    public class ProcInfo
    {
        public int Pid { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public double WsMb { get; set; }
        public bool HasWindow { get; set; }
        public string Aumid { get; set; }
        public string FailReason { get; set; }
        public string Note { get; set; }       // 记录本次使用的结束方式。
        // 计划生成时的进程身份（PID 复用预检用，docs/KNOWN-ISSUES.md）：
        // 计划与动手之间隔了 900ms 甚至更久，pid 完全可能已经被回收给新进程。
        public long StartTicks { get; set; }   // 0 = 取不到（受保护进程 / 已退出）
        internal int ParentPid;
        internal TargetIdentity RelaunchSource;
        internal long ExitUtcTicks;
        internal int ExitObservedRemaining;
        internal bool ClaimedForCleanup;
        internal bool ExitPending;
        internal string InstanceKey { get { return Pid + ":" + StartTicks; } }

        // --scan-only 的自检字段：这条进程是被哪条白名单规则留下的（null = 会被结束）
        public string KeepRuleMatch { get; set; }
    }

    public static partial class Program
    {
        private sealed class PhaseTrace
        {
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private readonly Dictionary<string, long> marks = new Dictionary<string, long>();

            public void Mark(string name)
            {
                if (!marks.ContainsKey(name)) { marks[name] = clock.ElapsedMilliseconds; }
            }

            public void RecordDuration(string name, long milliseconds)
            {
                marks[name] = milliseconds;
            }

            public void AddDuration(string name, long milliseconds)
            {
                long previous;
                marks.TryGetValue(name, out previous);
                marks[name] = previous + milliseconds;
            }

            public string ToLogString()
            {
                string[] order = new string[] { "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8", "t9", "t10",
                    "prepare_start", "memory_ready", "gpu_ms", "review_scan_done", "review_identity_ms", "review_taskkill_ms",
                    "probe_wait_ms", "claim_ms", "exit_confirm_ms" };
                List<string> values = new List<string>();
                foreach (string key in order)
                {
                    long value;
                    if (marks.TryGetValue(key, out value)) { values.Add(key + "=" + value); }
                }
                return string.Join(",", values.ToArray());
            }
        }

        private static bool suppressAnimation = false;   // screenshots capture the settled state
        private static bool autoReopen = false;          // internal test hook (--auto-reopen)
        private static bool previewAutoReopen = false;   // dry-run screenshot state; never starts programs
        private static int uiWidthOverride = 0;      // --ui-width  (design units; screenshot only)
        private static int uiHeightOverride = 0;     // --ui-height (design units; screenshot only)

        private const string AppName = "killer (KillAllBack)";

        private static string resolvedVersion = null;

        internal static string VersionText()
        {
            if (resolvedVersion != null) { return resolvedVersion; }
            var version = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                typeof(Program).Assembly, typeof(AssemblyInformationalVersionAttribute));
            resolvedVersion = version != null ? version.InformationalVersion : "dev";
            return resolvedVersion;
        }

        // 从更新日志里取版本号：第一个 "## vX" 标题里的 X。取不到返回 null。
        // 纯函数（只依赖传入的路径），离线单测直接喂临时文件（bench\keeptest.cs）。
        public static string ParseVersionFromChangelog(string changelogPath)
        {
            try
            {
                if (string.IsNullOrEmpty(changelogPath) || !File.Exists(changelogPath)) { return null; }
                foreach (string line in File.ReadAllLines(changelogPath, Encoding.UTF8))
                {
                    if (!line.StartsWith("## ", StringComparison.Ordinal)) { continue; }
                    Match m = Regex.Match(line, @"^##\s+v?([0-9][0-9A-Za-z.\-]*)");
                    if (m.Success && m.Groups[1].Value.Length > 0) { return m.Groups[1].Value; }
                }
            }
            catch { }
            return null;
        }

        // A configuration error stops execution before planning or cleanup.
        private static string keepConfigError = null;

        private static bool consoleAttached = false;
        // 启动本工具的终端 / 宿主进程（父链）默认不参与清理，--force 关闭这道保护。
        private static bool forceUnprotected = false;
        private static List<int> protectedPids = new List<int>();
        private static bool skipTrim = false;
        private static double trimFreedMb = 0;
        private static int trimCount = 0;

        // Lazy so the banner can carry the runtime version (CHANGELOG-derived) instead of
        // the compiled constant — see VersionText().
        private static string usageTextCache = null;
        private static string UsageText()
        {
            if (usageTextCache != null) { return usageTextCache; }
            usageTextCache = string.Join(Environment.NewLine, new string[]
        {
            AppName + " " + VersionText() + " — 结束所有非刚开机状态的后台进程",
            "",
            "用法: killer.exe [选项]",
            "",
            "  --dry-run              预演：只列出将被结束的进程与将停止的服务，不动任何进程",
            "  --silent               不显示结果窗口（配合 --dry-run 适合自动化）",
            "  --screenshot <path>    将预演或指定的安全预览渲染成 PNG",
            "  --check-config         只校验 keep.json 并退出（解析失败返回 2）",
            "  --scan-only            只扫描并写出计划 / 白名单命中来源，不碰任何进程",
            "  --plan-out <路径>      配合 --scan-only：计划 JSON 写到哪里（默认 killer-plan.json）",
            "  --keep-exclude <路径>  配合 --scan-only：白名单命中清单写到哪里（默认 killer-keep-exclude.txt）",
            "  --keep-editor          打开白名单编辑器：查看 / 增删 keep.json 的四组白名单",
            "  --keep-picker-preview  预览当前会话进程选择器（只读；可配合 --screenshot）",
            "  --no-trim              不修剪本轮仍运行的已认领候选进程的工作集",
            "  --install-shortcut     在桌面创建 killer 快捷方式",
            "  --force                连启动本工具的终端 / 宿主父链一起结束（默认保护它们）",
            "  --help                 显示本帮助",
            "  --version              显示版本",
            "",
            "退出码: 0=正常完成  1=有失败  2=参数或配置错误",
            "",
            "配置: keep.json / kill.log 都在 exe 同目录；白名单改 keep.json 的 user 数组，改完即生效，无需重新编译。"
        });
            return usageTextCache;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetApplicationUserModelId(IntPtr hProcess, ref int AppModelIdLength, StringBuilder AppModelId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void SetLastError(uint dwErrCode);

        [StructLayout(LayoutKind.Sequential)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        private static string baseDir;
        private static string keepPath;
        private static string logPath;
        private static List<string> keepSystem = new List<string>();
        private static List<string> keepUser = new List<string>();
        private static List<string> stopServiceNames = new List<string>();
        private static List<string> gracefulNames = new List<string>();
        private static bool networkRepair = true;

        private static readonly string[] DefaultSystem = new string[]
        {
            "csrss", "wininit", "winlogon", "services", "lsass", "smss", "svchost", "fontdrvhost",
            "dwm", "explorer", "sihost", "ctfmon", "TextInputHost", "ShellExperienceHost",
            "StartMenuExperienceHost", "SearchHost", "ShellHost", "RuntimeBroker",
            "ApplicationFrameHost", "taskhostw", "DllHost", "smartscreen", "WmiPrvSE", "audiodg",
            "Registry", "Memory Compression", "Secure System", "System", "Idle",
            "SecurityHealthSystray", "SecurityHealthService"
        };

        private static readonly string[] DefaultUser = new string[]
        {
            "ghelper", "RtkAudUService64", "TranslucentTB", "CrossDeviceResume", "Pogget",
            "nvcontainer", "NVDisplay.Container", "NVIDIA Overlay", "nvsphelper64",
            "nvfvsdksvc_x64", "nvrla", "FvContainer"
        };

        private static readonly string[] DefaultServices = new string[] { "Everything" };
        private static readonly string[] DefaultGraceful = new string[] { "Kuajiasu", "KuaijiasuCore", "KuaijiasuHelperService" };

        [STAThread]
        public static int Main(string[] args)
        {
            PhaseTrace phases = new PhaseTrace();
            phases.Mark("t0");
            Stopwatch runBudget = Stopwatch.StartNew();
            const int totalBudgetMs = 3000;
            int budgetDegradedFlag = 0;
            Func<int> budgetRemaining = delegate
            {
                return Math.Max(0, totalBudgetMs - (int)runBudget.ElapsedMilliseconds);
            };
            bool dryRun = false;
            bool silent = false;
            bool install = false;
            bool checkConfig = false;
            bool keepEditor = false;
            bool keepPickerPreview = false;
            bool showHelp = false;
            bool showVersion = false;
            bool scanOnly = false;
            bool instantPreview = false;
            bool instantFilm = false;
            string planOutPath = null;
            string keepExcludePath = null;
            string unknownArg = null;
            string screenshotPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                string raw = args[i];
                string a = raw.TrimStart('-', '/').ToLowerInvariant();
                if (a.Length == 0) { continue; }
                if (a == "dry-run" || a == "dryrun") { dryRun = true; }
                else if (a == "scan-only" || a == "scanonly") { scanOnly = true; }
                else if ((a == "plan-out" || a == "planout") && i + 1 < args.Length) { planOutPath = args[++i]; scanOnly = true; }
                else if ((a == "keep-exclude" || a == "keepexclude") && i + 1 < args.Length) { keepExcludePath = args[++i]; scanOnly = true; }
                else if (a == "silent") { silent = true; }
                else if (a == "install-shortcut" || a == "install") { install = true; }
                else if (a == "screenshot" && i + 1 < args.Length &&
                    !args[i + 1].StartsWith("-", StringComparison.Ordinal) &&
                    !args[i + 1].StartsWith("/", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    screenshotPath = args[++i];
                }
                else if (a == "instant-preview" || a == "instantpreview") { instantPreview = true; }
                else if (a == "instant-preview-film" || a == "instantfilm")
                {
                    instantFilm = true;
                    // Keep the established direct-output form while also accepting
                    // --screenshot <path> for scripted preview commands.
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal)
                        && !args[i + 1].StartsWith("/", StringComparison.Ordinal))
                    {
                        screenshotPath = args[++i];
                    }
                }
                else if (a == "check-config" || a == "checkconfig") { checkConfig = true; }
                else if (a == "keep-editor" || a == "keepeditor") { keepEditor = true; }
                else if (a == "keep-picker-preview" || a == "keeppickerpreview") { keepPickerPreview = true; }
                else if (a == "help" || a == "h" || a == "?") { showHelp = true; }
                else if (a == "version" || a == "v") { showVersion = true; }
                else if (a == "force") { forceUnprotected = true; }
                else if (a == "no-trim" || a == "notrim") { skipTrim = true; }
                else if (a == "auto-reopen") { autoReopen = true; }
                else if (a == "ui-width" || a == "ui-height")
                {
                    int dimension;
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out dimension) || dimension <= 0)
                    {
                        unknownArg = raw;
                    }
                    else
                    {
                        i++;
                        if (a == "ui-width") uiWidthOverride = dimension;
                        else uiHeightOverride = dimension;
                    }
                }
                else { unknownArg = raw; }
            }

            if ((screenshotPath != null || uiWidthOverride > 0 || uiHeightOverride > 0) && !install && !checkConfig && !scanOnly &&
                !keepEditor && !keepPickerPreview && !instantPreview && !instantFilm)
                dryRun = true;

            baseDir = Path.GetDirectoryName(Application.ExecutablePath);
            keepPath = Path.Combine(baseDir, "keep.json");
            logPath = Path.Combine(baseDir, "kill.log");

            // Pure-information modes never touch the system, so they run before the
            // resource coordination and before any whitelist reading.
            if (showHelp) { WriteConsole(UsageText()); return 0; }
            if (showVersion) { WriteConsole(AppName + " " + VersionText()); return 0; }
            if (unknownArg != null)
            {
                string bad = "未知参数：" + unknownArg;
                WriteConsole(bad);
                WriteConsole("");
                WriteConsole(UsageText());
                try
                {
                    AppendLogBlock("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | mode=bad-args | elevated="
                        + IsElevated() + " | elapsed=0s ===" + Environment.NewLine
                        + "error: 未知参数 " + unknownArg + Environment.NewLine
                        + "args : " + string.Join(" ", args) + Environment.NewLine
                        + Environment.NewLine);
                }
                catch { }
                if (!silent)
                {
                    MessageBox.Show(bad + "\r\n\r\n" + UsageText(), "killer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return 2;
            }

            Theme.Apply();                          // 雾蓝白与克莱因蓝：单一浅色主题

            // Declare system DPI awareness before any window exists, so the UI is
            // rendered natively at the monitor DPI instead of being bitmap-stretched.
            try { SetProcessDPIAware(); }
            catch { }

            if(!workerInvocation)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if(!dryRun&&!silent&&!install&&!checkConfig&&!keepEditor&&!keepPickerPreview&&!showHelp&&!showVersion&&!scanOnly&&!instantPreview&&!instantFilm&&screenshotPath==null)
                    return RunInteractive(delegate{return Main(args);});
            }

            // 截图从只读白名单页完成，首次启动时也不生成配置。
            if (keepPickerPreview)
            {
                if (screenshotPath == null)
                    screenshotPath = Path.Combine(baseDir, "preview\\evidence\\keep-process-picker.png");
                return ShowKeepPage(screenshotPath);
            }
            if (keepEditor && screenshotPath != null) return ShowKeepPage(screenshotPath);

            LoadKeep();
            if (checkConfig)
                return ReportConfig("check-config", !silent);

            if (keepEditor) return ShowKeepPage(screenshotPath);

            // --instant-preview-film：进度条丝滑度的量化口径（脚本化，不碰进程）。
            if (instantFilm)
            {
                RunInstantPreviewFilm(screenshotPath != null
                    ? screenshotPath
                    : Path.Combine(baseDir, "preview\\instant-film.png"));
                return 0;
            }

            // --instant-preview：三段界面状态的零风险出图（不碰进程、不写日志）。
            if (instantPreview)
            {
                RunInstantPreview(screenshotPath != null
                    ? screenshotPath
                    : Path.Combine(baseDir, "preview\\instant-preview.png"));
                return 0;
            }

            if (keepConfigError != null && (!install || scanOnly || dryRun))
            {
                string modeWithInvalidConfig = scanOnly ? "scan-only" : dryRun ? "dry-run" : "run";
                return ReportConfig(modeWithInvalidConfig, !silent && interactive == null && screenshotPath == null);
            }

            // 从终端或编辑器启动时保留启动进程的父链。
            protectedPids = forceUnprotected ? new List<int>() : AncestorPids();

            // --scan-only：只扫一遍把"计划 / 白名单命中来源"落盘，不碰任何进程、
            // 可独立并行运行，用来逐条比对白名单匹配结果。
            if (scanOnly)
            {
                try
                {
                    List<ProcInfo> found = FindCandidates();
                    string planFile, keepFile;
                    int kept = ExportPlanFiles(found, planOutPath, keepExcludePath, out planFile, out keepFile);
                    if (!silent)
                    {
                        WriteConsole("scan-only: 计划 " + found.Count + " 个进程，白名单/排除命中 " + kept + " 个");
                        WriteConsole("plan : " + planFile);
                        WriteConsole("keep : " + keepFile);
                    }
                }
                catch (Exception ex)
                {
                    WriteConsole("scan-only 失败：" + ex.Message);
                    return 2;
                }
                return 0;
            }

            if(!dryRun&&!install)
            {
                try{coordinator=new RunCoordinator(Path.GetFullPath(Application.ExecutablePath),runId);}
                catch(Exception ex){coordinationError=ex.Message;throw new InvalidOperationException("无法建立实例协调，未执行清理。",ex);}
            }

            if (install)
            {
                string result;
                try { result = InstallShortcut(); }
                catch (Exception ex) { result = "创建快捷方式失败：" + ex.Message; }
                if (!silent)
                {
                    MessageBox.Show(result, "killer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }

            bool doCleanup = !install && !dryRun;
            DateTime startedAt = DateTime.Now;
            MemorySnapshot memBefore = null;
            MemorySnapshot memAfter = null;
            UiLiveState live = null;          // 非空 = 清理中窗口已经开在屏幕上
            UiShell uiShell = null;
            List<ProcInfo> remaining = new List<ProcInfo>();

            List<ProcInfo> plan = new List<ProcInfo>();
            List<ProcInfo> killed = new List<ProcInfo>();
            List<ProcInfo> failed = new List<ProcInfo>();
            List<ProcInfo> selfExited = new List<ProcInfo>();
            ServiceReport services = new ServiceReport();
            NetworkReport net = new NetworkReport();
            double freedMb = 0;
            List<string> killCosts = new List<string>();

            if (dryRun || doCleanup)
            {
                phases.Mark("prepare_start");
                Task<GpuSample> gpuProbe = null;
                Task<NetworkReport> networkProbe = null;
                plan = WithProcessSnapshot(delegate(Process[] processes)
                {
                    // 先取得候选快照，让本次启动的诊断子进程不进入计划。
                    gpuProbe = StartDiagnostic(ReadGpuMemory);
                    if (dryRun) { networkProbe = StartDiagnostic(() => ReadNetworkReport(false)); }
                    int session = GetShellSession();
                    memBefore = TakeSnapshot(processes, session);
                    phases.Mark("memory_ready");
                    return FindCandidates(processes, session, true, true);
                });
                phases.Mark("t1");
                if (dryRun) { services.Planned = GetRunningConfiguredServices(); }
                phases.Mark("t2");
                if (dryRun)
                {
                    var wait = Stopwatch.StartNew();
                    GpuSample sample = gpuProbe.Result;
                    memBefore.ApplyGpu(sample);
                    net = networkProbe.Result;
                    phases.RecordDuration("gpu_ms", sample.ElapsedMs);
                    phases.RecordDuration("probe_wait_ms", wait.ElapsedMilliseconds);
                    phases.Mark("t8");
                }

                if (doCleanup)
                {
                    if(interactive!=null)
                    {
                        live=interactive.Live;uiShell=interactive.Shell;
                        interactive.Bind(killed,failed,selfExited,plan,services,net,memBefore,gpuProbe);
                        phases.Mark("t9");
                    }

                    try
                    {
                        if (gpuProbe.Status == TaskStatus.RanToCompletion) { memBefore = memBefore.WithGpu(gpuProbe.Result); }
                        using (var execution = new CleanupExecution(budgetRemaining, live, killed, failed, selfExited, killCosts, phases))
                        {
                            remaining = execution.Run(plan, services);
                        }
                        freedMb = killed.Sum(process => process.WsMb);

                        if (!skipTrim && budgetRemaining() > 0)
                        {
                            LivePhase(live, "温和回收：修剪仍在运行的候选进程工作集…");
                            trimFreedMb = TrimWorkingSets(remaining.Where(process =>
                                process.ClaimedForCleanup && !process.ExitPending), budgetRemaining);
                        }
                        if (budgetRemaining() == 0 || failed.Any(IsBudgetFailure) || services.Failed.Any(service => service.Skipped))
                        {
                            budgetDegradedFlag = 1;
                        }
                        phases.Mark("t7");

                        if (networkRepair)
                        {
                            networkProbe = StartDiagnostic(() => ReadNetworkReport(false));
                            var wait = Stopwatch.StartNew();
                            int waitMs = interactive == null ? Math.Min(2500, budgetRemaining()) : 0;
                            if (networkProbe.Wait(waitMs)) net = networkProbe.Result;
                            else net.State = interactive == null ? NetworkCheckState.Incomplete : NetworkCheckState.Pending;
                            phases.RecordDuration("probe_wait_ms", wait.ElapsedMilliseconds);
                        }
                        phases.Mark("t8");
                    }
                    finally
                    {
                        // 停止进度采样，在复查和修剪都结束后读取一次最终内存快照。
                        if (live != null && live.Sampler != null)
                        {
                            live.Sampler.Stop();
                        }
                        memAfter = TakeSnapshot();
                        if (gpuProbe.Status == TaskStatus.RanToCompletion)
                        {
                            phases.RecordDuration("gpu_ms", gpuProbe.Result.ElapsedMs);
                        }
                        uiHeartbeat = null;
                        if (live != null && uiShell != null)
                        {
                            live.Net = net;
                            live.MemBefore = memBefore;
                            live.MemAfter = memAfter;
                            live.FreedMb = freedMb;
                            live.Elapsed = Math.Round((DateTime.Now - startedAt).TotalSeconds, 1);
                            live.TrimCount = trimCount;
                            live.TrimFreedMb = trimFreedMb;
                            live.Degraded = System.Threading.Interlocked.CompareExchange(ref budgetDegradedFlag, 0, 0) != 0;
                            try
                            {
                                if (interactive != null)
                                {
                                    InteractiveSession session = interactive;
                                    UiShell shell = uiShell;
                                    session.Post(shell.FillResult);
                                    if (net.State == NetworkCheckState.Pending)
                                        networkProbe.ContinueWith(task =>
                                        {
                                            if (!shell.Form.IsDisposed)
                                                session.Post(() => shell.UpdateNetwork(task.Result));
                                        }, TaskScheduler.Default);
                                }
                                else uiShell.FillResult();
                                phases.Mark("t10");
                            }
                            catch { }
                        }
                    }
                }
            }

            double elapsed = Math.Round((DateTime.Now - startedAt).TotalSeconds, 1);
            string mode = dryRun ? "dry-run" : (doCleanup ? "run" : "install-shortcut");
            if (doCleanup && runBudget.ElapsedMilliseconds >= totalBudgetMs) { System.Threading.Interlocked.Exchange(ref budgetDegradedFlag, 1); }
            WriteLog(mode, dryRun, plan, killed, failed, selfExited, remaining, services, net, memBefore, memAfter, freedMb, elapsed,
                phases, killCosts, System.Threading.Interlocked.CompareExchange(ref budgetDegradedFlag, 0, 0) != 0);

            if (screenshotPath != null && dryRun)
            {
                SaveUiScreenshot(screenshotPath, dryRun, killed, failed, selfExited, plan, services, net, memBefore, memAfter, freedMb, elapsed);
            }
            else if (!silent)
            {
                if (uiShell != null)
                {
                    // 清理中窗口已经在屏幕上，而且刚刚原地切成了结果态：接着跑消息循环，
                    // 窗口就留在那儿直到你点「关闭」（不再有 5 秒自动关闭）。
                    if (!workerInvocation && !uiShell.Form.IsDisposed) { Application.Run(uiShell.Form); }
                }
                else
                {
                    RunUi(dryRun, killed, failed, selfExited, plan, services, net, memBefore, memAfter, freedMb, elapsed);
                }
            }

            if(!workerInvocation&&coordinator!=null){coordinator.Dispose();coordinator=null;}
            if(coordinationError!=null)return 2;
            if (failed.Any(p=>p.FailReason!="其他实例处理中") || services.Failed.Count > 0) { return 1; }
            return 0;
        }

        // GUI subsystem apps have no console of their own; attach to the parent's
        // (if it has one) so --help / --version / --check-config print where the
        // user typed the command. Failure is fine: the same text also goes to the
        // log and, unless --silent, into a message box.
        private static void WriteConsole(string text)
        {
            try
            {
                if (!consoleAttached)
                {
                    consoleAttached = true;
                    AttachConsole(-1);
                }
                Console.WriteLine(text);
            }
            catch { }
        }

        private static bool IsElevated()
        {
            try
            {
                System.Security.Principal.WindowsIdentity wi = System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal wp = new System.Security.Principal.WindowsPrincipal(wi);
                return wp.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static T WithProcessSnapshot<T>(Func<Process[], T> read)
        {
            Process[] processes = Process.GetProcesses();
            try { return read(processes); }
            finally { foreach (Process process in processes) { process.Dispose(); } }
        }

        private static int GetShellSession()
        {
            using (Process self = Process.GetCurrentProcess()) { return self.SessionId; }
        }

        private static MemorySnapshot TakeSnapshot()
        {
            return WithProcessSnapshot(processes => TakeSnapshot(processes, GetShellSession()));
        }

        private static MemorySnapshot TakeSnapshot(Process[] processes, int session)
        {
            MemorySnapshot s = new MemorySnapshot();
            try
            {
                MEMORYSTATUSEX ms = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(ms))
                {
                    s.TotalMB = ms.ullTotalPhys / 1048576.0;
                    s.AvailMB = ms.ullAvailPhys / 1048576.0;
                    s.UsedMB = s.TotalMB - s.AvailMB;
                    s.LoadPercent = ms.dwMemoryLoad;
                    s.CommitLimitMB = ms.ullTotalPageFile / 1048576.0;
                    s.CommitMB = (ms.ullTotalPageFile - ms.ullAvailPageFile) / 1048576.0;
                }
            }
            catch { }
            try
            {
                double sum = 0;
                foreach (Process p in processes)
                {
                    try
                    {
                        if (p.SessionId == session) { sum += p.WorkingSet64 / 1048576.0; }
                    }
                    catch { }
                }
                s.SessionWsMB = sum;
            }
            catch { }
            return s;
        }

        private static string SysTool(string exeName)
        {
            return Path.Combine(Environment.SystemDirectory, exeName);
        }

        private static Task<T> StartDiagnostic<T>(Func<T> work)
        {
            return Task.Factory.StartNew(work, System.Threading.CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private static GpuSample ReadGpuMemory()
        {
            var sample = new GpuSample();
            var clock = Stopwatch.StartNew();
            try
            {
                string tool = SysTool("nvidia-smi.exe");
                if (!File.Exists(tool))
                {
                    string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    if (string.IsNullOrEmpty(programFiles)) return sample;
                    tool = Path.Combine(programFiles, @"NVIDIA Corporation\NVSMI\nvidia-smi.exe");
                    if (!File.Exists(tool)) return sample;
                }
                ToolOutput result = RunBoundedTool(tool,
                    "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits", 2000);
                string line = result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (line == null) { return sample; }
                string[] parts = line.Split(',');
                double used;
                double total;
                if (parts.Length >= 2 && double.TryParse(parts[0].Trim(), out used) && double.TryParse(parts[1].Trim(), out total))
                {
                    sample.UsedMB = used;
                    sample.TotalMB = total;
                }
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            finally { sample.ElapsedMs = clock.ElapsedMilliseconds; }
            return sample;
        }

        private static NetworkReport ReadNetworkReport(bool repair)
        {
            var report = new NetworkReport { State = NetworkCheckState.Incomplete };
            try
            {
                RunNetworkCheck(report, repair);
                report.State = NetworkCheckState.Completed;
            }
            catch (Exception ex)
            {
                report.Notes.Add("网络检测未完成：" + ex.Message);
            }
            return report;
        }

        private static string FmtMiB(double mb)
        {
            return mb.ToString("0") + " MiB";
        }

        private static string FmtDeltaMiB(double deltaMb)
        {
            if (Math.Abs(deltaMb) < 1.0) { return "±0 MiB"; }
            return (deltaMb > 0 ? "+" : "-") + Math.Abs(deltaMb).ToString("0") + " MiB";
        }

        private static string FmtGB(double mb)
        {
            return (mb / 1024.0).ToString("0.00") + " GB";
        }

        private static string FmtDelta(double deltaMb)
        {
            return FmtDeltaValue(deltaMb) + " GB";
        }

        private static string FmtDeltaValue(double deltaMb)
        {
            double deltaGb = Math.Round(deltaMb / 1024.0, 2);
            if (deltaGb == 0) return "±0.00";
            return (deltaGb > 0 ? "+" : "-") + Math.Abs(deltaGb).ToString("0.00");
        }

        private static string FmtDeltaPercent(double delta)
        {
            if (Math.Abs(delta) < 0.5) { return "±0%"; }
            return (delta > 0 ? "+" : "-") + Math.Abs(delta).ToString("0") + "%";
        }

        private static string GetAumid(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) { return null; }
            try
            {
                int len = 0;
                int rc = GetApplicationUserModelId(h, ref len, null);
                if (rc == ERROR_INSUFFICIENT_BUFFER && len > 0)
                {
                    StringBuilder sb = new StringBuilder(len);
                    rc = GetApplicationUserModelId(h, ref len, sb);
                    if (rc == 0) { return sb.ToString(); }
                }
                return null;
            }
            catch { return null; }
            finally { CloseHandle(h); }
        }

        // 白名单条目语义（docs/KNOWN-ISSUES.md 第 1 条）：
        //   1. 含 \ / : 的条目 = 路径规则，按"路径子串"匹配进程的完整路径；
        //   2. 其余条目 = 进程名规则，只跟进程名全等比较（去掉结尾的 .exe）；
        //   3. 以前所有条目都走路径子串，于是默认 system 名单里的 "System" 把
        //      C:\Windows\System32\ 下的 notepad / cmd / powershell / taskmgr / regedit
        //      全部静默免疫 —— 那是 bug，不是设计。
        // 返回命中的规则原文（没命中返回 null），--scan-only 的 JSON 用它做自检。
        private static string FindMatchedKeepRule(string name, string path)
        {
            return MatchKeepRule(keepSystem, name, path) ?? MatchKeepRule(keepUser, name, path);
        }

        // 纯函数版本：不读任何全局状态，方便离线单测（bench\keeptest.cs 直接调用它，
        // 测的就是线上这一份实现，不存在测试副本漂移的问题）。
        public static string MatchKeepRule(List<string> rules, string name, string path)
        {
            foreach (string raw in rules)
            {
                if (raw == null) { continue; }
                string entry = raw.Trim();
                if (entry.Length == 0) { continue; }

                bool matched;
                if (KeepRule.TryMatch(entry, path, out matched))
                {
                    if (matched) return entry;
                    continue;
                }

                if (LooksLikePath(entry))
                {
                    if (!string.IsNullOrEmpty(path)
                        && path.IndexOf(entry, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return entry;
                    }
                    continue;                    // 路径规则绝不拿进程名去凑
                }

                string bare = KeepRule.NameWithoutExtension(entry);
                if (!string.IsNullOrEmpty(name) && name.Equals(bare, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
            return null;
        }

        private static bool LooksLikePath(string entry)
        {
            return entry.IndexOf('\\') >= 0 || entry.IndexOf('/') >= 0 || entry.IndexOf(':') >= 0;
        }

        private static bool Keep(string name, string path)
        {
            return FindMatchedKeepRule(name, path) != null;
        }


        // ---- 自保护父链：反查 killer 的祖先进程（终端 / 编辑器 / DSH 会话都用得上） ----

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        // 只排除自身的 pid 时，启动 killer 的那个 shell / 终端照样会被结束。这里用
        // Toolhelp32 建 pid -> 父 pid 表，从自己往上走，把祖先都记下来。
        private static List<int> AncestorPids()
        {
            List<int> ancestors = new List<int>();
            IntPtr snap = IntPtr.Zero;
            try
            {
                Dictionary<int, int> parent = new Dictionary<int, int>();
                snap = CreateToolhelp32Snapshot(0x00000002, 0);   // TH32CS_SNAPPROCESS
                if (snap == IntPtr.Zero || snap == new IntPtr(-1)) { return ancestors; }
                PROCESSENTRY32 pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (Process32First(snap, ref pe))
                {
                    do
                    {
                        parent[(int)pe.th32ProcessID] = (int)pe.th32ParentProcessID;
                        pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                    }
                    while (Process32Next(snap, ref pe));
                }
                int pid = Process.GetCurrentProcess().Id;
                for (int i = 0; i < 16; i++)
                {
                    int ppid;
                    if (!parent.TryGetValue(pid, out ppid)) { break; }
                    if (ppid <= 4 || ancestors.Contains(ppid)) { break; }
                    ancestors.Add(ppid);
                    pid = ppid;
                }
            }
            catch { }
            finally
            {
                if (snap != IntPtr.Zero && snap != new IntPtr(-1))
                {
                    try { CloseHandle(snap); }
                    catch { }
                }
            }
            return ancestors;
        }

        // ---- 白名单可视化：把 UI 上勾选的名字写回 keep.json ----

        public static string JsonStringLiteral(string s)
        {
            if (s == null) { return "\"\""; }
            StringBuilder sb = new StringBuilder();
            sb.Append("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') { sb.Append("\\u" + ((int)c).ToString("x4")); }
                        else { sb.Append(c); }
                        break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }

        private static string JsonStringArray(List<string> items)
        {
            if (items.Count == 0) { return "[]"; }
            StringBuilder sb = new StringBuilder();
            sb.Append("[");
            for (int i = 0; i < items.Count; i++)
            {
                sb.Append(i == 0 ? Environment.NewLine + "    " : "," + Environment.NewLine + "    ");
                sb.Append(JsonStringLiteral(items[i]));
            }
            sb.Append(Environment.NewLine + "  ]");
            return sb.ToString();
        }

        private static IList<KeepPageRow> ReadRunningKeepProcesses()
        {
            int session, selfPid;
            using (Process self = Process.GetCurrentProcess())
            {
                session = self.SessionId;
                selfPid = self.Id;
            }
            var grouped = new Dictionary<string, KeepPageRow>(StringComparer.OrdinalIgnoreCase);
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    int pid;
                    string name;
                    try
                    {
                        pid = process.Id;
                        if (pid <= 4 || pid == selfPid || process.SessionId != session) continue;
                        name = process.ProcessName;
                    }
                    catch (InvalidOperationException) { continue; }
                    catch (System.ComponentModel.Win32Exception) { continue; }
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    string path = ReadProcessImage(process, pid);

                    string key = string.IsNullOrEmpty(path) ? "name:" + name : "path:" + path;
                    KeepPageRow row;
                    if (!grouped.TryGetValue(key, out row))
                    {
                        row = new KeepPageRow { Name = name, Path = path, Pids = "" };
                        grouped.Add(key, row);
                    }
                    row.Pids += (row.Instances == 0 ? "" : ", ") + pid.ToString();
                    row.Instances++;
                }
            }
            return grouped.Values.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ReadProcessImage(Process process, int pid)
        {
            string path = NativeIdentity.Image(pid);
            if (path != null) return path;

            try
            {
                ProcessModule module = process.MainModule;
                return module == null ? "" : module.FileName;
            }
            catch (InvalidOperationException) { return ""; }
            catch (System.ComponentModel.Win32Exception) { return ""; }
            catch (NotSupportedException) { return ""; }
        }

        private static int ShowKeepPage(string screenshot)
        {
            using (var form = new DpiAwareForm { Text = "killer · 白名单", AutoScaleMode = AutoScaleMode.None,
                StartPosition = FormStartPosition.CenterScreen, BackColor = Theme.Bg })
            {
                float scale = form.UiScale;
                form.ClientSize = new Size((int)((uiWidthOverride > 0 ? uiWidthOverride : 980) * scale),
                    (int)((uiHeightOverride > 0 ? uiHeightOverride : 680) * scale));
                form.MinimumSize = new Size((int)(360 * scale), (int)(420 * scale));
                var page = CreateKeepPage(null, screenshot != null);
                form.Controls.Add(page);
                page.SetScale(scale);
                form.UiDpiChanged += delegate { page.SetScale(form.UiScale); };
                form.Shown += delegate { page.RefreshPage(); };
                if (screenshot == null)
                {
                    Application.Run(form);
                    return 0;
                }

                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000);
                form.Show();
                var clock = Stopwatch.StartNew();
                do
                {
                    Application.DoEvents();
                    if (!page.IsLoading) break;
                    System.Threading.Thread.Sleep(20);
                } while (clock.ElapsedMilliseconds < 2000);
                try
                {
                    if (page.IsLoading) throw new System.TimeoutException("截图前未能完成进程列表读取。");
                    string output = Path.GetFullPath(screenshot);
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(output, ImageFormat.Png);
                    }
                    WriteConsole("白名单截图：" + output);
                    return 0;
                }
                catch (IOException ex) { WriteConsole("白名单截图失败：" + ex.Message); return 2; }
                catch (UnauthorizedAccessException ex) { WriteConsole("白名单截图失败：" + ex.Message); return 2; }
                catch (System.TimeoutException ex) { WriteConsole(ex.Message); return 2; }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool K32EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long created,
            out long exited, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_SET_QUOTA = 0x0100;
        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint SYNCHRONIZE = 0x00100000;

        private static IntPtr OpenVerifiedProcess(ProcInfo candidate, uint access)
        {
            if (candidate.StartTicks <= 0) { return IntPtr.Zero; }
            IntPtr handle = OpenProcess(access | PROCESS_QUERY_LIMITED_INFORMATION, false, candidate.Pid);
            if (handle == IntPtr.Zero) { return IntPtr.Zero; }
            long created, exited, kernel, user;
            if (GetProcessTimes(handle, out created, out exited, out kernel, out user)
                && DateTime.FromFileTime(created).Ticks == candidate.StartTicks
                && (string.IsNullOrEmpty(candidate.Path) || string.Equals(NativeIdentity.Image(handle),
                    candidate.Path, StringComparison.OrdinalIgnoreCase)))
            {
                return handle;
            }
            CloseHandle(handle);
            return IntPtr.Zero;
        }

        // 对复查后仍在运行的候选进程清一次工作集，按工作集变化记录回收量。
        private sealed class TrimSample
        {
            public Process Process;
            public double BeforeMb;
        }

        internal static IEnumerable<ProcInfo> SelectWorkingSetTrimTargets(IEnumerable<ProcInfo> targets)
        {
            return targets.Where(p => p.WsMb >= 40 && p.StartTicks > 0).OrderByDescending(p => p.WsMb);
        }

        private static double TrimWorkingSets(IEnumerable<ProcInfo> targets, Func<int> remainingBudget)
        {
            double freed = 0;
            trimCount = 0;
            int done = 0;
            List<TrimSample> samples = new List<TrimSample>();
            List<ProcInfo> selected = SelectWorkingSetTrimTargets(targets).ToList();
            for (int offset = 0; offset < selected.Count && done < 60 && remainingBudget() > 0; offset += 16)
            {
                List<ProcInfo> batch = selected.GetRange(offset, Math.Min(16, selected.Count - offset));
                foreach (ProcInfo t in ClaimCleanupTargets(batch, remainingBudget, (candidate, reason) => { }))
                {
                    if (done >= 60 || remainingBudget() <= 0) { break; }
                    IntPtr h = IntPtr.Zero;
                    Process p = null;
                    try
                    {
                        h = OpenVerifiedProcess(t, PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA);
                        if (h == IntPtr.Zero) { continue; }
                        p = Process.GetProcessById(t.Pid);
                        double before = p.WorkingSet64 / 1048576.0;
                        if (remainingBudget() <= 0) { break; }
                        if (!K32EmptyWorkingSet(h)) { continue; }
                        samples.Add(new TrimSample { Process = p, BeforeMb = before });
                        p = null;
                        UiPump(0);
                        done++;
                        trimCount++;
                    }
                    catch { }
                    finally
                    {
                        if (p != null) { try { p.Dispose(); } catch { } }
                        if (h != IntPtr.Zero)
                        {
                            try { CloseHandle(h); }
                            catch { }
                        }
                    }
                }
            }
            foreach (TrimSample sample in samples)
            {
                try
                {
                    sample.Process.Refresh();
                    double after = sample.Process.WorkingSet64 / 1048576.0;
                    if (sample.BeforeMb > after) { freed += sample.BeforeMb - after; }
                }
                catch { }
                finally { try { sample.Process.Dispose(); } catch { } }
            }
            return Math.Round(freed, 1);
        }

        private static List<KeyValuePair<string, string>> scanKeepHits = new List<KeyValuePair<string, string>>();
        private static List<KeyValuePair<string, string>> scanBaseDirHits = new List<KeyValuePair<string, string>>();

        private static List<ProcInfo> FindCandidates()
        {
            return WithProcessSnapshot(processes => FindCandidates(processes, GetShellSession(), true, true));
        }

        private static List<ProcInfo> FindCandidatesLite(Func<int> remainingBudget)
        {
            return WithProcessSnapshot(processes => FindCandidates(processes, GetShellSession(), false, false, remainingBudget));
        }

        private static List<ProcInfo> FindCandidates(Process[] processes, int session, bool withAumid, bool sort,
            Func<int> remainingBudget = null)
        {
            int selfPid = NativeIdentity.SelfPid;
            List<ProcInfo> list = new List<ProcInfo>();
            scanKeepHits = new List<KeyValuePair<string, string>>();
            scanBaseDirHits = new List<KeyValuePair<string, string>>();

            int scannedForPump = 0;
            Dictionary<int, int> parents = NativeIdentity.Parents();
            foreach (Process p in processes)
            {
                if (remainingBudget != null && remainingBudget() <= 0) { break; }
                if ((++scannedForPump % 32) == 0) { UiPump(0); }
                int pid;
                try
                {
                    pid = p.Id;
                    if (pid == selfPid || pid <= 4) { continue; }
                    if (protectedPids.Contains(pid)) { continue; }
                    if (p.SessionId != session) { continue; }
                    if (NativeIdentity.Protected(pid, parents)) { continue; }
                }
                catch { continue; }

                string name = "";
                string path = ReadProcessImage(p, pid);
                double ws = 0;
                bool hasWindow = false;

                try { name = p.ProcessName; } catch { }

                if (KeepRule.IsInDirectory(path, baseDir))
                {
                    scanBaseDirHits.Add(new KeyValuePair<string, string>(
                        name + " (PID " + pid + ")", "baseDir: " + path));
                    continue;
                }
                string keepHit = FindMatchedKeepRule(name, path);
                if (keepHit != null)
                {
                    scanKeepHits.Add(new KeyValuePair<string, string>(
                        name + " (PID " + pid + ")", keepHit));
                    continue;
                }

                try { ws = Math.Round(p.WorkingSet64 / 1048576.0, 1); } catch { }
                try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { }
                ProcInfo info = new ProcInfo();
                info.Pid = pid;
                info.Name = name;
                info.Path = path;
                info.WsMb = ws;
                info.HasWindow = hasWindow;
                info.Aumid = withAumid && hasWindow ? GetAumid(pid) : null;
                info.StartTicks = ProcessStartTicks(p);
                list.Add(info);
            }

            if (!sort) { return list; }
            return list.OrderByDescending(x => x.WsMb).ToList();
        }

        // ---- --scan-only：把计划与"白名单命中来源"落盘（不碰任何进程）----
        // 目的：白名单语义这类改动唯一的离线逐条比对通道（交接手册 §5：改动清理逻辑
        // 时先 --dry-run / 逐条 diff）。两个文件都是纯文本，直接 diff 就行。

        private static int ExportPlanFiles(List<ProcInfo> found, string planOut, string keepOut,
                                           out string planResolved, out string keepResolved)
        {
            double planMb = Math.Round(found.Sum(x => x.WsMb), 1);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (string.IsNullOrEmpty(planOut)) { planOut = Path.Combine(baseDir, "killer-plan.json"); }
            if (string.IsNullOrEmpty(keepOut)) { keepOut = Path.Combine(baseDir, "killer-keep-exclude.txt"); }
            planResolved = planOut;
            keepResolved = keepOut;

            StringBuilder j = new StringBuilder();
            j.AppendLine("{");
            j.AppendLine("  \"scannedAt\": " + JsonStringLiteral(stamp) + ",");
            j.AppendLine("  \"version\": " + JsonStringLiteral(VersionText()) + ",");
            j.AppendLine("  \"elevated\": " + (IsElevated() ? "true" : "false") + ",");
            j.AppendLine("  \"baseDir\": " + JsonStringLiteral(baseDir) + ",");
            j.AppendLine("  \"planCount\": " + found.Count + ",");
            j.AppendLine("  \"planTotalMb\": " + planMb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ",");
            j.AppendLine("  \"keepHitCount\": " + scanKeepHits.Count + ",");
            j.AppendLine("  \"baseDirExcludedCount\": " + scanBaseDirHits.Count + ",");
            j.AppendLine("  \"plan\": [");
            for (int i = 0; i < found.Count; i++)
            {
                ProcInfo p = found[i];
                j.Append("    {\"pid\": " + p.Pid
                    + ", \"name\": " + JsonStringLiteral(p.Name)
                    + ", \"wsMb\": " + p.WsMb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + ", \"window\": " + (p.HasWindow ? "true" : "false")
                    + ", \"start\": " + p.StartTicks
                    + ", \"path\": " + JsonStringLiteral(p.Path) + "}");
                j.AppendLine(i == found.Count - 1 ? "" : ",");
            }
            j.AppendLine("  ],");
            j.AppendLine("  \"keepExcluded\": [");
            List<KeyValuePair<string, string>> allExcluded = new List<KeyValuePair<string, string>>();
            allExcluded.AddRange(scanKeepHits);
            allExcluded.AddRange(scanBaseDirHits);
            for (int i = 0; i < allExcluded.Count; i++)
            {
                j.Append("    {\"proc\": " + JsonStringLiteral(allExcluded[i].Key)
                    + ", \"rule\": " + JsonStringLiteral(allExcluded[i].Value) + "}");
                j.AppendLine(i == allExcluded.Count - 1 ? "" : ",");
            }
            j.AppendLine("  ]");
            j.AppendLine("}");
            File.WriteAllText(planOut, j.ToString(), new UTF8Encoding(true));

            StringBuilder t = new StringBuilder();
            t.AppendLine("# killer --scan-only 产出（" + stamp + " | version " + VersionText()
                + " | elevated=" + IsElevated() + "）");
            t.AppendLine("# PLAN  = 真跑会被结束的进程（" + found.Count + " 个，" + planMb + " MB）");
            t.AppendLine("# KEEP  = 被白名单 / 自身目录留下的进程（" + allExcluded.Count + " 个）");
            t.AppendLine();
            foreach (ProcInfo p in found)
            {
                t.AppendLine("PLAN " + p.Name + " pid=" + p.Pid + " ws=" + p.WsMb + "MB"
                    + (p.HasWindow ? " window" : "") + " " + (string.IsNullOrEmpty(p.Path) ? "(path n/a)" : p.Path));
            }
            t.AppendLine();
            foreach (KeyValuePair<string, string> kv in allExcluded)
            {
                t.AppendLine("KEEP " + kv.Key + " <- 命中规则 " + kv.Value);
            }
            File.WriteAllText(keepOut, t.ToString(), new UTF8Encoding(true));
            return allExcluded.Count;
        }

        private static KeepConfig ReadEffectiveKeep()
        {
            KeepConfig config = KeepRuleStore.Read(keepPath);
            if (config.system == null || config.system.Count == 0)
                config.system = new List<string>(DefaultSystem);
            config.user = config.user ?? new List<string>();
            config.services = config.services ?? new List<string>();
            config.graceful = config.graceful ?? new List<string>();
            return config;
        }

        private static void LoadKeep()
        {
            keepConfigError = null;
            try
            {
                if (!File.Exists(keepPath))
                {
                    using (new NamedGate("config:" + keepPath, 2000))
                        if (!File.Exists(keepPath)) WriteDefaultKeep();
                }
                KeepConfig config = ReadEffectiveKeep();
                keepSystem = config.system;
                keepUser = config.user;
                stopServiceNames = config.services;
                gracefulNames = config.graceful;
                networkRepair = config.networkRepair ?? true;
            }
            catch (Exception ex)
            {
                keepConfigError = "读取 keep.json 失败。本次操作已停止，请检查配置或备份。 " + ex.Message;
            }
        }

        private static int ReportConfig(string mode, bool showDialog)
        {
            WriteConfigLog(mode);
            string summary = ConfigSummary();
            WriteConsole(summary);
            if (showDialog)
            {
                MessageBox.Show(summary, "killer · keep.json", MessageBoxButtons.OK,
                    keepConfigError == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            return keepConfigError == null ? 0 : 2;
        }

        private static string ConfigSummary()
        {
            StringBuilder sb = new StringBuilder();
            if (keepConfigError != null) return keepConfigError + Environment.NewLine + "文件: " + keepPath;
            sb.AppendLine("keep.json 解析成功。");
            sb.AppendLine();
            sb.AppendLine("system  = " + keepSystem.Count + " 项");
            sb.AppendLine("user    = " + keepUser.Count + " 项");
            sb.AppendLine("services= " + stopServiceNames.Count + " 项");
            sb.AppendLine("graceful= " + gracefulNames.Count + " 项");
            sb.AppendLine("networkRepair = " + networkRepair);
            sb.AppendLine("文件: " + keepPath);
            return sb.ToString();
        }

        private static void WriteConfigLog(string mode)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | mode=" + mode + " | elevated="
                + IsElevated() + " | elapsed=0s ===");
            if (keepConfigError == null)
            {
                sb.AppendLine("config: OK keep.json parsed");
                sb.AppendLine("keep: system=" + keepSystem.Count + " entries, user=" + keepUser.Count + " entries");
                sb.AppendLine("services=" + stopServiceNames.Count + ", graceful=" + gracefulNames.Count
                    + ", networkRepair=" + networkRepair);
            }
            else
            {
                sb.AppendLine("config: ERROR " + keepConfigError);
            }
            sb.AppendLine();
            AppendLogBlock(sb.ToString());
        }

        // ---- network sanity helpers (accelerator leftovers) ----

        private static bool ProcessAlive(int pid)
        {
            try
            {
                Process p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch { return false; }
        }

        // ---- PID 复用预检（docs/KNOWN-ISSUES.md：无 PID 复用预检）----
        // 计划生成与真正动手之间隔着服务停止、优雅关闭等待、900ms 间隔，甚至第二轮
        // 复查；这段时间里 pid 完全可能被系统回收给一个全新的进程。杀之前先核对
        // 「名字 + 创建时间」，对不上就当它已经退出，绝不动手。

        private static long ProcessStartTicks(Process p)
        {
            try { return p.StartTime.Ticks; }
            catch { return 0; }
        }

        // 判定规则单独拆出来（纯函数）：离线单测直接喂"计划时/现在"的名字与创建时间。
        // 返回 null = 还是同一个进程；否则是给用户看的原因。
        public static string IdentityMismatch(string plannedName, long plannedTicks, string nowName, long nowTicks)
        {
            if (!string.IsNullOrEmpty(plannedName) && !string.IsNullOrEmpty(nowName)
                && !string.Equals(nowName, plannedName, StringComparison.OrdinalIgnoreCase))
            {
                return "pid 已被复用（现在是 " + nowName + "）";
            }
            if (plannedTicks > 0 && nowTicks > 0 && nowTicks != plannedTicks)
            {
                return "pid 已被复用（创建时间不一致）";
            }
            return null;
        }

        private static bool IsCoordinationFailure(ProcInfo candidate)
        {
            return candidate.FailReason == "其他实例处理中"
                || (candidate.FailReason != null && candidate.FailReason.StartsWith("协调失败", StringComparison.Ordinal));
        }

        private static bool IsBudgetFailure(ProcInfo candidate)
        {
            return candidate.FailReason != null && candidate.FailReason.StartsWith("总清理预算已耗尽", StringComparison.Ordinal);
        }

        internal static void RunTaskKillBatches(IList<ProcInfo> candidates, Func<int> remainingBudget,
            Func<ProcInfo, bool> admit, Action<ProcInfo> expired, Action<List<ProcInfo>, int> run)
        {
            const int batchSize = 16;
            var batch = new List<ProcInfo>(batchSize);
            Action submit = delegate()
            {
                if (batch.Count == 0) { return; }
                int remaining = remainingBudget();
                if (remaining > 0) { run(batch, remaining); }
                else { foreach (ProcInfo target in batch) { expired(target); } }
                batch.Clear();
            };

            foreach (ProcInfo candidate in candidates)
            {
                if (remainingBudget() <= 0)
                {
                    submit();
                    expired(candidate);
                    continue;
                }
                if (admit(candidate)) { batch.Add(candidate); }
                if (batch.Count == batchSize) { submit(); }
            }
            submit();
        }

        internal static HashSet<int> ReportedTaskKillPids(string output)
        {
            var result = new HashSet<int>();
            foreach (Match match in Regex.Matches(output, @"(?im)^[\t ]*(?:SUCCESS|成功)[\t ]*[:：][^\r\n]*?\bPID[^\d\r\n]*(\d+)\b"))
            {
                int pid;
                if (int.TryParse(match.Groups[1].Value, out pid)) { result.Add(pid); }
            }
            return result;
        }

        // 目标句柄保留到辅助程序退出或交给退出确认，避免 PID 被复用。
        private static List<ExitRequest> TaskKillBatch(List<ProcInfo> candidates, Func<int> remainingBudget, out long elapsedMs)
        {
            var handles = new Dictionary<int, IntPtr>();
            var accepted = new List<ExitRequest>();
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                var args = new StringBuilder("/F");
                foreach (ProcInfo candidate in candidates)
                {
                    if (remainingBudget() <= 0) { break; }
                    IntPtr handle = OpenVerifiedProcess(candidate, SYNCHRONIZE);
                    if (handle == IntPtr.Zero) { continue; }
                    handles.Add(candidate.Pid, handle);
                    args.Append(" /PID ").Append(candidate.Pid);
                }

                if (handles.Count == 0) { return accepted; }
                string toolPath = SysTool("taskkill.exe");
                int remaining = remainingBudget();
                if (remaining <= 0) { return accepted; }
                ToolOutput output = RunBoundedTool(toolPath, args.ToString(), remaining, delegate(Process tool)
                {
                    IntPtr[] pinned = handles.Values.ToArray();
                    var wait = new System.Threading.Thread(delegate()
                    {
                        using (tool)
                        {
                            try { WaitForSingleObject(tool.Handle, uint.MaxValue); }
                            finally { foreach (IntPtr handle in pinned) { CloseHandle(handle); } }
                        }
                    });
                    wait.IsBackground = true;
                    wait.Start();
                    handles.Clear();
                }, remainingBudget);
                var reported = ReportedTaskKillPids(output.StandardOutput + "\n" + output.StandardError);
                foreach (ProcInfo candidate in candidates)
                {
                    IntPtr handle;
                    if (!reported.Contains(candidate.Pid) || !handles.TryGetValue(candidate.Pid, out handle)) { continue; }
                    accepted.Add(new ExitRequest(candidate, handle, "taskkill /F"));
                    handles.Remove(candidate.Pid);
                }
                return accepted;
            }
            catch (System.ComponentModel.Win32Exception) { return accepted; }
            finally
            {
                foreach (IntPtr handle in handles.Values) { CloseHandle(handle); }
                elapsedMs = clock.ElapsedMilliseconds;
            }
        }

        private static IntPtr RequestTermination(ProcInfo target, Func<int> remainingBudget, out string reason)
        {
            IntPtr handle = OpenVerifiedProcess(target, PROCESS_TERMINATE | SYNCHRONIZE);
            if (handle == IntPtr.Zero)
            {
                reason = "无法以终止权限打开已核验的进程";
                return IntPtr.Zero;
            }
            bool requested = false;
            try
            {
                if (remainingBudget() <= 0)
                {
                    reason = "总清理预算已耗尽，跳过终止重试";
                    return IntPtr.Zero;
                }
                if (!TerminateProcess(handle, 1))
                {
                    reason = ClassifyKillFailure(new System.ComponentModel.Win32Exception());
                    return IntPtr.Zero;
                }
                requested = true;
                reason = null;
                return handle;
            }
            finally { if (!requested) { CloseHandle(handle); } }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string systemName, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
            ref TokPriv1Luid newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct TokPriv1Luid
        {
            public int Count;
            public long Luid;
            public int Attr;
        }

        private static bool seDebugEnabled = false;
        private static readonly object debugPrivilegeLock = new object();

        // SeDebugPrivilege is what opens the protected / elevated processes a plain
        // Kill() is refused on. It needs an elevated token, so unelevated runs no-op.
        private static void EnableDebugPrivilege()
        {
            lock (debugPrivilegeLock)
            {
                if (seDebugEnabled) { return; }
                IntPtr token = IntPtr.Zero;
                try
                {
                    if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out token)) { return; }
                    TokPriv1Luid tp = new TokPriv1Luid();
                    tp.Count = 1;
                    tp.Attr = 0x00000002;   // SE_PRIVILEGE_ENABLED
                    if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out tp.Luid)) { return; }
                    SetLastError(0);
                    if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) { return; }
                    if (Marshal.GetLastWin32Error() == 1300) { return; } // ERROR_NOT_ALL_ASSIGNED
                    seDebugEnabled = true;
                }
                catch { }
                finally
                {
                    if (token != IntPtr.Zero) { try { CloseHandle(token); } catch { } }
                }
            }
        }

        private static string ClassifyKillFailure(Exception ex)
        {
            string m = ex == null ? "" : ex.Message;
            string kind = "结束失败";
            if (m.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("拒绝访问") >= 0)
            {
                kind = "拒绝访问（受保护进程 / PPL）";
            }
            else if (m.IndexOf("privilege", StringComparison.OrdinalIgnoreCase) >= 0
                     || m.IndexOf("特权") >= 0)
            {
                kind = "缺少特权";
            }
            else if (ex is InvalidOperationException)
            {
                kind = "进程已退出";
            }
            else if (m.Length == 0)
            {
                kind = "未知错误";
            }
            return kind + "：" + m;
        }

        private static bool IsGraceful(string name, string path)
        {
            foreach (string raw in gracefulNames)
            {
                if (raw == null) { continue; }
                string entry = raw.Trim();
                if (entry.Length == 0) { continue; }
                if (entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { entry = entry.Substring(0, entry.Length - 4); }
                if (!string.IsNullOrEmpty(name) && name.Equals(entry, StringComparison.OrdinalIgnoreCase)) { return true; }
                if (!string.IsNullOrEmpty(path) && path.IndexOf(entry, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            }
            return false;
        }

        // 探测目标：Windows NCSI 自己用的那个 + 两个国内目标。单个微软域名在国内
        // 会被劫持或不可达，只看它一个必然误判，所以改成多数表决。
        private static readonly string[] NetProbeHosts = new string[]
        {
            "www.msftconnecttest.com",
            "www.baidu.com",
            "www.qq.com"
        };

        private const string NcsiUrl = "http://www.msftconnecttest.com/connecttest.txt";

        private static bool DnsResolve(string host)
        {
            try
            {
                IAsyncResult ar = Dns.BeginGetHostAddresses(host, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(2500)) { return false; }
                IPAddress[] addrs = Dns.EndGetHostAddresses(ar);
                return addrs != null && addrs.Length > 0;
            }
            catch { return false; }
        }

        // 返回成功数；多数通过才算解析正常。detail 收集逐目标结果，写进日志。
        private static int DnsVote(List<string> detail)
        {
            int ok = 0;
            foreach (string host in NetProbeHosts)
            {
                bool good = DnsResolve(host);
                if (good) { ok++; }
                if (detail != null) { detail.Add(host + (good ? "=ok" : "=fail")); }
            }
            return ok;
        }

        // 直连 HTTP 探测：系统代理是否可用由 ProxyState 单独判定，不在这里混着走。
        private static bool HttpGet(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = true;
                req.UserAgent = "killer-netselfcheck";
                req.Proxy = null;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    int code = (int)resp.StatusCode;
                    return code >= 200 && code < 400;
                }
            }
            catch { return false; }
        }

        private static bool TcpHandshake(string host, int port, int timeoutMs)
        {
            try
            {
                IPAddress[] addrs = Dns.GetHostAddresses(host);
                if (addrs == null || addrs.Length == 0) { return false; }
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(addrs[0], port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) { return false; }
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        // "真的能上网"：DNS 通了也可能打不开网页（解析被劫持 / 悬空默认路由），
        // 所以按 connecttest.txt -> 国内 HTTP -> TCP 443 实测，任一通过即可。
        private sealed class OnlineProbeResult
        {
            public string How;
            public bool Ok;
        }

        private static bool OnlineProbe(out string how, out string detail)
        {
            how = "";
            detail = "";
            List<KeyValuePair<string, Func<bool>>> probes = new List<KeyValuePair<string, Func<bool>>>();
            probes.Add(new KeyValuePair<string, Func<bool>>("connecttest.txt", delegate { return HttpGet(NcsiUrl, 2500); }));
            foreach (string host in NetProbeHosts)
            {
                string httpHost = host;
                probes.Add(new KeyValuePair<string, Func<bool>>("http " + host,
                    delegate { return HttpGet("http://" + httpHost + "/", 2500); }));
            }
            foreach (string host in NetProbeHosts)
            {
                string tcpHost = host;
                probes.Add(new KeyValuePair<string, Func<bool>>("tcp443 " + host,
                    delegate { return TcpHandshake(tcpHost, 443, 2000); }));
            }

            List<Task<OnlineProbeResult>> pending = new List<Task<OnlineProbeResult>>();
            foreach (KeyValuePair<string, Func<bool>> probe in probes)
            {
                string label = probe.Key;
                Func<bool> test = probe.Value;
                pending.Add(Task<OnlineProbeResult>.Factory.StartNew(delegate
                {
                    bool ok = false;
                    try { ok = test(); } catch { }
                    return new OnlineProbeResult { How = label, Ok = ok };
                }));
            }

            List<string> failed = new List<string>();
            Stopwatch budget = Stopwatch.StartNew();
            while (pending.Count > 0)
            {
                int left = 3000 - (int)budget.ElapsedMilliseconds;
                if (left <= 0) { break; }
                int completed = Task.WaitAny(pending.ToArray(), left);
                if (completed < 0) { break; }
                Task<OnlineProbeResult> task = pending[completed];
                pending.RemoveAt(completed);
                OnlineProbeResult result = task.Result;
                if (result.Ok) { how = result.How; return true; }
                failed.Add(result.How);
            }
            detail = pending.Count == 0 ? string.Join(", ", failed.ToArray()) : "并发探测超过 3000 ms";
            return false;
        }

        // 修复后复测：一次不过就报"已修复"是假的，隔 500ms 退避重试 2-3 次。
        private static bool Retest(Func<bool> test, int attempts, int delayMs)
        {
            for (int i = 0; i < attempts; i++)
            {
                if (i > 0) { System.Threading.Thread.Sleep(delayMs * i); }
                bool ok = false;
                try { ok = test(); }
                catch { }
                if (ok) { return true; }
            }
            return false;
        }

        private static string GetWinInetProxy()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (k == null) { return null; }
                    object en = k.GetValue("ProxyEnable");
                    if (en == null || Convert.ToInt32(en) == 0) { return null; }
                    object srv = k.GetValue("ProxyServer");
                    return srv == null ? "" : srv.ToString();
                }
            }
            catch { return null; }
        }

        private static string ProxyHost(string proxy)
        {
            if (string.IsNullOrEmpty(proxy)) { return ""; }
            string host = proxy;
            int eq = host.IndexOf('=');
            if (eq >= 0) { host = host.Substring(eq + 1); }
            int semi = host.IndexOf(';');
            if (semi >= 0) { host = host.Substring(0, semi); }
            int colon = host.LastIndexOf(':');
            if (colon > 0) { host = host.Substring(0, colon); }
            return host.Trim();
        }

        private static int ProxyPort(string proxy)
        {
            if (string.IsNullOrEmpty(proxy)) { return 0; }
            string host = proxy;
            int eq = host.IndexOf('=');
            if (eq >= 0) { host = host.Substring(eq + 1); }
            int semi = host.IndexOf(';');
            if (semi >= 0) { host = host.Substring(0, semi); }
            int colon = host.LastIndexOf(':');
            if (colon <= 0) { return 0; }
            int port;
            if (int.TryParse(host.Substring(colon + 1).Trim(), out port)) { return port; }
            return 0;
        }

        private static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) { return false; }
            return host.Equals("127.0.0.1") || host.StartsWith("127.") || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals("::1");
        }

        private static bool PortListening(int port)
        {
            if (port <= 0) { return false; }
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(400)) { return false; }
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        private static void DisableWinInetProxy()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
                {
                    if (k != null) { k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord); }
                }
            }
            catch { }
        }

        private sealed class ToolOutput
        {
            internal int? ExitCode;
            internal string StandardOutput;
            internal string StandardError;
        }

        private static ToolOutput RunBoundedTool(string file, string arguments, int timeoutMs,
            Action<Process> keepUntilExit = null, Func<int> submissionBudget = null)
        {
            string root = Path.GetPathRoot(file);
            if (root == null || root.Length < 3)
                throw new ArgumentException("辅助程序需要完整路径。", "file");
            var clock = Stopwatch.StartNew();
            var startInfo = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(file),
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (submissionBudget != null)
            {
                timeoutMs = Math.Min(timeoutMs, submissionBudget());
                if (timeoutMs <= 0) { return new ToolOutput(); }
            }
            Process process = Process.Start(startInfo);
            if (process == null) { throw new InvalidOperationException("无法启动 " + file); }
            try
            {
                var output = new StringBuilder();
                var error = new StringBuilder();
                var outputClosed = new TaskCompletionSource<bool>();
                var errorClosed = new TaskCompletionSource<bool>();
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) { outputClosed.TrySetResult(true); }
                    else { lock (output) { output.AppendLine(e.Data); } }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) { errorClosed.TrySetResult(true); }
                    else { lock (error) { error.AppendLine(e.Data); } }
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool exited = process.WaitForExit(Math.Max(0, timeoutMs - (int)clock.ElapsedMilliseconds));
                if (!exited)
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
                bool drained = Task.WaitAll(new Task[] { outputClosed.Task, errorClosed.Task },
                    Math.Max(0, timeoutMs - (int)clock.ElapsedMilliseconds));
                int? exitCode = exited && drained ? (int?)process.ExitCode : null;
                if (!exited && keepUntilExit != null && !process.WaitForExit(0))
                {
                    keepUntilExit(process);
                    process = null;
                }
                lock (output)
                lock (error)
                {
                    return new ToolOutput
                    {
                        ExitCode = exitCode,
                        StandardOutput = output.ToString(),
                        StandardError = error.ToString()
                    };
                }
            }
            finally { if (process != null) { process.Dispose(); } }
        }

        private static bool RunTool(string file, string arguments, out string output, int timeoutMs = 8000)
        {
            try
            {
                ToolOutput result = RunBoundedTool(file, arguments, timeoutMs);
                output = result.ExitCode.HasValue
                    ? (result.StandardOutput + " " + result.StandardError).Replace("\r", " ").Replace("\n", " ").Trim()
                    : "超时未完成";
                return result.ExitCode == 0;
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }

        private static void RunTool(string file, string arguments)
        {
            string ignored;
            RunTool(file, arguments, out ignored);
        }

        private static string GetRouteAdapterName()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) { continue; }
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) { continue; }
                    foreach (GatewayIPAddressInformation gw in ni.GetIPProperties().GatewayAddresses)
                    {
                        if (gw.Address != null && gw.Address.AddressFamily == AddressFamily.InterNetwork && !gw.Address.Equals(IPAddress.Any))
                        {
                            return ni.Name;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // TAP / Wintun / WireGuard 这类虚拟网卡退出后常留下一条 metric 更低的默认
        // 路由，于是"能解析但打不开网页"。这里只识别并提示，不自动删路由。
        private static string DanglingRouteAdapter(out int metric)
        {
            metric = 0;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) { continue; }
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) { continue; }
                    bool hasGateway = false;
                    foreach (GatewayIPAddressInformation gw in ni.GetIPProperties().GatewayAddresses)
                    {
                        if (gw.Address != null && gw.Address.AddressFamily == AddressFamily.InterNetwork
                            && !gw.Address.Equals(IPAddress.Any))
                        {
                            hasGateway = true;
                        }
                    }
                    if (!hasGateway) { continue; }
                    string d = (ni.Description ?? "") + " " + (ni.Name ?? "");
                    if (d.IndexOf("TAP", StringComparison.OrdinalIgnoreCase) < 0
                        && d.IndexOf("Wintun", StringComparison.OrdinalIgnoreCase) < 0
                        && d.IndexOf("WireGuard", StringComparison.OrdinalIgnoreCase) < 0
                        && d.IndexOf("OpenVPN", StringComparison.OrdinalIgnoreCase) < 0
                        && d.IndexOf("Sangfor", StringComparison.OrdinalIgnoreCase) < 0
                        && d.IndexOf("EasyConnect", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    try
                    {
                        // .NET Framework 的 IPv4InterfaceProperties 没有 InterfaceMetric，
                        // 接口跃点只能从 WMI 的 IPConnectionMetric 拿。
                        int idx = ni.GetIPProperties().GetIPv4Properties().Index;
                        System.Management.ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher(
                            "SELECT IPConnectionMetric FROM Win32_NetworkAdapterConfiguration WHERE InterfaceIndex=" + idx);
                        foreach (System.Management.ManagementObject mo in searcher.Get())
                        {
                            object v = mo["IPConnectionMetric"];
                            if (v != null) { metric = Convert.ToInt32(v); }
                        }
                    }
                    catch { metric = 0; }
                    return ni.Name;
                }
            }
            catch { }
            return null;
        }

        private static string GetAutoConfigUrl()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (k == null) { return null; }
                    object v = k.GetValue("AutoConfigURL");
                    if (v == null) { return null; }
                    string s = v.ToString().Trim();
                    return s.Length == 0 ? null : s;
                }
            }
            catch { return null; }
        }

        // WinHTTP 的代理是独立于 WinINet 的一套设置，浏览器正常不代表它正常。
        private static string GetWinHttpProxy()
        {
            string output;
            if (!RunTool(SysTool("netsh.exe"), "winhttp show proxy", out output)) { return "查询失败：" + output; }
            if (output.IndexOf("Direct access", StringComparison.OrdinalIgnoreCase) >= 0) { return ""; }
            return output;
        }

        private static bool AdapterDnsIsLoopback(string adapterName)
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!ni.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase)) { continue; }
                    foreach (IPAddress dns in ni.GetIPProperties().DnsAddresses)
                    {
                        if (dns.AddressFamily == AddressFamily.InterNetwork && (dns.Equals(IPAddress.Loopback) || dns.ToString().StartsWith("127."))) { return true; }
                    }
                }
            }
            catch { }
            return false;
        }

        private static void RunNetworkCheck(NetworkReport report, bool doRepair)
        {
            if (!doRepair)
            {
                RunNetworkCheckCore(report, false);
                return;
            }
            NamedGate gate;
            try { gate = new NamedGate("network-repair", 0); }
            catch (System.TimeoutException)
            {
                report.Notes.Add("其他实例正在修复网络，本实例仅检测。");
                RunNetworkCheckCore(report, false);
                return;
            }
            using (gate) RunNetworkCheckCore(report, true);
        }

        private static void RunNetworkCheckCore(NetworkReport r, bool doRepair)
        {
            // 1) 名字解析：多目标投票，单域名会被劫持 / 不可达而误判
            List<string> dnsDetail = new List<string>();
            int dnsVotes = DnsVote(dnsDetail);
            r.DnsBefore = dnsVotes * 2 > NetProbeHosts.Length;
            r.DnsAfter = r.DnsBefore;
            if (!r.DnsBefore)
            {
                r.Notes.Add("域名解析只有 " + dnsVotes + "/" + NetProbeHosts.Length + " 个目标通过（"
                    + string.Join(", ", dnsDetail.ToArray()) + "）");
            }

            // 2) 真的能上网：DNS 成功不等于能上网（解析可被劫持 / 悬空路由）
            string how;
            string probeDetail;
            r.OnlineBefore = OnlineProbe(out how, out probeDetail);
            r.OnlineAfter = r.OnlineBefore;
            r.OnlineDetail = r.OnlineBefore ? ("通过 " + how) : "HTTP / TCP 探测均失败";

            // 3) 系统代理（WinINet）：无条件检测，不只在解析失败时才看
            string proxy = GetWinInetProxy();
            r.ProxyState = (proxy == null) ? "未启用" : ("已启用 " + proxy);
            bool proxyDead = false;
            if (proxy != null)
            {
                string host = ProxyHost(proxy);
                int port = ProxyPort(proxy);
                proxyDead = IsLoopbackHost(host) && port > 0 && !PortListening(port);
                if (proxyDead)
                {
                    r.Notes.Add("系统代理 " + proxy + " 已失效（本地端口无进程监听）");
                }
            }
            if (proxyDead && doRepair)
            {
                DisableWinInetProxy();
                Func<bool> proxyGone = delegate { return GetWinInetProxy() == null; };
                if (Retest(proxyGone, 3, 500))
                {
                    proxyDead = false;
                    r.Actions.Add("系统代理指向已失效的本地端口 " + proxy + "，已关闭（复测通过）");
                }
                else
                {
                    r.Notes.Add("已尝试关闭失效的系统代理 " + proxy + "，但复测仍读到 " + GetWinInetProxy());
                }
            }

            // 4) PAC 可能需要 VPN 或公司网络。直连探测失败时保留配置。
            string pac = GetAutoConfigUrl();
            r.PacState = (pac == null) ? "未设置" : pac;
            if (pac != null && !HttpGet(pac, 2500))
                r.Notes.Add("直连探测未能访问 PAC 自动代理脚本 " + pac + "。请核对公司网络或 VPN 连接。");

            // 5) WinHTTP 代理：同样无条件检测（以前只在解析失败时顺手重置）
            string winhttp = GetWinHttpProxy();
            bool winhttpDead = false;
            if (!string.IsNullOrEmpty(winhttp))
            {
                string whHost = ProxyHost(winhttp);
                int whPort = ProxyPort(winhttp);
                winhttpDead = IsLoopbackHost(whHost) && whPort > 0 && !PortListening(whPort);
                if (winhttpDead)
                {
                    r.Notes.Add("WinHTTP 代理 " + winhttp + " 已失效");
                }
            }
            if (winhttpDead && doRepair)
            {
                string whOut;
                bool reset = RunTool(SysTool("netsh.exe"), "winhttp reset proxy", out whOut);
                Func<bool> whGone = delegate { return string.IsNullOrEmpty(GetWinHttpProxy()); };
                if (reset && Retest(whGone, 3, 500))
                {
                    winhttpDead = false;
                    r.Actions.Add("WinHTTP 代理已失效，已重置（复测通过）");
                }
                else
                {
                    r.Notes.Add("重置 WinHTTP 代理未生效：" + whOut);
                }
            }

            // 6) 悬空默认路由：能解析却打不开网页时的常见元凶，只提示不动路由表
            int vpnMetric;
            string vpn = DanglingRouteAdapter(out vpnMetric);
            if (vpn != null)
            {
                r.RouteState = vpn + " (metric=" + vpnMetric + ")";
                if (r.DnsBefore && !r.OnlineBefore)
                {
                    r.Notes.Add("虚拟网卡 " + vpn + " 上有默认网关（metric=" + vpnMetric
                        + "），加速器退出后可能残留：域名能解析但打不开网页，需手工核对路由表");
                }
            }

            // 7) 解析失败时的修复动作
            if (!r.DnsBefore)
            {
                if (doRepair)
                {
                    string out1;
                    if (RunTool(SysTool("ipconfig.exe"), "/flushdns", out out1))
                    {
                        r.Actions.Add("已刷新 DNS 缓存");
                    }
                    else
                    {
                        r.Notes.Add("刷新 DNS 缓存失败：" + out1);
                    }
                    string adapter = GetRouteAdapterName();
                    if (adapter != null && AdapterDnsIsLoopback(adapter))
                    {
                        string out2;
                        if (RunTool(SysTool("netsh.exe"), "interface ip set dns name=\"" + adapter + "\" source=dhcp", out out2))
                        {
                            r.Actions.Add("网卡 " + adapter + " 的 DNS 指向回环，已改回自动获取");
                        }
                        else
                        {
                            r.Notes.Add("网卡 " + adapter + " 的 DNS 改回自动获取失败：" + out2);
                        }
                    }
                }
                else
                {
                    r.Notes.Add("域名解析失败");
                }
            }

            // 8) 收尾复测：修复后隔 500ms 退避重试，通过了才算真的修好
            if (doRepair)
            {
                Func<bool> dnsAgain = delegate { return DnsVote(null) * 2 > NetProbeHosts.Length; };
                r.DnsAfter = Retest(dnsAgain, 3, 500);
                string how2 = "";
                string detail2 = "";
                Func<bool> onlineAgain = delegate { return OnlineProbe(out how2, out detail2); };
                r.OnlineAfter = Retest(onlineAgain, 1, 500);
                if (r.OnlineAfter)
                {
                    r.OnlineDetail = "修复后通过 " + how2;
                }
                else
                {
                    r.OnlineDetail = "修复后 HTTP / TCP 仍失败（" + detail2 + "）";
                    r.Notes.Add("修复后仍然上不了网，可能需要检查物理链路或代理软件");
                }
            }
            r.Repairable = proxyDead || winhttpDead || !r.DnsAfter;
        }

        // Everything and similar tools keep a Session 0 service alive; the process
        // pass never sees those, so they are stopped explicitly here.
        private static ServiceReport StopConfiguredService(string name, Func<int> remainingBudget,
            ConcurrentDictionary<int, TargetIdentity> sources)
        {
            var report = new ServiceReport();
            try
            {
                int left = remainingBudget();
                if (left <= 0) { throw new System.TimeoutException("总清理预算已耗尽"); }
                using (new NamedGate("service:" + name, Math.Min(500, left)))
                using (var service = new ServiceController(name))
                {
                    ServiceControllerStatus status = service.Status;
                    if (status == ServiceControllerStatus.Stopped)
                    {
                        report.NotRunning.Add(name);
                        return report;
                    }

                    bool requested = status != ServiceControllerStatus.StopPending;
                    if (requested && !RequestServiceStop(service.ServiceName, remainingBudget, sources))
                    {
                        report.NotRunning.Add(name);
                        return report;
                    }

                    service.Refresh();
                    while (service.Status != ServiceControllerStatus.Stopped && (left = remainingBudget()) > 0)
                    {
                        System.Threading.Thread.Sleep(Math.Min(30, left));
                        service.Refresh();
                    }
                    if (service.Status == ServiceControllerStatus.Stopped)
                    {
                        (requested ? report.Stopped : report.NotRunning).Add(name);
                    }
                    else
                    {
                        report.Failed.Add(new ServiceFailure(name, "停止超时，尚未确认停止"));
                    }
                }
            }
            catch (System.TimeoutException ex)
            {
                report.Failed.Add(new ServiceFailure(name, ex.Message, remainingBudget() <= 0));
            }
            catch (Exception ex)
            {
                report.Failed.Add(new ServiceFailure(name, ex.Message));
            }
            return report;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeServiceStatus
        {
            public uint Type, State, AcceptedControls, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(IntPtr service, uint control, out NativeServiceStatus status);
        [DllImport("advapi32.dll")]
        private static extern bool CloseServiceHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeServiceStatusProcess
        {
            public NativeServiceStatus Status;
            public uint ProcessId, Flags;
        }
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr service, int level, out NativeServiceStatusProcess status,
            int size, out int needed);

        private static bool RequestServiceStop(string name, Func<int> remainingBudget,
            ConcurrentDictionary<int, TargetIdentity> sources)
        {
            const uint managerConnect = 0x0001, queryStatus = 0x0004, serviceStop = 0x0020, stopControl = 0x0001;
            const int dependentServicesRunning = 1051, serviceNotActive = 1062;
            IntPtr manager = OpenSCManager(null, null, managerConnect);
            if (manager == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                IntPtr service = OpenService(manager, name, serviceStop | queryStatus);
                if (service == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                try
                {
                    const uint processType = 0x30, ownProcess = 0x10, running = 4;
                    NativeServiceStatusProcess process;
                    int needed;
                    if (QueryServiceStatusEx(service, 0, out process, Marshal.SizeOf(typeof(NativeServiceStatusProcess)), out needed)
                        && (process.Status.Type & processType) == ownProcess
                        && process.Status.State == running && process.ProcessId != 0)
                    {
                        int pid = (int)process.ProcessId;
                        long? started = NativeIdentity.ReadStarted(pid);
                        if (started.GetValueOrDefault() > 0)
                        {
                            sources.TryAdd(pid, new TargetIdentity(pid, started.Value));
                        }
                    }
                    if (remainingBudget() <= 0) { throw new System.TimeoutException("总清理预算已耗尽"); }
                    NativeServiceStatus status;
                    if (ControlService(service, stopControl, out status)) return true;
                    int error = Marshal.GetLastWin32Error();
                    if (error == serviceNotActive) return false;
                    if (error == dependentServicesRunning)
                        throw new InvalidOperationException("有其他正在运行的服务依赖此服务，已保留。");
                    throw new System.ComponentModel.Win32Exception(error);
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        private static List<string> GetRunningConfiguredServices()
        {
            List<string> running = new List<string>();
            foreach (string raw in stopServiceNames)
            {
                if (raw == null) { continue; }
                string name = raw.Trim();
                if (name.Length == 0) { continue; }
                try
                {
                    using (var sc = new ServiceController(name))
                        if (sc.Status != ServiceControllerStatus.Stopped) running.Add(name);
                }
                catch { }
            }
            return running;
        }

        private static void WriteDefaultKeep()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"system\": [");
            sb.AppendLine("    \"csrss\", \"wininit\", \"winlogon\", \"services\", \"lsass\", \"smss\", \"svchost\", \"fontdrvhost\",");
            sb.AppendLine("    \"dwm\", \"explorer\", \"sihost\", \"ctfmon\", \"TextInputHost\", \"ShellExperienceHost\",");
            sb.AppendLine("    \"StartMenuExperienceHost\", \"SearchHost\", \"ShellHost\", \"RuntimeBroker\",");
            sb.AppendLine("    \"ApplicationFrameHost\", \"taskhostw\", \"DllHost\", \"smartscreen\", \"WmiPrvSE\", \"audiodg\",");
            sb.AppendLine("    \"Registry\", \"Memory Compression\", \"Secure System\", \"System\", \"Idle\",");
            sb.AppendLine("    \"SecurityHealthSystray\", \"SecurityHealthService\"");
            sb.AppendLine("  ],");
            sb.AppendLine("  \"user\": [");
            sb.AppendLine("    \"ghelper\", \"RtkAudUService64\", \"TranslucentTB\", \"CrossDeviceResume\", \"Pogget\",");
            sb.AppendLine("    \"nvcontainer\", \"NVDisplay.Container\", \"NVIDIA Overlay\", \"nvsphelper64\",");
            sb.AppendLine("    \"nvfvsdksvc_x64\", \"nvrla\", \"FvContainer\"");
            sb.AppendLine("  ],");
            sb.AppendLine("  \"services\": [");
            sb.AppendLine("    \"Everything\"");
            sb.AppendLine("  ],");
            sb.AppendLine("  \"graceful\": [");
            sb.AppendLine("    \"Kuajiasu\", \"KuaijiasuCore\", \"KuaijiasuHelperService\"");
            sb.AppendLine("  ],");
            sb.AppendLine("  \"networkRepair\": true");
            sb.AppendLine("}");
            using (var file = new FileStream(keepPath, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(file, new UTF8Encoding(true)))
                writer.Write(sb.ToString());
        }

        private static string InstallShortcut()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string lnk = Path.Combine(desktop, "killer.lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type scType = shortcut.GetType();
            scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath });
            scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { baseDir });
            scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "结束所有非刚开机状态的后台进程" });
            scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath + ",0" });
            scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            return "已创建桌面快捷方式 killer：\r\n" + lnk;
        }

        private static void WriteLog(string mode, bool dryRun, List<ProcInfo> plan, List<ProcInfo> killed,
                                     List<ProcInfo> failed, List<ProcInfo> selfExited, List<ProcInfo> remaining,
                                     ServiceReport services,
                                     NetworkReport net, MemorySnapshot memBefore, MemorySnapshot memAfter,
                                     double freedMb, double elapsed, PhaseTrace phases = null,
                                     List<string> killCosts = null, bool degraded = false)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | mode=" + mode
                + " | elevated=" + IsElevated() + " | elapsed=" + elapsed + "s"
                + (phases == null || phases.ToLogString().Length == 0 ? "" : " | phases=" + phases.ToLogString())
                + (degraded ? " | degraded=1 reason=budget" : "") + " ===");
            if (killCosts != null)
            {
                foreach (string cost in killCosts) { sb.AppendLine(cost); }
            }
            sb.AppendLine("keep: system=" + keepSystem.Count + " entries, user=" + keepUser.Count + " entries");
            sb.AppendLine("runId: "+runId);
            sb.AppendLine("protected: " + protectedPids.Count + " ancestor pid(s)"
                + (forceUnprotected ? " (--force: 保护已关闭)" : ""));

            if (memBefore != null)
            {
                sb.AppendLine("memory before: total=" + FmtGB(memBefore.TotalMB) + " used=" + FmtGB(memBefore.UsedMB)
                    + " avail=" + FmtGB(memBefore.AvailMB) + " load=" + memBefore.LoadPercent + "%"
                    + " commit=" + FmtGB(memBefore.CommitMB) + "/" + FmtGB(memBefore.CommitLimitMB)
                    + " sessionWS=" + FmtGB(memBefore.SessionWsMB)
                    + " gpu=" + (memBefore.GpuTotalMB > 0 ? FmtMiB(memBefore.GpuUsedMB) + "/" + FmtMiB(memBefore.GpuTotalMB) : "未采样"));
            }

            sb.AppendLine(NetworkLogLine(net));
            if (memAfter != null)
            {
                sb.AppendLine("memory after : total=" + FmtGB(memAfter.TotalMB) + " used=" + FmtGB(memAfter.UsedMB)
                    + " avail=" + FmtGB(memAfter.AvailMB) + " load=" + memAfter.LoadPercent + "%"
                    + " commit=" + FmtGB(memAfter.CommitMB) + "/" + FmtGB(memAfter.CommitLimitMB)
                    + " sessionWS=" + FmtGB(memAfter.SessionWsMB)
                    + " gpu=" + (memAfter.GpuTotalMB > 0 ? FmtMiB(memAfter.GpuUsedMB) + "/" + FmtMiB(memAfter.GpuTotalMB) : "未采样"));
                sb.AppendLine("memory delta : used=" + FmtDelta(memAfter.UsedMB - memBefore.UsedMB)
                    + " avail=" + FmtDelta(memAfter.AvailMB - memBefore.AvailMB)
                    + " load=" + FmtDeltaPercent(memAfter.LoadPercent - memBefore.LoadPercent)
                    + " commit=" + FmtDelta(memAfter.CommitMB - memBefore.CommitMB)
                    + " sessionWS=" + FmtDelta(memAfter.SessionWsMB - memBefore.SessionWsMB)
                    + " gpu=" + (memBefore.GpuTotalMB > 0 && memAfter.GpuTotalMB > 0
                        ? FmtDeltaMiB(memAfter.GpuUsedMB - memBefore.GpuUsedMB) : "未采样"));
            }

            if (dryRun)
            {
                sb.AppendLine("plan: " + plan.Count + " process(es) would be killed, total " + Math.Round(plan.Sum(x => x.WsMb), 1) + " MB");
                foreach (ProcInfo p in plan)
                {
                    sb.AppendLine("  - " + Format(p));
                }
                if (services.Planned.Count > 0)
                {
                    sb.AppendLine("plan: " + services.Planned.Count + " service(s) would be stopped");
                    foreach (string s in services.Planned) { sb.AppendLine("  - service " + s); }
                }
            }
            else if (mode == "run")
            {
                // 三套口径别读混（docs/KNOWN-ISSUES.md 第 10 条）：
                //   killed 行的 freed  = 被结束进程"自身"工作集之和（WS 累加，不是真实释放）
                //   memory delta       = 清理前后整机物理内存实测差（最接近"真释放了多少"）
                //   trim 记录修剪目标的工作集降幅，memory delta 包括修剪后的整机变化
                sb.AppendLine("killed: " + killed.Count + " process(es), freed " + Math.Round(freedMb, 1) + " MB");
                sb.AppendLine("  (口径: freed = 被结束进程自身 WS 累加, 非实测释放; 实测释放看下面的 memory delta)");
                foreach (ProcInfo p in killed)
                {
                    sb.AppendLine("  - " + Format(p)
                        + (string.IsNullOrEmpty(p.Note) ? "" : " via=" + p.Note));
                }
                if (selfExited.Count > 0)
                {
                    // These were never killed by us: they were gone before we got to
                    // them (the plan is built a moment before the killing starts).
                    sb.AppendLine("exited: " + selfExited.Count + " process(es) 已自行退出（计划生成之后）");
                    foreach (ProcInfo p in selfExited)
                    {
                        sb.AppendLine("  - " + Format(p) + " reason=" + p.FailReason);
                    }
                }
                if (failed.Count > 0)
                {
                    sb.AppendLine("failed: " + failed.Count + " process(es)");
                    foreach (ProcInfo p in failed)
                    {
                        sb.AppendLine("  - " + Format(p) + " reason=" + p.FailReason);
                    }
                }
                if (services.Stopped.Count > 0)
                {
                    sb.AppendLine("services stopped: " + services.Stopped.Count);
                    foreach (string s in services.Stopped) { sb.AppendLine("  - service " + s); }
                }
                if (services.NotRunning.Count > 0)
                {
                    sb.AppendLine("services already stopped: " + string.Join(", ", services.NotRunning.ToArray()));
                }
                if (services.Failed.Count > 0)
                {
                    sb.AppendLine("services failed: " + string.Join(" | ", services.Failed.Select(service => service.ToString())));
                }
                if (trimFreedMb > 0)
                {
                    sb.AppendLine("trim: " + trimCount + " remaining candidate process(es) working set trimmed, freed "
                        + trimFreedMb + " MB (未结束进程)");
                }
                sb.AppendLine("remaining non-keep processes: " + remaining.Count);
                foreach (ProcInfo p in remaining)
                {
                    sb.AppendLine("  - " + Format(p));
                }
            }
            sb.AppendLine();
            AppendLogBlock(sb.ToString());
        }

        private static void AddKillCost(List<string> costs, ProcInfo c, string via, Stopwatch clock)
        {
            if (costs == null || c == null || clock == null) { return; }
            clock.Stop();
            AddKillCostMs(costs, c, via, clock.ElapsedMilliseconds);
        }

        private static void AddKillCostMs(List<string> costs, ProcInfo c, string via, long elapsedMs)
        {
            if (costs == null || c == null) { return; }
            string line = "killcost: pid=" + c.Pid + " name=" + c.Name + " via=" + via + " ms=" + elapsedMs;
            lock (costs) { costs.Add(line); }
        }

        // 日志写入：纯追加 + 块感知截断（docs/KNOWN-ISSUES.md：kill.log 仍是"全量读-
        // 改-写 + 保留最近 20 段"）。
        //   1. 正常路径只 AppendAllText：不再每次运行把整个日志读进来再重写一遍；
        //   2. 只有日志超过 MaxLogBytes 时才做一次截断，且只读文件尾部 TailWindowBytes
        //      这么多字节，不会把几十 MB 的日志整个拉进内存；
        //   3. 截断按块（"=== " 开头）切，只保留最近 MaxLogBlocks 段，切点若落在某段
        //      中间，从该段结尾之后的第一个块头开始，宁可多留也不留半段。
        private const int MaxLogBlocks = 20;
        private const long MaxLogBytes = 512 * 1024;
        private const int TailWindowBytes = 4 * 1024 * 1024;
        private static readonly object logLock = new object();

        private static void AppendLogBlock(string text)
        {
            AppendLogBlock(text, logPath);
        }

        // 可传路径的版本：离线单测用临时文件直接跑，不需要提权（bench\keeptest.cs）。
        public static void AppendLogBlock(string text, string path)
        {
            lock (logLock)
            using(new NamedGate("file:"+Path.GetFullPath(path),2000))
            {
                try
                {
                    if(text!=null&&!text.Contains("runId: ")){int line=text.IndexOf('\n');if(line>=0)text=text.Insert(line+1,"runId: "+runId+Environment.NewLine);}
                    File.AppendAllText(path, text ?? "", new UTF8Encoding(true));
                }
                catch { return; }

                try
                {
                    FileInfo fi = new FileInfo(path);
                    if (!fi.Exists || fi.Length <= MaxLogBytes) { return; }
                    TrimLogFile(path, (int)Math.Min(fi.Length, TailWindowBytes));
                }
                catch { }
            }
        }

        // 只处理文件尾部 tailBytes 字节；输出仍是"块头开头"的完整若干段。
        private static void TrimLogFile(int tailBytes)
        {
            TrimLogFile(logPath, tailBytes);
        }

        public static void TrimLogFile(string path, int tailBytes)
        {
            using(new NamedGate("file:"+Path.GetFullPath(path),2000))TrimLogFileCore(path,tailBytes);
        }
        private static void TrimLogFileCore(string path,int tailBytes)
        {
            string text;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long start = Math.Max(0, fs.Length - tailBytes);
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buf = new byte[fs.Length - start];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = fs.Read(buf, read, buf.Length - read);
                    if (n <= 0) { break; }
                    read += n;
                }
                text = new UTF8Encoding(true).GetString(buf, 0, read);
            }
            if (text.Length == 0) { return; }

            int firstBlock = text.IndexOf("=== ", StringComparison.Ordinal);
            if (firstBlock < 0) { return; }         // 没有块头（空文件 / 异常内容）：不动
            string window = text.Substring(firstBlock);

            List<string> blocks = new List<string>();
            StringBuilder cur = new StringBuilder();
            foreach (string line in window.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith("=== ", StringComparison.Ordinal) && cur.Length > 0)
                {
                    blocks.Add(cur.ToString());
                    cur = new StringBuilder();
                }
                cur.AppendLine(line);
            }
            if (cur.Length > 0) { blocks.Add(cur.ToString()); }
            if (blocks.Count <= MaxLogBlocks) { return; }
            blocks = blocks.Skip(blocks.Count - MaxLogBlocks).ToList();
            File.WriteAllText(path, string.Join("", blocks.ToArray()), new UTF8Encoding(true));
        }

        private static string Format(ProcInfo p)
        {
            string path = string.IsNullOrEmpty(p.Path) ? "(path n/a)" : p.Path;
            string aumid = string.IsNullOrEmpty(p.Aumid) ? "" : " aumid=" + p.Aumid;
            return p.Name + " (PID " + p.Pid + ", " + p.WsMb + " MB) " + path + aumid;
        }

        private static void AddCell(TableLayoutPanel table, string text, int row, int col, Font font, Color color, ContentAlignment align)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = font;
            l.ForeColor = color;
            l.TextAlign = align;
            l.Dock = DockStyle.Fill;
            l.AutoSize = false;
            table.Controls.Add(l, col, row);
        }

        private static Color SavedColor(double delta, bool lowerIsBetter)
        {
            if (Math.Abs(delta) < 50.0) { return Theme.Muted; }   // ±50 MB noise floor
            bool good = lowerIsBetter ? (delta < 0) : (delta > 0);
            return good ? Theme.Ok : Theme.Danger;
        }

        private static string NarrowPair(string before, string after, bool hasAfter, bool micro)
        {
            if (!hasAfter) { return before; }
            return before + (micro ? "\r\n→ " : " → ") + after;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

        // "Show animations in Windows" (SPI_GETCLIENTAREAANIMATION) — the desktop
        // equivalent of prefers-reduced-motion; when off, no intro motion runs.
        internal static bool AnimationsEnabled()
        {
            try
            {
                int v = 1;
                if (SystemParametersInfo(0x1042u, 0u, ref v, 0u)) { return v != 0; }
            }
            catch { }
            return true;
        }

        private static Label MakeLabel(Control parent, string text, float pt, FontStyle style,
                                       Color fore, Color back, ContentAlignment align)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font(Theme.UiFont, pt, style);
            l.ForeColor = fore;
            l.BackColor = back;
            l.AutoSize = false;
            l.AutoEllipsis = true;
            l.TextAlign = align;
            parent.Controls.Add(l);
            return l;
        }

        // Dark, 8px-grid result window: header · memory hero · detail table ·
        // network/toast line · process list pinned to the bottom · footer actions.
        private static Form BuildResultForm(bool dryRun, List<ProcInfo> killed, List<ProcInfo> failed,
                                            List<ProcInfo> selfExited, List<ProcInfo> plan,
                                            ServiceReport services, NetworkReport net, MemorySnapshot memBefore,
                                            MemorySnapshot memAfter, double freedMb, double elapsed)
        {
            return BuildUiShell(dryRun, killed, failed, selfExited, plan, services, net,
                                memBefore, memAfter, freedMb, elapsed, null).Form;
        }

        // 清理中窗口的实时状态：清理循环往里写，UI 的 fillLive 读。
        private sealed class UiLiveState
        {
            public bool Preparing,UnknownPhase,Failed;
            public int PlanCount;
            public double PlanTotalMb;
            public int Killed;
            public int SelfExited, Processed;
            public double KilledMb;
            public string Action = "正在准备…";
            public string LiveStatsOverride = "";   // 非空则顶掉底部那行统计（复查节奏等）
            public double Pct;
            // 缓动：目标值由"计数进度 + 当前动作内插值"合成，显示值向目标指数逼近且只增不减
            public double ActionPctTarget;
            public double ActionPctShown;

            public double RingTarget;
            public double RingShown;

            public bool LayoutOnce = true;           // 清理中的布局只算一次
            public bool LayoutDone = false;
            public DateTime LastEaseAt = DateTime.Now;
            public double ActiveSub = -1;            // 当前动作内插值 0..1；<0 = 按时间自动爬
            public DateTime ActiveSince = DateTime.Now;
            public double PhaseStart = -1;           // 阶段爬升（没有计数进度时用）：-1 = 关
            public double PhaseCeil = -1;
            public DateTime PhaseSince = DateTime.Now;
            public string AvailText = "";
            public string MemStats = "";            // 后台采样线程给的「已用 / 提交 / GPU」
            public DateTime LastSnapAt = DateTime.MinValue;
            public SnapshotSampler Sampler;
            public List<ProcRow> Rows = new List<ProcRow>();
            public ProcRow Active;
            public DateTime StartedAt = DateTime.Now;
            // 结果态要用的最终值：net / memAfter 在清理里会被重新赋值，收尾时同步进来
            public NetworkReport Net = new NetworkReport();
            public MemorySnapshot MemBefore;
            public MemorySnapshot MemAfter;
            public double FreedMb;
            public double Elapsed;
            public int TrimCount;
            public double TrimFreedMb;
            public bool Degraded;
            public Form Form;
            public Action Refresh;
            public Action RefreshSummary;
            public Action<ProcRow> AddRow;
            public Action<ProcRow> RefreshRow;
            public Action<double> FilmSample;
            public long LastProgressPostTicks;
        }

        // 进度界面读取后台采样发布的快照，显存数据就绪后随快照发布。
        private sealed class SnapshotSampler
        {
            private volatile MemorySnapshot latest;
            private volatile bool stop;
            private System.Threading.Thread th;
            private readonly Task<GpuSample> gpuProbe;

            public SnapshotSampler(MemorySnapshot seed, Task<GpuSample> gpuProbe)
            {
                latest = seed;
                this.gpuProbe = gpuProbe;
            }

            public MemorySnapshot Latest { get { return latest; } }

            public void Start()
            {
                stop = false;
                th = new System.Threading.Thread(delegate()
                {
                    while (!stop)
                    {
                        try
                        {
                            MemorySnapshot s = TakeSnapshot();
                            if (gpuProbe.Status == TaskStatus.RanToCompletion) { s.ApplyGpu(gpuProbe.Result); }
                            latest = s;
                        }
                        catch { }
                        for (int i = 0; i < 30 && !stop; i++)
                        {
                            try { System.Threading.Thread.Sleep(100); }
                            catch { }
                        }
                    }
                });
                th.IsBackground = true;
                try { th.Start(); }
                catch { }
            }

            public void Stop()
            {
                stop = true;
            }
        }

        // 单线程 + DoEvents：清理期间让窗口能重绘、能点按钮。够这个用途，不引入线程同步。
        private static void PumpUi()
        {
            try
            {
                if(!workerInvocation)Application.DoEvents();
            }
            catch { }
        }

        // 清理期间的"心跳"：谁在等，谁就把消息循环让出去。30ms 的缓动定时器全靠它走格，
        // 一刀切的 Thread.Sleep 会让进度条"一顿一顿"。没开窗（--silent / 截图 / 预演）
        // 时退化成普通 Sleep。
        private static Action uiHeartbeat;

        private static void UiPump(int ms)
        {
            Action hb = uiHeartbeat;
            if (hb == null)
            {
                if (ms > 0) { try { System.Threading.Thread.Sleep(ms); } catch { } }
                return;
            }
            if (ms <= 0)
            {
                try { hb(); } catch { }
                return;
            }
            if (ms <= 16)
            {
                try { hb(); } catch { }
                try { System.Threading.Thread.Sleep(ms); } catch { }
                try { hb(); } catch { }
                return;
            }
            int waited = 0;
            while (waited < ms)
            {
                int step = ms - waited;
                if (step > 25) { step = 25; }
                try { hb(); } catch { }
                try { System.Threading.Thread.Sleep(step); } catch { }
                waited += step;
            }
        }

        // 把一个进程"开始处理"推到窗口上：清单首行插一条「正在结束」。
        private static void DispatchLive(UiLiveState live, Action action)
        {
            if (live == null || action == null) { return; }
            Form form = live.Form;
            if (form == null || form.IsDisposed) { return; }
            try
            {
                if (form.InvokeRequired) { form.BeginInvoke(action); }
                else { action(); }
            }
            catch { }
        }

        private static ProcRow LiveBegin(UiLiveState live, ProcInfo c)
        {
            ProcRow row = new ProcRow();
            row.State = "正在结束";
            row.StateColor = Theme.Primary;
            row.Name = c.Name;
            row.IconPath = c.Path ?? "";
            row.Memory = c.WsMb.ToString("0.0");
            row.Path = "整树 " + c.WsMb.ToString("0") + " MB · pid " + c.Pid;
            if (live == null) { return row; }
            DispatchLive(live, delegate
            {
                live.UnknownPhase=false;
                live.Active = row;
                live.Rows.Insert(0, row);
                live.Action = "正在结束 " + c.Name + " #" + c.Pid + "（整树 " + c.WsMb.ToString("0") + " MB）";
                live.ActiveSub = -1;
                live.ActiveSince = DateTime.Now;
                live.PhaseStart = -1;
                live.PhaseCeil = -1;
                if (live.AddRow != null) { live.AddRow(row); }
                else if (live.Refresh != null) { live.Refresh(); }
                if (live.RefreshSummary != null) { live.RefreshSummary(); }
            });
            return row;
        }

        // 该进程的结局上屏：kind 0=自行退出 1=已结束 2=失败
        private static void LiveSettle(UiLiveState live, ProcInfo c, string state, Color color, string pathNote,
                                       int kind, ProcRow row = null)
        {
            if (live == null) { return; }
            double settledMb=c.WsMb;
            DispatchLive(live, delegate
            {
                ProcRow target = row ?? live.Active;
                if (target != null)
                {
                    target.State = state;
                    target.StateColor = color;
                    if (!string.IsNullOrEmpty(pathNote)) { target.Path = pathNote; }
                    if (live.Active == target) { live.Active = null; }
                    if (live.RefreshRow != null) { live.RefreshRow(target); }
                }
                live.Processed++;
                if (kind == 1) { live.Killed++; live.KilledMb += settledMb; }
                else if (kind == 0) { live.SelfExited++; }
                if (live.Active == target)
                {
                    live.ActiveSub = -1;
                    live.ActiveSince = DateTime.Now;
                }
                if (live.RefreshSummary != null) { live.RefreshSummary(); }
                else if (live.Refresh != null) { live.Refresh(); }
            });
        }

        private static void LiveAction(UiLiveState live, string action, double activeSub)
        {
            if (live == null) { return; }
            long now = DateTime.UtcNow.Ticks;
            while (true)
            {
                long previous = System.Threading.Interlocked.Read(ref live.LastProgressPostTicks);
                if (now - previous < TimeSpan.TicksPerMillisecond * 40) { return; }
                if (System.Threading.Interlocked.CompareExchange(ref live.LastProgressPostTicks, now, previous) == previous) { break; }
            }
            DispatchLive(live, delegate
            {
                live.Action = action;
                live.ActiveSub = activeSub;
                live.ActiveSince = DateTime.Now;
                if (live.RefreshSummary != null) { live.RefreshSummary(); }
            });
        }

        private struct ExitRequest
        {
            internal readonly ProcInfo Target;
            internal readonly IntPtr Handle;
            internal readonly string Method;

            internal ExitRequest(ProcInfo target, IntPtr handle, string method)
            {
                Target = target;
                Handle = handle;
                Method = method;
            }
        }

        private static bool ProcessExitSignaled(ExitRequest request, int waitMs)
        {
            return WaitForSingleObject(request.Handle, (uint)waitMs) == 0;
        }

        internal static void ConfirmExitRequests<T>(List<T> pending, Func<int> remainingBudget,
            Func<T, int, bool> waitForExit, Action<T> confirmed)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                T request = pending[i];
                if (!waitForExit(request, remainingBudget())) { continue; }
                pending.RemoveAt(i);
                confirmed(request);
            }
        }

        // 阶段切换（停服务 / 复查 / 温和回收）时换动作文案。
        private static void LivePhase(UiLiveState live,string action)
        {
            if(live==null)return;
            var snapshot=new ProgressValue(action,0,0,false);
            DispatchLive(live,delegate{live.Action=snapshot.Stage;live.UnknownPhase=true;live.Refresh();});
        }
        private static void LivePhaseRamp(UiLiveState live,string action,double startPct,double ceilPct){LivePhase(live,action);}

        // 清理中窗口 → 结果窗口 是同一个 Form：先按 live 态开窗，跑完调用
        // FillResult() 原地切成结果态（第三步 双窗合一）。
        private sealed class UiShell
        {
            public Form Form;
            public ProcList List;
            public HeroCard Hero;
            public Action FillResult;
            public Action<NetworkReport> UpdateNetwork;
            public Action LayoutUi;
            public Action<List<ProcInfo>,List<ProcInfo>,List<ProcInfo>,List<ProcInfo>,ServiceReport,NetworkReport,MemorySnapshot> Rebind;
        }

        private static Button PageTab(string name, string label)
        {
            var button = new Button { Name = name, Text = label, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Theme.Bg;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private static void SetPageTabState(Button button, bool selected)
        {
            button.BackColor = selected ? Theme.Bg : Theme.Surface;
            button.ForeColor = selected ? Theme.Primary : Theme.Text2;
            button.AccessibleDescription = selected ? "当前页面" : "切换页面";
        }

        private static KeepPage CreateKeepPage(Action back, bool preview = false)
        {
            return new KeepPage(ReadRunningKeepProcesses,
                delegate
                {
                    if (preview && !File.Exists(keepPath))
                        return new KeepPageConfig
                        {
                            System = new List<string>(DefaultSystem), User = new List<string>(DefaultUser),
                            Services = new List<string>(DefaultServices), Graceful = new List<string>(DefaultGraceful)
                        };

                    KeepConfig config = ReadEffectiveKeep();
                    return new KeepPageConfig
                    {
                        System = config.system, User = config.user,
                        Services = config.services, Graceful = config.graceful
                    };
                },
                delegate(string group, string rule, bool add) { return KeepRuleStore.Change(keepPath, group, rule, add); },
                back);
        }

        private static UiShell BuildUiShell(bool dryRun, List<ProcInfo> killed, List<ProcInfo> failed,
                                            List<ProcInfo> selfExited, List<ProcInfo> plan,
                                            ServiceReport services, NetworkReport net, MemorySnapshot memBefore,
                                            MemorySnapshot memAfter, double freedMb, double elapsed,
                                            UiLiveState live)
        {
            float sc = 1F;
            try
            {
                using (Graphics gdc = Graphics.FromHwnd(IntPtr.Zero)) { sc = gdc.DpiX / 96F; }
            }
            catch { }
            if (sc <= 0F) { sc = 1F; }
            Func<float, int> S = delegate(float v) { return (int)Math.Round(v * sc); };

            int designW = uiWidthOverride > 0 ? uiWidthOverride : 1040;
            int designH = uiHeightOverride > 0 ? uiHeightOverride : 760;
            bool narrow = designW < 1000;
            bool compact = designW < 900;
            bool micro = designW <= 390;
            bool tiny = designW < 560;

            Form f = new DpiAwareForm();
            f.Text = dryRun ? "killer · 预演" : "killer · 清理结果";
            f.AutoScaleMode = AutoScaleMode.None;
            f.BackColor = Theme.Bg;
            f.ForeColor = Theme.Text;
            f.Font = new Font(Theme.UiFont, 10.5F);
            f.MinimumSize = new Size(S(360F), S(420F));
            if (uiWidthOverride > 0 || uiHeightOverride > 0)
            {
                f.MinimumSize = new Size(S(320F), S(360F));   // allow the narrow verification shots
            }

            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int winW;
            int winH;
            if (uiWidthOverride > 0 || uiHeightOverride > 0)
            {
                winW = S(designW);
                winH = S(designH);
            }
            else
            {
                winW = Math.Min(S(designW), Math.Max(S(360F), wa.Width - S(32F)));
                winH = Math.Min(S(designH), Math.Max(S(420F), wa.Height - S(32F)));
            }
            f.ClientSize = new Size(winW, winH);
            f.StartPosition = FormStartPosition.CenterScreen;

            Panel pages = new Panel { Dock = DockStyle.Fill };
            Panel resultPage = new Panel { Name = "CleanupResultPage", Dock = DockStyle.Fill };
            Panel body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            resultPage.Controls.Add(body);
            pages.Controls.Add(resultPage);
            f.Controls.Add(pages);

            Panel navigation = new Panel { Dock = DockStyle.Top, BackColor = Theme.Surface, Visible = live == null };
            Button resultTab = PageTab("CleanupResultTab", "清理结果");
            Button keepTab = PageTab("KeepPageTab", "白名单");
            navigation.Controls.Add(resultTab);
            navigation.Controls.Add(keepTab);
            Label separator = new Label { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line };
            navigation.Controls.Add(separator);
            f.Controls.Add(navigation);
            navigation.SendToBack();
            KeepPage keepPage = null;

            Action<bool> selectPage = delegate(bool whitelist)
            {
                if (whitelist && keepPage == null)
                {
                    keepPage = CreateKeepPage(null);
                    keepPage.Dock = DockStyle.Fill;
                    keepPage.Visible = false;
                    pages.Controls.Add(keepPage);
                }

                resultPage.Visible = !whitelist;
                if (keepPage != null) keepPage.Visible = whitelist;
                SetPageTabState(resultTab, !whitelist);
                SetPageTabState(keepTab, whitelist);
                if (whitelist)
                {
                    keepPage.SetScale(((DpiAwareForm)f).UiScale);
                    keepPage.RefreshPage();
                }
            };

            keepTab.Click += delegate { if (keepPage == null || !keepPage.Visible) selectPage(true); };
            SetPageTabState(resultTab, true);
            SetPageTabState(keepTab, false);

            // ---------- header: mark + mode + network state ----------
            LogoBox logo = new LogoBox();
            logo.UiScale = sc;
            logo.BackColor = Theme.Bg;
            body.Controls.Add(logo);

            Label lTitle = MakeLabel(body, "killer", 18F, FontStyle.Bold, Theme.Text, Theme.Bg, ContentAlignment.MiddleLeft);
            Label lSub = MakeLabel(body, dryRun ? "预览模式" : "清理完成",
                                   9F, FontStyle.Regular, Theme.Muted, Theme.Bg, ContentAlignment.MiddleLeft);
            Label lStatus = MakeLabel(body, "", 9F, FontStyle.Regular, Theme.Muted, Theme.Bg, ContentAlignment.MiddleRight);
            lStatus.Name = "CleanupSummary";
            lStatus.AutoSize = false;
            lStatus.AutoEllipsis = true;

            StatusChip chip = new StatusChip();
            chip.UiScale = sc;
            chip.BackColor = Theme.Bg;
            body.Controls.Add(chip);


            // ---------- Hero: usage context + released-memory figure ----------
            HeroCard hero = new HeroCard();
            hero.UiScale = sc;
            hero.BackColor = Theme.Bg;
            body.Controls.Add(hero);

            // ---------- memory detail table ----------
            MemTable table = new MemTable();
            table.UiScale = sc;
            table.BackColor = Theme.Bg;
            table.Narrow = narrow;
            table.Micro = micro;
            body.Controls.Add(table);
            // 明细行由 fillResult 填（清理中/结果态共用同一批控件）

            // ---------- network + toast line ----------
            CardPanel statusCard = new CardPanel();
            statusCard.UiScale = sc;
            statusCard.BackColor = Theme.Bg;
            body.Controls.Add(statusCard);
            Label lNet = MakeLabel(statusCard, "", 9F, FontStyle.Regular, Theme.Muted, Theme.Surface, ContentAlignment.MiddleLeft);
            lNet.Name = "NetworkDetails";
            Label lToast = MakeLabel(statusCard, "", 9F, FontStyle.Regular, Theme.Ok, Theme.Surface, ContentAlignment.MiddleRight);
            lToast.Name = "ResultNotice";
            FlatButton btnRepair = new FlatButton
            {
                Name = "RepairNetwork", Text = "修复网络…", BusyText = "正在修复…",
                Kind = FlatButton.Style.Secondary, BackColor = Theme.Surface, Visible = false
            };
            statusCard.Controls.Add(btnRepair);

            // ---------- process list ----------
            ProcList list = new ProcList();
            list.UiScale = sc;
            list.BackColor = Theme.Bg;
            list.ShowPath = designW >= 820;
            list.RowHeight = designW < 760 ? 27 : (designW < 900 ? 30 : 32);
            body.Controls.Add(list);

            // 列表行由 fillResult / fillLive 填（清理中逐条追加，结束后整体重填）


            // ---------- footer ----------
            FlatButton btnReopen = new FlatButton();
            btnReopen.UiScale = sc;
            btnReopen.Kind = FlatButton.Style.Primary;
            btnReopen.Text = "重开刚才关掉的程序";
            btnReopen.BusyText = "正在重开…";
            btnReopen.BackColor = Theme.Surface;
            btnReopen.Enabled = (!dryRun && (killed.Count > 0 || services.Stopped.Count > 0));
            body.Controls.Add(btnReopen);

            FlatButton btnLog = new FlatButton();
            btnLog.UiScale = sc;
            btnLog.Kind = FlatButton.Style.Secondary;
            btnLog.Text = "打开日志";
            btnLog.BackColor = Theme.Surface;
            body.Controls.Add(btnLog);

            FlatButton btnClose = new FlatButton();
            btnClose.UiScale = sc;
            btnClose.Kind = FlatButton.Style.Ghost;
            btnClose.Text = "关闭";
            btnClose.BackColor = Theme.Surface;
            body.Controls.Add(btnClose);

            FlatButton btnKeep = new FlatButton();
            btnKeep.UiScale = sc;
            btnKeep.Kind = FlatButton.Style.Secondary;
            btnKeep.Text = "保留勾选的进程";
            btnKeep.BackColor = Theme.Surface;
            btnKeep.Visible = dryRun && plan.Count > 0;
            body.Controls.Add(btnKeep);

            Panel footer = new Panel { Dock = DockStyle.Bottom, BackColor = Theme.Surface };
            resultPage.Controls.Add(footer);
            footer.SendToBack();
            foreach (Control control in new Control[] { btnReopen, btnLog, btnClose, btnKeep })
                footer.Controls.Add(control);
            body.AutoScroll=true;
            bool detailsExpanded=false, detailsReady=live==null;
            NetworkReport shownNetwork = net;
            bool resultHasAttention = false;
            bool repairingNetwork = false;
            Func<bool> canRepairNetwork = () => !dryRun && networkRepair &&
                shownNetwork.State == NetworkCheckState.Completed && shownNetwork.Repairable;
            FlatButton details=new FlatButton();details.Name="DetailsToggle";details.Kind=FlatButton.Style.Ghost;details.UiScale=sc;details.BackColor=Theme.Bg;details.Text="展开内存与网络详情";body.Controls.Add(details);
            ToolTip help=new ToolTip();help.AutoPopDelay=20000;help.InitialDelay=500;
            logo.Visible=false;lTitle.Text="清理概览";if(lTitle.Font.Size!=18F){Font oldTitleFont=lTitle.Font;lTitle.Font=(Font)Theme.GetFont(18F,FontStyle.Bold,false).Clone();oldTitleFont.Dispose();}
            lSub.Visible=false;lStatus.TextAlign=ContentAlignment.MiddleLeft;
            btnReopen.Text="重新打开应用";btnKeep.Text="保留所选";
            bool layoutBusy=false;
            Action layout = delegate
            {
                if(layoutBusy)return;layoutBusy=true;body.SuspendLayout();
                try {
                sc=((DpiAwareForm)f).UiScale;
                navigation.Height = S(46);
                resultTab.SetBounds(S(16), S(6), S(102), S(32));
                keepTab.SetBounds(S(122), S(6), S(90), S(32));
                int viewportWidth=Math.Max(1,body.Width-SystemInformation.VerticalScrollBarWidth);
                float dw=viewportWidth/sc;
                narrow=dw<900;compact=dw<760;micro=dw<=420;tiny=dw<560;
                table.Narrow=narrow;table.Micro=tiny;list.ShowPath=dw>=820;list.RowHeight=36;list.UiScale=sc;
                int pad=S(tiny?16:24),gap=S(16),w=Math.Max(S(200),viewportWidth-2*pad);
                bool shortWindow=f.ClientSize.Height/sc<540;
                int y=pad+body.AutoScrollPosition.Y;
                lTitle.Visible=!shortWindow;
                lTitle.Bounds=new Rectangle(pad,y,w-(micro?0:S(150)),S(32));
                chip.Bounds=new Rectangle(viewportWidth-pad-S(140),y+S(2),S(140),S(28));chip.Visible=!micro;
                if(!shortWindow)y+=S(38);lStatus.Bounds=new Rectangle(pad,y,w,S(22));y+=S(shortWindow?26:36);
                hero.Bounds=new Rectangle(pad,y,w,S(shortWindow?(hero.Live?104:88):(hero.Live?174:148)));y=hero.Bottom+S(10);
                details.Enabled=detailsReady;details.Text=detailsExpanded?"收起内存与网络详情":"展开内存与网络详情";
                if (detailsReady && !detailsExpanded)
                {
                    if (repairingNetwork) details.Text += " · 修复中";
                    else if (shownNetwork.State == NetworkCheckState.Pending) details.Text += " · 检测中";
                    else if (shownNetwork.State == NetworkCheckState.Incomplete) details.Text += " · 检测未完成";
                    else if (canRepairNetwork()) details.Text += " · 待修复";
                    else if (shownNetwork.State == NetworkCheckState.Completed &&
                        !(dryRun ? shownNetwork.OnlineBefore : shownNetwork.OnlineAfter)) details.Text += " · 连接异常";
                }
                details.Bounds=new Rectangle(pad,y,w,S(shortWindow?26:32));y+=S(shortWindow?32:44);
                table.Visible=detailsReady&&detailsExpanded;
                statusCard.Visible=(detailsReady&&detailsExpanded)||!string.IsNullOrEmpty(lToast.Text);
                if(statusCard.Visible)
                {
                    bool showRepair = detailsReady && detailsExpanded && (repairingNetwork || canRepairNetwork());
                    btnRepair.Visible = showRepair;
                    statusCard.Bounds=new Rectangle(pad,y,w,S(detailsExpanded?(showRepair?136:100):52));
                    lNet.Visible=detailsExpanded;lNet.AutoSize=false;lNet.AutoEllipsis=true;
                    lNet.Bounds=new Rectangle(S(20),S(12),w-S(40),S(50));
                    btnRepair.UiScale = sc;
                    btnRepair.Bounds = new Rectangle(S(20), S(65), S(120), S(30));
                    lToast.Bounds=new Rectangle(S(20),S(detailsExpanded?(showRepair?101:66):12),w-S(40),S(28));lToast.TextAlign=ContentAlignment.MiddleLeft;
                    y=statusCard.Bottom+gap;
                }
                else btnRepair.Visible = false;
                if(table.Visible){table.Bounds=new Rectangle(pad,y,w,S(tiny?422:280));y=table.Bottom+gap;}
                footer.Height=S(compact?(shortWindow?80:112):72);int fw=footer.ClientSize.Width,p=S(tiny?16:24),inner=fw-2*p;
                if(compact)
                {
                    int first=btnKeep.Visible?(inner-S(8))/2:inner;
                    btnReopen.Bounds=new Rectangle(p,S(shortWindow?6:12),first,S(shortWindow?30:36));
                    if(btnKeep.Visible)btnKeep.Bounds=new Rectangle(p+first+S(8),S(shortWindow?6:12),first,S(shortWindow?30:36));
                    int half=(inner-S(8))/2;
                    btnLog.Bounds=new Rectangle(p,S(shortWindow?44:60),half,S(shortWindow?30:36));
                    btnClose.Bounds=new Rectangle(p+half+S(8),S(shortWindow?44:60),half,S(shortWindow?30:36));
                }
                else
                {
                    btnReopen.Bounds=new Rectangle(p,S(16),S(176),S(40));btnLog.Bounds=new Rectangle(p+S(188),S(16),S(108),S(40));
                    btnKeep.Bounds=new Rectangle(p+S(308),S(16),S(130),S(40));
                    btnClose.Bounds=new Rectangle(fw-p-S(72),S(16),S(72),S(40));
                }
                int logicalY=y-body.AutoScrollPosition.Y;
                int listH=Math.Max(S(shortWindow?120:210),body.Height-logicalY-pad);
                list.Bounds=new Rectangle(pad,y,w,listH);
                list.RefreshKeepActions();
                int contentHeight=list.Bottom-body.AutoScrollPosition.Y+pad;
                Size extent=new Size(0,contentHeight);if(body.AutoScrollMinSize!=extent)body.AutoScrollMinSize=extent;
                help.SetToolTip(hero,hero.NoteText+Environment.NewLine+hero.LiveStats);
                help.SetToolTip(lStatus,lStatus.Text);help.SetToolTip(lNet,lNet.Text);help.SetToolTip(lToast,lToast.Text);
                }finally{body.ResumeLayout(true);body.PerformLayout();body.PerformLayout();layoutBusy=false;}
            };
            details.Click += delegate
            {
                detailsExpanded = !detailsExpanded;
                layout();
                if (detailsExpanded && canRepairNetwork()) body.ScrollControlIntoView(statusCard);
            };
            ((DpiAwareForm)f).UiDpiChanged += delegate
            {
                layout();
                if (keepPage != null) keepPage.SetScale(((DpiAwareForm)f).UiScale);
            };
            ((DpiAwareForm)f).UiLayoutSettled+=delegate {layout();};
            f.FormClosed+=delegate {help.Dispose();};
            body.Resize+=delegate {layout();};
            layout();

            Action<string, Color> toast = delegate(string msg, Color color)
            {
                lToast.Text = msg.Replace("\r\n", "  ").Replace("\n", "  ");
                lToast.ForeColor = color;
                layout();
            };
            Action<NetworkReport> updateNetwork = delegate(NetworkReport report)
            {
                shownNetwork = report;
                lNet.Text = repairingNetwork ? "正在修复网络设置并复测…" : NetworkText(dryRun, report);
                lNet.ForeColor = repairingNetwork ? Theme.Primary : NetworkColor(dryRun, report);
                chip.Busy = !resultHasAttention && (repairingNetwork || report.State == NetworkCheckState.Pending);
                chip.Neutral = !resultHasAttention && report.State == NetworkCheckState.NotRun;
                chip.IsOk = !resultHasAttention && report.State == NetworkCheckState.Completed &&
                    !report.Repairable && (dryRun ? report.OnlineBefore : report.OnlineAfter);
                chip.LabelText = resultHasAttention ? "有未完成项"
                    : repairingNetwork ? "正在修复网络" : NetworkStatus(dryRun, report);
                chip.AccessibleName = chip.LabelText;
                chip.Invalidate();
                layout();
            };

            string lastKeepRule = null;
            string lastKeepPath = null;
            Func<KeepConfig, ProcRow, bool> isKept = delegate(KeepConfig config, ProcRow row)
            {
                return MatchKeepRule(config.system, row.Name, row.IconPath) != null ||
                    MatchKeepRule(config.user, row.Name, row.IconPath) != null;
            };
            Action<KeepConfig> showKeepState = delegate(KeepConfig config)
            {
                if (lastKeepRule != null && !config.user.Contains(lastKeepRule))
                    lastKeepRule = lastKeepPath = null;
                foreach (ProcRow row in list.Rows)
                {
                    if (!row.CanKeepNextTime) continue;
                    row.KeptNextTime = isKept(config, row);
                    row.CanUndoKeep = lastKeepRule != null &&
                        string.Equals(row.IconPath, lastKeepPath, StringComparison.OrdinalIgnoreCase);
                }
                list.RefreshKeepActions();
            };
            Action<ProcRow> editResultKeep = delegate(ProcRow row)
            {
                string message = null;
                try
                {
                    KeepConfig config = ReadEffectiveKeep();
                    if (row.CanUndoKeep)
                    {
                        bool removed = KeepRuleStore.Change(keepPath, "user", lastKeepRule, false);
                        lastKeepRule = lastKeepPath = null;
                        message = removed ? "已撤销本次添加的规则。" : "此规则已移除。";
                    }
                    else if (isKept(config, row))
                    {
                        message = "该应用已受白名单保护。";
                    }
                    else
                    {
                        string rule = KeepRule.File(row.IconPath);
                        bool added = KeepRuleStore.Change(keepPath, "user", rule, true);
                        if (added)
                        {
                            lastKeepRule = rule;
                            lastKeepPath = row.IconPath;
                        }
                        message = added ? "已加入白名单，下次清理生效。" : "这条保留规则已存在。";
                    }
                    showKeepState(ReadEffectiveKeep());
                    toast(message, Theme.OkText);
                }
                catch (Exception ex)
                {
                    string prefix = message == null ? "白名单操作失败。 " : message + " 刷新规则失败。 ";
                    toast(prefix + ex.Message, Theme.DangerInk);
                }
                body.ScrollControlIntoView(list);
            };
            resultTab.Click += delegate
            {
                if (resultPage.Visible) return;
                selectPage(false);
                if (!list.Rows.Any(row => row.CanKeepNextTime)) return;
                try
                {
                    showKeepState(ReadEffectiveKeep());
                    toast("", Theme.Muted);
                }
                catch (Exception ex) { toast("读取白名单失败。 " + ex.Message, Theme.DangerInk); }
            };

            // ---------- 结果态填充（可重复调用：清理中窗口跑完原地切过来） ----------
            Action resultTransition=null;
            Action fillResult = delegate
            {
                f.Text=dryRun?"killer · 预演":"killer · 清理结果";
                if (live != null && live.MemBefore != null) { memBefore = live.MemBefore; }
                int sharedCount = failed.Count(p => p.FailReason == "其他实例处理中");
                int processSkipped = failed.Count(IsBudgetFailure);
                int failedProcesses = failed.Count - sharedCount - processSkipped;
                int skipped = processSkipped + services.Failed.Count(service => service.Skipped);
                int failedServices = services.Failed.Count(service => !service.Skipped);
                resultHasAttention = !dryRun && (failedProcesses + failedServices + skipped > 0);
                NetworkReport netNow = (live != null ? live.Net : net);
                MemorySnapshot memAfterNow = (live != null ? live.MemAfter : memAfter);
                double freedNow = (live != null ? live.FreedMb : freedMb);
                double elapsedNow = (live != null ? live.Elapsed : elapsed);
                int trimCountNow = (live != null ? live.TrimCount : trimCount);
                double trimFreedNow = (live != null ? live.TrimFreedMb : trimFreedMb);

                lTitle.Text = dryRun ? "清理预演" : resultHasAttention ? "清理有未完成项" : "清理完成";
                lStatus.Text = dryRun
                    ? ("将结束 " + plan.Count + " 个进程"
                        + (services.Planned.Count > 0 ? "、停止 " + services.Planned.Count + " 个服务" : "")
                        + " · 耗时 " + elapsedNow + " 秒")
                    : ((failedProcesses > 0 ? failedProcesses + " 个进程未结束 · " : "")
                        + (failedServices > 0 ? failedServices + " 个服务未停 · " : "")
                        + (skipped > 0 ? "跳过 " + skipped + " 项 · " : "")
                        + "已结束 " + killed.Count + " 个进程"
                        + (services.Stopped.Count > 0 ? "、停止 " + services.Stopped.Count + " 个服务" : "")
                        + " · 耗时 " + elapsedNow + " 秒"
                        + (selfExited.Count > 0 ? " · " + selfExited.Count + " 个已自行退出" : "")
                        + (sharedCount > 0 ? " · " + sharedCount + " 个由其他实例处理" : "")
                        + (live != null && live.Degraded && skipped == 0 ? " · 达到清理时间预算" : ""));
                lStatus.ForeColor = resultHasAttention ? Theme.DangerInk : Theme.Muted;

                // 明细表 / 网络卡片回到结果态
                detailsReady = true;
                table.ColBefore = micro ? "前 / 后" : (narrow ? "前 → 后" : "清理前");
                table.Rows.Clear();
                if (memBefore != null)
                {
                    bool hasAfter = (memAfterNow != null);
                    double dUsed = hasAfter ? memAfterNow.UsedMB - memBefore.UsedMB : 0;
                    double dAvail = hasAfter ? memAfterNow.AvailMB - memBefore.AvailMB : 0;
                    double dLoad = hasAfter ? memAfterNow.LoadPercent - memBefore.LoadPercent : 0;
                    double dCommit = hasAfter ? memAfterNow.CommitMB - memBefore.CommitMB : 0;
                    double dGpu = hasAfter ? memAfterNow.GpuUsedMB - memBefore.GpuUsedMB : 0;

                    MemRow m1 = new MemRow();
                    m1.Label = "物理内存已用";
                    m1.TickColor = Theme.Blue;
                    m1.Before = FmtGB(memBefore.UsedMB);
                    m1.After = hasAfter ? FmtGB(memAfterNow.UsedMB) : "—";
                    m1.Delta = hasAfter ? FmtDelta(dUsed) : "—";
                    m1.DeltaColor = hasAfter ? SavedColor(dUsed, true) : Theme.Muted;
                    table.Rows.Add(m1);

                    MemRow m2 = new MemRow();
                    m2.Label = "可用物理内存";
                    m2.TickColor = Theme.Yellow;
                    m2.Before = FmtGB(memBefore.AvailMB);
                    m2.After = hasAfter ? FmtGB(memAfterNow.AvailMB) : "—";
                    m2.Delta = hasAfter ? FmtDelta(dAvail) : "—";
                    m2.DeltaColor = hasAfter ? SavedColor(dAvail, false) : Theme.Muted;
                    table.Rows.Add(m2);

                    MemRow m3 = new MemRow();
                    m3.Label = "内存占用率";
                    m3.TickColor = Theme.Blue;
                    m3.Before = memBefore.LoadPercent.ToString("0") + "%";
                    m3.After = hasAfter ? memAfterNow.LoadPercent.ToString("0") + "%" : "—";
                    m3.Delta = hasAfter ? FmtDeltaPercent(dLoad) : "—";
                    m3.DeltaColor = hasAfter ? SavedColor(dLoad, true) : Theme.Muted;
                    table.Rows.Add(m3);

                    MemRow m4 = new MemRow();
                    m4.Label = narrow ? "提交 / 上限" : "提交量与上限";
                    m4.TickColor = Theme.Yellow;
                    string commitBefore = narrow
                        ? (memBefore.CommitMB / 1024.0).ToString("0.0") + "/" + (memBefore.CommitLimitMB / 1024.0).ToString("0.0") + " GB"
                        : (FmtGB(memBefore.CommitMB) + " / " + FmtGB(memBefore.CommitLimitMB));
                    string commitAfter = hasAfter
                        ? (narrow
                            ? (memAfterNow.CommitMB / 1024.0).ToString("0.0") + "/" + (memAfterNow.CommitLimitMB / 1024.0).ToString("0.0") + " GB"
                            : (FmtGB(memAfterNow.CommitMB) + " / " + FmtGB(memAfterNow.CommitLimitMB)))
                        : "";
                    m4.Before = commitBefore;
                    m4.After = hasAfter ? commitAfter : "—";
                    m4.Delta = hasAfter ? FmtDelta(dCommit) : "—";
                    m4.DeltaColor = hasAfter ? SavedColor(dCommit, true) : Theme.Muted;
                    table.Rows.Add(m4);

                    MemRow m5 = new MemRow();
                    m5.Label = "GPU 显存";
                    m5.TickColor = Theme.Blue;
                    string gpuBefore = memBefore.GpuTotalMB > 0
                        ? (narrow
                            ? (micro
                                ? (memBefore.GpuUsedMB / 1024.0).ToString("0.0") + "/" + (memBefore.GpuTotalMB / 1024.0).ToString("0.0") + " GB"
                                : memBefore.GpuUsedMB.ToString("0") + "/" + memBefore.GpuTotalMB.ToString("0") + " MiB")
                            : (FmtMiB(memBefore.GpuUsedMB) + " / " + FmtMiB(memBefore.GpuTotalMB)))
                        : "—";
                    string gpuAfter = (hasAfter && memAfterNow.GpuTotalMB > 0)
                        ? (narrow
                            ? (micro
                                ? (memAfterNow.GpuUsedMB / 1024.0).ToString("0.0") + "/" + (memAfterNow.GpuTotalMB / 1024.0).ToString("0.0") + " GB"
                                : memAfterNow.GpuUsedMB.ToString("0") + "/" + memAfterNow.GpuTotalMB.ToString("0") + " MiB")
                            : (FmtMiB(memAfterNow.GpuUsedMB) + " / " + FmtMiB(memAfterNow.GpuTotalMB)))
                        : "";
                    m5.Before = gpuBefore;
                    m5.After = string.IsNullOrEmpty(gpuAfter) ? "—" : gpuAfter;
                    m5.Delta = (hasAfter && memBefore.GpuTotalMB > 0 && memAfterNow.GpuTotalMB > 0) ? FmtDeltaMiB(dGpu) : "—";
                    m5.DeltaColor = (hasAfter && memBefore.GpuTotalMB > 0) ? SavedColor(dGpu, true) : Theme.Muted;
                    table.Rows.Add(m5);
                }
                table.Invalidate();

                lToast.Text = "";

                bool comparable = memBefore != null && memAfterNow != null;
                MemorySnapshot latestMemory = memAfterNow ?? memBefore;
                double physicalDelta = comparable ? memAfterNow.UsedMB - memBefore.UsedMB : 0;
                hero.Live = false;
                hero.Preparing = false;
                hero.Progress.Finish();
                hero.Progress.Visible = false;
                hero.UsagePct = latestMemory == null ? 0 : latestMemory.LoadPercent;
                hero.FreedLabel = dryRun ? "计划进程工作集" : "物理内存已用变化";
                hero.HeroText = dryRun ? (plan.Sum(p => p.WsMb) / 1024.0).ToString("0.00")
                    : comparable ? FmtDeltaValue(physicalDelta) : "—";
                hero.HeroUnit = dryRun || comparable ? "GB" : "";
                hero.SummaryText = comparable
                    ? "物理内存已用 " + (memBefore.UsedMB / 1024.0).ToString("0.00") + " → " + FmtGB(memAfterNow.UsedMB)
                    : latestMemory == null ? "暂无物理内存采样" : "当前物理内存已用 " + FmtGB(latestMemory.UsedMB);
                if (latestMemory != null) hero.SummaryText += " · 可用 " + FmtGB(latestMemory.AvailMB);
                hero.CaptionText = dryRun ? "工作集包含共享内存" : "采样差包含同期其他系统活动";
                hero.LiveStats = "";
                hero.NoteText = hero.SummaryText + "\r\n" + hero.CaptionText
                    + (dryRun ? "" : "\r\n已结束进程工作集累计 " + FmtGB(freedNow))
                    + (!dryRun && trimFreedNow > 0
                        ? "\r\n温和回收 " + trimCountNow + " 个未结束的候选进程 · 工作集下降 " + FmtGB(trimFreedNow) : "");
                hero.Invalidate();

                // 清单整体重填
                list.Title = dryRun ? "清理计划" : "处理记录";
                list.Subtitle = dryRun ? plan.Count + " 个进程 · " + services.Planned.Count + " 个服务"
                    : (resultHasAttention ? "未完成项优先显示 · " : "") + "进程工作集累计 " + FmtGB(freedNow);
                list.Selectable = dryRun && plan.Count > 0;
                if (list.Selectable) { list.Subtitle += " · 点一下行 = 把它留下"; }
                list.Rows.Clear();
                if (dryRun)
                {
                    foreach (ProcInfo p in plan)
                    {
                        ProcRow pr = new ProcRow();
                        pr.State = "将结束";
                        pr.StateColor = Theme.Warn;
                        pr.Name = p.Name;
                        pr.IconPath = p.Path ?? "";
                        pr.Memory = p.WsMb.ToString("0.0");
                        pr.Path = p.Path;
                        pr.Keepable = true;
                        list.Rows.Add(pr);
                    }
                    foreach (string s in services.Planned)
                    {
                        ProcRow pr = new ProcRow();
                        pr.State = "将停服务";
                        pr.StateColor = Theme.Primary;
                        pr.Name = s;
                        pr.Memory = "—";
                        pr.Path = "Windows 服务 · 临时停止";
                        list.Rows.Add(pr);
                    }
                }
                else
                {
                    foreach (ProcInfo p in killed)
                    {
                        ProcRow pr = new ProcRow();
                        pr.State = "已结束";
                        pr.StateColor = Theme.Ok;
                        pr.Name = p.Name;
                        pr.IconPath = p.Path ?? "";
                        pr.CanKeepNextTime = !string.IsNullOrEmpty(p.Path);
                        pr.Memory = p.WsMb.ToString("0.0");
                        pr.Path = (pr.CanKeepNextTime ? p.Path : "路径不可读") +
                            (string.IsNullOrEmpty(p.Note) ? "" : " · " + p.Note);
                        list.Rows.Add(pr);
                    }
                    foreach (ProcInfo p in selfExited)
                    {
                        ProcRow pr = new ProcRow();
                        pr.State = p.FailReason=="其他实例处理中"?"协同处理":"已退出";
                        pr.StateColor = Theme.Muted;
                        pr.Name = p.Name;
                        pr.IconPath = p.Path ?? "";
                        pr.Memory = p.WsMb.ToString("0.0");
                        pr.Path = string.IsNullOrEmpty(p.FailReason)?"计划生成后自行退出":p.FailReason;
                        list.Rows.Add(pr);
                    }
                    foreach (ProcInfo p in failed)
                    {
                        ProcRow pr = new ProcRow();
                        pr.IsProblem = p.FailReason != "其他实例处理中";
                        pr.State = !pr.IsProblem ? "协同处理" : IsBudgetFailure(p) ? "已跳过" : "未结束";
                        pr.StateColor = pr.IsProblem ? Theme.Danger : Theme.Muted;
                        pr.Name = p.Name;
                        pr.IconPath = p.Path ?? "";
                        pr.Memory = p.WsMb.ToString("0.0");
                        pr.Path = p.FailReason;
                        list.Rows.Add(pr);
                    }
                    foreach (string s in services.Stopped)
                    {
                        ProcRow pr = new ProcRow();
                        pr.State = "已停服务";
                        pr.StateColor = Theme.Primary;
                        pr.Name = s;
                        pr.Memory = "—";
                        pr.Path = "Windows 服务 · 已临时停止";
                        list.Rows.Add(pr);
                    }
                    foreach (ServiceFailure service in services.Failed)
                    {
                        ProcRow pr = new ProcRow();
                        pr.IsProblem = true;
                        pr.State = service.Skipped ? "服务跳过" : "服务未停";
                        pr.StateColor = Theme.Danger;
                        pr.Name = service.Name;
                        pr.Memory = "—";
                        pr.Path = service.Reason;
                        list.Rows.Add(pr);
                    }
                }
                if (resultHasAttention) list.Rows = list.Rows.OrderByDescending(row => row.IsProblem).ToList();
                list.Offset = 0;
                list.SetKeepActions(editResultKeep);
                list.Invalidate();

                btnReopen.Enabled = (!dryRun && (killed.Count > 0 || services.Stopped.Count > 0)) || previewAutoReopen;
                btnKeep.Visible = dryRun && plan.Count > 0;
                btnClose.Text = "关闭";
                navigation.Visible = true;
                if (previewAutoReopen)
                {
                    btnReopen.Busy = true;
                    lToast.Text = "忙碌态预览 · 未启动程序";
                    lToast.ForeColor = Theme.Muted;
                }
                updateNetwork(netNow);
                if(resultTransition!=null&&f.Visible)resultTransition();
            };

            // ---------- 清理中态填充（每结束一个进程刷一次） ----------
            Action<bool> fillLive = delegate(bool rebuildRows)
            {
                if (live == null) { return; }
                // 清理期间用户可能已经把窗口关掉（关闭按钮是通的）：这时别再碰控件
                if (live.Form != null && live.Form.IsDisposed) { return; }
                f.Text=live.Failed?"killer · 未完成":live.Preparing?"killer · 正在准备":"killer · 清理中";
                int done = live.Processed>0?live.Processed:live.Killed + live.SelfExited;
                int total = live.PlanCount > 0 ? live.PlanCount : 1;
                double secs = Math.Round((DateTime.Now - live.StartedAt).TotalSeconds, 1);

                lSub.Text = live.Failed?"未完成":live.Preparing?"正在准备":"清理中";
                lStatus.Text = live.Preparing?"正在读取配置与采样系统状态":live.Failed?live.Action:"已处理 " + done + (live.UnknownPhase?" 个":" / " + live.PlanCount + " 个") + " · " + secs + " 秒";
                lStatus.ForeColor = Theme.Muted;
                chip.Busy = !live.Failed;
                chip.IsOk = false;
                chip.Neutral = false;
                chip.LabelText = live.Failed?"运行未完成":live.Preparing?"正在准备":"清理进行中";
                chip.Invalidate();

                if (!live.LayoutDone)
                {
                    live.LayoutDone = true;
                    table.Visible = false;             // 清理中：中部整块让给清单
                    statusCard.Visible = false;
                }

                // 后台采样的最新一份快照（拿不到就沿用上一次的文案）
                MemorySnapshot snap = (live.Sampler != null ? live.Sampler.Latest : null);
                if (snap != null)
                {
                    live.LastSnapAt = snap.At;
                    live.AvailText = "可用 " + (snap.AvailMB / 1024.0).ToString("0.00") + " GB";
                    live.MemStats = "已用 " + (snap.UsedMB / 1024.0).ToString("0.00") + " GB · 提交 "
                        + (snap.CommitMB / 1024.0).ToString("0.00") + " GB"
                        + (snap.GpuTotalMB > 0 ? " · GPU " + snap.GpuUsedMB.ToString("0") + " MiB" : "");
                }

                // 缓动：目标值下发，显示值由 30ms 定时器逼近
                live.ActionPctTarget = Math.Min(99,100.0*done/total);
                live.RingTarget = 100.0 * done / total;

                hero.Live = true;
                hero.Preparing=live.Preparing;hero.Indeterminate=live.Preparing||live.UnknownPhase;
                details.Enabled=!live.Preparing&&!live.Failed;
                hero.Progress.Visible=true;hero.Progress.SetTarget(live.ActionPctTarget,hero.Indeterminate,!AnimationsEnabled()||suppressAnimation,live.Failed);
                hero.UsagePct = snap!=null?snap.LoadPercent:memBefore!=null?memBefore.LoadPercent:0;
                hero.AvailText = live.AvailText;
                hero.FreedLabel = live.Preparing?"正在准备清理":"进程工作集累计";
                hero.HeroText = live.Preparing?"—":(live.KilledMb / 1024.0).ToString("0.00");
                hero.NoteText = "已处理 " + done + " / 计划 " + live.PlanCount + " 个 · "
                    + "结束后显示整机物理内存已用量变化";
                hero.ActionText = live.Action;
                hero.ActionPct = live.ActionPctShown;
                hero.LiveStats = string.IsNullOrEmpty(live.LiveStatsOverride)
                    ? (string.IsNullOrEmpty(live.MemStats) ? "正在采样内存…" : live.MemStats)
                    : live.LiveStatsOverride;
                hero.Invalidate();

                list.Title = live.Preparing?"正在准备":live.Failed?"运行未完成":"进程活动";
                list.Subtitle = live.PlanCount > 0
                    ? ("(计划 " + live.PlanCount + " 个 · 整树 " + FmtGB(live.PlanTotalMb) + " · 每结束一个立即上屏，无需等待)")
                    : "(正在生成计划…)";
                list.Selectable = false;
                if (rebuildRows)
                {
                    list.Rows.Clear();
                    list.Rows.AddRange(live.Rows);
                    list.Invalidate();
                }
                else { list.InvalidateHeader(); }

                btnReopen.Enabled = false;
                btnKeep.Visible = false;
                btnClose.Text = live.Failed ? "关闭" : "关闭窗口";
                if (live.Failed) list.SetKeepActions(editResultKeep);
                if (live.Failed) navigation.Visible = true;
                // 布局只在第一帧算一次：之后每次事件都重排+整页重绘会平白吃掉几十毫秒
                if (live.LayoutOnce) { layout(); live.LayoutOnce = false; }
            };

            btnClose.Click += delegate { f.Close(); };
            btnLog.Click += delegate
            {
                try { OpenFromDesktop(logPath, "", baseDir); }
                catch (Exception ex) { toast("打开日志失败：" + ex.Message, Theme.Warn); }
            };
            btnRepair.Click += delegate
            {
                if (repairingNetwork || !canRepairNetwork()) return;
                string message = "将复查当前网络，并修复仍存在的问题。可能关闭失效的本地代理、"
                    + "重置 WinHTTP 代理或把回环 DNS 改为自动获取。是否继续？";
                if (MessageBox.Show(f, message, "修复网络", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

                repairingNetwork = true;
                btnRepair.Busy = true;
                updateNetwork(shownNetwork);
                StartDiagnostic(delegate
                {
                    NetworkReport report = ReadNetworkReport(true);
                    try
                    {
                        AppendLogBlock("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                            " | mode=repair-network ===" + Environment.NewLine + NetworkLogLine(report) + Environment.NewLine);
                    }
                    catch (System.TimeoutException ex)
                    {
                        report.Notes.Add("网络修复日志写入失败：" + ex.Message);
                    }
                    return report;
                }).ContinueWith(task =>
                {
                    if (f.IsDisposed) return;
                    try
                    {
                        f.BeginInvoke(new Action(delegate
                        {
                            repairingNetwork = false;
                            btnRepair.Busy = false;
                            updateNetwork(task.Result);
                        }));
                    }
                    catch (InvalidOperationException) { }
                }, TaskScheduler.Default);
            };
            f.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (repairingNetwork && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    MessageBox.Show(f, "网络修复正在进行。等待复测结果后即可关闭窗口。", "修复网络",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            Action doReopen = delegate
            {
                if (btnReopen.Busy) { return; }
                btnReopen.Busy = true;
                btnReopen.Invalidate();
                System.Threading.ThreadPool.QueueUserWorkItem(delegate{
                    string msg;
                    try { msg=Reopen(killed,services.Stopped); } catch(Exception ex){msg=ex.Message;}
                    if(!f.IsDisposed)try{f.BeginInvoke(new Action(delegate{btnReopen.Busy=false;btnReopen.Invalidate();toast(msg,msg.IndexOf("需要手动打开")>=0?Theme.Warn:Theme.Ok);}));}catch(InvalidOperationException){}
                });
            };
            btnReopen.Click += delegate { doReopen(); };

            list.KeptChanged += delegate
            {
                int n = 0;
                foreach (ProcRow r in list.Rows) { if (r.Keepable && r.Kept) { n++; } }
                toast(n > 0 ? "已勾选 " + n + " 个，按「保留勾选的进程」写入 keep.json" : "",
                      n > 0 ? Theme.Ok : Theme.Muted);
            };

            btnKeep.Click += delegate
            {
                List<ProcRow> selected = list.Rows.Where(row => row.Keepable && row.Kept).ToList();
                if (selected.Count == 0)
                {
                    toast("先选择要保留的进程。", Theme.Warn);
                    return;
                }
                if (selected.Any(row => string.IsNullOrEmpty(row.IconPath)))
                {
                    toast("所选进程中有路径不可读的项目。请在白名单页明确选择保留范围。", Theme.Warn);
                    return;
                }

                try
                {
                    int added = KeepRuleStore.AddUser(keepPath, selected.Select(row => KeepRule.File(row.IconPath)));
                    foreach (ProcRow row in selected)
                    {
                        row.State = "已加白名单";
                        row.StateColor = Theme.Ok;
                        row.Keepable = false;
                        row.Kept = false;
                    }
                    list.Invalidate();
                    toast(added > 0 ? "已保存 " + added + " 条精确文件规则，下次清理生效。" : "所选进程的规则已存在。", Theme.Ok);
                }
                catch (Exception ex)
                {
                    toast("保存失败：" + ex.Message, Theme.Danger);
                }
            };

            // Content transition: absolute time, 180ms, interruptible on resize.
            Control[] sections={hero,table,statusCard,list};
            int[] finalTops=new int[sections.Length];
            Timer intro=new Timer();intro.Interval=15;
            Stopwatch introClock=new Stopwatch();
            bool introActive=false;
            intro.Tick+=delegate{
                double t=MotionCurve.Ease(introClock.Elapsed.TotalMilliseconds/180.0);
                for(int i=0;i<sections.Length;i++)sections[i].Top=finalTops[i]+S((float)(6*(1-t)));
                if(t>=1){intro.Stop();introActive=false;layout();}
            };
            Action transition=delegate{
                intro.Stop();layout();
                for(int i=0;i<sections.Length;i++)finalTops[i]=sections[i].Top;
                if(AnimationsEnabled()&&!suppressAnimation){introActive=true;introClock.Restart();intro.Start();}
            };
            resultTransition=transition;
            f.Shown+=delegate{transition();if(autoReopen&&btnReopen.Enabled&&!previewAutoReopen)doReopen();};
            f.Resize+=delegate{if(introActive){intro.Stop();introActive=false;layout();}};
            Timer sampleTimer=new Timer();sampleTimer.Interval=250;
            sampleTimer.Tick+=delegate{
                if(live==null||!hero.Live||live.Failed){sampleTimer.Stop();return;}
                MemorySnapshot snap=live.Sampler!=null?live.Sampler.Latest:null;
                if(snap!=null&&snap.At!=live.LastSnapAt){live.LastSnapAt=snap.At;hero.AvailText="可用 "+(snap.AvailMB/1024).ToString("0.00")+" GB";hero.Invalidate();}
            };
            if(live!=null)sampleTimer.Start();
            hero.Progress.Sample+=delegate(double value){if(live!=null){live.ActionPctShown=value;live.RingShown=value;if(live.FilmSample!=null)live.FilmSample(value);}};
            f.FormClosed+=delegate{intro.Dispose();sampleTimer.Dispose();hero.Progress.Finish();if(live!=null&&live.Sampler!=null)live.Sampler.Stop();};

            UiShell shell = new UiShell();
            shell.Rebind=delegate(List<ProcInfo> k,List<ProcInfo> bad,List<ProcInfo> exited,List<ProcInfo> planned,ServiceReport sr,NetworkReport nr,MemorySnapshot before){killed=k;failed=bad;selfExited=exited;plan=planned;services=sr;net=nr;memBefore=before;};
            shell.Form = f;
            shell.List = list;
            shell.Hero = hero;
            shell.FillResult = fillResult;
            shell.UpdateNetwork = report => { if (live == null || !live.Failed) updateNetwork(report); };
            shell.LayoutUi = layout;
            if (live == null)
            {
                fillResult();
            }
            else
            {
                live.Form = f;
                live.Refresh = delegate { fillLive(true); };
                live.RefreshSummary = delegate { fillLive(false); };
                live.AddRow = delegate(ProcRow row)
                {
                    list.Rows.Insert(0, row);
                    list.Invalidate();
                };
                live.RefreshRow = delegate(ProcRow row) { list.InvalidateRow(row); };
                fillLive(true);
            }
            return shell;
        }

        private static string NetworkStatus(bool dryRun, NetworkReport net)
        {
            if (net.State == NetworkCheckState.NotRun) return "尚未检测网络";
            if (net.State == NetworkCheckState.Pending) return "网络检测中";
            if (net.State == NetworkCheckState.Incomplete) return "网络检测未完成";
            if (net.Repairable) return "网络设置有异常";
            return (dryRun ? net.OnlineBefore : net.OnlineAfter) ? "网络自检正常" : "网络连接异常";
        }

        private static string NetworkText(bool dryRun, NetworkReport net)
        {
            bool ok = dryRun ? net.OnlineBefore : net.OnlineAfter;
            string t = NetworkStatus(dryRun, net);
            if (net.State == NetworkCheckState.Completed && !ok && net.OnlineDetail.Length > 0)
                t += "（" + net.OnlineDetail + "）";
            if (net.Actions.Count > 0)
            {
                t += "。" + string.Join("。", net.Actions.ToArray());
            }
            if (net.Notes.Count > 0)
            {
                t += "。" + string.Join("。", net.Notes.ToArray());
            }
            return t;
        }

        private static Color NetworkColor(bool dryRun, NetworkReport net)
        {
            if (net.State == NetworkCheckState.Incomplete) return Theme.WarnInk;
            if (net.State != NetworkCheckState.Completed) return Theme.Muted;
            if (net.Repairable || !(dryRun ? net.OnlineBefore : net.OnlineAfter)) return Theme.DangerInk;
            return net.Actions.Count > 0 ? Theme.OkText : Theme.Muted;
        }

        private static string NetworkLogLine(NetworkReport net)
        {
            string line = "network: state=" + net.State.ToString().ToLowerInvariant();
            if (net.State == NetworkCheckState.Completed)
                line += " dnsBefore=" + net.DnsBefore + " onlineBefore=" + net.OnlineBefore
                    + " (" + net.OnlineDetail + ") proxy=" + net.ProxyState + " pac=" + net.PacState
                    + " route=" + (net.RouteState.Length > 0 ? net.RouteState : "none")
                    + " dnsAfter=" + net.DnsAfter + " onlineAfter=" + net.OnlineAfter;
            if (net.Actions.Count > 0) line += " actions=[" + string.Join(" | ", net.Actions.ToArray()) + "]";
            if (net.Notes.Count > 0) line += " notes=[" + string.Join(" | ", net.Notes.ToArray()) + "]";
            return line;
        }

        // --instant-preview：零风险演示"清理中 → 驻留复查 → 结果"三段状态（不碰任何
        // 进程、不写日志），配 --screenshot <png> 出三张图，用于界面验收。
        // internal：bench 也能直接调，省掉提权。
        internal static void RunInstantPreview(string shotPath)
        {
            suppressAnimation = true;
            double MB = 1024.0;                 // MemorySnapshot 的字段单位是 MB

            // ---- 演示数据（与 preview/preview3、preview4 同源）----
            List<ProcInfo> killed = new List<ProcInfo>();
            killed.Add(DemoProc("迅雷", 4820, @"D:\Program\Thunder\Program\Thunder.exe", 214.6, "taskkill /F /T"));
            killed.Add(DemoProc("Kuajiasu", 11044, @"D:\Program\Kuajiasu\Kuajiasu.exe", 486.3, "优雅关闭 + 树杀"));
            killed.Add(DemoProc("Msedgewebview2", 7712, @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\msedgewebview2.exe", 352.8, "随主进程树结束（Kuajiasu #11044）"));
            killed.Add(DemoProc("Client-Win64-Shipping", 3392, @"D:\Games\Wuthering Waves\Client-Win64-Shipping.exe", 1184.2, "复查补杀 taskkill /F /T"));
            killed.Add(DemoProc("快聊", 6620, @"C:\Apps\快聊\快聊.exe", 53.7, "taskkill /F /T（含子进程）"));
            killed.Add(DemoProc("Bongo Cat Mver", 2210, @"D:\Tools\BongoCat\Bongo Cat Mver.exe", 41.2, "taskkill /F /T（含子进程）"));
            List<ProcInfo> selfExited = new List<ProcInfo>();
            ProcInfo mi = DemoProc("MiXplorer", 9008, @"D:\Tools\MiXplorer\MiXplorer.exe", 12.1, null);
            mi.FailReason = "计划生成后自行退出";
            selfExited.Add(mi);
            List<ProcInfo> failed = new List<ProcInfo>();
            ProcInfo ace = DemoProc("ACE-Service64", 5140, @"C:\Program Files\AntiCheatExpert\SGuard\ACE-Service64.exe", 96.3, null);
            ace.FailReason = "复查补杀被拒绝（反作弊 / 受保护进程）";
            failed.Add(ace);
            List<ProcInfo> plan = new List<ProcInfo>();
            plan.AddRange(killed);
            plan.AddRange(selfExited);
            plan.AddRange(failed);
            for (int i = plan.Count; i < 12; i++) { plan.Add(DemoProc("DemoHelper" + i, 7000 + i, @"D:\Tools\Demo\helper.exe", 100.0 + i, null)); }

            ServiceReport services = new ServiceReport();
            services.Planned = new List<string>(new string[] { "Everything", "MuMuRemoteService" });
            services.Stopped = new List<string>(new string[] { "Everything", "MuMuRemoteService" });

            NetworkReport net = new NetworkReport { State = NetworkCheckState.Completed };
            net.OnlineBefore = true;
            net.OnlineAfter = true;

            MemorySnapshot memBefore = new MemorySnapshot();
            memBefore.TotalMB = 31942.0;
            memBefore.UsedMB = 11.24 * MB;
            memBefore.AvailMB = 4.85 * MB;
            memBefore.LoadPercent = 71;
            memBefore.CommitMB = 14.31 * MB;
            memBefore.CommitLimitMB = 31.19 * MB;
            memBefore.SessionWsMB = 12400.0;
            memBefore.GpuUsedMB = 1102;
            memBefore.GpuTotalMB = 8151;

            MemorySnapshot memAfter = new MemorySnapshot();
            memAfter.TotalMB = 31942.0;
            memAfter.UsedMB = 8.02 * MB;
            memAfter.AvailMB = 8.07 * MB;
            memAfter.LoadPercent = 50;
            memAfter.CommitMB = 11.09 * MB;
            memAfter.CommitLimitMB = 31.19 * MB;
            memAfter.SessionWsMB = 12400.0 - 3.18 * MB;
            memAfter.GpuUsedMB = 824;
            memAfter.GpuTotalMB = 8151;

            UiLiveState live = new UiLiveState();
            live.PlanCount = plan.Count;
            foreach (ProcInfo p in plan) { live.PlanTotalMb += p.WsMb; }
            live.AvailText = "可用 " + (memBefore.AvailMB / 1024.0).ToString("0.00") + " GB";
            live.KilledMb = 2.32 * 1024.0;
            live.StartedAt = DateTime.Now.AddSeconds(-6.4);      // 演示：耗时看起来像真的
            live.ActionPctShown = 44;                            // 演示：跳过缓动直接到位
            live.RingShown = 50;
            live.MemStats = "已用 9.31 GB · 提交 12.40 GB · GPU 1102 MiB";   // 演示：后台采样那行的样子

            UiShell shell = BuildUiShell(false, killed, failed, selfExited, plan, services, net,
                                        memBefore, null, 0, 0, live);
            Form f = shell.Form;
            f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);
            f.Show();
            Application.DoEvents();

            // 第一帧：清理中（正在结束 迅雷）
            live.Killed = 5;
            live.SelfExited = 1;
            live.Action = "正在结束 迅雷 #4820（整树 312 MB）";
            live.Pct = 44;
            live.Active = LiveRow("正在结束", Theme.Primary, "迅雷", 214.6, @"D:\Program\Thunder\Program\Thunder.exe");
            ProcRow thunderRow = live.Active;
            live.Rows.Add(live.Active);
            live.Rows.Add(LiveRow("已结束", Theme.Ok, "Kuajiasu", 486.3, @"D:\Program\Kuajiasu\Kuajiasu.exe · 优雅关闭 + 树杀"));
            live.Rows.Add(LiveRow("已结束", Theme.Ok, "Msedgewebview2", 352.8, "随主进程树结束（Kuajiasu #11044）"));
            live.Rows.Add(LiveRow("已结束", Theme.Ok, "快聊", 53.7, @"C:\Apps\快聊\快聊.exe · taskkill /F /T（含子进程）"));
            live.Rows.Add(LiveRow("已结束", Theme.Ok, "Bongo Cat Mver", 41.2, @"D:\Tools\BongoCat\Bongo Cat Mver.exe · taskkill /F /T（含子进程）"));
            live.Rows.Add(LiveRow("已退出", Theme.Muted, "MiXplorer", 12.1, "计划生成后自行退出"));
            live.Refresh();
            CaptureForm(f, Frame(shotPath, "1-cleaning"), 300);

            // 第二帧：驻留复查
            live.Killed = 9;
            live.SelfExited = 2;
            live.Action = "驻留复查 +5s：名单内还有 1 个";
            live.Pct = 74;
            live.ActionPctShown = 74;
            live.RingShown = 92;
            live.LiveStatsOverride = "复查节奏：+2s 已扫 (0 个) · +5s 已扫 (1 个) · +10s 待扫";
            live.Active = null;
            thunderRow.State = "已结束";
            thunderRow.StateColor = Theme.Ok;
            thunderRow.Path = @"D:\Program\Thunder\Program\Thunder.exe · taskkill /F /T";
            live.Rows.Insert(0, LiveRow("未结束", Theme.Danger, "ACE-Service64", 96.3, "复查补杀被拒绝（反作弊 / 受保护进程）"));
            live.Rows.Insert(0, LiveRow("复查补杀", Theme.Warn, "Client-Win64-Shipping", 1184.2, @"D:\Games\Wuthering Waves\Client-Win64-Shipping.exe"));
            live.Rows.Add(LiveRow("已停服务", Theme.Primary, "Everything", 0, "Windows 服务（Session 0）"));
            live.Rows.Add(LiveRow("已停服务", Theme.Primary, "MuMuRemoteService", 0, "Windows 服务（Session 0）"));
            live.Refresh();
            CaptureForm(f, Frame(shotPath, "2-review"), 300);

            // 第三帧：原地切成结果态
            live.LiveStatsOverride = "";
            live.Net = net;
            live.MemAfter = memAfter;
            live.FreedMb = 3.18 * 1024.0;
            live.Elapsed = 11.8;
            live.TrimCount = 0;
            live.TrimFreedMb = 0;
            shell.FillResult();
            CaptureForm(f, Frame(shotPath, "3-result"), 300);

            f.Close();
            Console.WriteLine("instant preview written: " + shotPath);
        }

        // --instant-preview-film：把"清理中"的进度条跑一遍（脚本化、不碰任何进程），
        // 每 30ms 采一次显示值，最后给出"最大单帧跳变 / 连续帧占比"和一个胶片图。
        // 这是"进度条到底丝不丝滑"唯一可复现的量化口径。
        internal static void RunInstantPreviewFilm(string outPng)
        {
            suppressAnimation = true;
            double MB = 1024.0;
            string[] names = new string[] { "迅雷", "Kuajiasu", "Msedgewebview2", "快聊", "Bongo Cat Mver",
                                            "MiXplorer", "ThunderHelper", "ACE-Service64", "aha_doctor",
                                            "vivoesService", "MuMuRemoteService", "Everything" };
            List<ProcInfo> plan = new List<ProcInfo>();
            for (int i = 0; i < names.Length; i++)
            {
                plan.Add(DemoProc(names[i], 4000 + i, @"D:\Tools\Demo\" + names[i] + ".exe", 80 + i * 37, null));
            }
            List<ProcInfo> killed = new List<ProcInfo>();
            List<ProcInfo> failed = new List<ProcInfo>();
            List<ProcInfo> selfExited = new List<ProcInfo>();
            ServiceReport services = new ServiceReport();
            services.Planned = new List<string>(new string[] { "Everything" });
            NetworkReport net = new NetworkReport { State = NetworkCheckState.Completed };
            net.OnlineBefore = true;
            net.OnlineAfter = true;
            MemorySnapshot memBefore = new MemorySnapshot();
            memBefore.TotalMB = 31942.0;
            memBefore.UsedMB = 11.24 * MB;
            memBefore.AvailMB = 4.85 * MB;
            memBefore.LoadPercent = 71;
            memBefore.CommitMB = 14.31 * MB;
            memBefore.CommitLimitMB = 31.19 * MB;
            memBefore.SessionWsMB = 12400.0;

            UiLiveState live = new UiLiveState();
            live.PlanCount = plan.Count;
            foreach (ProcInfo p in plan) { live.PlanTotalMb += p.WsMb; }
            live.AvailText = "可用 4.85 GB";
            live.MemStats = "已用 11.24 GB · 提交 14.31 GB";

            UiShell shell = BuildUiShell(false, killed, failed, selfExited, plan, services, net,
                                        memBefore, null, 0, 0, live);
            Form f = shell.Form;
            f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);
            f.Show();
            Application.DoEvents();
            uiHeartbeat = PumpUi;
            live.StartedAt = DateTime.Now;

            List<double> samples = new List<double>();
            List<Bitmap> strips = new List<Bitmap>();
            live.FilmSample = delegate(double value) { samples.Add(value); };
            long ms = 0;
            for (int i = 0; i < plan.Count; i++)
            {
                ProcInfo c = plan[i];
                LiveBegin(live, c);
                int waited = 0;
                while (waited < 240)
                {
                    UiPump(10);
                    waited += 10;
                    ms += 10;
                    if (ms % 300 == 0 && strips.Count < 8) { strips.Add(CaptureControl(shell.Hero)); }
                }
                bool ok = (i % 7) != 5;
                if (ok) { killed.Add(c); LiveSettle(live, c, "已结束", Theme.Ok, c.Path + " · Kill()", 1); }
                else { failed.Add(c); LiveSettle(live, c, "未结束", Theme.Danger, "拒绝访问", 2); }
            }

            // ---- 统计 ----
            double maxJump = 0;
            int frozen = 0;
            int bigJumps = 0;
            double prev = -1;
            string trace = "";
            for (int i = 0; i < samples.Count; i++)
            {
                if (prev >= 0)
                {
                    double d = samples[i] - prev;
                    if (d > maxJump) { maxJump = d; }
                    if (d > 2.0) { bigJumps++; }
                    if (d == 0) { frozen++; }
                }
                prev = samples[i];
                if (i % 5 == 0) { trace += samples[i].ToString("0.0") + " "; }
            }
            Console.WriteLine("film samples=" + samples.Count + " (每 30ms 一个)");
            Console.WriteLine("film 最大单帧跳变 = " + maxJump.ToString("0.00") + "%  (>2% 的帧: " + bigJumps + " 个)");
            Console.WriteLine("film 未变化帧 = " + frozen + "/" + samples.Count);
            Console.WriteLine("film 轨迹(每 150ms): " + trace.Trim());
            Console.WriteLine("film 起始 " + (samples.Count > 0 ? samples[0].ToString("0.0") : "?")
                + "% → 结束 " + (samples.Count > 0 ? samples[samples.Count - 1].ToString("0.0") : "?") + "%");

            if (strips.Count > 0 && outPng != null)
            {
                int w = strips[0].Width;
                int h = strips[0].Height;
                using (Bitmap sheet = new Bitmap(w, h * strips.Count))
                using (Graphics g = Graphics.FromImage(sheet))
                {
                    g.Clear(Color.White);
                    for (int i = 0; i < strips.Count; i++) { g.DrawImage(strips[i], 0, i * h); }
                    sheet.Save(outPng, ImageFormat.Png);
                }
                Console.WriteLine("film 胶片图: " + outPng);
            }
            foreach (Bitmap b in strips) { b.Dispose(); }
            uiHeartbeat = null;
            f.Close();
            Environment.Exit(0);      // 胶片模式跑完就退，不等窗口
        }

        private static Bitmap CaptureControl(Control c)
        {
            Bitmap bmp = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height));
            try { c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height)); }
            catch { }
            return bmp;
        }

        private static string Frame(string shotPath, string tag)
        {
            int dot = shotPath.LastIndexOf('.');
            if (dot < 0) { return shotPath + "-" + tag + ".png"; }
            return shotPath.Substring(0, dot) + "-" + tag + shotPath.Substring(dot);
        }

        private static ProcInfo DemoProc(string name, int pid, string path, double wsMb, string note)
        {
            ProcInfo p = new ProcInfo();
            p.Name = name;
            p.Pid = pid;
            p.Path = path;
            p.WsMb = wsMb;
            p.Note = note;
            p.HasWindow = true;
            return p;
        }

        private static ProcRow LiveRow(string state, Color color, string name, double mb, string path)
        {
            ProcRow r = new ProcRow();
            r.State = state;
            r.StateColor = color;
            r.Name = name;
            r.Memory = mb > 0 ? mb.ToString("0.0") : "—";
            r.Path = path;
            return r;
        }

        private static void DrawShotStamp(Graphics g, Bitmap bmp, Form f)
        {
            uint realDpi = 0;
            try { realDpi = GetDpiForWindow(f.Handle); }
            catch { }
            string text = f.Width + "x" + f.Height + " px   windowDpi=" + realDpi
                + "   scale=" + (realDpi / 96F).ToString("0.00");
            using (Font font = new Font("Consolas", 9F))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(190, 85, 101, 92)))
            {
                SizeF size = g.MeasureString(text, font);
                float x = Math.Max(8F, bmp.Width - size.Width - 12F);
                float y = Math.Max(4F, f.Height - f.ClientSize.Height + 2F);
                g.DrawString(text, font, brush, x, y);
            }
        }

        private static void CaptureForm(Form f, string path, int settleMs)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(settleMs);
            Application.DoEvents();
            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                using (Graphics g = Graphics.FromImage(bmp))
                DrawShotStamp(g, bmp, f);
                bmp.Save(path, ImageFormat.Png);
            }
            Console.WriteLine("ui screenshot written: " + path);
        }

        // Renders the result window to a PNG without showing it (--screenshot <file>),
        // handy for checking the layout without waiting for the auto-close countdown.
        // internal（而非 private）：bench\shottest.cs 用它做零 UAC 的界面出图回归。
        internal static void SaveUiScreenshot(string path, bool dryRun, List<ProcInfo> killed, List<ProcInfo> failed,
                                             List<ProcInfo> selfExited, List<ProcInfo> plan, ServiceReport services,
                                             NetworkReport net, MemorySnapshot memBefore, MemorySnapshot memAfter,
                                             double freedMb, double elapsed)
        {
            suppressAnimation = true;   // capture the settled layout, not a mid-animation frame
            previewAutoReopen = dryRun && autoReopen;
            Form f = BuildResultForm(dryRun, killed, failed, selfExited, plan, services, net, memBefore, memAfter, freedMb, elapsed);
            f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);
            f.Show();
            Application.DoEvents();
            System.Threading.Thread.Sleep(300);
            Application.DoEvents();
            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                using (Graphics g = Graphics.FromImage(bmp))
                DrawShotStamp(g, bmp, f);
                bmp.Save(path, ImageFormat.Png);
            }
            f.Close();
            previewAutoReopen = false;
            Console.WriteLine("ui screenshot written: " + path);
        }

        private static void RunUi(bool dryRun, List<ProcInfo> killed, List<ProcInfo> failed, List<ProcInfo> selfExited,
                                  List<ProcInfo> plan, ServiceReport services, NetworkReport net,
                                  MemorySnapshot memBefore, MemorySnapshot memAfter, double freedMb, double elapsed)
        {
            UiShell shell = BuildUiShell(dryRun, killed, failed, selfExited, plan, services, net,
                                         memBefore, memAfter, freedMb, elapsed, null);
            Application.Run(shell.Form);
        }

        // 宿主 / 子进程类的进程名：自动重开只会拉出一堆看不见的后台进程，
        // 交给它们的主程序自己拉起。
        private static bool IsBackgroundChild(string name)
        {
            if (string.IsNullOrEmpty(name)) { return false; }
            string[] skip = new string[] { "conhost", "cmd", "node", "crashpad_handler", "adb",
                                           "WerFault", "powershell", "pwsh", "wscript", "cscript",
                                           "msedgewebview2", "pip_main", "aha_doctor" };
            foreach (string s in skip)
            {
                if (name.Equals(s, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        private static string Reopen(List<ProcInfo> killed, List<string> stoppedServices)
        {
            int ok = 0;
            int skipped = 0;
            List<string> manual = new List<string>();
            // 一个程序常有十几个进程（Edge、Electron 应用），逐个 Process.Start 会开出
            // 一堆窗口：先让"有窗口"的排前面，再按 exe 路径 / AUMID 去重。
            List<ProcInfo> ordered = new List<ProcInfo>();
            foreach (ProcInfo p in killed) { if (p.HasWindow) { ordered.Add(p); } }
            foreach (ProcInfo p in killed) { if (!p.HasWindow) { ordered.Add(p); } }

            List<string> started = new List<string>();
            foreach (ProcInfo p in ordered)
            {
                if (IsBackgroundChild(p.Name)) { skipped++; continue; }
                if (string.IsNullOrEmpty(p.Aumid) && string.IsNullOrEmpty(p.Path))
                {
                    manual.Add(p.Name);
                    continue;
                }
                string key = !string.IsNullOrEmpty(p.Aumid)
                    ? ("aumid:" + p.Aumid.ToLowerInvariant())
                    : ("path:" + p.Path.ToLowerInvariant());
                if (started.Contains(key)) { continue; }
                started.Add(key);
                try
                {
                    if (!string.IsNullOrEmpty(p.Aumid))
                    {
                        OpenFromDesktop("shell:AppsFolder\\" + p.Aumid, "", "");
                        ok++;
                    }
                    else if (File.Exists(p.Path)
                             && !KeepRule.IsInDirectory(p.Path, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
                    {
                        OpenFromDesktop(p.Path, "", Path.GetDirectoryName(p.Path));
                        ok++;
                    }
                    else
                    {
                        manual.Add(p.Name);
                    }
                }
                catch { manual.Add(p.Name); }
            }
            string msg = "已提交 " + ok + " 个程序的打开请求。";
            if (skipped > 0)
            {
                msg += "\r\n已跳过 " + skipped + " 个后台子进程（node / cmd / crashpad 之类，宿主程序会自己拉起）。";
            }
            List<string> restartedServices = new List<string>();
            foreach (string s in stoppedServices)
            {
                try
                {
                    using (new NamedGate("service:" + s, 2000))
                    using (var sc = new ServiceController(s))
                    {
                        var status = sc.Status;
                        if (status != ServiceControllerStatus.Running)
                        {
                            if (status != ServiceControllerStatus.StartPending) sc.Start();
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(8));
                        }
                        restartedServices.Add(s);
                    }
                }
                catch { manual.Add(s + "（服务）"); }
            }
            if (restartedServices.Count > 0)
            {
                msg += "\r\n已运行服务：" + string.Join("、", restartedServices.ToArray());
            }
            if (manual.Count > 0)
            {
                msg += "\r\n以下项目需要手动打开：\r\n" + string.Join("、", manual.Distinct().ToArray());
            }
            return msg;
        }
    }
}
