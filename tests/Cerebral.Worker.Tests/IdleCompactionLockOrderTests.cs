using System.Reflection;
using System.Runtime.ExceptionServices;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Activation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

internal static partial class IdleCompactionTests
{
    /// <summary>Actual ready adoption with deterministic provider preparation and real store/controller gates.</summary>
    internal static async Task<int> RunLockOrderAsync()
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); Interlocked.Increment(ref checks); }
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var begin = typeof(WorkerWorkflows).GetMethod("BeginIdleAdmission", flags)!;
        var admit = typeof(WorkerWorkflows).GetMethod("AdmitWithIdle", flags)!;
        var submit = typeof(WorkerWorkflows).GetMethod("SubmitWithIdle", flags)!;
        object Invoke(MethodInfo method, object owner, params object?[] arguments)
        {
            try { return method.Invoke(owner, arguments)!; }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        foreach (var mode in new[] { "former", "correct", "ambient", "paused", "epoch" })
        {
            using var fixture = new Fixture();
            var engineRoot = Path.Combine(fixture.Store.DirectoryPath, "engines");
            EngineHostTests.Pack(engineRoot, "A", true);
            EngineHostTests.Pack(engineRoot, "B", false);
            await using var engine = new EngineController(engineRoot, Path.Combine(fixture.Store.DirectoryPath, "engine-selection"));
            await engine.InitializeAsync();
            Check(!engine.IsPaused, "The actual default engine initializes for " + mode + ".");
            fixture.Host = fixture.NewHost(engine);
            await fixture.SeedAsync(large: true);
            await fixture.PrepareAsync();
            var summaries = fixture.Models.Summaries;
            Check(summaries > 0 && fixture.Models.Active == 0, "A measured fixture candidate is ready without a retained provider lease.");
            Check(fixture.Host.Store.GetConversationCheckpoint("idle-chat", long.MaxValue) is null,
                "Preparation has not durably adopted the optional checkpoint.");
            using var admission = (IDisposable)Invoke(begin, fixture.Host, "idle-chat");
            var ready = admission.GetType().GetProperty("Ready", flags | BindingFlags.Public)!;
            Check(ready.GetValue(admission) is not null, "The actual idle admission takes the prepared candidate.");
            var engineGate = typeof(EngineController).GetField("gate", flags)!.GetValue(engine)!;
            var storeGate = typeof(StateStore).GetField("gate", flags)!.GetValue(fixture.Store)!;
            var transactionConnection = typeof(StateStore).GetField("transactionConnection", flags)!;
            var submission = fixture.Completed.Submission with
            {
                RequestId = "idle-lock-order-" + mode, ConversationSequence = 0,
                UserText = "Accept this exact next turn once."
            };
            var creates = 0;
            WorkflowRun Create()
            {
                creates++;
                var fenced = mode != "paused" && (mode != "epoch" || creates == 1);
                var created = fixture.Store.Locked(() =>
                {
                    Check(Monitor.IsEntered(storeGate), "DurableCreate owns the real Worker store gate.");
                    Check(!fenced || Monitor.IsEntered(engineGate),
                        "Active-engine admission keeps durable work inside Dispatch.");
                    fixture.Store.Put("idle-lock-order", submission.RequestId, true);
                    return (WorkflowRun)Invoke(submit, fixture.Host, submission, admission);
                });
                Check(transactionConnection.GetValue(fixture.Store) is null && (!fenced || Monitor.IsEntered(engineGate)),
                    "The complete Worker transaction has ended while the applicable epoch fence remains held.");
                // A fresh connection proves physical COMMIT while Dispatch still owns its epoch fence.
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = Path.Combine(fixture.Store.DirectoryPath, "cerebral.db"), Pooling = false }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM records WHERE kind='idle-lock-order' AND id=$id";
                command.Parameters.AddWithValue("$id", submission.RequestId);
                Check((long)command.ExecuteScalar()! == 1, "A separate connection observes the completed Worker COMMIT.");
                return created;
            }
            WorkflowRun Admit() => (WorkflowRun)Invoke(admit, fixture.Host, (Func<WorkflowRun>)Create, admission);

            if (mode == "ambient")
            {
                fixture.Store.Locked(() =>
                {
                    try { Admit(); throw new InvalidOperationException("An ambient ready adoption was accepted."); }
                    catch (InvalidOperationException error) when (error.Message.Contains("refuses an ambient", StringComparison.Ordinal)) { Interlocked.Increment(ref checks); }
                    Check(creates == 0 && fixture.Host.Store.TryGet(submission.RequestId) is null &&
                        fixture.Host.Store.GetConversationCheckpoint("idle-chat", long.MaxValue) is null,
                        "Ambient rejection invokes no create and persists neither run nor checkpoint.");
                    return true;
                });
                Check(ready.GetValue(admission) is not null, "Ambient refusal leaves the same prepared candidate available for a clean retry.");
            }
            if (mode is "paused" or "epoch")
            {
                var status = engine.Status();
                engine.Pause(status.Epoch);
                if (mode == "epoch")
                {
                    status = await engine.ActivateAsync("B", status.Epoch);
                    engine.Resume(status.Epoch);
                }
            }

            WorkflowRun accepted;
            if (mode is "former" or "correct")
            {
                Exception? failure = null;
                WorkflowRun? result = null;
                using var entered = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    entered.Set();
                    try { result = mode == "former" ? engine.Dispatch(Create) : Admit(); }
                    catch (Exception error) { failure = error; }
                }) { IsBackground = true, Name = "idle-ready-admission-lock-order" };
                var joined = false;
                try
                {
                    await using var unusedRuntime = new ModelRuntime(fixture.Store, fixture.Configuration.Get, fixture.Events);
                    using var scheduler = new JobScheduler(fixture.Store, fixture.Configuration, new ForegroundExecutor(),
                        unusedRuntime, fixture.Events, NullLogger<JobScheduler>.Instance, fixture.Host);
                    var readable = fixture.Store.Locked(() =>
                    {
                        Check(fixture.Host.Store.ListReady().Count == 0, "No queued workflow is required to reach the idle scheduler read.");
                        thread.Start();
                        if (!entered.Wait(TimeSpan.FromSeconds(5)) ||
                            !SpinWait.SpinUntil(() => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0 || !thread.IsAlive,
                                TimeSpan.FromSeconds(5)) || !thread.IsAlive)
                            throw new InvalidOperationException("Idle admission did not reach bounded gate contention.");
                        var taken = Monitor.TryEnter(engineGate, TimeSpan.FromSeconds(1));
                        if (!taken) return false;
                        try
                        {
                            Check(!engine.IsPaused, "The engine remains readable while the ready admission waits for StateStore.");
                            typeof(JobScheduler).GetMethod("StartIdleCompaction", flags)!.Invoke(scheduler, [CancellationToken.None]);
                            return true;
                        }
                        finally { Monitor.Exit(engineGate); }
                    });
                    joined = thread.Join(TimeSpan.FromSeconds(5));
                    if (!joined) throw new InvalidOperationException("Idle admission did not settle after releasing StateStore.");
                    if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                    Check(readable == (mode == "correct"), mode == "correct"
                        ? "Actual ready adoption takes StateStore before Engine.Dispatch, leaving the scheduler readable."
                        : "The bounded former-order control detects the engine-to-store cycle without hanging.");
                    accepted = result ?? throw new InvalidOperationException("Idle admission returned no run.");
                }
                finally
                {
                    // The store scope has ended before a failed assertion can join the owned thread.
                    if (!joined && thread.IsAlive && !thread.Join(TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("Fixture admission thread remained blocked after releasing all owned gates.");
                }
            }
            else accepted = Admit();

            var checkpoint = fixture.Host.Store.GetConversationCheckpoint("idle-chat", long.MaxValue);
            Check(accepted.Id == submission.RequestId && accepted.Status == WorkflowStatus.Queued,
                "The exact next workflow is durably admitted for " + mode + ".");
            Check((checkpoint is not null) == (mode is not ("paused" or "epoch")),
                "Only a ready candidate from the current active epoch is adopted for " + mode + ".");
            if (mode is "paused" or "epoch")
                Check(ready.GetValue(admission) is null, "The optional stale or paused candidate is discarded under the admission boundary.");
            var revision = fixture.Host.Journal.GetRevision("idle-chat");
            var commits = engine.Status().CommittedDecisions;
            var repeated = Admit();
            Check(repeated.Id == accepted.Id && repeated.Revision == accepted.Revision &&
                fixture.Host.Journal.GetRevision("idle-chat") == revision,
                "Repeating the same RID preserves the winning run and writes no duplicate adoption or journal events.");
            Check(engine.Status().CommittedDecisions == commits && engine.Status().ActiveEvaluations == 0,
                "Dispatch admission neither fabricates a decision commit nor retains an evaluation lease.");
            Check(fixture.Models.Summaries == summaries && fixture.Models.Active == 0 && creates == 2,
                "Adoption and duplicate admission perform no additional provider generation.");
        }
        Console.WriteLine($"Idle-ready lock-order tests passed ({checks}; real gates/SQLite/default engine, deterministic provider preparation; no native model or Worker process).");
        return checks;
    }
}
