using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class ExecutionMetricsTests
{
    private const string Installation = "accepted-installation";
    private static readonly DateTimeOffset SampleTime = DateTimeOffset.UnixEpoch.AddDays(1);

    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Execution metrics: " + message);
            checks++;
        }
        ExecutionMetricsSnapshot Project(params ExecutionMetricsInput[] inputs) =>
            JobScheduler.ProjectExecutionMetrics(Installation, "fixture-scheduler-generation", SampleTime, inputs);

        var first = Workflow("one", Profile("lead", 4, "primary-one"));
        var second = Workflow("two", Profile("lead", 5, "primary-two"));
        var sameAgent = Project(first, second);
        Check(sameAgent.LocalWorkingAgentCount == 1 && sameAgent.ObservedLocalExecutionCount == 2 && sameAgent.Observations.Count == 2,
            "simultaneous accepted revisions/routes remain independent executions but one installation/agent identity");
        Check(sameAgent.Observations.Select(item => item.ExecutionId).Distinct().Count() == 2 &&
            sameAgent.Observations.Select(item => item.AgentRevision).SequenceEqual(new int?[] { 4, 5 }),
            "exact ownership IDs and accepted revisions survive projection");

        var primary = Profile("lead", 6, "remote-primary", "model-worker");
        var compactor = Profile("compactor", 3, "remote-compactor", "compactor-worker");
        var accepted = Workflow("remote-model", primary, "verified-model-installation", compactor);
        var localRemoteModel = Project(accepted);
        var row = localRemoteModel.Observations.Single();
        Check(localRemoteModel.LocalWorkingAgentCount == 1 && row.ExecutionOwnerInstallationId == Installation &&
            row.ExecutionWorkerId == "local" && localRemoteModel.ConfirmedRemoteWorkingAgentCount is null,
            "accepted remote model routing cannot turn local execution ownership into confirmed remote work");
        Check(row.AcceptedModelRoutes.Count == 2 && row.AcceptedModelRoutes[0].ModelWorkerInstallationId == "verified-model-installation" &&
            row.AcceptedModelRoutes[1].Purpose == "compactor" && row.AcceptedModelRoutes[1].AgentId == "compactor" &&
            localRemoteModel.ObservedLocalExecutionCount == 1,
            "separately accepted compactor provenance does not create a second execution or agent");
        primary.ModelId = "later-current-model"; primary.Revision = 100;
        Check(Project(accepted).Observations.Single().AcceptedModelRoutes[0].ModelId == "remote-primary",
            "accepted serialized workflow settings are independent of later mutable profile changes");

        var remoteJob = Job("remote-poll", Profile("sender", 2, "sender-model"), "receiver-worker");
        remoteJob.RemoteConfirmed = true; remoteJob.RemoteJobId = "receiver-job"; remoteJob.Status = "running";
        var remote = Project(new ExecutionMetricsInput(remoteJob.Id, false, Job: remoteJob));
        Check(remote.LocalWorkingAgentCount == 0 && remote.ObservedRemoteDispatchOrReconciliationCount == 1 &&
            remote.ConfirmedRemoteWorkingAgentCount is null && remote.Observations.Single().ExecutionOwnerInstallationId is null,
            "sender polling and a prior confirmation flag cannot prove current receiver execution");
        Check(remote.Observations.Single().AcceptedModelRoutes.Single().ModelWorkerInstallationId is null,
            "legacy model-worker installation remains unobserved instead of borrowing a current registration");

        var compact = Job("legacy-compaction", Profile("compactor", 2, "compact-model")); compact.Kind = "compact";
        var maintenance = Project(new(WorkerWorkflows.IdleCompactionExecutionKey, false), new(compact.Id, false, Job: compact));
        Check(maintenance.ObservedMaintenanceCount == 2 && maintenance.LocalWorkingAgentCount == 0 && maintenance.ObservedLocalExecutionCount == 0,
            "idle and legacy compaction ownership remain separate from working-agent counts");
        var cancelling = Project(first with { CancellationRequested = true });
        Check(cancelling.LocalWorkingAgentCount == 1 && cancelling.Observations.Single().CancellationRequested,
            "cancellation intent does not retire ownership before finally releases it");

        var mismatched = first with { Submission = first.Submission! with { ProfileId = "other-agent" } };
        var invalid = Project(mismatched);
        Check(invalid.Partial && invalid.Unprojectable && invalid.UnprojectableCount == 1 && invalid.LocalWorkingAgentCount is null && invalid.ObservedLocalWorkingAgentCount == 0,
            "unprojectable required identity cannot become a complete zero count");
        var unsupported = first with { Submission = first.Submission! with { SettingsSnapshot = CerebralJson.Element(new { resolvedSettingsVersion = 99 }) } };
        Check(Project(unsupported).UnprojectableCount == 1, "unsupported accepted snapshot version degrades without recapturing settings");
        var invalidRemoteV2 = accepted.Submission!.SettingsSnapshot.Deserialize<WorkerExecutionSnapshot.Snapshot>(CerebralJson.Options)!;
        invalidRemoteV2 = invalidRemoteV2 with { ResolvedSettingsVersion = 2 };
        Check(Project(accepted with { Submission = accepted.Submission! with { SettingsSnapshot = CerebralJson.Element(invalidRemoteV2) } }).UnprojectableCount == 1,
            "legacy snapshot without verified remote origin cannot claim a current route");

        var enumerated = 0;
        IEnumerable<ExecutionMetricsInput> Many()
        {
            for (var index = 0; index < 300; index++) { enumerated++; yield return Workflow("bounded-" + index, Profile("lead", 1, "model")); }
        }
        var bounded = JobScheduler.ProjectExecutionMetrics(Installation, "bounded-generation", SampleTime, Many());
        Check(enumerated == 257 && bounded.Observations.Count == 256 && bounded.Truncated && bounded.Partial && bounded.LocalWorkingAgentCount is null,
            "projection bounds enumeration and output and retains uncertainty on truncation");
        var empty = Project();
        Check(empty.State == "observed" && empty.LocalWorkingAgentCount == 0 && empty.ConfirmedRemoteWorkingAgentCount is null,
            "an observed empty local owner set does not fabricate a remote zero");
        var drift = JobScheduler.ProjectExecutionMetrics(Installation, "drift-generation", SampleTime, [first], ownershipChangedCount: 1);
        Check(drift.Partial && drift.OwnershipChangedCount == 1 && drift.LocalWorkingAgentCount is null,
            "ownership drift suppresses a complete unique count");
        Check(sameAgent.ProcessGenerationScope.Contains("scheduler-instance", StringComparison.Ordinal) && sameAgent.SampledAt == SampleTime,
            "sample provenance names scheduler scope rather than model runtime generation");
        var json = JsonSerializer.Serialize(localRemoteModel, CerebralJson.Options);
        Check(!json.Contains("PRIVATE", StringComparison.Ordinal) && !json.Contains("settingsSnapshot", StringComparison.OrdinalIgnoreCase) &&
            !json.Contains("prompt", StringComparison.OrdinalIgnoreCase) && !json.Contains("instructions", StringComparison.OrdinalIgnoreCase),
            "public observation contains no names, prompt, instructions, tool bodies or accepted settings body");
        Check(json.Contains("configuration provenance", StringComparison.Ordinal) && json.Contains("not current provider activity", StringComparison.Ordinal),
            "accepted routes cannot be mistaken for active provider or slot evidence");
        var badBound = false;
        try { JobScheduler.ProjectExecutionMetrics(Installation, "fixture", SampleTime, [], 257); }
        catch (ArgumentOutOfRangeException) { badBound = true; }
        Check(badBound, "caller cannot expand the 256-observation boundary");

        CurrentOwnership(Check);
        await DispatcherBoundaryAsync(Check);
        Console.WriteLine($"Execution metrics: {checks} deterministic projection and isolated scheduler-ownership assertions passed; no Worker, provider or network started.");
    }

    private static void CurrentOwnership(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-execution-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new StateStore(root);
            // Deliberately unavailable runtime/configuration/executor: this read must not use them.
            using var scheduler = new JobScheduler(store, null!, null!, null!, new EventHub(), NullLogger<JobScheduler>.Instance);
            var ownership = (ConcurrentDictionary<string, CancellationTokenSource>)typeof(JobScheduler)
                .GetField("running", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scheduler)!;
            var job = Job("owned-job", Profile("accepted-agent", 7, "accepted-model"));
            job.Status = "waiting-human"; // A durable status alone is neither added nor substituted for ownership.
            store.Put("job", job.Id, job);
            store.Put("job", "unowned-job", Job("unowned-job", Profile("other-agent", 1, "other-model")));
            store.Put("agent", job.ProfileId, Profile("accepted-agent", 99, "mutable-current-model"));
            using var cancellation = new CancellationTokenSource();
            ownership[job.Id] = cancellation;
            var first = scheduler.ObserveExecutionMetrics(Installation);
            check(first.LocalWorkingAgentCount == 1 && first.Observations.Single().AgentRevision == 7 &&
                first.Observations.Single().AcceptedModelRoutes.Single().ModelId == "accepted-model",
                "current cancellation ownership reads the exact accepted job, not mutable agent state or all durable jobs");
            cancellation.Cancel();
            check(scheduler.ObserveExecutionMetrics(Installation).Observations.Single().CancellationRequested,
                "current owner cancellation remains observable until ownership is removed");
            ownership.TryRemove(job.Id, out _);
            var after = scheduler.ObserveExecutionMetrics(Installation);
            check(after.LocalWorkingAgentCount == 0 && after.Observations.Count == 0 && first.ProcessGenerationId == after.ProcessGenerationId,
                "release removes only current ownership and generation remains stable for that scheduler instance");
        }
        finally { SqliteConnection.ClearAllPools(); Cleanup(root); }
    }

    private static async Task DispatcherBoundaryAsync(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"cerebral-execution-metrics-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store=new StateStore(root);
            var host=new HostSettings { InstallationId="11111111111111111111111111111111",AccessKey="",Coordinator=true };
            store.Put("settings","host",host);
            var config=new Configuration(store);var events=new EventHub();
            using var scheduler=new JobScheduler(store,null!,null!,null!,events,NullLogger<JobScheduler>.Instance);
            var commands=new CommandDispatcher(store,config,null!,scheduler,null!,events);
            var before=JsonSerializer.Serialize(store.Get<HostSettings>("settings","host"),CerebralJson.Options);
            var result=await commands.ExecuteAsync(new("metrics.execution.status",CerebralJson.Element(new { })));
            check(result.Success && result.Data.GetProperty("issuingInstallationId").GetString()==host.InstallationId &&
                result.Data.GetProperty("localWorkingAgentCount").GetInt32()==0,
                "real owner dispatcher returns an observed empty local count without requiring an HTTP credential or provider");
            check(config.GetInvocationCount==0 && before==JsonSerializer.Serialize(store.Get<HostSettings>("settings","host"),CerebralJson.Options),
                "owner metric path preserves configuration and never invokes normalization");
            var admin=Profile("admin",1,"private-model");admin.Role="admin";admin.Capabilities=["administration"];
            store.Put("agent",admin.Id,admin);
            var refused=await commands.ExecuteAsync(new("metrics.execution.status",CerebralJson.Element(new { }),admin.Id));
            check(!refused.Success && refused.Error?.Contains("owner interface",StringComparison.Ordinal)==true,
                "an administration profile cannot read owner-only accepted routing observations");
            var invalid=await commands.ExecuteAsync(new("metrics.execution.status",CerebralJson.Element(new { maximumObservations=1000 })));
            check(!invalid.Success,"an owner cannot expand the fixed operation payload or observation bound");
            check(config.GetInvocationCount==0,"refused metric requests do not normalize configuration");
        }
        finally { SqliteConnection.ClearAllPools();Cleanup(root); }
    }

    private static void Cleanup(string root)
    {
        var full=Path.GetFullPath(root);var temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if(Path.GetDirectoryName(full)!=temp || !Path.GetFileName(full).StartsWith("cerebral-execution-metrics-",StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside the exact owned metrics fixture directory.");
        Directory.Delete(full,true);
    }

    private static AgentProfile Profile(string id, int revision, string model, string modelWorker = "local") => new()
    {
        Id = id, Revision = revision, Name = "PRIVATE NAME", Instructions = "PRIVATE INSTRUCTIONS",
        WorkerId = "local", ModelId = model, ModelWorkerId = modelWorker
    };

    private static AgentJob Job(string id, AgentProfile profile, string worker = "local") => new()
    { Id = id, ProfileId = profile.Id, Profile = profile, WorkerId = worker, Prompt = "PRIVATE PROMPT" };

    private static ExecutionMetricsInput Workflow(string id, AgentProfile profile, string? modelInstallation = null, AgentProfile? compactor = null)
    {
        WorkerExecutionSnapshot.ModelBinding Bind(AgentProfile value, string? origin) => new(value.ModelId,
            "PRIVATE MODEL PATH", null, [], 4096, 2, 0, ModelWorkerId: value.ModelWorkerId, ModelWorkerInstallationId: origin);
        var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, profile,
            "PRIVATE SHARED INSTRUCTIONS", "PRIVATE ROLE INSTRUCTIONS", Bind(profile, modelInstallation),
            [], [], [], [], false, "accepted-routing-fingerprint", [], [],
            compactor is null ? null : new(compactor, "PRIVATE COMPACTOR INSTRUCTIONS", Bind(compactor, "verified-compactor-installation")));
        return new("workflow:" + id, false, new(id, "fixture-conversation", profile.Id, "fixture.definition", "1",
            "PRIVATE USER TEXT", "PRIVATE ASSIGNMENT", WorkflowContract.EmptyObject, CerebralJson.Element(snapshot)));
    }
}
