using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Threading;
using System.Windows.Forms;

namespace KillAllBack
{
    public static partial class Program
    {
        internal static void CheckPartialCleanupResult(string output)
        {
            var before = new MemorySnapshot { TotalMB = 16384, UsedMB = 8192, AvailMB = 8192, LoadPercent = 50, SessionWsMB = 5120 };
            var after = new MemorySnapshot { TotalMB = 16384, UsedMB = 9216, AvailMB = 7168, LoadPercent = 56, SessionWsMB = 3072 };
            var killed = new List<ProcInfo> { new ProcInfo { Name = "viewer", Path = @"C:\Apps\viewer.exe" } };
            var failed = new List<ProcInfo> { new ProcInfo { Name = "locked", FailReason = "访问被拒绝" } };
            var net = new NetworkReport();
            var services = new ServiceReport();
            services.Failed.Add(new ServiceFailure("ExampleService", "清理预算已耗尽", true));
            services.Planned.Add("ExampleService");
            UiShell shell = BuildUiShell(false, killed, failed, new List<ProcInfo>(), killed,
                services, net, before, after, 2048, 1.8, null);
            using (Form form = shell.Form)
            {
                WhitelistUiTests.ShowOffscreen(form);
                if (shell.Hero.FreedLabel != "物理内存已用变化" || shell.Hero.HeroText != "+1.00" ||
                    shell.List.Rows[0].State != "未结束" || shell.List.Rows[0].Path != "访问被拒绝" ||
                    shell.List.Rows[1].State != "服务跳过" || shell.List.Rows[1].Path != "清理预算已耗尽" ||
                    !WhitelistUiTests.Find(form, "CleanupSummary").Text.StartsWith("1 个进程未结束 · 跳过 1 项"))
                    throw new InvalidOperationException("部分未完成时应先列失败，并按物理内存显示有符号差值。");
                if (NetworkText(false, net) != "尚未检测网络")
                    throw new InvalidOperationException("未检测的网络不应显示连接异常。");
                WhitelistUiTests.Screenshot(form, output);
                shell.UpdateNetwork(new NetworkReport { State = NetworkCheckState.Pending });
                if (WhitelistUiTests.Find(form, "NetworkDetails").Text != "网络检测中")
                    throw new InvalidOperationException("等待只读网络检测应显示检测中。");
                var originalRow = shell.List.Rows[0];
                var networkIssue = new NetworkReport
                {
                    State = NetworkCheckState.Completed, Repairable = true, OnlineDetail = "合成探测失败"
                };
                shell.UpdateNetwork(networkIssue);
                WhitelistUiTests.ToggleDetails(form);
                if (!WhitelistUiTests.Find(form, "RepairNetwork").Visible ||
                    !ReferenceEquals(originalRow, shell.List.Rows[0]))
                    throw new InvalidOperationException("检测完成后应在详情提供手动修复入口，并保留处理记录。");
                shell.UpdateNetwork(new NetworkReport { State = NetworkCheckState.Completed, OnlineAfter = true });
                if (WhitelistUiTests.Find(form, "RepairNetwork").Visible)
                    throw new InvalidOperationException("网络正常时应收起修复入口。");
                form.Close();

                UiShell preview = BuildUiShell(true, new List<ProcInfo>(), new List<ProcInfo>(),
                    new List<ProcInfo>(), killed, services, networkIssue, before, null, 0, 0, null);
                using (Form previewForm = preview.Form)
                {
                    WhitelistUiTests.ShowOffscreen(previewForm);
                    WhitelistUiTests.ToggleDetails(previewForm);
                    if (preview.Hero.FreedLabel != "计划进程工作集" ||
                        preview.List.Rows[1].Path != "Windows 服务 · 临时停止" ||
                        WhitelistUiTests.Find(previewForm, "RepairNetwork").Visible)
                        throw new InvalidOperationException("预演只应显示计划和只读检测，且应说明服务临时停止。");
                    previewForm.Close();
                }
            }
        }

        internal static void CheckWhitelistNavigation(string configurationPath, string screenshot)
        {
            keepPath = configurationPath;
            baseDir = Path.GetDirectoryName(configurationPath);
            suppressAnimation = true;
            uiWidthOverride = 1040;
            uiHeightOverride = 680;

            var before = new MemorySnapshot { TotalMB = 16384, UsedMB = 9300, AvailMB = 7084, SessionWsMB = 4300, LoadPercent = 57 };
            var after = new MemorySnapshot { TotalMB = 16384, UsedMB = 7300, AvailMB = 9084, SessionWsMB = 2300, LoadPercent = 45 };
            var killed = new List<ProcInfo>
            {
                new ProcInfo { Name = "viewer", Path = @"C:\Apps\Viewer\viewer.exe", WsMb = 275 },
                new ProcInfo { Name = "viewer", Path = @"D:\Apps\Viewer\viewer.exe", WsMb = 140 },
                new ProcInfo { Name = "viewer", Path = @"c:\Apps\Viewer\viewer.exe", WsMb = 80 },
                new ProcInfo { Name = "unreadable", Path = "" }
            };
            var none = new List<ProcInfo>();
            var live = new UiLiveState { Preparing = true, Net = new NetworkReport { OnlineAfter = true }, MemAfter = after, FreedMb = 2000, Elapsed = 1.8 };
            UiShell shell = BuildUiShell(false, killed, none, none, killed, new ServiceReport(), new NetworkReport(), before, after, 2000, 1.8, live);
            using (Form form = shell.Form)
            {
                WhitelistUiTests.ShowOffscreen(form);
                var whitelist = (Button)WhitelistUiTests.Find(form, "KeepPageTab");
                if (whitelist.Visible) throw new InvalidOperationException("清理进行中不应显示白名单页面入口。");

                shell.FillResult();
                var result = WhitelistUiTests.Find(form, "CleanupResultPage");
                if (!whitelist.Visible || !result.Visible || shell.List.Rows.Count != killed.Count)
                    throw new InvalidOperationException("清理完成后没有形成预期的结果页。");

                var recordedRow = shell.List.Rows[0];
                whitelist.PerformClick();
                Application.DoEvents();
                var page = WhitelistUiTests.Find(form, "KeepPage");
                if (!page.Visible || result.Visible || !ReferenceEquals(page.FindForm(), form) || Application.OpenForms.Count != 1)
                    throw new InvalidOperationException("白名单应在原窗口内切换。");

                ((Button)WhitelistUiTests.Find(form, "CleanupResultTab")).PerformClick();
                Application.DoEvents();
                if (!result.Visible || page.Visible || shell.List.Rows.Count != killed.Count ||
                    !ReferenceEquals(recordedRow, shell.List.Rows[0]))
                    throw new InvalidOperationException("返回清理结果时列表发生了变化。");

                string original = File.ReadAllText(configurationPath);
                var keep = (Button)WhitelistUiTests.Find(form, "KeepResultRow0");
                var other = (Button)WhitelistUiTests.Find(form, "KeepResultRow1");
                var sameFile = (Button)WhitelistUiTests.Find(form, "KeepResultRow2");
                if (keep.Text != "下次保留" || other.Text != "下次保留" ||
                    !keep.Visible || !keep.CanSelect || form.Controls.Find("KeepResultRow3", true).Length != 0)
                    throw new InvalidOperationException("结果页只应为可识别路径的进程提供可操作的保留入口。");
                WhitelistUiTests.Screenshot(form, screenshot);

                form.ClientSize = new Size(390, 580);
                Application.DoEvents();
                keep.PerformClick();
                LoadKeep();
                string rule = KeepRule.File(killed[0].Path);
                if (FindMatchedKeepRule(killed[0].Name, killed[0].Path) != rule ||
                    FindMatchedKeepRule(killed[1].Name, killed[1].Path) != null ||
                    FindMatchedKeepRule(killed[2].Name, killed[2].Path) != rule ||
                    sameFile.Text != "撤销保留" || other.Text != "下次保留" ||
                    recordedRow.State != "已结束" || keep.Text != "撤销保留")
                    throw new InvalidOperationException("结果页应精确保留所选文件，并保留本次结束记录。");

                Application.DoEvents();
                Control viewport = shell.List.Parent;
                if (!keep.Visible || !viewport.RectangleToScreen(viewport.ClientRectangle)
                        .Contains(keep.RectangleToScreen(keep.ClientRectangle)))
                    throw new InvalidOperationException("窄窗口保存后应能在可视区域直接撤销。");
                WhitelistUiTests.Screenshot(form, Path.Combine(Path.GetDirectoryName(screenshot), "cleanup-result-kept-narrow.png"));

                KeepRuleStore.Change(configurationPath, "user", "viewer", true);
                keep.PerformClick();
                LoadKeep();
                if (keepUser.Contains(rule) || FindMatchedKeepRule(killed[0].Name, killed[0].Path) != "viewer" ||
                    keep.Text != "已保留" || other.Text != "已保留")
                    throw new InvalidOperationException("撤销应仅移除刚添加的规则，保留并显示其他保护。");

                File.WriteAllText(configurationPath, original);
                whitelist.PerformClick();
                ((Button)WhitelistUiTests.Find(form, "CleanupResultTab")).PerformClick();
                const string invalid = "{\"user\":[";
                File.WriteAllText(configurationPath, invalid);
                keep.PerformClick();
                if (File.ReadAllText(configurationPath) != invalid || keep.Text != "下次保留" ||
                    !WhitelistUiTests.Find(form, "ResultNotice").Text.Contains("白名单操作失败"))
                    throw new InvalidOperationException("结果页应报告写入失败并保留原配置与按钮状态。");

                File.WriteAllText(configurationPath, original);
                form.Close();
            }
        }
    }

    internal static class WhitelistUiTests
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.Apply();
            string output = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(output);
            string configDirectory = Path.Combine(Path.GetTempPath(), "killer-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(configDirectory);
            string config = Path.Combine(configDirectory, "keep.json");
            const string original = "{\"system\":[\"explorer\"],\"user\":[],\"services\":[],\"graceful\":[]}";
            try
            {
                File.WriteAllText(config, original);
                Program.CheckWhitelistNavigation(config, Path.Combine(output, "cleanup-result.png"));
                if (File.ReadAllText(config) != original) throw new InvalidOperationException("仅切换页面不应写入配置。");
                Console.WriteLine("通过：清理中、清理结果、同窗白名单、返回结果、结果行精确保留与撤销。");
                Program.CheckPartialCleanupResult(Path.Combine(output, "cleanup-partial.png"));
                Console.WriteLine("通过：失败与服务跳过置顶、物理内存有符号变化、网络状态与手动修复入口、预演只读。");
                CheckRulesPage(config, output);
                Console.WriteLine("通过：搜索、图标、行内保存、撤销、手动目录及未运行软件、路径不可读、写入失败及窄窗。");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
            finally
            {
                Directory.Delete(configDirectory, true);
            }
        }

        private static void CheckRulesPage(string file, string output)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string app = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
            var inventory = new List<KeepPageRow>
            {
                new KeepPageRow { Name = "PowerShell", Path = app, Pids = "1048, 1132", Instances = 2 },
                new KeepPageRow { Name = "同名工具", Path = @"C:\Apps\Example\viewer.exe", Pids = "1780", Instances = 1 },
                new KeepPageRow { Name = "同名工具", Path = @"D:\Apps\Example\viewer.exe", Pids = "1812", Instances = 1 },
                new KeepPageRow { Name = "PathUnavailable.exe", Path = "", Pids = "1920", Instances = 1 }
            };
            var serializer = new JavaScriptSerializer();
            Func<KeepPageConfig> read = delegate
            {
                var cfg = serializer.Deserialize<KeepConfig>(File.ReadAllText(file));
                return new KeepPageConfig { System = cfg.system, User = cfg.user, Services = cfg.services, Graceful = cfg.graceful };
            };
            bool failWrite = false;
            var page = new KeepPage(() => inventory, read, (group, rule, add) =>
            {
                if (failWrite) throw new IOException("测试用写入失败");
                return KeepRuleStore.Change(file, group, rule, add);
            }, null);

            using (var window = new Form { Text = "killer · 白名单（合成数据）", ClientSize = new Size(1040, 650) })
            {
                window.Controls.Add(page);
                ShowOffscreen(window);
                page.RefreshPage();
                WaitUntil(() => !page.IsLoading, "进程列表读取未完成");
                var grid = (DataGridView)Find(window, "KeepProcesses");
                var search = (TextBox)Find(window, "KeepSearch");
                var undo = (Button)Find(window, "KeepUndo");
                var apply = (Button)Find(window, "ApplyKeep");
                var status = Find(window, "KeepStatus");

                WaitUntil(() => grid.Rows.Count == 4 &&
                    !ReferenceEquals(grid.Rows[0].Cells[0].Value, grid.Rows[1].Cells[0].Value), "可读取的本地图标未显示");
                if (!Convert.ToString(grid.Rows[1].Cells[1].Value).Contains(@"C:\Apps\Example") ||
                    !Convert.ToString(grid.Rows[2].Cells[1].Value).Contains(@"D:\Apps\Example"))
                    throw new InvalidOperationException("列表应直接显示同名进程的所在目录。");
                if (!((TextBox)Find(window, "KeepPath")).WordWrap)
                    throw new InvalidOperationException("进程详情中的长路径应能换行。");
                grid.CurrentCell = grid.Rows[0].Cells[1];
                Screenshot(window, Path.Combine(output, "whitelist-wide.png"));

                search.Text = @"C:\Apps\Example";
                if (grid.Rows.Count != 1) throw new InvalidOperationException("完整路径搜索未缩小到唯一一行。");
                ClickProcessAction(grid);
                if (!read().User.Contains(KeepRule.File(inventory[1].Path)) || !undo.Visible ||
                    Program.MatchKeepRule(read().User, inventory[2].Name, inventory[2].Path) != null ||
                    !string.Equals(Convert.ToString(grid.Rows[0].Cells[2].Value), "已保留", StringComparison.Ordinal))
                    throw new InvalidOperationException("一击保留应仅添加当前 exe，保存后更新保留状态。");

                KeepRuleStore.Change(file, "user", inventory[1].Name, true);
                undo.PerformClick();
                if (read().User.Contains(KeepRule.File(inventory[1].Path)) ||
                    Program.MatchKeepRule(read().User, inventory[1].Name, inventory[1].Path) == null ||
                    !string.Equals(Convert.ToString(grid.Rows[0].Cells[2].Value), "已保留", StringComparison.Ordinal))
                    throw new InvalidOperationException("撤销应保留并显示其它仍命中的规则。");

                search.Text = "PathUnavailable";
                ClickProcessAction(grid);
                if (read().User.Contains("PathUnavailable.exe.exe") || apply.Text != "按进程名保留")
                    throw new InvalidOperationException("不可读路径需要明确采用进程名范围。");
                apply.PerformClick();
                if (!read().User.Contains("PathUnavailable.exe.exe"))
                    throw new InvalidOperationException("明确选择后应允许按进程名保存。");
                undo.PerformClick();

                search.Text = "PowerShell";
                failWrite = true;
                ClickProcessAction(grid);
                if (read().User.Contains(KeepRule.File(app)) || !status.Text.Contains("测试用写入失败") ||
                    !string.Equals(Convert.ToString(grid.Rows[0].Cells[2].Value), "未保留", StringComparison.Ordinal))
                    throw new InvalidOperationException("保存失败应就地提示并维持真实状态。");

                failWrite = false;
                var scope = (ComboBox)Find(window, "KeepScope");
                var entry = (TextBox)Find(window, "KeepFolder");
                scope.SelectedIndex = 2;
                entry.Text = Path.GetDirectoryName(app);
                apply.PerformClick();
                string directoryRule = KeepRule.Directory(entry.Text);
                if (!read().User.Contains(directoryRule))
                    throw new InvalidOperationException("显式选择目录后应保存目录规则。");
                undo.PerformClick();

                ((Button)Find(window, "KeepRulesTab")).PerformClick();
                scope.SelectedIndex = 0;
                entry.Text = @"C:\Apps\Example\offline.exe";
                apply.PerformClick();
                if (!read().User.Contains(KeepRule.File(@"C:\Apps\Example\offline.exe")))
                    throw new InvalidOperationException("未运行的软件应可通过已保存规则页添加。");
                Screenshot(window, Path.Combine(output, "whitelist-rules.png"));
                undo.PerformClick();
                ((Button)Find(window, "KeepRunningTab")).PerformClick();
                search.Clear();
                page.RefreshPage();
                WaitUntil(() => !page.IsLoading, "刷新未完成");
                window.ClientSize = new Size(360, 380);
                Application.DoEvents();
                if (grid.GetRowDisplayRectangle(0, true).Height < grid.RowTemplate.Height / 2)
                    throw new InvalidOperationException("最小窗口未保留可用的首行。");
                Screenshot(window, Path.Combine(output, "whitelist-narrow.png"));

                window.ClientSize = new Size(1560, 975);
                page.SetScale(1.5F);
                Application.DoEvents();
                Screenshot(window, Path.Combine(output, "whitelist-scale150.png"));
                window.Close();
            }
        }

        private static void ClickProcessAction(DataGridView grid)
        {
            if (grid.Rows.Count != 1) throw new InvalidOperationException("测试需要一条可操作的进程。");
            var click = typeof(DataGridView).GetMethod("OnCellContentClick", BindingFlags.Instance | BindingFlags.NonPublic);
            click.Invoke(grid, new object[] { new DataGridViewCellEventArgs(grid.Columns["KeepAction"].Index, 0) });
            Application.DoEvents();
        }

        private static void WaitUntil(Func<bool> condition, string message)
        {
            var wait = Stopwatch.StartNew();
            do
            {
                Application.DoEvents();
                if (condition()) return;
                Thread.Sleep(20);
            } while (wait.ElapsedMilliseconds < 4500);
            throw new TimeoutException(message);
        }

        internal static Control Find(Control owner, string name)
        {
            Control[] found = owner.Controls.Find(name, true);
            if (found.Length != 1) throw new InvalidOperationException("找不到控件 " + name);
            return found[0];
        }

        internal static void ToggleDetails(Form form)
        {
            typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(Find(form, "DetailsToggle"), new object[] { EventArgs.Empty });
            Application.DoEvents();
        }

        internal static void ShowOffscreen(Form form)
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-4000, -4000);
            form.Show();
            Application.DoEvents();
        }

        internal static void Screenshot(Form form, string output)
        {
            using (var image = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(output, ImageFormat.Png);
            }
        }
    }
}
