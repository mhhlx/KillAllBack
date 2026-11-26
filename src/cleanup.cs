using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KillAllBack
{
    public static partial class Program
    {
        internal static int InitialCleanupRemaining(int remainingMs)
        {
            return Math.Max(0, remainingMs - 500);
        }

        internal static int RelaunchWaitMs(int remainingMs, int exitObservedRemainingMs)
        {
            if (exitObservedRemainingMs <= 0) { return 0; }
            return Math.Max(0, Math.Min(InitialCleanupRemaining(remainingMs),
                remainingMs - (exitObservedRemainingMs - 250)));
        }

        internal static int ReviewCleanupRemaining(int remainingMs, bool pendingGraceful)
        {
            return Math.Max(0, remainingMs - (pendingGraceful ? 50 : 0));
        }

        internal static void ObserveRelaunches(Func<int> remainingBudget, Func<int> lastExitObserved,
            Func<bool> hasPendingSource, Action<int> waitAndPoll)
        {
            int initial = remainingBudget();
            int end = initial - Math.Min(250, InitialCleanupRemaining(initial));
            while (true)
            {
                int remaining = remainingBudget();
                int observed = lastExitObserved();
                int wait = Math.Min(remaining - end,
                    observed > 0 ? RelaunchWaitMs(remaining, observed) : hasPendingSource() ? remaining - end : 0);
                if (wait <= 0) { return; }
                waitAndPoll(Math.Min(25, wait));
            }
        }

        internal static Dictionary<string, Task<ServiceReport>> StartServiceStops(IEnumerable<string> services,
            Func<int> remainingBudget, Func<string, Func<int>, ServiceReport> stop)
        {
            var work = new Dictionary<string, Task<ServiceReport>>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in services.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string service = name;
                work.Add(service, Task.Factory.StartNew(() => stop(service, remainingBudget),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            }
            return work;
        }

        internal static void CollectServiceStops(Dictionary<string, Task<ServiceReport>> work,
            ServiceReport result, Func<int> remainingBudget)
        {
            int remaining = remainingBudget();
            if (remaining > 0 && work.Values.Any(task => !task.IsCompleted))
            {
                Task.WaitAll(work.Values.Cast<Task>().ToArray(), remaining);
            }

            foreach (var operation in work)
            {
                if (!operation.Value.IsCompleted)
                {
                    result.Failed.Add(new ServiceFailure(operation.Key, "总清理预算已耗尽，尚未确认服务停止"));
                    continue;
                }
                ServiceReport completed = operation.Value.Result;
                result.Stopped.AddRange(completed.Stopped);
                result.NotRunning.AddRange(completed.NotRunning);
                result.Failed.AddRange(completed.Failed);
            }
        }

        internal static List<ProcInfo> SelectCleanupReviewTargets(IEnumerable<ProcInfo> plan,
            IEnumerable<ProcInfo> found, ISet<string> submitted, Func<int, long?> readSourceStarted, Func<ProcInfo, int> readParent)
        {
            Dictionary<string, ProcInfo> original = plan.Where(p => p.StartTicks > 0).ToDictionary(p => p.InstanceKey);
            var exitsByPath = original.Values.Where(p => p.ExitUtcTicks > 0 && p.RelaunchSource != null
                && !string.IsNullOrEmpty(p.Path)).ToLookup(p => p.Path, StringComparer.OrdinalIgnoreCase);
            var sourceStarts = new Dictionary<int, long?>();
            var first = new List<ProcInfo>();
            var relaunched = new List<ProcInfo>();

            foreach (ProcInfo current in found)
            {
                if (current.StartTicks <= 0 || submitted.Contains(current.InstanceKey)) { continue; }
                ProcInfo previous;
                if (original.TryGetValue(current.InstanceKey, out previous))
                {
                    if (previous.ExitUtcTicks == 0 && !IsCoordinationFailure(previous)) { first.Add(previous); }
                    continue;
                }

                if (string.IsNullOrEmpty(current.Path) || !exitsByPath.Contains(current.Path)) { continue; }
                int parent = readParent(current);
                var origins = exitsByPath[current.Path].Where(p => p.RelaunchSource.Pid == parent);
                if (!origins.Any()) { continue; }

                long? started;
                if (!sourceStarts.TryGetValue(parent, out started))
                {
                    started = readSourceStarted(parent);
                    sourceStarts.Add(parent, started);
                }
                if (started.HasValue && origins.Any(p => p.RelaunchSource.Started == started.Value
                    && LocalProcessTicksToUtc(current.StartTicks) > p.ExitUtcTicks))
                {
                    relaunched.Add(current);
                }
            }

            first.AddRange(relaunched);
            return first;
        }

        private static long LocalProcessTicksToUtc(long ticks)
        {
            return new DateTime(ticks, DateTimeKind.Local).ToUniversalTime().Ticks;
        }
        private static void ReadRelaunchSources(IEnumerable<ProcInfo> plan,
            ConcurrentDictionary<int, TargetIdentity> serviceSources, Dictionary<int, ProcInfo> background, int session)
        {
            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (ProcInfo candidate in plan.Where(p => p.ClaimedForCleanup && p.ParentPid > 0
                && p.RelaunchSource == null && (p.ExitPending || p.ExitUtcTicks > 0)))
            {
                TargetIdentity origin;
                if (!serviceSources.TryGetValue(candidate.ParentPid, out origin))
                {
                    if (string.IsNullOrEmpty(candidate.Path)) { continue; }
                    ProcInfo parent;
                    if (!background.TryGetValue(candidate.ParentPid, out parent))
                    {
                        parent = ReadBackgroundParent(candidate.ParentPid, session);
                        background.Add(candidate.ParentPid, parent);
                    }
                    if (parent != null && !parent.Path.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Path.GetDirectoryName(parent.Path), Path.GetDirectoryName(candidate.Path),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        origin = new TargetIdentity(parent.Pid, LocalProcessTicksToUtc(parent.StartTicks));
                    }
                }
                if (origin != null && origin.Pid != candidate.Pid && origin.Started <= LocalProcessTicksToUtc(candidate.StartTicks))
                {
                    candidate.RelaunchSource = origin;
                }
            }
        }

        private static ProcInfo ReadBackgroundParent(int pid, int session)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    if (process.SessionId != session || process.MainWindowHandle != IntPtr.Zero) { return null; }
                    var parent = new ProcInfo { Pid = pid, StartTicks = ProcessStartTicks(process) };
                    IntPtr handle = OpenVerifiedProcess(parent, 0);
                    if (handle == IntPtr.Zero) { return null; }
                    try
                    {
                        parent.Path = NativeIdentity.Image(handle);
                        return string.IsNullOrEmpty(parent.Path) ? null : parent;
                    }
                    finally { CloseHandle(handle); }
                }
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }

        internal static int ReadVerifiedParent(ProcInfo process, Dictionary<int, int> parents)
        {
            int parent;
            if (!parents.TryGetValue(process.Pid, out parent) || parent <= 0) { return 0; }

            // 子进程创建时间在快照前取得，快照后用同一身份的句柄复核映像。
            IntPtr handle = OpenVerifiedProcess(process, 0);
            if (handle == IntPtr.Zero) { return 0; }
            CloseHandle(handle);
            return parent;
        }

        private sealed class CleanupExecution : IDisposable
        {
            private const string Graceful = "优雅关闭";
            private const string BudgetSkip = "总清理预算已耗尽，跳过终止";
            private readonly Func<int> remaining;
            private readonly UiLiveState live;
            private readonly List<ProcInfo> killed;
            private readonly List<ProcInfo> failed;
            private readonly List<ProcInfo> exited;
            private readonly List<string> costs;
            private readonly PhaseTrace phases;
            private readonly Dictionary<string, ProcInfo> failures = new Dictionary<string, ProcInfo>();
            private readonly HashSet<string> finished = new HashSet<string>();
            private readonly HashSet<string> usedTaskkill = new HashSet<string>();
            private readonly Dictionary<string, ProcRow> rows = new Dictionary<string, ProcRow>();
            private readonly List<ExitRequest> requested = new List<ExitRequest>();
            private readonly List<ExitRequest> graceful = new List<ExitRequest>();

            internal CleanupExecution(Func<int> remaining, UiLiveState live, List<ProcInfo> killed,
                List<ProcInfo> failed, List<ProcInfo> exited, List<string> costs, PhaseTrace phases)
            {
                this.remaining = remaining;
                this.live = live;
                this.killed = killed;
                this.failed = failed;
                this.exited = exited;
                this.costs = costs;
                this.phases = phases;
            }

            internal List<ProcInfo> Run(List<ProcInfo> plan, ServiceReport services)
            {
                var sourceServices = new ConcurrentDictionary<int, TargetIdentity>();
                LivePhase(live, "正在关闭目标进程并停止已配置服务…");
                var serviceWork = StartServiceStops(stopServiceNames, remaining,
                    (name, budget) => StopConfiguredService(name, budget, sourceServices));

                Func<int> firstBudget = () => InitialCleanupRemaining(remaining());
                var firstFailures = Submit(plan, firstBudget);
                phases.Mark("t3");
                PollExits();
                phases.Mark("t5");
                RetryFailed(firstFailures, firstBudget);
                phases.Mark("t4");

                PollExits();
                var background = new Dictionary<int, ProcInfo>();
                int session = GetShellSession();
                ReadRelaunchSources(plan, sourceServices, background, session);
                ObserveRelaunches(remaining,
                    () => plan.Where(p => p.RelaunchSource != null && p.ExitObservedRemaining > 0)
                        .Select(p => p.ExitObservedRemaining).DefaultIfEmpty(0).Min(),
                    () => plan.Any(p => p.RelaunchSource != null && p.ExitPending),
                    wait =>
                    {
                        UiPump(wait);
                        PollExits();
                    });
                ReadRelaunchSources(plan, sourceServices, background, session);

                LivePhase(live, "正在复查仍在运行的清理目标…");
                Func<int> reviewBudget = () => ReviewCleanupRemaining(remaining(), graceful.Count > 0);
                List<ProcInfo> found = reviewBudget() > 0 ? FindCandidatesLite(reviewBudget) : plan;
                phases.Mark("review_scan_done");
                var identityClock = Stopwatch.StartNew();
                var submitted = new HashSet<string>(requested.Concat(graceful).Select(request => request.Target.InstanceKey));
                submitted.UnionWith(finished);
                var parents = new Lazy<Dictionary<int, int>>(NativeIdentity.Parents);
                var review = SelectCleanupReviewTargets(plan, found, submitted, NativeIdentity.ReadStarted,
                    process => reviewBudget() > 0 ? ReadVerifiedParent(process, parents.Value) : 0);
                identityClock.Stop();
                phases.RecordDuration("review_identity_ms", identityClock.ElapsedMilliseconds);

                var originals = new HashSet<string>(plan.Select(p => p.InstanceKey));
                if (live != null)
                {
                    int added = review.Count(p => !originals.Contains(p.InstanceKey));
                    if (added > 0) { DispatchLive(live, () => live.PlanCount += added); }
                }

                var reviewFailures = Submit(review, reviewBudget);
                phases.RecordDuration("review_taskkill_ms", RetryFailed(reviewFailures, reviewBudget));
                phases.Mark("t6");

                var confirmClock = Stopwatch.StartNew();
                FinishGraceful();
                ConfirmExitRequests(requested, remaining, ProcessExitSignaled, Confirm);
                foreach (ExitRequest request in requested)
                {
                    Fail(request.Target, "已请求结束，收尾时仍未确认退出");
                }
                phases.RecordDuration("exit_confirm_ms", confirmClock.ElapsedMilliseconds);
                CollectServiceStops(serviceWork, services, remaining);

                foreach (ProcInfo target in failures.Values)
                {
                    failed.Add(target);
                    bool budget = IsBudgetFailure(target);
                    bool peer = target.FailReason == "其他实例处理中";
                    LiveSettle(live, target, budget ? "超时跳过" : peer ? "协同处理" : "未结束",
                        peer ? Theme.Muted : budget ? Theme.Warn : Theme.Danger, target.FailReason, 2, Row(target));
                }

                var seen = new HashSet<string>();
                return failures.Values.Concat(found).Where(p => !finished.Contains(p.InstanceKey)
                    && seen.Add(p.InstanceKey)).ToList();
            }

            private ProcRow Row(ProcInfo target)
            {
                if (live == null) { return null; }
                ProcRow row;
                if (!rows.TryGetValue(target.InstanceKey, out row))
                {
                    row = LiveBegin(live, target);
                    rows.Add(target.InstanceKey, row);
                }
                return row;
            }

            private void Fail(ProcInfo target, string reason)
            {
                target.FailReason = reason;
                failures[target.InstanceKey] = target;
            }

            private void Reject(ProcInfo target, string reason)
            {
                if (reason == "进程已不存在")
                {
                    failures.Remove(target.InstanceKey);
                    finished.Add(target.InstanceKey);
                    target.FailReason = reason;
                    exited.Add(target);
                    LiveSettle(live, target, "已退出", Theme.Muted, reason, 0, Row(target));
                    return;
                }
                Fail(target, reason);
            }

            private List<ProcInfo> Submit(IList<ProcInfo> candidates, Func<int> budget)
            {
                var retry = new List<ProcInfo>();
                RunTaskKillBatches(candidates, budget, candidate => true,
                    candidate => Fail(candidate, BudgetSkip), delegate(List<ProcInfo> batch, int waitMs)
                {
                    var claimClock = Stopwatch.StartNew();
                    List<ProcInfo> admitted = ClaimCleanupTargets(batch, budget, Reject);
                    claimClock.Stop();
                    phases.AddDuration("claim_ms", claimClock.ElapsedMilliseconds);
                    foreach (ProcInfo target in admitted)
                    {
                        target.ClaimedForCleanup = true;
                        Row(target);
                    }
                    SubmitClaimed(admitted, budget, true, retry);
                    PollExits();
                });
                return retry;
            }

            private void SubmitClaimed(List<ProcInfo> admitted, Func<int> budget, bool allowGraceful, List<ProcInfo> retry)
            {
                if (admitted.Count == 0) { return; }
                var requests = new ExitRequest[admitted.Count];
                var reasons = new string[admitted.Count];
                var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(12, Math.Max(4, Environment.ProcessorCount)) };
                try
                {
                    Parallel.For(0, admitted.Count, options, index =>
                    {
                        ProcInfo candidate = admitted[index];
                        var clock = Stopwatch.StartNew();
                        requests[index] = BeginExit(candidate, budget, allowGraceful, out reasons[index]);
                        AddKillCost(costs, candidate, requests[index].Method ?? "TerminateProcess", clock);
                    });
                }
                finally
                {
                    for (int index = 0; index < admitted.Count; index++)
                    {
                        ProcInfo target = admitted[index];
                        ExitRequest request = requests[index];
                        if (request.Handle != IntPtr.Zero)
                        {
                            Track(request);
                        }
                        else if (reasons[index] != null)
                        {
                            FailOrExited(target, reasons[index]);
                            if (!finished.Contains(target.InstanceKey) && !IsBudgetFailure(target)) { retry.Add(target); }
                        }
                    }
                }
            }

            private void Track(ExitRequest request)
            {
                failures.Remove(request.Target.InstanceKey);
                request.Target.FailReason = null;
                request.Target.ExitPending = true;
                if (request.Method == Graceful) { graceful.Add(request); }
                else { requested.Add(request); }
            }

            private void FailOrExited(ProcInfo target, string reason)
            {
                long? started = NativeIdentity.ReadStarted(target.Pid);
                if (!started.HasValue || (started.Value > 0 && target.StartTicks > 0
                    && LocalProcessTicksToUtc(target.StartTicks) != started.Value))
                {
                    Reject(target, "进程已不存在");
                    return;
                }
                Fail(target, reason);
            }

            private long RetryFailed(List<ProcInfo> candidates, Func<int> budget)
            {
                if (candidates.Count == 0 || budget() <= 0) { return 0; }
                EnableDebugPrivilege();
                long elapsed = 0;
                RunTaskKillBatches(candidates, budget, candidate => !usedTaskkill.Contains(candidate.InstanceKey),
                    candidate => Fail(candidate, BudgetSkip), delegate(List<ProcInfo> batch, int waitMs)
                {
                    foreach (ProcInfo candidate in batch) { usedTaskkill.Add(candidate.InstanceKey); }
                    long batchMs;
                    List<ExitRequest> attempts = TaskKillBatch(batch, budget, out batchMs);
                    elapsed += batchMs;
                    foreach (ExitRequest request in attempts) { Track(request); }
                    var accepted = new HashSet<string>(attempts.Select(request => request.Target.InstanceKey));
                    foreach (ProcInfo candidate in batch)
                    {
                        AddKillCostMs(costs, candidate, "taskkill", batchMs);
                        if (!accepted.Contains(candidate.InstanceKey)) { FailOrExited(candidate, "taskkill /F 未确认结束"); }
                    }
                    PollExits();
                });
                return elapsed;
            }

            private void PollExits()
            {
                ConfirmExitRequests(requested, () => 0, ProcessExitSignaled, Confirm);
                ConfirmExitRequests(graceful, () => 0, ProcessExitSignaled, Confirm);
            }

            private void FinishGraceful()
            {
                PollExits();
                while (graceful.Count > 0 && remaining() > 50)
                {
                    int wait = Math.Min(25, remaining() - 50);
                    if (wait > 0) { ProcessExitSignaled(graceful[0], wait); }
                    PollExits();
                }

                var force = new List<ProcInfo>();
                foreach (ExitRequest request in graceful)
                {
                    CloseHandle(request.Handle);
                    request.Target.ExitPending = false;
                    force.Add(request.Target);
                }
                graceful.Clear();
                var retry = new List<ProcInfo>();
                SubmitClaimed(force, remaining, false, retry);
                RetryFailed(retry, remaining);
            }

            private void Confirm(ExitRequest request)
            {
                ProcInfo target = request.Target;
                long created, exitedAt, kernel, user;
                try
                {
                    if (GetProcessTimes(request.Handle, out created, out exitedAt, out kernel, out user) && exitedAt > 0)
                    {
                        target.ExitUtcTicks = DateTime.FromFileTimeUtc(exitedAt).Ticks;
                    }
                }
                finally { CloseHandle(request.Handle); }
                target.ExitObservedRemaining = remaining();
                target.ExitPending = false;
                target.Note = request.Method;
                target.FailReason = null;
                failures.Remove(target.InstanceKey);
                finished.Add(target.InstanceKey);
                killed.Add(target);
                LiveSettle(live, target, "已结束", Theme.Ok, target.Path + " · " + request.Method, 1, Row(target));
            }

            public void Dispose()
            {
                foreach (ExitRequest request in requested.Concat(graceful)) { CloseHandle(request.Handle); }
            }
        }

        private static ExitRequest BeginExit(ProcInfo target, Func<int> remainingBudget, bool allowGraceful, out string reason)
        {
            reason = null;
            if (remainingBudget() <= 0)
            {
                reason = "总清理预算已耗尽，跳过终止";
                return default(ExitRequest);
            }

            if (allowGraceful && IsGraceful(target.Name, target.Path))
            {
                IntPtr query = OpenVerifiedProcess(target, SYNCHRONIZE);
                if (query != IntPtr.Zero)
                {
                    bool asked = false;
                    try
                    {
                        using (Process process = Process.GetProcessById(target.Pid))
                        {
                            if (ProcessStartTicks(process) == target.StartTicks && remainingBudget() > 0)
                            {
                                asked = process.CloseMainWindow();
                            }
                        }
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    finally { if (!asked) { CloseHandle(query); } }
                    if (asked) { return new ExitRequest(target, query, "优雅关闭"); }
                }
            }

            IntPtr handle = RequestTermination(target, remainingBudget, out reason);
            return handle == IntPtr.Zero ? default(ExitRequest) : new ExitRequest(target, handle, "TerminateProcess");
        }
    }
}
