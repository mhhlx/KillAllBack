using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KillAllBack
{
    internal sealed class KeepPageRow
    {
        internal string Name;
        internal string Path;
        internal string Pids;
        internal int Instances;
    }

    internal sealed class KeepPageConfig
    {
        internal List<string> System = new List<string>();
        internal List<string> User = new List<string>();
        internal List<string> Services = new List<string>();
        internal List<string> Graceful = new List<string>();
    }

    internal sealed class KeepPage : UserControl
    {
        private static readonly string[] GroupKeys = { "user", "system", "services", "graceful" };
        private readonly Func<IList<KeepPageRow>> scan;
        private readonly Func<KeepPageConfig> read;
        private readonly Func<string, string, bool, bool> change;
        private readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly Bitmap genericIcon = SystemIcons.Application.ToBitmap();
        private readonly ToolTip tips = new ToolTip();
        private readonly Label title = LabelFor("白名单", 17, FontStyle.Bold);
        private readonly Button backButton = ButtonFor("返回结果", "KeepBack", false);
        private readonly Button processTab = ButtonFor("运行中的进程", "KeepRunningTab", false);
        private readonly Button rulesTab = ButtonFor("已保存规则", "KeepRulesTab", false);
        private readonly TextBox search = TextFor("KeepSearch", false, false);
        private readonly RoundedField searchField;
        private readonly Button refresh = ButtonFor("刷新", "KeepReload", false);
        private readonly ComboBox group = Choice("KeepRuleGroup", "规则用途",
            "我的保留规则", "系统保留规则（只读）", "停止服务", "优雅关闭");
        private readonly KeepGrid processes = new KeepGrid("KeepProcesses", "运行中的进程");
        private readonly KeepGrid rules = new KeepGrid("KeepRules", "已保存规则");
        private readonly Label empty = LabelFor("", 9.5F, FontStyle.Regular);
        private readonly Panel detail = new Panel();
        private readonly Label detailTitle = LabelFor("进程详情", 10.5F, FontStyle.Bold);
        private readonly Label context = LabelFor("", 9F, FontStyle.Regular);
        private readonly TextBox location = TextFor("KeepPath", true, true);
        private readonly Button copy = ButtonFor("复制路径", "KeepCopyPath", false);
        private readonly TextBox matches = TextFor("KeepMatches", true, true);
        private readonly ComboBox scope = Choice("KeepScope", "保留范围");
        private readonly TextBox entry = TextFor("KeepFolder", false, false);
        private readonly Button apply = ButtonFor("保留", "ApplyKeep", true);
        private readonly Button remove = ButtonFor("移除规则", "KeepRemove", false);
        private readonly Label notice = LabelFor("", 9F, FontStyle.Regular);
        private readonly Button undo = ButtonFor("撤销", "KeepUndo", false);
        private IList<KeepPageRow> inventory = new List<KeepPageRow>();
        private KeepPageConfig config = new KeepPageConfig();
        private KeepPageRow selectedProcess;
        private string undoGroup;
        private string undoRule;
        private string scanMessage = "";
        private bool canEdit;
        private bool loading;
        private bool showRules;
        private bool populating;
        private readonly bool hasBack;
        private float scale = 1;

        internal KeepPage(Func<IList<KeepPageRow>> scan, Func<KeepPageConfig> read,
            Func<string, string, bool, bool> change, Action back)
        {
            this.scan = scan;
            this.read = read;
            this.change = change;
            hasBack = back != null;
            Name = "KeepPage";
            AccessibleName = "白名单";
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            Dock = DockStyle.Fill;
            TabStop = false;

            SuspendLayout();
            searchField = new RoundedField(search) { Radius = 6 };
            Controls.AddRange(new Control[] { backButton, title, processTab, rulesTab, searchField, refresh,
                group, processes, rules, empty, detail, notice, undo });
            backButton.Visible = hasBack;
            detail.BackColor = Theme.Surface;
            detail.AutoScroll = true;
            detail.Controls.AddRange(new Control[] { detailTitle, context, location, copy, matches, scope, entry, apply, remove });
            TabOrder(backButton, processTab, rulesTab, searchField, refresh, group, processes, rules, detail, undo);
            TabOrder(location, copy, matches, scope, entry, apply, remove);

            search.AccessibleDescription = "输入应用名称或可执行文件路径中的文字";
            AppleUi.Cue(search, "搜索名称或路径");
            location.AccessibleName = "可执行文件路径或规则";
            location.BackColor = Theme.Bg;
            location.WordWrap = true;
            matches.BackColor = Theme.Surface;
            matches.AccessibleName = "已命中的保留规则";
            matches.WordWrap = true;
            matches.ScrollBars = ScrollBars.None;
            empty.TextAlign = ContentAlignment.MiddleCenter;
            empty.ForeColor = Theme.Muted;
            empty.BackColor = Theme.Surface;
            empty.BringToFront();
            context.ForeColor = Theme.Muted;
            notice.Name = "KeepStatus";
            notice.ForeColor = Theme.Muted;
            group.SelectedIndex = 0;
            ConfigureTables();

            if (hasBack) backButton.Click += delegate { back(); };
            processTab.Click += delegate { ShowTab(false); };
            rulesTab.Click += delegate { ShowTab(true); };
            refresh.Click += delegate { RefreshPage(); };
            search.TextChanged += delegate { RenderProcesses(); };
            group.SelectedIndexChanged += delegate { ResetEntry(); RenderRules(); };
            processes.SelectionChanged += delegate { if (!populating && !showRules) UpdateDetails(); };
            rules.SelectionChanged += delegate { if (!populating && showRules) UpdateDetails(); };
            scope.SelectedIndexChanged += delegate
            {
                if (populating) return;
                if (showRules && group.SelectedIndex == 0) UpdateManualEntry();
                else UpdateScope();
            };
            scope.SelectionChangeCommitted += delegate
            {
                if (!showRules && scope.SelectedIndex == 2) detail.ScrollControlIntoView(apply);
            };
            apply.Click += delegate { ApplySelected(); };
            remove.Click += delegate
            {
                string key = CurrentRule();
                if (key != null) EditRule(GroupKeys[group.SelectedIndex], key, false, false);
            };
            copy.Click += delegate
            {
                try { Clipboard.SetText(location.Text); SetNotice("路径已复制", Theme.Muted); }
                catch (ExternalException ex) { SetNotice("复制路径失败。 " + ex.Message, Theme.DangerInk); }
            };
            undo.Click += delegate
            {
                if (undoRule != null) EditRule(undoGroup, undoRule, false, true);
            };
            entry.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter || !apply.Enabled) return;
                e.SuppressKeyPress = true;
                ApplySelected();
            };
            VisibleChanged += delegate { if (Visible) LayoutPage(); };

            ShowTab(false);
            ResumeLayout(false);
        }

        private void ConfigureTables()
        {
            processes.Columns.Add(new DataGridViewImageColumn { Name = "Icon", HeaderText = "",
                Image = genericIcon, ImageLayout = DataGridViewImageCellLayout.Zoom, SortMode = DataGridViewColumnSortMode.NotSortable });
            processes.Columns.Add(TextColumn("Application", "应用 / 进程", true));
            processes.Columns.Add(TextColumn("State", "保留状态", false));
            processes.Columns.Add(new DataGridViewLinkColumn { Name = "KeepAction", HeaderText = "操作",
                LinkColor = Theme.Primary, ActiveLinkColor = Theme.PrimaryHover, TrackVisitedState = false,
                LinkBehavior = LinkBehavior.HoverUnderline, SortMode = DataGridViewColumnSortMode.NotSortable });
            processes.CellContentClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == 3) ActivateRow((KeepPageRow)processes.Rows[e.RowIndex].Tag);
            };
            processes.ActivateRow = delegate { ActivateRow((KeepPageRow)processes.CurrentRow.Tag); };
            rules.Columns.Add(TextColumn("Rule", "规则", true));
            rules.Columns.Add(TextColumn("Scope", "范围", false));
        }

        internal bool IsLoading { get { return loading; } }

        internal void RefreshPage()
        {
            if (ReloadConfig() && notice.ForeColor == Theme.DangerInk) SetNotice("", Theme.Muted);
            RenderProcesses();
            RenderRules();
            if (!loading) LoadProcesses();
        }

        private bool ReloadConfig()
        {
            try { config = read(); canEdit = true; }
            catch (Exception ex)
            {
                canEdit = false;
                SetNotice("读取规则失败。 " + ex.Message, Theme.DangerInk);
                return false;
            }
            return true;
        }

        private void LoadProcesses()
        {
            if (!IsHandleCreated) CreateControl();
            loading = true;
            refresh.Enabled = false;
            refresh.Text = "读取中";
            UpdateEmpty();
            Task.Run(delegate
            {
                IList<KeepPageRow> result;
                try { result = scan(); }
                catch (Exception ex)
                {
                    string message = "读取进程失败。 " + ex.Message;
                    Post(delegate
                    {
                        scanMessage = message;
                        SetNotice(scanMessage, Theme.DangerInk);
                        CompleteScan();
                    });
                    return;
                }
                Post(delegate
                {
                    inventory = result;
                    scanMessage = "";
                    CompleteScan();
                    RenderProcesses();
                    LoadIcons(result);
                });
            });
        }

        private void CompleteScan()
        {
            loading = false;
            refresh.Enabled = true;
            refresh.Text = "刷新";
            UpdateEmpty();
        }

        private void LoadIcons(IList<KeepPageRow> rows)
        {
            var paths = new List<string>();
            foreach (KeepPageRow row in rows)
            {
                if (string.IsNullOrEmpty(row.Path) || icons.ContainsKey(row.Path)) continue;
                icons.Add(row.Path, null);
                paths.Add(row.Path);
            }
            if (paths.Count == 0) return;

            Task.Run(delegate
            {
                const int batchSize = 12;
                for (int index = 0; index < paths.Count && !IsDisposed; index += batchSize)
                {
                    List<string> batch = paths.GetRange(index, Math.Min(batchSize, paths.Count - index));
                    var images = new Bitmap[batch.Count];
                    for (int i = 0; i < batch.Count; i++) images[i] = LocalIcon(batch[i]);
                    if (!Post(delegate { ApplyIcons(batch, images); }))
                        foreach (Bitmap image in images) if (image != null) image.Dispose();
                }
            });
        }

        private void ApplyIcons(List<string> paths, Bitmap[] images)
        {
            for (int i = 0; i < images.Length; i++) icons[paths[i]] = images[i];
            foreach (DataGridViewRow item in processes.Rows)
            {
                var row = (KeepPageRow)item.Tag;
                Bitmap image;
                if (!string.IsNullOrEmpty(row.Path) && icons.TryGetValue(row.Path, out image) && image != null
                    && !ReferenceEquals(item.Cells[0].Value, image)) item.Cells[0].Value = image;
            }
        }

        private bool Post(MethodInvoker action)
        {
            if (IsDisposed || Disposing) return false;
            try { BeginInvoke(action); }
            catch (InvalidOperationException)
            {
                if (!IsDisposed && !Disposing) throw;
                return false;
            }
            return true;
        }

        internal static Bitmap LocalIcon(string path)
        {
            try
            {
                string root = Path.GetPathRoot(path);
                if (root == null || root.Length != 3 || root[1] != ':') return null;
                DriveType drive = new DriveInfo(root).DriveType;
                if (drive != DriveType.Fixed && drive != DriveType.Removable && drive != DriveType.Ram) return null;
                using (Icon icon = Icon.ExtractAssociatedIcon(path)) return icon == null ? null : icon.ToBitmap();
            }
            catch (ArgumentException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
            catch (System.Security.SecurityException) { return null; }
        }

        private void ShowTab(bool saved)
        {
            if (showRules != saved) ResetEntry();
            showRules = saved;
            StyleTab(processTab, !saved);
            StyleTab(rulesTab, saved);
            searchField.Visible = refresh.Visible = processes.Visible = !saved;
            group.Visible = rules.Visible = saved;
            if (saved) RenderRules();
            else RenderProcesses();
        }

        private void ResetEntry()
        {
            entry.Clear();
            populating = true;
            scope.Items.Clear();
            populating = false;
        }

        private void ConfigureScope()
        {
            populating = true;
            scope.Items.Clear();
            scope.Items.AddRange(new object[] { "仅此 exe", "同名进程", "整个目录" });
            scope.SelectedIndex = 0;
            populating = false;
        }

        private void RenderProcesses()
        {
            string query = search.Text.Trim();
            var names = inventory.ToLookup(row => row.Name, StringComparer.OrdinalIgnoreCase);
            KeepPageRow previous = processes.CurrentRow == null ? null : (KeepPageRow)processes.CurrentRow.Tag;
            int selected = 0;
            populating = true;
            processes.Rows.Clear();
            foreach (KeepPageRow row in inventory)
            {
                if (row.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                    && (row.Path == null || row.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                string systemRule = Program.MatchKeepRule(config.System, row.Name, row.Path);
                string userRule = Program.MatchKeepRule(config.User, row.Name, row.Path);
                bool kept = canEdit && (systemRule != null || userRule != null);
                string state = !canEdit ? "未读取" : systemRule != null ? "系统保留" : kept ? "已保留" : "未保留";
                string action = kept || !canEdit ? "详情" : string.IsNullOrEmpty(row.Path) ? "选范围" : "保留";
                Bitmap image = null;
                if (!string.IsNullOrEmpty(row.Path)) icons.TryGetValue(row.Path, out image);
                string label = row.Instances > 1 ? row.Name + "  (" + row.Instances + ")" : row.Name;
                bool sameName = names[row.Name].Count() > 1;
                if (sameName)
                {
                    string folder = string.IsNullOrEmpty(row.Path) ? "" : ParentFolder(row.Path);
                    label += Environment.NewLine + (folder.Length > 0 ? folder : "路径不可读");
                }
                int index = processes.Rows.Add(image ?? genericIcon, label, state, action);
                DataGridViewRow item = processes.Rows[index];
                item.Tag = row;
                if (sameName)
                {
                    item.Cells[1].Style.WrapMode = DataGridViewTriState.True;
                    item.Height = S(48);
                }
                item.Cells[1].ToolTipText = string.IsNullOrEmpty(row.Path) ? "路径不可读" : row.Path;
                item.Cells[2].Style.ForeColor = kept ? Theme.OkText : Theme.Muted;
                item.Cells[2].Style.SelectionForeColor = item.Cells[2].Style.ForeColor;
                if (previous != null && string.Equals(row.Name, previous.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(row.Path, previous.Path, StringComparison.OrdinalIgnoreCase)) selected = index;
            }
            if (processes.Rows.Count > 0) processes.CurrentCell = processes.Rows[selected].Cells[1];
            populating = false;
            if (!showRules) UpdateDetails();
        }

        private void RenderRules()
        {
            string previous = CurrentRule();
            List<string> source = group.SelectedIndex == 0 ? config.User : group.SelectedIndex == 1 ? config.System
                : group.SelectedIndex == 2 ? config.Services : config.Graceful;
            int selected = 0;
            populating = true;
            rules.Rows.Clear();
            foreach (string rule in source)
            {
                if (string.IsNullOrWhiteSpace(rule)) continue;
                bool keepRule = group.SelectedIndex < 2;
                string display = keepRule ? KeepRule.Display(rule) : rule;
                string description = keepRule ? KeepRule.ScopeLabel(rule) : group.SelectedIndex == 2 ? "服务名" : "进程匹配";
                int index = rules.Rows.Add(display, description);
                DataGridViewRow item = rules.Rows[index];
                item.Tag = rule;
                item.Cells[0].ToolTipText = display;
                if (previous == rule) selected = index;
            }
            if (rules.Rows.Count > 0) rules.CurrentCell = rules.Rows[selected].Cells[0];
            populating = false;
            if (showRules) UpdateDetails();
        }

        private string CurrentRule()
        {
            return rules.CurrentRow == null ? null : (string)rules.CurrentRow.Tag;
        }

        private void UpdateDetails()
        {
            if (showRules)
            {
                selectedProcess = null;
                string rule = CurrentRule();
                detailTitle.Text = "规则详情";
                switch (group.SelectedIndex)
                {
                    case 0: context.Text = "这些规则会保留下次清理时匹配的进程"; break;
                    case 1: context.Text = "系统保留规则在此处只读"; break;
                    case 2: context.Text = "清理时临时停止指定服务"; break;
                    default: context.Text = "匹配进程优先尝试正常关闭"; break;
                }
                location.Text = rule == null ? "" : group.SelectedIndex < 2 ? KeepRule.Display(rule) : rule;
                location.Visible = rule != null;
                matches.Visible = copy.Visible = false;
                scope.Visible = group.SelectedIndex == 0;
                remove.Visible = rule != null && group.SelectedIndex != 1;
                remove.Enabled = canEdit;
                entry.Visible = apply.Visible = group.SelectedIndex != 1;
                apply.Text = "添加规则";
                apply.Enabled = canEdit;
                if (group.SelectedIndex == 0)
                {
                    if (scope.Items.Count == 0) ConfigureScope();
                    UpdateManualEntry();
                }
                else if (group.SelectedIndex >= 2)
                    SetEntryCue(group.SelectedIndex == 2 ? "输入服务名" : "输入进程名或路径匹配规则");
            }
            else
            {
                var row = processes.CurrentRow == null ? null : (KeepPageRow)processes.CurrentRow.Tag;
                detailTitle.Text = row == null ? "进程详情" : row.Name;
                string pids = row == null ? "" : row.Instances > 1 ? row.Instances + " 个实例" : "PID " + row.Pids;
                bool hasPath = row != null && !string.IsNullOrEmpty(row.Path);
                context.Text = row == null ? "选择一行查看详情" : hasPath ? pids : pids + " · 路径不可读";
                location.Visible = copy.Visible = hasPath;
                location.Text = hasPath ? row.Path : "";
                string systemRule = row == null ? null : Program.MatchKeepRule(config.System, row.Name, row.Path);
                string userRule = row == null ? null : Program.MatchKeepRule(config.User, row.Name, row.Path);
                matches.Visible = row != null;
                matches.Text = !canEdit ? "保留规则读取失败"
                    : systemRule == null && userRule == null ? "未命中保留规则"
                    : (systemRule == null ? "" : MatchText("系统", systemRule))
                        + (systemRule != null && userRule != null ? Environment.NewLine : "")
                        + (userRule == null ? "" : MatchText("我的规则", userRule));
                remove.Visible = false;
                scope.Visible = hasPath;
                if (!ReferenceEquals(selectedProcess, row))
                {
                    selectedProcess = row;
                    if (hasPath) ConfigureScope();
                    else ResetEntry();
                    entry.Text = hasPath ? ParentFolder(row.Path) : "";
                }
                UpdateScope();
            }
            tips.SetToolTip(location, location.Text);
            UpdateEmpty();
            LayoutPage();
        }

        private static string ParentFolder(string path)
        {
            int separator = path.LastIndexOfAny(new[] { '\\', '/' });
            return separator < 0 ? "" : path.Substring(0, separator == 2 ? 3 : separator);
        }

        private static string MatchText(string source, string rule)
        {
            return source + " · " + KeepRule.ScopeLabel(rule) + "  " + KeepRule.Display(rule);
        }

        private void UpdateScope()
        {
            if (showRules) return;
            bool selected = selectedProcess != null;
            bool readable = selected && !string.IsNullOrEmpty(selectedProcess.Path);
            entry.Visible = selected && scope.SelectedIndex == 2;
            if (selected && scope.SelectedIndex == 2) SetEntryCue("输入要保留的完整目录");
            apply.Visible = selected;
            apply.Enabled = canEdit && selected;
            apply.Text = !readable || scope.SelectedIndex == 1 ? "按进程名保留"
                : scope.SelectedIndex == 2 ? "保留目录" : "保留";
            LayoutPage();
        }

        private void UpdateManualEntry()
        {
            int mode = scope.SelectedIndex;
            SetEntryCue(mode == 2 ? "输入要保留的完整目录" : mode == 1 ? "输入要保留的进程名" : "输入完整 exe 路径");
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr param, string text);

        private void SetEntryCue(string cue)
        {
            if (entry.AccessibleName == cue) return;
            entry.AccessibleName = cue;
            SendMessage(entry.Handle, 0x1501, (IntPtr)1, cue);
        }

        private void ActivateRow(KeepPageRow row)
        {
            if (!canEdit || string.IsNullOrEmpty(row.Path)
                || Program.MatchKeepRule(config.System, row.Name, row.Path) != null
                || Program.MatchKeepRule(config.User, row.Name, row.Path) != null)
            {
                if (scope.CanFocus) scope.Focus();
                else if (apply.CanFocus) apply.Focus();
                else if (matches.CanFocus) matches.Focus();
                return;
            }
            ApplyProcess(row, 0);
        }

        private void ApplySelected()
        {
            if (!canEdit) return;
            if (!showRules)
            {
                ApplyProcess(selectedProcess, scope.SelectedIndex);
                return;
            }
            if (group.SelectedIndex == 0)
            {
                if (AddUserRule(entry.Text, scope.SelectedIndex)) entry.Clear();
                return;
            }
            string key = entry.Text.Trim();
            if (key.Length == 0)
            {
                SetNotice(group.SelectedIndex == 2 ? "请输入要停止的服务名" : "请输入优雅关闭匹配规则", Theme.WarnInk);
                entry.Focus();
                return;
            }
            if (EditRule(GroupKeys[group.SelectedIndex], key, true, false)) entry.Clear();
        }

        private void ApplyProcess(KeepPageRow row, int mode)
        {
            if (mode == 2) AddUserRule(entry.Text, mode);
            else if (mode == 1 || string.IsNullOrEmpty(row.Path)) AddUserRule(KeepRule.ForProcessName(row.Name), 1);
            else AddUserRule(row.Path, 0);
        }

        private bool AddUserRule(string value, int mode)
        {
            string key;
            try
            {
                if (mode == 2) key = KeepRule.Directory(value);
                else if (mode == 1)
                {
                    key = value.Trim();
                    if (key.Length == 0 || !KeepRule.IsName(key))
                        throw new ArgumentException("请输入单独的进程名。");
                }
                else key = KeepRule.File(value);
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) && !(ex is IOException) && !(ex is NotSupportedException)) throw;
                SetNotice("规则范围无效。 " + ex.Message, Theme.DangerInk);
                if (entry.CanFocus) entry.Focus();
                return false;
            }
            return EditRule("user", key, true, false);
        }

        private bool EditRule(string groupKey, string rule, bool add, bool isUndo)
        {
            bool changed;
            try { changed = change(groupKey, rule, add); }
            catch (Exception ex)
            {
                SetNotice((add ? "保存失败。 " : isUndo ? "撤销失败。 " : "移除失败。 ") + ex.Message, Theme.DangerInk);
                return false;
            }

            if (add && changed)
            {
                undoGroup = groupKey;
                undoRule = rule;
            }
            else if (!add && undoGroup == groupKey && string.Equals(undoRule, rule, StringComparison.OrdinalIgnoreCase))
            {
                undoGroup = undoRule = null;
            }

            bool loaded = ReloadConfig();
            if (loaded)
            {
                string message = add ? changed ? "已保存，下次清理生效" : "这条规则已存在"
                    : changed ? isUndo ? "已撤销" : "已移除规则" : "这条规则已移除";
                SetNotice(message, Theme.OkText);
            }
            else if (changed) SetNotice((add ? "已保存。" : "已移除。") + notice.Text, Theme.DangerInk);
            RenderProcesses();
            RenderRules();
            return changed;
        }

        private void SetNotice(string message, Color color)
        {
            notice.Text = message;
            notice.ForeColor = color;
            tips.SetToolTip(notice, message);
            LayoutPage();
        }

        private void UpdateEmpty()
        {
            bool isEmpty = showRules ? rules.Rows.Count == 0 : processes.Rows.Count == 0;
            empty.Text = showRules ? !canEdit ? "读取规则失败" : "当前没有规则"
                : loading ? "正在读取进程…" : scanMessage.Length > 0 ? "读取进程失败，请刷新"
                : search.Text.Trim().Length > 0 ? "未找到匹配的进程" : "当前没有可显示的进程";
            empty.Visible = isEmpty;
            tips.SetToolTip(empty, scanMessage);
        }

        internal void SetScale(float currentScale)
        {
            scale = currentScale;
            LayoutPage();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (backButton != null) LayoutPage();
        }

        private int S(int design) { return (int)Math.Round(design * scale); }

        private void LayoutPage()
        {
            if (processes.Columns.Count == 0) return;
            int inset = S(ClientSize.Width / scale < 520 ? 12 : 22);
            int width = Math.Max(1, ClientSize.Width - inset * 2);
            int right = inset + width;
            int gap = S(8);
            int buttonHeight = S(29);
            int backWidth = S(90);
            bool compact = ClientSize.Height < S(440);
            int titleSpace = hasBack ? backWidth + gap : 0;
            title.SetBounds(inset, S(compact ? 4 : 9), Math.Max(1, width - titleSpace), S(compact ? 30 : 35));
            backButton.SetBounds(right - backWidth, S(compact ? 5 : 12), backWidth, buttonHeight);

            title.Visible = hasBack || !compact;
            int tabY = S(compact ? hasBack ? 37 : 8 : 53);
            processTab.SetBounds(inset, tabY, S(115), buttonHeight);
            rulesTab.SetBounds(inset + S(121), tabY, S(102), buttonHeight);
            int fieldY = tabY + S(compact ? 33 : 38);
            int refreshWidth = S(70);
            searchField.UiScale = scale;
            searchField.SetBounds(inset, fieldY, Math.Max(1, width - refreshWidth - gap), buttonHeight);
            searchField.UpdateInputLayout();
            refresh.SetBounds(right - refreshWidth, fieldY, refreshWidth, buttonHeight);
            group.SetBounds(inset, fieldY, Math.Min(width, S(228)), buttonHeight);

            int top = fieldY + S(compact ? 32 : 36);
            int footer = Math.Max(top + S(24), ClientSize.Height - S(34));
            bool wide = width >= S(700);
            int detailHeight = S(showRules ? scope.Visible ? 142 : entry.Visible ? 110 : 98
                : entry.Visible ? 176 : location.Visible ? 144 : 119);
            if (location.Visible) detailHeight += S(20);
            int detailWidth = wide ? Math.Min(S(360), Math.Max(S(280), width * 39 / 100)) : width;
            int listWidth = wide ? width - detailWidth - gap : width;
            int detailTop = wide ? top : Math.Max(top + S(68), footer - detailHeight - gap);
            int listHeight = Math.Max(S(24), (wide ? footer - gap : detailTop - S(5)) - top);
            processes.SetBounds(inset, top, listWidth, listHeight);
            rules.Bounds = processes.Bounds;
            empty.SetBounds(inset + 1, top + Math.Min(S(26), listHeight / 2),
                Math.Max(1, listWidth - 2), Math.Max(1, listHeight - Math.Min(S(26), listHeight / 2) - 1));
            int detailLeft = wide ? right - detailWidth : inset;
            int detailSpace = Math.Max(S(24), footer - gap - detailTop);
            detail.SetBounds(detailLeft, detailTop, detailWidth, Math.Min(detailSpace, detailHeight));
            detail.AutoScrollMinSize = new Size(0, detailHeight);
            LayoutDetails(detailHeight);
            UpdateGridSizing();

            int undoWidth = S(65);
            undo.Visible = undoRule != null;
            undo.SetBounds(right - undoWidth, footer, undoWidth, buttonHeight);
            notice.SetBounds(inset, footer, Math.Max(1, width - (undoRule != null ? undoWidth + gap : 0)), buttonHeight);
        }

        private void LayoutDetails(int contentHeight)
        {
            int margin = S(10);
            int scrollGutter = detail.ClientSize.Height < contentHeight ? S(17) : 0;
            int width = Math.Max(1, detail.ClientSize.Width - margin * 2 - scrollGutter);
            int right = margin + width;
            int copyWidth = S(74);
            int height = S(28);
            int scrollY = detail.AutoScrollPosition.Y;
            int y = scrollY + S(5);
            detailTitle.SetBounds(margin, y, Math.Max(1, width - (copy.Visible ? copyWidth + S(6) : 0)), S(23));
            copy.SetBounds(right - copyWidth, y, copyWidth, S(24));
            context.SetBounds(margin, y + S(20), width, S(17));
            y += S(38);
            int locationHeight = S(42);
            location.SetBounds(margin, y, width, locationHeight);
            if (location.Visible)
            {
                int measured = TextRenderer.MeasureText(location.Text, location.Font, new Size(width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding).Height;
                location.ScrollBars = measured > locationHeight ? ScrollBars.Vertical : ScrollBars.None;
                y += S(45);
            }

            if (!showRules)
            {
                int actionWidth = S(apply.Text == "按进程名保留" ? 110 : 78);
                bool compactDirectory = entry.Visible && scrollGutter > 0;
                int matchHeight = S(compactDirectory ? 19 : 37);
                int actionY = y + matchHeight + S(3);
                matches.SetBounds(margin, y, width, matchHeight);
                int textHeight = TextRenderer.MeasureText(matches.Text, matches.Font,
                    new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
                matches.ScrollBars = textHeight > matchHeight ? ScrollBars.Vertical : ScrollBars.None;
                apply.SetBounds(right - actionWidth, actionY, actionWidth, height);
                scope.SetBounds(margin, actionY, entry.Visible ? width : Math.Max(1, width - actionWidth - S(7)), height);
                if (entry.Visible)
                {
                    entry.SetBounds(margin, actionY + S(compactDirectory ? 25 : 31), Math.Max(1, width - actionWidth - S(7)), height);
                    apply.Top = entry.Top;
                }
                return;
            }

            int removeWidth = S(85);
            int ruleY = y + S(scope.Visible ? 31 : 0);
            remove.SetBounds(right - removeWidth, ruleY, removeWidth, height);
            int applyWidth = S(81);
            if (entry.Visible)
            {
                if (remove.Visible) remove.SetBounds(right - removeWidth, scrollY + S(6), removeWidth, S(24));
                detailTitle.Width = Math.Max(1, width - (remove.Visible ? removeWidth + S(6) : 0));
                if (scope.Visible) scope.SetBounds(margin, ruleY - S(31), width, height);
                entry.SetBounds(margin, ruleY, Math.Max(1, width - applyWidth - S(7)), height);
                apply.SetBounds(right - applyWidth, ruleY, applyWidth, height);
            }
        }

        private void UpdateGridSizing()
        {
            int rowHeight = S(34);
            int headHeight = S(28);
            foreach (DataGridView grid in new DataGridView[] { processes, rules })
            {
                if (grid.ColumnHeadersHeight != headHeight) grid.ColumnHeadersHeight = headHeight;
                if (grid.RowTemplate.Height == rowHeight) continue;
                grid.RowTemplate.Height = rowHeight;
                foreach (DataGridViewRow row in grid.Rows)
                    row.Height = grid == processes && row.Cells[1].Style.WrapMode == DataGridViewTriState.True
                        ? S(48) : rowHeight;
            }
            processes.Columns[0].Width = S(33);
            processes.Columns[2].Width = S(79);
            processes.Columns[3].Width = S(61);
            processes.Columns[0].DefaultCellStyle.Padding = new Padding(S(6));
            rules.Columns[1].Width = S(88);
        }

        private static Label LabelFor(string text, float size, FontStyle style)
        {
            return new Label { Text = text, Font = Theme.GetFont(size, style, false), ForeColor = Theme.Text,
                AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        }

        private static Button ButtonFor(string text, string name, bool primary)
        {
            var button = new Button { Text = text, Name = name, FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false, Font = Theme.GetFont(9F, FontStyle.Regular, false) };
            StyleButton(button, primary);
            return button;
        }

        private static void StyleButton(Button button, bool primary)
        {
            button.BackColor = primary ? Theme.Primary : Theme.Surface;
            button.ForeColor = primary ? Theme.OnPrimary : Theme.Text;
            button.FlatAppearance.BorderColor = primary ? Theme.Primary : Theme.SecondaryLine;
            button.FlatAppearance.MouseOverBackColor = primary ? Theme.PrimaryHover : Theme.Track;
        }

        private static void StyleTab(Button button, bool active)
        {
            button.BackColor = active ? Theme.Blend(Theme.Surface, Theme.Primary, 0.10) : Theme.Bg;
            button.ForeColor = active ? Theme.Primary : Theme.Text2;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Theme.Track;
            button.AccessibleDescription = active ? "当前视图" : "切换视图";
        }

        private static TextBox TextFor(string name, bool readOnly, bool multiline)
        {
            return new TextBox { Name = name, ReadOnly = readOnly, Multiline = multiline,
                WordWrap = false, BorderStyle = readOnly ? BorderStyle.None : BorderStyle.Fixed3D, ScrollBars = ScrollBars.None,
                Font = Theme.GetFont(9.5F, FontStyle.Regular, false), BackColor = Theme.Surface, ForeColor = Theme.Text };
        }

        private static ComboBox Choice(string name, string label, params object[] choices)
        {
            var choice = new ComboBox { Name = name, AccessibleName = label, DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat, Font = Theme.GetFont(9.5F, FontStyle.Regular, false) };
            choice.Items.AddRange(choices);
            return choice;
        }

        private static DataGridViewTextBoxColumn TextColumn(string key, string label, bool fill)
        {
            return new DataGridViewTextBoxColumn { Name = key, HeaderText = label, AutoSizeMode = fill
                ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
                SortMode = DataGridViewColumnSortMode.NotSortable };
        }

        private static void TabOrder(params Control[] controls)
        {
            for (int index = 0; index < controls.Length; index++) controls[index].TabIndex = index;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            tips.Dispose();
            genericIcon.Dispose();
            foreach (Bitmap image in icons.Values) if (image != null) image.Dispose();
            icons.Clear();
        }

        private sealed class KeepGrid : DataGridView
        {
            internal Action ActivateRow;

            internal KeepGrid(string name, string label)
            {
                Name = name;
                AccessibleName = label;
                ReadOnly = true;
                AllowUserToAddRows = AllowUserToDeleteRows = AllowUserToResizeRows = AllowUserToResizeColumns = false;
                RowHeadersVisible = MultiSelect = EnableHeadersVisualStyles = false;
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
                SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                ScrollBars = ScrollBars.Vertical;
                BackgroundColor = Theme.Surface;
                BorderStyle = BorderStyle.None;
                GridColor = Theme.Line;
                StandardTab = true;
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
                DoubleBuffered = true;
                DefaultCellStyle.BackColor = Theme.Surface;
                DefaultCellStyle.ForeColor = Theme.Text;
                DefaultCellStyle.SelectionBackColor = Theme.Blend(Theme.Surface, Theme.Primary, 0.08);
                DefaultCellStyle.SelectionForeColor = Theme.Text;
                DefaultCellStyle.Font = Theme.GetFont(9.5F, FontStyle.Regular, false);
                ColumnHeadersDefaultCellStyle.BackColor = Theme.Bg;
                ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Bg;
                ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
                ColumnHeadersDefaultCellStyle.Font = Theme.GetFont(9F, FontStyle.Regular, false);
            }

            protected override bool ProcessDialogKey(Keys keyData)
            {
                if (keyData != Keys.Enter || ActivateRow == null || CurrentRow == null)
                    return base.ProcessDialogKey(keyData);
                ActivateRow();
                return true;
            }

            protected override bool ProcessDataGridViewKey(KeyEventArgs e)
            {
                if ((e.KeyData != Keys.Enter && e.KeyData != Keys.Space) || ActivateRow == null || CurrentRow == null)
                    return base.ProcessDataGridViewKey(e);
                ActivateRow();
                return true;
            }
        }
    }
}
