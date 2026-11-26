using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace KillAllBack
{
    internal sealed class NamedGate : IDisposable
    {
        private Mutex mutex;
        internal NamedGate(string key, int timeout)
        {
            mutex=new Mutex(false,"Local\\KillAllBack.R2.Gate."+Key(key));
            bool acquired=false;
            try { try { acquired=mutex.WaitOne(timeout); } catch(AbandonedMutexException){acquired=true;} }
            catch {mutex.Dispose();mutex=null;throw;}
            if(!acquired){mutex.Dispose();mutex=null;throw new TimeoutException("共享资源忙碌，未执行修改。");}
        }
        internal static string Key(string value)
        {
            using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant()))).Replace("-","").Substring(0,32);
        }
        public void Dispose(){if(mutex!=null){mutex.ReleaseMutex();mutex.Dispose();mutex=null;}}
    }
    internal sealed class TargetIdentity
    {
        public int Pid;
        public long Started;
        public TargetIdentity(){}
        internal TargetIdentity(int pid,long started){Pid=pid;Started=started;}
        internal string Key {get{return Pid+":"+Started;}}
    }
    internal sealed class RunLease
    {
        public string RunId;
        public int Owner;
        public long OwnerStarted;
        public List<TargetIdentity> Targets=new List<TargetIdentity>();
    }
    internal sealed class LeaseState {public List<RunLease> Leases=new List<RunLease>();}
    internal sealed class RunCoordinator : IDisposable
    {
        private const int Capacity = 1024 * 1024;
        private readonly MemoryMappedFile map;
        private readonly MemoryMappedViewAccessor view;
        private readonly string gateKey;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = Capacity };
        internal readonly string RunId;
        private readonly int owner = NativeIdentity.SelfPid;
        private readonly long started;
        private readonly HashSet<string> owned = new HashSet<string>();
        private readonly HashSet<string> shared = new HashSet<string>();
        private bool disposed;

        internal int SharedCount { get { lock (owned) { return shared.Count; } } }

        internal RunCoordinator(string scope, string runId = null)
        {
            RunId = runId ?? Guid.NewGuid().ToString("N");
            started = NativeIdentity.ReadStarted(owner).GetValueOrDefault();
            if (started <= 0) { throw new InvalidOperationException("无法确认本实例创建时间，未建立清理协调。"); }
            gateKey = "lease:" + scope;
            map = MemoryMappedFile.CreateOrOpen("Local\\KillAllBack.R2.Leases." + NamedGate.Key(scope), Capacity, MemoryMappedFileAccess.ReadWrite);
            try { view = map.CreateViewAccessor(); }
            catch { map.Dispose(); throw; }
        }

        // 由命名互斥锁保护同一个本地共享映射的读改写。
        private void UpdateLeaseState(Func<LeaseState, bool> change)
        {
            int size = view.ReadInt32(0);
            if (size < 0 || size > Capacity - 4) { throw new InvalidDataException("实例协调状态无效，未执行清理。"); }
            LeaseState state = new LeaseState();
            if (size > 0)
            {
                byte[] data = new byte[size];
                if (view.ReadArray(4, data, 0, size) != size) { throw new EndOfStreamException(); }
                state = serializer.Deserialize<LeaseState>(Encoding.UTF8.GetString(data));
            }
            if (state == null || state.Leases == null) { throw new InvalidDataException("实例协调状态无效。"); }

            int removed = state.Leases.RemoveAll(l => l.Owner == owner ? l.OwnerStarted != started
                : !NativeIdentity.IsAlive(l.Owner, l.OwnerStarted));
            if (!change(state) && removed == 0) { return; }
            byte[] next = Encoding.UTF8.GetBytes(serializer.Serialize(state));
            if (next.Length > Capacity - 4) { throw new InvalidDataException("实例协调容量已满。"); }
            view.WriteArray(4, next, 0, next.Length);
            view.Write(0, next.Length);
        }

        internal bool TryClaim(IList<TargetIdentity> targets, Func<int> remainingBudget = null)
        {
            return Claim(targets, remainingBudget, false).Count > 0;
        }

        internal HashSet<string> ClaimAvailable(IList<TargetIdentity> targets, Func<int> remainingBudget)
        {
            return Claim(targets, remainingBudget, true);
        }

        private HashSet<string> Claim(IList<TargetIdentity> targets, Func<int> remainingBudget, bool allowPartial)
        {
            if (targets.Count == 0 || targets.Any(t => t.Started <= 0))
            {
                throw new InvalidOperationException("无法验证目标进程创建时间，未执行终止。");
            }
            var keys = new HashSet<string>(targets.Select(t => t.Key));
            lock (owned)
            {
                if (disposed) { throw new ObjectDisposedException("RunCoordinator"); }
                if (keys.IsSubsetOf(owned)) { return keys; }
            }

            int waitMs = remainingBudget == null ? 2000 : Math.Min(2000, remainingBudget());
            if (waitMs <= 0) { throw new TimeoutException("总清理预算已耗尽，跳过认领。"); }
            using (new NamedGate(gateKey, waitMs))
            lock (owned)
            {
                if (disposed) { throw new ObjectDisposedException("RunCoordinator"); }
                if (keys.IsSubsetOf(owned)) { return keys; }
                if (remainingBudget != null && remainingBudget() <= 0)
                {
                    throw new TimeoutException("总清理预算已耗尽，跳过认领。");
                }

                var available = new HashSet<string>(keys);
                var conflicts = new HashSet<string>();
                UpdateLeaseState(delegate(LeaseState state)
                {
                    foreach (RunLease lease in state.Leases.Where(lease => lease.RunId != RunId))
                    {
                        foreach (TargetIdentity target in lease.Targets)
                        {
                            if (available.Remove(target.Key)) { conflicts.Add(target.Key); }
                        }
                    }

                    if ((!allowPartial && conflicts.Count > 0) || available.Count == 0)
                    {
                        available.Clear();
                        return false;
                    }

                    RunLease mine = state.Leases.FirstOrDefault(lease => lease.RunId == RunId);
                    if (mine == null)
                    {
                        mine = new RunLease { RunId = RunId, Owner = owner, OwnerStarted = started };
                        state.Leases.Add(mine);
                    }
                    var known = new HashSet<string>(mine.Targets.Select(target => target.Key));
                    int previousCount = mine.Targets.Count;
                    foreach (TargetIdentity target in targets)
                    {
                        if (available.Contains(target.Key) && known.Add(target.Key)) { mine.Targets.Add(target); }
                    }
                    return mine.Targets.Count != previousCount;
                });
                owned.UnionWith(available);
                shared.UnionWith(conflicts);
                return available;
            }
        }

        public void Dispose()
        {
            bool hasClaims;
            lock (owned)
            {
                if (disposed) { return; }
                disposed = true;
                hasClaims = owned.Count > 0;
            }
            try
            {
                if (hasClaims)
                {
                    using (new NamedGate(gateKey, 2000)) { UpdateLeaseState(state => state.Leases.RemoveAll(l => l.RunId == RunId) > 0); }
                }
            }
            finally
            {
                try { view.Dispose(); }
                finally { map.Dispose(); }
            }
        }
    }
    internal static class NativeIdentity
    {
        private const int QueryLimitedInformation = 0x1000;
        private const int InvalidProcessId = 87;
        private const int InsufficientBuffer = 122;
        internal static readonly int SelfPid = Process.GetCurrentProcess().Id;

        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageName(IntPtr process,int flags,StringBuilder path,ref int size);
        [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr OpenProcess(int access,bool inherit,int pid);
        [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool Process32First(IntPtr snap,ref ENTRY entry);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool Process32Next(IntPtr snap,ref ENTRY entry);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct ENTRY{public uint Size,Usage,Pid;public IntPtr Heap;public uint Module,Threads,Parent;public int Priority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Exe;}
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file,out FILEINFO info);
        [StructLayout(LayoutKind.Sequential)]private struct FILEINFO{public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
        internal static long Started(Process p){try{return p.StartTime.ToUniversalTime().Ticks;}catch{return 0;}}
        internal static bool IsAlive(int pid,long stamp){try{using(var p=Process.GetProcessById(pid)){if(p.HasExited)return false;long actual=Started(p);return actual==0||stamp<=0||actual==stamp;}}catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}}
        internal static string FileId(string path){using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){FILEINFO i;if(!GetFileInformationByHandle(f.SafeFileHandle,out i))throw new System.ComponentModel.Win32Exception();return i.Volume+":"+i.IndexHigh+":"+i.IndexLow;}}

        private static long ReadStarted(IntPtr handle)
        {
            long created, exited, kernel, user;
            return GetProcessTimes(handle, out created, out exited, out kernel, out user)
                ? DateTime.FromFileTimeUtc(created).Ticks : 0;
        }

        internal static long? ReadStarted(int pid)
        {
            IntPtr handle = OpenProcess(QueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) { return Marshal.GetLastWin32Error() == InvalidProcessId ? (long?)null : 0; }
            try { return ReadStarted(handle); }
            finally { CloseHandle(handle); }
        }

        internal static string Image(int pid)
        {
            IntPtr handle = OpenProcess(QueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) { return null; }
            try { return Image(handle); }
            finally { CloseHandle(handle); }
        }

        internal static string Image(IntPtr handle)
        {
            var path = new StringBuilder(260);
            int size = path.Capacity;
            if (QueryFullProcessImageName(handle, 0, path, ref size)) { return path.ToString(); }
            if (Marshal.GetLastWin32Error() != InsufficientBuffer) { return null; }

            path = new StringBuilder(32768);
            size = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
        }

        internal static Dictionary<int,int> Parents(){var result=new Dictionary<int,int>();IntPtr h=CreateToolhelp32Snapshot(2,0);if(h==new IntPtr(-1))throw new System.ComponentModel.Win32Exception();try{ENTRY e=new ENTRY();e.Size=(uint)Marshal.SizeOf(typeof(ENTRY));if(Process32First(h,ref e))do{result[(int)e.Pid]=(int)e.Parent;e.Size=(uint)Marshal.SizeOf(typeof(ENTRY));}while(Process32Next(h,ref e));return result;}finally{CloseHandle(h);}}
        internal static List<TargetIdentity> PlannedTree(int pid, long plannedLocalTicks, ILookup<int, int> children)
        {
            // 扫描计划保存本地时间。跨实例租约统一使用 UTC 时间。
            long? started = ReadStarted(pid);
            if (!started.HasValue) { throw new ArgumentException("进程已不存在", "pid"); }
            if (started.Value <= 0 || new DateTime(started.Value, DateTimeKind.Utc).ToLocalTime().Ticks != plannedLocalTicks)
            {
                throw new InvalidOperationException("目标进程身份已改变或无法确认。");
            }
            var result = new List<TargetIdentity> { new TargetIdentity(pid, started.Value) };
            if (!children.Contains(pid)) { return result; }
            var visited = new HashSet<int> { pid };
            for (int i = 0; i < result.Count; i++)
            {
                TargetIdentity parent = result[i];
                foreach (int childPid in children[parent.Pid])
                {
                    if (!visited.Add(childPid)) { continue; }
                    long? time = ReadStarted(childPid);
                    if (!time.HasValue) { continue; }
                    if (time.Value == 0) { throw new InvalidOperationException("无法确认目标子进程身份。"); }
                    if (time.Value >= parent.Started) { result.Add(new TargetIdentity(childPid, time.Value)); }
                }
            }
            return result;
        }
        private static readonly string self=Path.GetFullPath(Application.ExecutablePath);
        private static readonly string selfId=FileId(self);
        internal static bool IsApplication(int pid)
        {
            return IsApplicationImage(Image(pid));
        }

        private static bool IsApplicationImage(string path)
        {
            return path != null && string.Equals(Path.GetFullPath(path), self, StringComparison.OrdinalIgnoreCase)
                && FileId(path) == selfId;
        }

        internal static bool Protected(int pid, Dictionary<int, int> parents)
        {
            if (pid == SelfPid) { return true; }
            long childStamp = long.MaxValue;
            var seen = new HashSet<int>();
            for (int depth = 0; depth < 48 && pid > 4 && seen.Add(pid); depth++)
            {
                IntPtr handle = OpenProcess(QueryLimitedInformation, false, pid);
                if (handle == IntPtr.Zero) { return false; }
                try
                {
                    long stamp = ReadStarted(handle);
                    if (stamp <= 0 || stamp > childStamp) { return false; }
                    if (IsApplicationImage(Image(handle))) { return true; }
                    childStamp = stamp;
                }
                finally { CloseHandle(handle); }
                if (!parents.TryGetValue(pid, out pid)) { break; }
            }
            return false;
        }
    }
    internal sealed class ProgressValue
    {
        internal readonly string Stage;internal readonly int Completed,Total;internal readonly bool Final;
        internal ProgressValue(string stage,int completed,int total,bool final){Stage=stage;Completed=completed;Total=total;Final=final;}
    }
    internal static class AtomicConfig
    {
        internal static void Replace(string path, string expected, string next, string backup)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, next, new UTF8Encoding(false));
                if (!string.Equals(File.ReadAllText(path, Encoding.UTF8), expected, StringComparison.Ordinal))
                    throw new IOException("其他程序已修改配置，本次规则变更未写入。");
                File.Replace(temp, path, backup, true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }
    public static partial class Program
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int informationClass,
            out int information, int length, out int returned);

        internal static void OpenFromDesktop(string file, string arguments, string directory)
        {
            const uint tokenQuery = 0x0008;
            const int tokenElevation = 20, desktopClass = 8, needDispatch = 1;
            IntPtr shell = GetShellWindow();
            int shellPid;
            if (shell == IntPtr.Zero || GetWindowThreadProcessId(shell, out shellPid) == 0)
                throw new InvalidOperationException("当前会话没有可用的 Windows 桌面。");

            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
            if (process == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                IntPtr token;
                if (!OpenProcessToken(process, tokenQuery, out token)) throw new System.ComponentModel.Win32Exception();
                try
                {
                    int elevated, returned;
                    if (!GetTokenInformation(token, tokenElevation, out elevated, sizeof(int), out returned))
                        throw new System.ComponentModel.Win32Exception();
                    if (elevated != 0)
                        throw new InvalidOperationException("当前 Windows 桌面以管理员权限运行，请手动打开。");
                }
                finally { CloseHandle(token); }

                // 让登录用户的 Explorer 执行打开请求，沿用桌面会话和文件关联。
                dynamic application = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                dynamic windows = application.Windows();
                object location = null, root = null;
                int hwnd;
                dynamic desktop = windows.FindWindowSW(ref location, ref root, desktopClass, out hwnd, needDispatch);
                if (desktop == null || GetShellWindow() != shell)
                    throw new InvalidOperationException("当前会话没有可用的桌面文件管理器。");
                desktop.Document.Application.ShellExecute(file, arguments, directory, "open", 1);
            }
            finally { CloseHandle(process); }
        }

        [ThreadStatic]private static bool workerInvocation;
        private static InteractiveSession interactive;
        private static RunCoordinator coordinator;
        private static readonly string runId=Guid.NewGuid().ToString("N");
        private static string coordinationError;
        private static List<ProcInfo> ClaimCleanupTargets(IList<ProcInfo> candidates, Func<int> remainingBudget,
            Action<ProcInfo, string> rejected)
        {
            const string expired = "总清理预算已耗尽，跳过认领";
            var admitted = new List<ProcInfo>();
            Dictionary<int, int> parents;
            try { parents = NativeIdentity.Parents(); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                coordinationError = ex.Message;
                foreach (ProcInfo candidate in candidates) { rejected(candidate, "协调失败：" + ex.Message); }
                return admitted;
            }
            var children = parents.ToLookup(entry => entry.Value, entry => entry.Key);
            var prepared = new List<KeyValuePair<ProcInfo, TargetIdentity>>();

            foreach (ProcInfo candidate in candidates)
            {
                if (remainingBudget() <= 0) { rejected(candidate, expired); continue; }
                if (candidate.StartTicks <= 0) { rejected(candidate, "无法确认目标进程创建时间"); continue; }
                if (protectedPids.Contains(candidate.Pid))
                {
                    rejected(candidate, "已被父链保护");
                    continue;
                }

                try
                {
                    if (NativeIdentity.Protected(candidate.Pid, parents))
                    {
                        rejected(candidate, "本软件实例或工作进程，已保护");
                        continue;
                    }
                    var tree = NativeIdentity.PlannedTree(candidate.Pid, candidate.StartTicks, children);
                    if (tree.Any(target => NativeIdentity.IsApplication(target.Pid)))
                    {
                        rejected(candidate, "目标树包含本软件实例，已保护");
                        continue;
                    }
                    int parent;
                    candidate.ParentPid = parents.TryGetValue(candidate.Pid, out parent) ? parent : 0;
                    prepared.Add(new KeyValuePair<ProcInfo, TargetIdentity>(candidate, tree[0]));
                }
                catch (ArgumentException) { rejected(candidate, "进程已不存在"); }
                catch (InvalidOperationException ex) { rejected(candidate, ex.Message); }
                catch (IOException ex) { rejected(candidate, "无法核验进程映像：" + ex.Message); }
                catch (System.ComponentModel.Win32Exception ex) { rejected(candidate, "无法核验进程映像：" + ex.Message); }
                catch (UnauthorizedAccessException ex) { rejected(candidate, "无法核验进程映像：" + ex.Message); }
            }

            if (prepared.Count == 0) { return admitted; }
            try
            {
                var available = coordinator.ClaimAvailable(prepared.Select(entry => entry.Value).ToList(), remainingBudget);
                foreach (var entry in prepared)
                {
                    if (available.Contains(entry.Value.Key)) { admitted.Add(entry.Key); }
                    else { rejected(entry.Key, "其他实例处理中"); }
                }
            }
            catch (Exception ex)
            {
                bool timedOut = ex is TimeoutException && remainingBudget() <= 0;
                if (!timedOut) { coordinationError = ex.Message; }
                string reason = timedOut ? expired : "协调失败：" + ex.Message;
                foreach (var entry in prepared) { rejected(entry.Key, reason); }
            }
            return admitted;
        }

        private sealed class InteractiveSession : ApplicationContext
        {
            internal UiShell Shell;internal UiLiveState Live=new UiLiveState();
            internal readonly Stopwatch Clock=Stopwatch.StartNew();
            internal long FirstPaintMs=-1;internal volatile bool WorkerDone;
            internal int ExitCode;internal event Action Finished;
            internal readonly SynchronizationContext UiContext=new WindowsFormsSynchronizationContext();
            internal void Post(Action action){UiContext.Post(delegate{if(Shell!=null&&!Shell.Form.IsDisposed)action();},null);}
            internal void Bind(List<ProcInfo> killed,List<ProcInfo> failed,List<ProcInfo> exited,List<ProcInfo> plan,ServiceReport services,NetworkReport net,MemorySnapshot before,Task<GpuSample> gpuProbe)
            {
                if(Shell.Form.IsDisposed)return;
                Shell.Form.Invoke(new Action(delegate{
                    Live.PlanCount=plan.Count;Live.PlanTotalMb=plan.Sum(p=>p.WsMb);Live.Preparing=false;Live.Sampler=new SnapshotSampler(before,gpuProbe);Live.Sampler.Start();
                    Shell.Rebind(killed,failed,exited,plan,services,net,before);
                    Shell.Form.Text="killer · 清理中";
                    if(Live.Refresh!=null)Live.Refresh();
                }));
            }
            internal void Complete(int code,Exception error)
            {
                ExitCode=code;WorkerDone=true;
                if(Live.Sampler!=null)Live.Sampler.Stop();
                if(Shell.Form.IsDisposed){ExitThread();return;}
                if(code==2&&error==null)error=new InvalidOperationException(keepConfigError??coordinationError??"运行未完成，请查看日志。");
                if(error!=null){Live.Preparing=false;Live.Action="运行未完成："+error.Message;Live.Failed=true;if(Live.Refresh!=null)Live.Refresh();Shell.Form.Text="killer · 未完成";}
                if(Finished!=null)Finished();
            }
        }
        private static int RunInteractive(Func<int> work)
        {
            var session=new InteractiveSession();interactive=session;
            session.Live.Preparing=true;
            session.Shell=BuildUiShell(false,new List<ProcInfo>(),new List<ProcInfo>(),new List<ProcInfo>(),new List<ProcInfo>(),new ServiceReport(),new NetworkReport(),new MemorySnapshot(),null,0,0,session.Live);
            session.Live.Form=session.Shell.Form;session.Shell.Form.Text="killer · 正在准备";
            bool started=false;
            bool workerStarted=false;
            PaintEventHandler recordFirstPaint=delegate{
                if(session.FirstPaintMs<0)session.FirstPaintMs=session.Clock.ElapsedMilliseconds;
            };
            session.Shell.Form.Paint+=recordFirstPaint;
            session.Shell.Hero.Paint+=recordFirstPaint;
            // A fully covered client area may never raise Form.Paint. Startup must
            // follow window lifecycle, independently of repainting or screenshots.
            session.Shell.Form.Shown+=delegate{
                if(started)return;started=true;
                session.Shell.Form.BeginInvoke(new Action(delegate{
                    if(session.Shell.Form.IsDisposed){session.WorkerDone=true;session.ExitThread();return;}
                    var thread=new Thread(delegate(){int code=2;Exception error=null;try{workerInvocation=true;code=work();}catch(Exception ex){error=ex;}finally{workerInvocation=false;try{if(coordinator!=null)coordinator.Dispose();}catch(Exception ex){if(error==null)error=ex;code=2;}coordinator=null;}
                        session.UiContext.Post(delegate{session.Complete(code,error);},null);
                    });workerStarted=true;thread.IsBackground=false;thread.Start();
                }));
            };
            session.Shell.Form.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing && workerStarted && !session.WorkerDone)
                {
                    e.Cancel = MessageBox.Show(session.Shell.Form,
                        "清理仍在进行。关闭窗口后，清理会继续执行。确定关闭窗口吗？",
                        "关闭清理窗口", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes;
                }
            };
            session.Shell.Form.FormClosed+=delegate{if(session.WorkerDone)session.ExitThread();};
            session.Shell.Form.Show();Application.Run(session);interactive=null;return session.ExitCode;
        }
    }
}
