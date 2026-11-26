// killer 离线自测（不需要提权，不碰任何进程）
//
// 运行：在仓库根目录执行 .\test.ps1。
//
// 覆盖四块纯逻辑：
//   [1] 白名单匹配 MatchKeepRule —— 含"System 静默免疫 System32"那个 P0 bug 的回归
//   [2] PID 复用判定 IdentityMismatch
//   [3] 日志追加 / 块感知截断 AppendLogBlock(path)（用临时文件，MaxLogBytes 在源码里是 512KB，
//       这里为了能跑出截断直接调 TrimLogFile(path, tail)）
//   [4] 版本解析 ParseVersionFromChangelog + JSON 字面量 JsonStringLiteral

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KillerKeepTest
{
    public static class Program
    {
        private static int pass = 0;
        private static int fail = 0;

        private static readonly List<string> Rules = new List<string>(new string[]
        {
            // system 组（节选，含那个闯祸的 "System"）
            "csrss", "svchost", "dwm", "explorer", "SearchHost", "System", "Idle",
            // user 组（节选：纯名字 / 带 .exe / 带中文 / 真的路径规则）
            "ghelper", "RtkAudUService64", "nvcontainer", "Pogget", "Bongo Cat Mver",
            "vivo_remote_watch.exe", "C:\\Apps\\Example", "快聊 LanChat"
        });

        private static void Check(string title, string expected, string actual)
        {
            bool ok = string.Equals(expected, actual, StringComparison.Ordinal);
            if (ok) { pass++; } else { fail++; }
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + title
                + "  (期望 " + Show(expected) + "，实际 " + Show(actual) + ")");
        }

        private static void CheckBool(string title, bool ok)
        {
            if (ok) { pass++; } else { fail++; }
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + title);
        }

        private static string Show(string s)
        {
            return s == null ? "<不匹配>" : "<" + s + ">";
        }

        // 旧实现（含 bug）原样复刻，只用来演示差异 —— 不参与线上代码
        private static string LegacyKeepRule(List<string> rules, string name, string path)
        {
            foreach (string raw in rules)
            {
                if (raw == null) { continue; }
                string entry = raw.Trim();
                if (entry.Length == 0) { continue; }
                if (entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    entry = entry.Substring(0, entry.Length - 4);
                }
                if (!string.IsNullOrEmpty(name) && name.Equals(entry, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
                if (!string.IsNullOrEmpty(path) && path.IndexOf(entry, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return entry;
                }
            }
            return null;
        }

        public static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== killer 离线自测 ===");
            Console.WriteLine();

            KeepRules();
            ScopedRules();
            try { RuleStorage(); }
            catch (Exception ex) { fail++; Console.WriteLine("  FAIL [6] " + ex); }
            Identity();
            WorkingSetTrimOrder();
            CleanupBatchDispatch();
            ExitConfirmationBudget();
            TaskKillOutput();
            RunClaims();
            PartialBatchClaims();
            CleanupDeadlines();
            ServiceStopsDoNotBlock();
            AttributedRelaunches();
            LateRelaunchObservation();
            ParentSnapshotIdentity();
            try { LogBlocks(); }
            catch (Exception ex) { fail++; Console.WriteLine("  FAIL [3] 抛出异常: " + ex.GetType().Name + " " + ex.Message); }
            try { VersionAndJson(); }
            catch (Exception ex) { fail++; Console.WriteLine("  FAIL [4] 抛出异常: " + ex.GetType().Name + " " + ex.Message); }

            Console.WriteLine("结果: " + pass + " 通过 / " + fail + " 失败");
            return fail == 0 ? 0 : 1;
        }

        // ---------------- [1] 白名单匹配 ----------------
        private static void KeepRules()
        {
            Console.WriteLine("[1] 白名单匹配 MatchKeepRule");
            Check("System 名字本身仍然放行（不能把系统进程放进计划）", "System", Match("System", null));
            Check("csrss 放行", "csrss", Match("csrss", "C:\\Windows\\System32\\csrss.exe"));
            Check("带 .exe 的条目同样匹配裸进程名", "vivo_remote_watch.exe",
                Match("vivo_remote_watch", "D:\\vivo\\vivo_remote_watch.exe"));
            Check("带空格的程序名", "Bongo Cat Mver", Match("Bongo Cat Mver", "D:\\tools\\Bongo Cat Mver.exe"));
            Check("路径规则：MuMu 目录下的进程放行", "C:\\Apps\\Example",
                Match("MuMuNxMain", "C:\\Apps\\Example\\nx_main\\MuMuNxMain.exe"));

            Console.WriteLine("  -- 修复点：System32 全家不再被 'System' 静默免疫 --");
            Check("notepad", null, Match("notepad", "C:\\Windows\\System32\\notepad.exe"));
            Check("cmd", null, Match("cmd", "C:\\Windows\\System32\\cmd.exe"));
            Check("powershell", null, Match("powershell", "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"));
            Check("taskmgr", null, Match("taskmgr", "C:\\Windows\\System32\\Taskmgr.exe"));
            Check("regedit", null, Match("regedit", "C:\\Windows\\regedit.exe"));

            Console.WriteLine("  -- 旧实现对照（bug 根源）--");
            Check("旧逻辑：notepad 被 'System' 命中路径子串", "System",
                LegacyKeepRule(Rules, "notepad", "C:\\Windows\\System32\\notepad.exe"));
            Check("旧逻辑：taskmgr 同样被命中", "System",
                LegacyKeepRule(Rules, "taskmgr", "C:\\Windows\\System32\\Taskmgr.exe"));

            Console.WriteLine("  -- 边界 --");
            Check("名字恰好等于路径规则里的目录名时不命中", null, Match("C:\\Apps\\Example", "C:\\Windows\\System32\\cmd.exe"));
            Check("'快聊 LanChat'（文件描述式条目）对不上进程名就不命中", null,
                Match("快聊", "C:\\Users\\admin\\Desktop\\doc\\快聊\\快聊.exe"));
            Check("但进程名真叫 '快聊 LanChat' 时放行", "快聊 LanChat", Match("快聊 LanChat", "C:\\x\\快聊 LanChat.exe"));
            Check("空条目 / null 不炸", null, Match(null, null));
            Check("路径为空时路径规则不命中", null, Match("anything", ""));
            Check("大小写不敏感", "csrss", Match("CSRSS", "C:\\Windows\\System32\\csrss.exe"));
            Console.WriteLine();
        }

        private static void ScopedRules()
        {
            Console.WriteLine("[5] 精确文件与目录规则");
            var exact = new List<string> { @"exe=C:\Apps\Tool\viewer.exe" };
            var folder = new List<string> { @"dir=C:\Apps\Tool" };
            Check("完整路径匹配，忽略大小写", exact[0], KillAllBack.Program.MatchKeepRule(exact, "viewer", @"c:\apps\Tool\VIEWER.exe"));
            Check("同名并列安装不命中", null, KillAllBack.Program.MatchKeepRule(exact, "viewer", @"C:\Other\viewer.exe"));
            Check("路径不可读时不按名称扩大", null, KillAllBack.Program.MatchKeepRule(exact, "viewer", null));
            string embeddedExtension = KillAllBack.KeepRule.ForProcessName("helper.exe");
            Check("从 Windows 进程名生成规则时保留名称本身的 .exe", "helper.exe.exe", embeddedExtension);
            Check("双 exe 文件名能按实际进程名保留", embeddedExtension,
                KillAllBack.Program.MatchKeepRule(new List<string> { embeddedExtension }, "helper.exe", null));
            Check("目录下的辅助进程命中", folder[0], KillAllBack.Program.MatchKeepRule(folder, "helper", @"C:\Apps\Tool\bin\helper.exe"));
            Check("并列的同前缀目录不命中", null, KillAllBack.Program.MatchKeepRule(folder, "helper", @"C:\Apps\ToolBackup\helper.exe"));
            Check("目录匹配支持 Windows 的两种分隔符", folder[0], KillAllBack.Program.MatchKeepRule(folder, "helper", @"C:/Apps/Tool/bin/helper.exe"));
            Check("驱动器相对路径不能意外保留整个磁盘", null,
                KillAllBack.Program.MatchKeepRule(new List<string> { "dir=C:" }, "viewer", @"C:\Apps\Tool\viewer.exe"));
            Check("旧配置中含等号的进程名仍匹配", "exe=helper.exe",
                KillAllBack.Program.MatchKeepRule(new List<string> { "exe=helper.exe" }, "exe=helper", @"C:\Apps\exe=helper.exe"));
            Check("旧配置中含等号的相对路径片段仍匹配", @"dir=tool\",
                KillAllBack.Program.MatchKeepRule(new List<string> { @"dir=tool\" }, "helper", @"C:\Apps\dir=tool\helper.exe"));
            Check("原路径子串规则仍按旧语义命中", @"C:\Apps\Tool", KillAllBack.Program.MatchKeepRule(new List<string> { @"C:\Apps\Tool" }, "helper", @"C:\Apps\ToolBackup\helper.exe"));
            CheckBool("安装目录内的进程应保留", KillAllBack.KeepRule.IsInDirectory(
                @"C:\Apps\Tool\bin\viewer.exe", @"C:\Apps\Tool"));
            CheckBool("安装目录保护不匹配同前缀的相邻目录", !KillAllBack.KeepRule.IsInDirectory(
                @"C:\Apps\ToolBackup\viewer.exe", @"C:\Apps\Tool"));
        }

        private static void RuleStorage()
        {
            Console.WriteLine("[6] 白名单立即保存与撤销");
            string directory = Path.Combine(Path.GetTempPath(), "killer-rules-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            string config = Path.Combine(directory, "keep.json");
            string original = "{\"user\":[\"viewer\"],\"system\":[\"explorer\"],\"services\":[\"Everything\"],\"graceful\":[],\"networkRepair\":false,\"custom\":{\"revision\":1}}";
            string rule = KillAllBack.KeepRule.File(@"C:\Apps\Tool\viewer.exe");
            Check("粘贴带引号的完整文件路径", rule, KillAllBack.KeepRule.File("\"C:\\Apps\\Tool\\viewer.exe\""));
            try
            {
                File.WriteAllText(config, original);
                CheckBool("添加规则后写入配置", KillAllBack.KeepRuleStore.Change(config, "user", rule, true));
                string backupDirectory = Path.Combine(directory, "backups");
                string[] backups = System.IO.Directory.Exists(backupDirectory)
                    ? System.IO.Directory.GetFiles(backupDirectory, "keep.json.bak-*")
                    : new string[0];
                CheckBool("备份目录只保存一份变更前的完整配置", backups.Length == 1 &&
                    File.ReadAllText(backups[0]) == original &&
                    System.IO.Directory.GetFiles(directory, "keep.json.bak-*").Length == 0);
                string after = File.ReadAllText(config);
                CheckBool("相同路径不同大小写的规则不重复添加", !KillAllBack.KeepRuleStore.Change(config, "user",
                    KillAllBack.KeepRule.File(@"c:\apps\tool\VIEWER.exe"), true) && File.ReadAllText(config) == after);
                CheckBool("保留现有系统、服务、网络开关与扩展配置",
                    after.Contains("\"system\": [\"explorer\"]") && after.Contains("\"services\": [\"Everything\"]") &&
                    after.Contains("\"networkRepair\": false") && after.Contains("\"custom\": {\"revision\":1}"));
                File.WriteAllText(config, after.Replace("\"revision\":1", "\"revision\":2"));
                CheckBool("撤销保留外部写入和旧规则", KillAllBack.KeepRuleStore.Change(config, "user", rule, false) &&
                    File.ReadAllText(config).Contains("\"custom\": {\"revision\":2}") &&
                    File.ReadAllText(config).Contains("\"user\": [\"viewer\"]"));
                CheckBool("撤销已有其他保护时仍可命中", KillAllBack.Program.MatchKeepRule(
                    new List<string> { "viewer" }, "viewer", @"C:\Apps\Tool\viewer.exe") == "viewer");
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                string variant = KillAllBack.KeepRule.File(@"c:\apps\tool\VIEWER.exe");
                string concurrent = File.ReadAllText(config).Replace("\"viewer\"]",
                    "\"viewer\"," + serializer.Serialize(variant) + "," + serializer.Serialize(rule) + "]");
                File.WriteAllText(config, concurrent);
                CheckBool("撤销只移除原始条目，保留外部加入的大小写变体",
                    KillAllBack.KeepRuleStore.Change(config, "user", rule, false) &&
                    File.ReadAllText(config).Contains(serializer.Serialize(variant)) &&
                    !File.ReadAllText(config).Contains(serializer.Serialize(rule)));
                string actual = File.ReadAllText(config);
                string rejectedBackup = Path.Combine(directory, "conflict.bak");
                bool conflict = false;
                try { KillAllBack.AtomicConfig.Replace(config, concurrent, original, rejectedBackup); }
                catch (IOException) { conflict = true; }
                CheckBool("文件内容变更后，原子写入中止并保留最新配置",
                    conflict && File.ReadAllText(config) == actual && !File.Exists(rejectedBackup) &&
                    System.IO.Directory.GetFiles(directory, "*.tmp-*").Length == 0);
                var existingRules = new List<string> { "viewer", @"exe=C:/Apps/Tool/helper.exe", @"dir=C:\Apps\Tool\" };
                File.WriteAllText(config, "{\"User\":" + serializer.Serialize(existingRules) + "}");
                CheckBool("语义相同的分隔符和目录结尾不会生成重复规则",
                    !KillAllBack.KeepRuleStore.Change(config, "user", KillAllBack.KeepRule.File(@"C:\Apps\Tool\helper.exe"), true) &&
                    !KillAllBack.KeepRuleStore.Change(config, "user", KillAllBack.KeepRule.Directory(@"C:\Apps\Tool"), true));
                CheckBool("按原有大小写写回规则组并保留旧保护",
                    KillAllBack.KeepRuleStore.Change(config, "user", rule, true) &&
                    KillAllBack.KeepRuleStore.Read(config).user.Count == 4 &&
                    File.ReadAllText(config).Contains("\"User\"") && !File.ReadAllText(config).Contains("\"user\""));
                File.WriteAllText(config, "{\"USER\":[null,\"viewer\"],\"networkRepair\":false}");
                CheckBool("清理和写入共用规则读取，保留旧配置的空条目和大小写",
                    KillAllBack.KeepRuleStore.Change(config, "user", rule, true) &&
                    KillAllBack.KeepRuleStore.Read(config).user.SequenceEqual(new[] { null, "viewer", rule }) &&
                    KillAllBack.KeepRuleStore.Read(config).networkRepair == false);
                const string ambiguous = "{\"User\":[\"first\"],\"user\":[\"second\"]}";
                File.WriteAllText(config, ambiguous);
                bool duplicateGroup = false;
                try { KillAllBack.KeepRuleStore.Read(config); }
                catch (InvalidDataException) { duplicateGroup = true; }
                CheckBool("启动读取拒绝大小写重复的规则组", duplicateGroup && File.ReadAllText(config) == ambiguous);
                foreach (var invalid in new Dictionary<string, string>
                {
                    { "重复的 user 字段", "{\"user\":[\"first\"],\"user\":[\"second\"]}" },
                    { "转义后重复的 user 字段", "{\"user\":[\"first\"],\"\\u0075ser\":[\"second\"]}" },
                    { "只有空白项的 system 组", "{\"system\":[\" \",null]}" },
                    { "相对的 exe 规则", "{\"user\":[\"exe=viewer.exe\"]}" },
                    { "不完整的目录规则", "{\"user\":[\"dir=C:\"]}" },
                    { "只指向根目录的 exe 规则", "{\"user\":[\"exe=C:\\\\\"]}" }
                })
                {
                    File.WriteAllText(config, invalid.Value);
                    bool rejectedRule = false;
                    try { KillAllBack.KeepRuleStore.Read(config); }
                    catch (InvalidDataException) { rejectedRule = true; }
                    CheckBool("启动读取拒绝" + invalid.Key, rejectedRule);
                }
                const string invalidOtherGroup = "{\"system\":\"bad\",\"user\":[]}";
                File.WriteAllText(config, invalidOtherGroup);
                bool invalidConfig = false;
                try { KillAllBack.KeepRuleStore.Change(config, "user", rule, true); }
                catch (InvalidDataException) { invalidConfig = true; }
                CheckBool("其余规则组无法被清理引擎读取时拒绝保存", invalidConfig &&
                    File.ReadAllText(config) == invalidOtherGroup);
                File.WriteAllText(config, "{invalid");
                bool rejected = false;
                try { KillAllBack.KeepRuleStore.Change(config, "user", rule, true); }
                catch (ArgumentException) { rejected = true; }
                CheckBool("现有配置无效时拒绝写入", rejected && File.ReadAllText(config) == "{invalid");
            }
            finally
            {
                System.IO.Directory.Delete(directory, true);
            }
        }

        private static string Match(string name, string path)
        {
            return KillAllBack.Program.MatchKeepRule(Rules, name, path);
        }

        // ---------------- [2] PID 复用判定 ----------------
        private static void WorkingSetTrimOrder()
        {
            var remaining = new[]
            {
                new KillAllBack.ProcInfo { Pid = 101, WsMb = 40, StartTicks = 1000 },
                new KillAllBack.ProcInfo { Pid = 102, WsMb = 512, StartTicks = 2000 },
                new KillAllBack.ProcInfo { Pid = 103, WsMb = 39.9, StartTicks = 3000 },
                new KillAllBack.ProcInfo { Pid = 104, WsMb = 1024, StartTicks = 0 },
                new KillAllBack.ProcInfo { Pid = 105, WsMb = 256, StartTicks = 5000 }
            };
            var targets = KillAllBack.Program.SelectWorkingSetTrimTargets(remaining);
            Check("工作集回收大户优先，跳过低于阈值和身份未知的进程",
                "102,105,101", string.Join(",", targets.Select(p => p.Pid)));
        }

        private static void CleanupBatchDispatch()
        {
            var targets = Enumerable.Range(1, 35).Select(pid => new KillAllBack.ProcInfo { Pid = pid }).ToList();
            var groups = new List<int>();
            var seen = new List<int>();
            KillAllBack.Program.RunTaskKillBatches(targets, () => 50, p => p.Pid != 20 && p.Pid != 35,
                p => { throw new InvalidOperationException("预算充足时不应跳过"); },
                (batch, time) => { groups.Add(batch.Count); seen.AddRange(batch.Select(p => p.Pid)); });
            Check("33 个通过核验的目标合并为 3 批", "16,16,1", string.Join(",", groups));
            CheckBool("未通过核验的 PID 不进入任何批次", seen.Count == 33 && !seen.Contains(20) && !seen.Contains(35));

            var expired = new List<int>();
            int remaining = 10;
            KillAllBack.Program.RunTaskKillBatches(targets.Take(3).ToList(), () => remaining, p =>
                { if (p.Pid == 2) { remaining = 0; } return true; }, p => expired.Add(p.Pid),
                (batch, time) => { throw new InvalidOperationException("核验耗尽预算后不得启动外部工具"); });
            Check("核验耗尽预算时连同已排队的目标一起跳过", "1,2,3", string.Join(",", expired.OrderBy(pid => pid)));
        }

        private static void ExitConfirmationBudget()
        {
            var pending = new List<int> { 200, 201 };
            var confirmed = new List<int>();
            KillAllBack.Program.ConfirmExitRequests(pending, () => 0,
                (pid, waitMs) => waitMs == 0 && pid == 200, confirmed.Add);
            CheckBool("首轮立即确认已发出退出信号的目标，迟退目标继续等待",
                confirmed.SequenceEqual(new[] { 200 }) && pending.SequenceEqual(new[] { 201 }));

            int elapsed = 1172;
            KillAllBack.Program.ConfirmExitRequests(pending, () => 3000 - elapsed, delegate(int pid, int waitMs)
            {
                elapsed += Math.Min(waitMs, 1500 - elapsed);
                return elapsed == 1500;
            }, confirmed.Add);
            CheckBool("收尾用剩余预算接住晚于首轮等待的退出信号",
                elapsed == 1500 && pending.Count == 0 && confirmed.Contains(201));

            elapsed = 2800;
            pending.AddRange(new[] { 301, 302, 303 });
            var waits = new List<int>();
            KillAllBack.Program.ConfirmExitRequests(pending, () => 3000 - elapsed, delegate(int pid, int waitMs)
            {
                waits.Add(waitMs);
                if (pid == 302) { return true; }
                elapsed += waitMs;
                return false;
            }, confirmed.Add);
            CheckBool("多个迟退进程共用总截止时间，耗尽后仍检查已退出目标",
                waits.SequenceEqual(new[] { 200, 0, 0 }) && pending.SequenceEqual(new[] { 301, 303 })
                    && confirmed.Contains(302));
        }

        private static void TaskKillOutput()
        {
            string output = "SUCCESS: The process with PID 12 (child process of PID 900) has been terminated.\r\n"
                + "ERROR: The process with PID 17 could not be terminated.\r\n"
                + "成功: 已终止 PID 23。\r\n";
            Check("混合批次逐条记录成功，不把父进程和失败项计入", "12,23",
                string.Join(",", KillAllBack.Program.ReportedTaskKillPids(output).OrderBy(pid => pid)));
        }

        private static void CleanupDeadlines()
        {
            Check("第 2.5 秒前首轮尚有 1 毫秒", "1", KillAllBack.Program.InitialCleanupRemaining(501).ToString());
            Check("第 2.5 秒起转入复查", "0", KillAllBack.Program.InitialCleanupRemaining(500).ToString());
            Check("已重叠等待的时间从复活窗口扣除", "50", KillAllBack.Program.RelaunchWaitMs(2200, 2400).ToString());
            Check("复活观察不占用最后 500 毫秒", "30", KillAllBack.Program.RelaunchWaitMs(530, 550).ToString());
            Check("没有明确来源时立即复查", "0", KillAllBack.Program.RelaunchWaitMs(2200, 0).ToString());

            var candidates = Enumerable.Range(1000, 128).Select(id => new KillAllBack.ProcInfo { Pid = id }).ToList();
            int batches = 0;
            int requests = 0;
            KillAllBack.Program.RunTaskKillBatches(candidates, () => 1000, candidate => true,
                candidate => { throw new InvalidOperationException("预算充足"); },
                (batch, time) => { batches++; requests += batch.Count; });
            CheckBool("128 个候选分为八批处理", batches == 8 && requests == 128);

            int skipped = 0;
            int remaining = 1;
            KillAllBack.Program.RunTaskKillBatches(candidates, () => remaining, candidate => true,
                candidate => skipped++, (batch, time) => { remaining = 0; });
            Check("截止后剩余批次停止提交", "112", skipped.ToString());
        }

        private static void ServiceStopsDoNotBlock()
        {
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                var work = KillAllBack.Program.StartServiceStops(new[] { "slow", "fast", "FAST" }, () => 1000,
                    delegate(string name, Func<int> budget)
                    {
                        if (name == "slow") { entered.Set(); release.WaitOne(); }
                        var status = new KillAllBack.ServiceReport();
                        status.Stopped.Add(name);
                        return status;
                    });
                try
                {
                    CheckBool("慢服务在后台等待，其他工作继续", entered.WaitOne(1500) && work["fast"].Wait(1500) && !work["slow"].IsCompleted);
                    var report = new KillAllBack.ServiceReport();
                    KillAllBack.Program.CollectServiceStops(work, report, () => 0);
                    Check("到点保留已确认的服务结果", "fast", string.Join(",", report.Stopped));
                    CheckBool("未返回的停服工作记为未确认", report.Failed.Count == 1 && report.Failed[0].Name == "slow" && !report.Failed[0].Skipped);
                }
                finally
                {
                    release.Set();
                    Task.WaitAll(work.Values.Cast<Task>().ToArray());
                }
            }
        }

        private static void LateRelaunchObservation()
        {
            int time = 0;
            KillAllBack.Program.ObserveRelaunches(() => 2700 - time, () => time >= 15 ? 2685 : 0,
                () => time < 15, wait => time += wait);
            Check("提供时间给迟到退出信号和 200 毫秒后的复活实例", "250", time.ToString());

            time = 0;
            KillAllBack.Program.ObserveRelaunches(() => 530 - time, () => 0, () => true,
                wait => time += wait);
            Check("观察未确认的退出也给复查留出 500 毫秒", "30", time.ToString());

            time = 0;
            KillAllBack.Program.ObserveRelaunches(() => 2700 - time, () => 0, () => false,
                wait => time += wait);
            Check("没有明确来源的退出请求时没有主动等待", "0", time.ToString());

            Check("待确认的优雅关闭在复查时保留最后 50 毫秒", "15",
                KillAllBack.Program.ReviewCleanupRemaining(65, true).ToString());
        }

        private static void ParentSnapshotIdentity()
        {
            using (var self = Process.GetCurrentProcess())
            {
                var candidate = new KillAllBack.ProcInfo
                {
                    Pid = self.Id, Path = self.MainModule.FileName, StartTicks = self.StartTime.Ticks
                };
                var parents = KillAllBack.NativeIdentity.Parents();
                int expected = parents[self.Id];
                CheckBool("实际句柄与快照身份一致时取得父 PID", expected > 0
                    && KillAllBack.Program.ReadVerifiedParent(candidate, parents) == expected);
                candidate.StartTicks++;
                CheckBool("创建时间不符时不信旧父 PID",
                    KillAllBack.Program.ReadVerifiedParent(candidate, parents) == 0);
                candidate.StartTicks--;
                candidate.Path = @"C:\Other\not-this-program.exe";
                CheckBool("映像路径不符时不信旧父 PID",
                    KillAllBack.Program.ReadVerifiedParent(candidate, parents) == 0);
            }
        }

        private static void AttributedRelaunches()
        {
            string path = @"C:\Apps\Example\app.exe";
            var started = new DateTime(2026, 11, 25, 10, 0, 0);
            var source = new KillAllBack.TargetIdentity(500, started.ToUniversalTime().Ticks);
            var previous = new KillAllBack.ProcInfo
            {
                Pid = 501, Path = path, StartTicks = started.AddSeconds(1).Ticks, ParentPid = 500, RelaunchSource = source,
                ExitUtcTicks = started.AddSeconds(2).ToUniversalTime().Ticks,
                ExitObservedRemaining = 2500
            };
            var original = new KillAllBack.ProcInfo { Pid = 502, Path = path, StartTicks = started.AddSeconds(1).Ticks };
            var pending = new KillAllBack.ProcInfo { Pid = 503, Path = path, StartTicks = started.AddSeconds(1).Ticks };
            var revived = new KillAllBack.ProcInfo { Pid = 601, Path = path.ToUpperInvariant(), StartTicks = started.AddMilliseconds(2200).Ticks, ParentPid = 500 };
            var manual = new KillAllBack.ProcInfo { Pid = 602, Path = path, StartTicks = started.AddMilliseconds(2200).Ticks, ParentPid = 700 };
            var earlier = new KillAllBack.ProcInfo { Pid = 603, Path = path, StartTicks = started.AddMilliseconds(1900).Ticks, ParentPid = 500 };
            var otherFile = new KillAllBack.ProcInfo { Pid = 604, Path = @"C:\Other\app.exe", StartTicks = started.AddMilliseconds(2200).Ticks, ParentPid = 500 };
            var replaced = new KillAllBack.ProcInfo { Pid = 502, Path = otherFile.Path, StartTicks = revived.StartTicks, ParentPid = 500 };
            var busy = new HashSet<string> { pending.InstanceKey };
            var targets = KillAllBack.Program.SelectCleanupReviewTargets(new[] { previous, original, pending },
                new[] { revived, manual, earlier, otherFile, replaced, pending, original }, busy,
                pid => pid == 500 ? source.Started : (long?)null, candidate => candidate.ParentPid);
            Check("优先处理原目标，200 毫秒后只补杀可归因的复活", "502,601", string.Join(",", targets.Select(p => p.Pid)));
            targets = KillAllBack.Program.SelectCleanupReviewTargets(new[] { previous }, new[] { revived },
                new HashSet<string>(), pid => source.Started + 1, candidate => candidate.ParentPid);
            CheckBool("后台父进程 PID 复用后不沿用旧归因", targets.Count == 0);
            targets = KillAllBack.Program.SelectCleanupReviewTargets(new[] { previous }, new[] { revived },
                new HashSet<string>(), pid => source.Started, candidate => 700);
            CheckBool("旧父 PID 提示不能覆盖同一候选句柄读出的实际父进程", targets.Count == 0);
            previous.ExitUtcTicks = 0;
            targets = KillAllBack.Program.SelectCleanupReviewTargets(new[] { previous }, new[] { revived },
                new HashSet<string>(), pid => source.Started, candidate => candidate.ParentPid);
            CheckBool("原实例未确认退出时不追新实例", targets.Count == 0);
        }

        private static void PartialBatchClaims()
        {
            string scope = "offline-batch-" + Guid.NewGuid().ToString("N");
            var conflict = new KillAllBack.TargetIdentity(410, 100);
            var fresh = new KillAllBack.TargetIdentity(411, 200);
            var reused = new KillAllBack.TargetIdentity(410, 300);
            using (var first = new KillAllBack.RunCoordinator(scope))
            using (var peer = new KillAllBack.RunCoordinator(scope))
            using (var observer = new KillAllBack.RunCoordinator(scope))
            {
                first.TryClaim(new[] { conflict });
                var admitted = peer.ClaimAvailable(new[] { conflict, fresh, reused }, () => 1000);
                Check("同批部分冲突时继续认领其他目标", "410:300,411:200", string.Join(",", admitted.OrderBy(key => key)));
                CheckBool("仅记录发生冲突的目标", peer.SharedCount == 1);
                CheckBool("第三个实例无法取得对方已认领的目标", !observer.TryClaim(new[] { fresh }));
            }
        }

        private static void RunClaims()
        {
            string scope = "offline-cleanup-" + Guid.NewGuid().ToString("N");
            var first = new KillAllBack.TargetIdentity(331, 100);
            var child = new KillAllBack.TargetIdentity(332, 120);
            var reused = new KillAllBack.TargetIdentity(331, 200);
            using (var peer = new KillAllBack.RunCoordinator(scope))
            {
                using (var owner = new KillAllBack.RunCoordinator(scope))
                {
                    CheckBool("首次认领进程树", owner.TryClaim(new[] { first, child }));
                    CheckBool("其他实例与任一已认领子进程冲突", !peer.TryClaim(new[] { reused, child }));
                    CheckBool("PID 复用后的新实例可独立认领", peer.TryClaim(new[] { reused }));

                    using (var held = new ManualResetEvent(false))
                    using (var release = new ManualResetEvent(false))
                    {
                        var blocker = new Thread(delegate()
                        {
                            using (new KillAllBack.NamedGate("lease:" + scope, 1000))
                            {
                                held.Set();
                                release.WaitOne();
                            }
                        });
                        blocker.Start();
                        bool repeated = false;
                        bool expired = false;
                        bool finishedWithoutClaim = false;
                        var idle = new KillAllBack.RunCoordinator(scope);
                        try
                        {
                            if (held.WaitOne(1000))
                            {
                                repeated = owner.TryClaim(new[] { child });
                                try { peer.TryClaim(new[] { first }, () => 0); }
                                catch (TimeoutException) { expired = true; }
                                idle.Dispose();
                                finishedWithoutClaim = true;
                            }
                        }
                        catch (TimeoutException) { }
                        finally { release.Set(); blocker.Join(); idle.Dispose(); }
                        CheckBool("同一运行复用已认领目标，不等待共享锁", repeated);
                        CheckBool("预算耗尽时不等待共享锁以申请新租约", expired);
                        CheckBool("没有租约的实例退出时不等待共享锁", finishedWithoutClaim);
                    }
                }
                CheckBool("释放租约后，其他实例获得原目标", peer.TryClaim(new[] { first, child }));
            }
        }

        private static void Identity()
        {
            Console.WriteLine("[2] PID 复用判定 IdentityMismatch");
            Check("名字与创建时间都一致 -> 还是同一个进程", null,
                KillAllBack.Program.IdentityMismatch("chrome", 100L, "chrome", 100L));
            Check("名字大小写不同不算复用", null,
                KillAllBack.Program.IdentityMismatch("Chrome", 100L, "chrome", 100L));
            Check("名字变了 -> 复用", "pid 已被复用（现在是 notepad）",
                KillAllBack.Program.IdentityMismatch("chrome", 100L, "notepad", 100L));
            Check("同名但创建时间不同 -> 复用", "pid 已被复用（创建时间不一致）",
                KillAllBack.Program.IdentityMismatch("chrome", 100L, "chrome", 200L));
            Check("创建时间取不到（0）时只按名字判 -> 同名放行", null,
                KillAllBack.Program.IdentityMismatch("chrome", 0L, "chrome", 0L));
            Check("计划里没记名字时只按时间判 -> 时间不同即复用", "pid 已被复用（创建时间不一致）",
                KillAllBack.Program.IdentityMismatch(null, 100L, "chrome", 200L));
            Console.WriteLine();
        }

        // ---------------- [3] 日志追加 / 截断 ----------------
        private static void LogBlocks()
        {
            Console.WriteLine("[3] 日志追加与块感知截断");
            string log = SelfDir() + "\\_keeptest-kill.log";
            string log2 = SelfDir() + "\\_keeptest-noblock.log";
            try
            {
                if (File.Exists(log)) { File.Delete(log); }
                if (File.Exists(log2)) { File.Delete(log2); }

                for (int i = 1; i <= 25; i++)
                {
                    KillAllBack.Program.AppendLogBlock(
                        "=== 2026-10-06 12:00:" + i.ToString("00") + " | mode=dry-run | elevated=True | elapsed=1s ==="
                        + Environment.NewLine + "plan: " + i + " process(es)" + Environment.NewLine, log);
                }
                string all = File.ReadAllText(log, Encoding.UTF8);
                CheckBool("追加 25 段后文件里 25 个块头", CountBlocks(all) == 25);
                CheckBool("纯追加：第一段还在开头", all.StartsWith("=== 2026-10-06 12:00:01"));

                // 手动触发一次截断（线上条件是超过 512KB，这里直接调）
                KillAllBack.Program.TrimLogFile(log, 1024 * 1024);
                string trimmed = File.ReadAllText(log, Encoding.UTF8);
                CheckBool("截断后只剩最近 20 段", CountBlocks(trimmed) == 20);
                CheckBool("截断保留了最新的那段", trimmed.Contains("12:00:25"));
                CheckBool("截断丢掉了最老的那段", !trimmed.Contains("12:00:01 |"));
                CheckBool("截断后仍以块头开头（不留半段）", trimmed.StartsWith("=== "));

                KillAllBack.Program.AppendLogBlock("没有块头的普通文本", log2);
                CheckBool("没有块头的文件截断时不动它", File.ReadAllText(log2).Contains("没有块头"));
            }
            finally
            {
                try { if (File.Exists(log)) { File.Delete(log); } } catch { }
                try { if (File.Exists(log2)) { File.Delete(log2); } } catch { }
            }
            Console.WriteLine();
        }

        private static string SelfDir()
        {
            try { return Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); }
            catch { return "."; }
        }

        private static int CountBlocks(string text)
        {
            int n = 0;
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith("=== ", StringComparison.Ordinal)) { n++; }
            }
            return n;
        }

        // ---------------- [4] 版本解析 + JSON 字面量 ----------------
        private static void VersionAndJson()
        {
            Console.WriteLine("[4] 版本解析与 JSON 字面量");
            string dir = SelfDir();
            string cl = dir + "\\_keeptest-changelog.md";
            string cl2 = dir + "\\_keeptest-noversion.md";
            try
            {
                File.WriteAllText(cl, "# 更新日志" + Environment.NewLine + Environment.NewLine
                    + "## v1.1.1-dev — 2026-10-06（§9 落地）" + Environment.NewLine
                    + "- 内容" + Environment.NewLine
                    + "## v1.1.0-dev — 2026-09-28" + Environment.NewLine, new UTF8Encoding(true));
                Check("从 CHANGELOG 首个版本标题解析", "1.1.1-dev", KillAllBack.Program.ParseVersionFromChangelog(cl));

                File.WriteAllText(cl2, "# 没有版本号的文件" + Environment.NewLine + "## 只是一个标题" + Environment.NewLine);
                Check("没有版本标题时返回不匹配", null, KillAllBack.Program.ParseVersionFromChangelog(cl2));
                Check("文件不存在时返回不匹配", null, KillAllBack.Program.ParseVersionFromChangelog(dir + "\\_不存在.md"));
                Check("路径为 null 不炸", null, KillAllBack.Program.ParseVersionFromChangelog(null));
            }
            finally
            {
                try { if (File.Exists(cl)) { File.Delete(cl); } } catch { }
                try { if (File.Exists(cl2)) { File.Delete(cl2); } } catch { }
            }

            Check("null -> \"\"", "\"\"", KillAllBack.Program.JsonStringLiteral(null));
            Check("引号被转义", "\"a\\\"b\"", KillAllBack.Program.JsonStringLiteral("a\"b"));
            Check("反斜杠被转义", "\"C:\\\\Windows\"", KillAllBack.Program.JsonStringLiteral("C:\\Windows"));
            Check("换行 / 制表符被转义", "\"a\\nb\\tc\"", KillAllBack.Program.JsonStringLiteral("a\nb\tc"));
            Check("中文原样保留", "\"白名单\"", KillAllBack.Program.JsonStringLiteral("白名单"));
            Console.WriteLine();
        }
    }
}
