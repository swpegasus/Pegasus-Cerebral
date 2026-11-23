using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic provider plus real SQLite tests. No native/provider inference is claimed.</summary>
internal static partial class IdleCompactionTests
{
    internal static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); checks++; }

        await CheckpointSourceChecksAsync(Check);

        using (var fixture = new Fixture())
        {
            await fixture.SeedAsync(large: true);
            var terminal = fixture.Host.Store.Get(fixture.Completed.Id);
            await fixture.PrepareAsync();
            Check(fixture.Models.Summaries > 0 && fixture.Models.Active == 0, "idle reducer prepares measured summary and releases confirmed slot");
            Check(fixture.Host.Store.GetConversationCheckpoint("idle-chat", long.MaxValue) is null,
                "ready maintenance does not rewrite durable context");
            var next = await fixture.SubmitAsync("next");
            var checkpoint = fixture.Host.Store.GetConversationCheckpoint("idle-chat", next.Submission.ConversationSequence);
            Check(checkpoint is not null && checkpoint.SourceRevision == fixture.Completed.Submission.ConversationSequence,
                "next submit atomically adopts checkpoint before latest user exchange cutoff");
            Check(fixture.Host.Store.Get(terminal.Id).Revision == terminal.Revision &&
                  fixture.Host.Store.Get(terminal.Id).Status == WorkflowStatus.Completed,
                "adoption never rewrites completed run");
            var history = await fixture.HistoryAsync(next);
            var text = Text(history.Context);
            Check(text.Contains(Fixture.OriginalGoal, StringComparison.Ordinal) &&
                  text.Contains(Fixture.LastUser, StringComparison.Ordinal) &&
                  text.Contains(Fixture.LastAnswer, StringComparison.Ordinal),
                "actual next-turn history keeps initial goal and latest exchange verbatim");
            Check(history.Context.Contributions.Any(source => source.Requirement == ContextRequirement.Required &&
                source.ExactReadHandle == fixture.Host.Store.ReadConversationGoal(ConversationWorkflowModule.DefinitionId, "idle-chat").Original.EventId),
                "initial goal is a required exact-source anchor");
            Check(history.Context.Checkpoint?.Summary == fixture.Models.Summary,
                "next-turn host history actually reuses the prepared summary");
            var adopted = fixture.Host.Journal.ReadExact("idle-chat", checkpoint!.Checkpoint.Id, next.Submission.ConversationSequence);
            Check(adopted.Type == "context.idle-compaction" && adopted.Data!.Value.GetProperty("usage").GetArrayLength() > 0,
                "adopted candidate has exact bounded source/usage receipt");
        }

        using (var fixture = new Fixture(receiptConsumer: true))
        {
            await fixture.SeedAsync(large: true);
            Check(WorkerExecutionSnapshot.Read(fixture.Completed.Submission.SettingsSnapshot)
                .ToolReceiptConsumer is { SchemaVersion: 1, Supported: true },
                "the accepted loaded definition freezes receipt-consumer support before idle maintenance");
            Check(WorkerExecutionSnapshot.Read(fixture.Completed.Submission.SettingsSnapshot)
                .SelectedToolOwners?.Single() is { ToolName: "fixture.builtin", PackageId: null },
                "normal admission explicitly freezes a selected built-in tool without a service owner");
            await fixture.PrepareAsync();
            Check(fixture.Models.Summaries > 0 && fixture.Models.Active == 0,
                "idle recapture keeps the same loaded receiver declaration and prepares the measured summary");
        }

        foreach (var change in new[] { "head", "profile", "routing", "preference", "restart", "baseline" })
        {
            using var fixture = new Fixture();
            await fixture.SeedAsync(large: true); await fixture.PrepareAsync();
            switch (change)
            {
                case "head": fixture.Append("stale-head", "New retained source.", 0); break;
                case "profile": fixture.Profile.Revision++; fixture.Profile.Instructions += "Changed guidance.";
                    fixture.Store.Put("agent", fixture.Profile.Id, fixture.Profile); break;
                case "routing": var config = fixture.Configuration.Get(); config.ExternalCommandsEnabled = !config.ExternalCommandsEnabled;
                    fixture.Configuration.Save(config); break;
                case "preference": fixture.Profile.PluginSettings[IdleCompactionPreference.Key] =
                    CerebralJson.Element(new { schemaVersion = 1, enabled = false, triggerPercent = 70 });
                    fixture.Store.Put("agent", fixture.Profile.Id, fixture.Profile); break;
                case "restart": fixture.Host = fixture.NewHost(); break;
                case "baseline":
                    // A concurrent exclusion/baseline edit must invalidate prepared work without changing a terminal run.
                    var protectedSource = new ContextContribution("baseline-source", "fixture", "conversation", 1,
                        ContextRequirement.Reducible, [new("user", [new TextContentPart("baseline")])], BeforeCurrentInput: true);
                    var window = new ContextWindow(1, 1, [protectedSource]);
                    var candidate = new ContextCheckpoint("baseline-checkpoint", 1, 1,
                        ContextValidation.GetCoveredManifest(window), "Other retained baseline.", "baseline-checkpoint", true);
                    using (var connection = new SqliteConnection("Data Source=" + Path.Combine(fixture.Store.DirectoryPath, "engine.db")))
                    {
                        connection.Open(); using var command = connection.CreateCommand();
                        command.CommandText = "INSERT INTO conversation_context_checkpoints(conversation_id,source_revision,run_id,effect_id,checkpoint,adopted_at) VALUES('idle-chat',1,'other','baseline-checkpoint',$checkpoint,$at)";
                        command.Parameters.AddWithValue("$checkpoint", JsonSerializer.Serialize(candidate, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
                    }
                    break;
            }
            await fixture.SubmitAsync("after-" + change);
            var retained = fixture.Host.Store.GetConversationCheckpoint("idle-chat", long.MaxValue);
            Check(change == "baseline" ? retained?.Checkpoint.Id == "baseline-checkpoint" : retained is null,
                change + " invalidates candidate and admits authoritative existing context");
        }

        using (var fixture = new Fixture())
        {
            await fixture.SeedAsync(large: false); await fixture.PrepareAsync();
            Check(fixture.Models.Summaries == 0 && fixture.Models.Active == 0, "below threshold dispatches no compactor");
            await fixture.SubmitAsync("below");
            Check(fixture.Host.Store.GetConversationCheckpoint("idle-chat", long.MaxValue) is null,
                "below threshold creates no durable candidate");
        }

        foreach (var invalid in new[] { "malformed", "nonreducing" })
        {
            using var fixture = new Fixture();
            await fixture.SeedAsync(large: true);
            fixture.Models.RawSummary = invalid == "malformed" ? "not a checkpoint object" :
                JsonSerializer.Serialize(new { checkpoint = new string('s', 30_000) });
            await fixture.PrepareAsync(); await fixture.SubmitAsync(invalid);
            Check(fixture.Host.Store.GetConversationCheckpoint("idle-chat", long.MaxValue) is null &&
                  fixture.Models.Active == 0, invalid + " summary is discarded without losing context or capacity");
        }

        foreach (var unknown in new[] { false, true })
        {
            using var fixture = new Fixture();
            await fixture.SeedAsync(large: true);
            fixture.Models.Hold = true; fixture.Models.UnknownCancellation = unknown;
            var cancellation = fixture.Host.TryClaimIdleCompaction(default)!;
            var maintenance = fixture.Host.RunIdleCompactionAsync(cancellation);
            await fixture.Models.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var foreground = await fixture.SubmitAsync(unknown ? "unknown" : "cancel");
            await fixture.Models.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(foreground.Status == WorkflowStatus.Queued && !maintenance.IsCompleted && fixture.Models.Active == 1,
                "foreground submits promptly while occupied provider capacity stays reserved until terminal evidence");
            Check(fixture.Host.TryClaimIdleCompaction(default) is null,
                "cancelled caller await does not make maintenance capacity reusable");
            fixture.Models.Terminal.TrySetResult();
            var confirmed = await maintenance.WaitAsync(TimeSpan.FromSeconds(5));
            Check(confirmed == !unknown && fixture.Models.Active == (unknown ? 1 : 0),
                unknown ? "unknown provider cancellation leaves capacity quarantined" : "confirmed provider cancellation releases capacity");
            Check(fixture.Host.Store.GetConversationCheckpoint("idle-chat", long.MaxValue) is null,
                "unfinished maintenance cannot be adopted");
        }

        using (var fixture = new Fixture())
        {
            fixture.Profile.PluginSettings.Clear(); fixture.Store.Put("agent", fixture.Profile.Id, fixture.Profile);
            await fixture.SeedAsync(large: true);
            Check(fixture.Host.TryClaimIdleCompaction(default) is null && fixture.Models.Summaries == 0,
                "feature is disabled by default independently of ordinary automatic compaction");
        }
        using (var fixture = new Fixture())
        {
            await fixture.SeedAsync(large: true);
            var baseline = new ContextCheckpoint("old-checkpoint", 1, 7,
                [new("old-exact-source", 1)], "Earlier baseline summary.", "old-checkpoint", true);
            fixture.SetBaseline(baseline, 1);
            await fixture.PrepareAsync(); var next = await fixture.SubmitAsync("baseline-reuse");
            var adopted = fixture.Host.Store.GetConversationCheckpoint("idle-chat", next.Submission.ConversationSequence);
            Check(adopted is not null && adopted.Checkpoint.ExclusionRevision == 7 &&
                adopted.Checkpoint.CoveredSources.Contains(new(baseline.Id, baseline.ContextRevision)),
                "successful adoption preserves exclusion revision and exact prior checkpoint chain");
        }
        foreach (var remote in new[] { false, true })
        {
            using var fixture = new Fixture(); await fixture.SeedAsync(large: true);
            if (remote)
            {
                var frozen = WorkerExecutionSnapshot.Read(fixture.Completed.Submission.SettingsSnapshot);
                frozen.Profile.ModelWorkerId = "remote";
                frozen.Compactor!.Profile.ModelWorkerId = "remote";
                frozen = frozen with
                {
                    Model = frozen.Model! with { ModelWorkerId = "remote", ModelWorkerInstallationId = "remote-fixture" },
                    Compactor = frozen.Compactor with { Model = frozen.Compactor.Model with
                        { ModelWorkerId = "remote", ModelWorkerInstallationId = "remote-fixture" } }
                };
                fixture.Host = fixture.NewHost();
                fixture.Host.Publish(fixture.Completed with { Submission = fixture.Completed.Submission with
                    { SettingsSnapshot = CerebralJson.Element(frozen) } });
            }
            var cancellation = fixture.Host.TryClaimIdleCompaction(default, localResourcesPaused: true);
            Check(remote ? cancellation is not null && !fixture.Host.IdleCompactionUsesLocalModel : cancellation is null,
                remote ? "local resource pause permits a frozen remote-only owner and compactor" :
                    "closed local resource admission prevents a new local maintenance claim");
            if (!remote) cancellation = fixture.Host.TryClaimIdleCompaction(default)!;
            await using var runtime = new ModelRuntime(fixture.Store, fixture.Configuration.Get, new EventHub());
            var controls = new PauseControls();
            using var scheduler = new JobScheduler(fixture.Store, fixture.Configuration, new ForegroundExecutor(),
                runtime, new EventHub(), NullLogger<JobScheduler>.Instance, fixture.Host, controls);
            await scheduler.PauseLocalResourcesAsync(false);
            Check(cancellation is not null && cancellation.IsCancellationRequested == !remote,
                remote ? "local pause leaves already claimed remote-only maintenance functional" :
                    "closing admission then rechecking cancels a claim accepted just before local pause");
            await scheduler.WaitForResourcePauseTransitionAsync(TimeSpan.FromSeconds(3));
        }
        using (var fixture = new Fixture())
        {
            await fixture.SeedAsync(large: true);
            var settings = fixture.Configuration.Get(); settings.MaxConcurrentJobs = 1; fixture.Configuration.Save(settings);
            fixture.Models.Hold = true; fixture.Models.UnknownCancellation = true;
            var other = CerebralJson.Read<AgentProfile>(CerebralJson.Element(fixture.Profile));
            other.Id = "other-agent"; other.ModelId = "other-model";
            fixture.Store.Put("agent", other.Id, other);
            var unaffectedPath = Path.Combine(fixture.Store.DirectoryPath, "unaffected-fixture.gguf");
            File.WriteAllBytes(unaffectedPath, "GGUF"u8.ToArray());
            fixture.Store.Put("model", other.ModelId, new ModelRecord { Id = other.ModelId, Path = unaffectedPath });
            await using var runtime = new ModelRuntime(fixture.Store, fixture.Configuration.Get, new EventHub());
            var executor = new ForegroundExecutor();
            using var scheduler = new JobScheduler(fixture.Store, fixture.Configuration, executor, runtime,
                new EventHub(), NullLogger<JobScheduler>.Instance, fixture.Host);
            await scheduler.StartAsync(default);
            try
            {
                await fixture.Models.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                _ = scheduler.Submit(new() { ProfileId = other.Id, Prompt = "Unrelated provider foreground." });
                await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(fixture.Models.Active == 1 && !fixture.Models.Terminal.Task.IsCompleted,
                    "concurrency-one scheduler dispatches unrelated foreground while maintenance provider ownership remains held");
                fixture.Models.Terminal.TrySetResult();
                await fixture.Models.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                fixture.Models.Terminal.TrySetResult();
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await scheduler.StopAsync(stop.Token);
            }
            Check(fixture.Models.Active == 1, "global scheduler cleanup does not release unknown provider capacity");
        }
        using (var fixture = new Fixture())
        {
            await fixture.SeedAsync(large: true); fixture.Host = fixture.NewHost();
            fixture.Host.Publish(fixture.Completed with { Status = WorkflowStatus.Waiting });
            fixture.Host.Publish(fixture.Completed with { Questions = new("pending-question", 1,
                [new("answer", "Owner answer is pending.")], new Dictionary<string, string>()) });
            Check(fixture.Host.TryClaimIdleCompaction(default) is null,
                "pending questions and waiting runs never trigger idle preparation");
        }
        using (var fixture = new Fixture())
        {
            fixture.Profile.PluginSettings[IdleCompactionPreference.Key] =
                CerebralJson.Element(new { schemaVersion = 1, enabled = "invalid", triggerPercent = 70 });
            fixture.Store.Put("agent", fixture.Profile.Id, fixture.Profile);
            await fixture.SeedAsync(large: true); var next = await fixture.SubmitAsync("invalid-preference");
            Check(next.Status == WorkflowStatus.Queued && fixture.Host.TryClaimIdleCompaction(default) is null,
                "malformed optional preference skips maintenance while normal submission succeeds");
        }
        using (var fixture = new Fixture())
        {
            await fixture.SeedAsync(large: true);
            var head = fixture.Host.Journal.GetRevision("idle-chat");
            var malformed = new ConversationIdleCheckpoint(fixture.Completed.Id, fixture.Completed.Revision,
                head, fixture.Completed.Submission.ConversationSequence, "fixture",
                new(head, 0, []), new("bad-candidate", head, 0, [], "", "bad-candidate", true), null,
                WorkflowContract.EmptyObject);
            var next = fixture.Host.Store.Submit(fixture.Completed.Submission with
                { RequestId = "malformed-cache", UserText = "Normal accepted input survives invalid cache." }, malformed, "fixture");
            Check(next.Status == WorkflowStatus.Queued && fixture.Host.Store.GetConversationCheckpoint("idle-chat", long.MaxValue) is null,
                "malformed optional host candidate becomes a cache miss without blocking next submission");
        }
        foreach (var invalidate in new[] { "paused", "epoch" })
        {
            using var fixture = new Fixture();
            var root = Path.Combine(fixture.Store.DirectoryPath, "engines");
            EngineHostTests.Pack(root, "A", true); EngineHostTests.Pack(root, "B", false);
            await using var engine = new EngineController(root, Path.Combine(fixture.Store.DirectoryPath, "engine-selection"));
            await engine.InitializeAsync(); fixture.Host = fixture.NewHost(engine);
            await fixture.SeedAsync(large: true); await fixture.PrepareAsync();
            var status = engine.Status(); engine.Pause(status.Epoch);
            if (invalidate == "epoch")
            {
                status = await engine.ActivateAsync("B", status.Epoch);
                engine.Resume(status.Epoch);
            }
            var next = await fixture.SubmitAsync("engine-" + invalidate);
            Check(next.Status == WorkflowStatus.Queued &&
                fixture.Host.Store.GetConversationCheckpoint("idle-chat", long.MaxValue) is null,
                invalidate == "epoch" ? "engine replacement invalidates ready candidate under activation gate" :
                    "paused engine admits normal work and discards optional ready checkpoint");
        }
        await RunPreservationChecksAsync(Check);
        Console.WriteLine($"Idle compaction tests passed ({checks}; deterministic provider plus real SQLite, no live inference).");
    }

    private static string Text(ContextWindow window) => string.Join("\n",
        window.Contributions.SelectMany(source => source.Messages).SelectMany(message => message.Content)
            .OfType<TextContentPart>().Select(part => part.Text));

    private sealed class ReceiptConsumerModule : IWorkflowModule
    {
        public string Id => ConversationWorkflowModule.ModuleId;
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } =
            [new ReceiptConsumerDefinition(new ConversationWorkflowModule().Workflows.Single())];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
    }

    private sealed class ReceiptConsumerDefinition(IWorkflowDefinition inner) : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = inner.Descriptor with
            { ToolReceiptConsumer = new(1, true) };
        public WorkflowStep Advance(WorkflowRun snapshot) => inner.Advance(snapshot);
    }

    private sealed class BuiltInTools : IToolDispatcher
    {
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ToolDescriptor>>([
                new("fixture.builtin", "Idle built-in fixture", CerebralJson.Element(new { type = "object" }),
                    "fixture.builtin")]);
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token) =>
            throw new Exception("FAIL: idle built-in fixture dispatched a tool");
        public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context,
            ToolExecutionEvidence? previousEvidence, CancellationToken token) =>
            throw new Exception("FAIL: idle built-in fixture reconciled a tool");
    }

    private sealed class Fixture : IDisposable
    {
        internal const string OriginalGoal = "Retain the exact first accepted task goal.";
        internal const string LastUser = "This is the latest exact user instruction.";
        internal const string LastAnswer = "This is the latest exact final assistant response.";
        private readonly string root = Path.Combine(Path.GetTempPath(), "cerebral-idle-compaction-" + Guid.NewGuid().ToString("N"));
        internal StateStore Store { get; }
        internal Configuration Configuration { get; }
        internal AgentProfile Profile { get; }
        internal WorkerWorkflowEffects Bridge { get; }
        internal MeasuredProvider Models { get; } = new();
        internal EventHub Events { get; } = new();
        internal WorkerWorkflows Host { get; set; }
        internal WorkflowRun Completed { get; private set; } = null!;
        internal WorkflowRun Original { get; private set; } = null!;
        private readonly IToolDispatcher tools;
        private readonly bool receiptConsumer;
        private int sequence;
        internal Fixture(bool receiptConsumer = false)
        {
            this.receiptConsumer = receiptConsumer;
            tools = receiptConsumer ? new BuiltInTools() : new ContextCompactionTests.NoTools();
            Store = new StateStore(root); Configuration = new(Store); Configuration.Get();
            Profile = new() { Id = "idle-agent", Name = "Idle fixture", ModelId = "idle-model", FollowsModelBudgets = false,
                ContextTokens = 8192, ResponseTokens = 512, ReasoningMode = "off", AutomaticCompaction = true,
                CompactionReductionPercent = 50,
                AllowedTools = receiptConsumer ? ["fixture.builtin"] : [],
                Capabilities = receiptConsumer ? ["fixture.builtin"] : [] };
            Profile.PluginSettings[IdleCompactionPreference.Key] = CerebralJson.Element(new { schemaVersion = 1, enabled = true, triggerPercent = 70 });
            Store.Put("agent", Profile.Id, Profile);
            Store.Put("model", Profile.ModelId, new ModelRecord { Id = Profile.ModelId, Path = Path.Combine(root, "fixture.gguf"),
                ContextTokens = 8192, MaximumContextTokens = 8192, AutomaticContextSizing = false });
            // Bridge invokes the resolver only after the host assignment on the next line.
            Func<WorkerWorkflows>? hostBinding = receiptConsumer ? () => Host! : null;
            Bridge = new(Store, Configuration, Models, tools, workflowHost: hostBinding); Host = NewHost();
        }
        internal WorkerWorkflows NewHost(EngineController? engine = null) => new(Store, Configuration, Events,
            [Bridge, receiptConsumer ? new ReceiptConsumerModule() : new ConversationWorkflowModule()],
            [new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, []), ConversationWorkflowModule.PackageBinding], tools, engine: engine);
        internal Task<WorkflowRun> SubmitAsync(string id, string text = "Next ordinary user turn.") => Host.SubmitAsync(
            new(id, "idle-chat", Profile.Id, ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                text, null, WorkflowContract.EmptyObject, 20), null, default);
        internal async Task<WorkflowRun> CompleteAsync(string id, string text, string answer)
        {
            var run = await SubmitAsync(id, text);
            run = Host.Store.TryClaim(run.Id)!;
            var args = new WorkerWorkflowEffects.GenerateArguments(
                [new("system", [new TextContentPart("Stable fixture policy remains separate.")]),
                 new("user", [new TextContentPart("Required fixture build-evidence anchor stays verbatim.")])],
                Context: new(run.Submission.ConversationSequence, 0, []));
            var descriptor = Bridge.Commands.Single(handler => handler.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && handler.Descriptor.Version == 1).Descriptor;
            run = Host.Store.ApplyStep(run.Id, run.Revision, new(WorkflowContract.EmptyObject,
                new InvokeWorkflowCommand(descriptor.Id, descriptor.Version, CerebralJson.Element(args))), descriptor);
            run = Host.Store.DispatchEffect(run.Id, run.Revision, executionMilliseconds: 1000);
            var generated = await Bridge.Commands.Single(handler => handler.Descriptor.Id == descriptor.Id).ExecuteAsync(
                new(run.Effect!.Id, run.Submission, run.Effect.Arguments) { RunId = run.Id }, default);
            if (generated.State != WorkflowEffectState.Succeeded) throw new Exception(generated.Error);
            run = Host.Store.CompleteEffect(run.Id, run.Effect.Id, generated, 1);
            run = Host.Store.TryClaim(run.Id)!;
            run = Host.Store.ApplyStep(run.Id, run.Revision, new(WorkflowContract.EmptyObject,
                new CompleteWorkflow(CerebralJson.Element(new { text = answer }))));
            return Host.Publish(run);
        }
        internal async Task SeedAsync(bool large)
        {
            Original = await CompleteAsync("original", OriginalGoal, "Earlier final response.");
            for (var index = 0; index < (large ? 14 : 1); index++)
                Append("older-" + index, new string('x', large ? 2200 : 100), index);
            Completed = await CompleteAsync("completed", LastUser, LastAnswer);
        }
        internal void Append(string id, string text, int index)
        {
            var head = Host.Journal.GetRevision("idle-chat");
            Host.Journal.Append(new(id + "-" + ++sequence, "idle-chat", "original", 0, CerebralEventTypes.Message,
                new(EventActorKind.Agent, Profile.Id), [new TextContentPart(text)], new(ModelInclusion.Evidence),
                new(EventSourceKind.Agent, AgentId: Profile.Id), DateTimeOffset.UtcNow), head);
        }
        internal void SetBaseline(ContextCheckpoint checkpoint, long revision)
        {
            using var connection = new SqliteConnection("Data Source=" + Path.Combine(Store.DirectoryPath, "engine.db"));
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO conversation_context_checkpoints(conversation_id,source_revision,run_id,effect_id,checkpoint,adopted_at) VALUES('idle-chat',$revision,'prior',$id,$checkpoint,$at)";
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$id", checkpoint.Id);
            command.Parameters.AddWithValue("$checkpoint", JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
        }
        internal async Task PrepareAsync()
        {
            var cancellation = Host.TryClaimIdleCompaction(default) ?? throw new Exception("No idle candidate was queued.");
            if (!await Host.RunIdleCompactionAsync(cancellation)) throw new Exception("Unexpected unconfirmed fixture.");
        }
        internal async Task<WorkerWorkflowEffects.HistoryData> HistoryAsync(WorkflowRun run)
        {
            var handler = Bridge.Commands.Single(item => item.Descriptor.Id == WorkerWorkflowEffects.HistoryCommand);
            var result = await handler.ExecuteAsync(new("read-history", run.Submission,
                CerebralJson.Element(new { })) { RunId = run.Id }, default);
            if (result.State != WorkflowEffectState.Succeeded) throw new Exception(result.Error);
            return result.Data!.Value.Deserialize<WorkerWorkflowEffects.HistoryData>(CerebralJson.Options)!;
        }
        public void Dispose()
        {
            Host.DiscardIdleCompactionState();
            SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ForegroundExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RunAsync(AgentJob job, CancellationToken token)
        {
            Entered.TrySetResult(); job.Status = "completed"; job.Result = "Fixture completed.";
            return Task.CompletedTask;
        }
    }
    private sealed class PauseControls : IResourcePauseModelControl
    {
        public Task<IReadOnlyList<string>> BeginPauseAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task UnloadPausedAsync(CancellationToken token) => Task.CompletedTask;
        public Task ResumePausedAsync(IReadOnlyList<string> ids, CancellationToken token) => Task.CompletedTask;
        public Task ReopenPausedAdmissionAsync(Action commit, CancellationToken token) { commit(); return Task.CompletedTask; }
    }

    private sealed class MeasuredProvider : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("idle-fixture", "1", ["text"]);
        internal int Active, Summaries;
        internal string Summary = "Older source facts retained with exact handles; no new task authority.";
        internal string? RawSummary;
        internal bool Hold, UnknownCancellation;
        internal ModelGenerationRequest? LastRequest;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Active != 0) throw new Exception("An occupied fixture slot was reused.");
            LastRequest = request;
            var text = string.Join("\n", request.Messages.SelectMany(message => message.Content).OfType<TextContentPart>().Select(part => part.Text));
            var count = text.Length / 4 + 64; // Fixture tokenizer only; production uses actual runtime metadata.
            var metadata = new PreparedModelMetadata("fixture-slot", Descriptor.AdapterId, "1", request.ModelId,
                count, request.ContextLimit, request.ResponseLimit, "deterministic fixture tokenizer") { SafetyReserveTokens = 128 };
            if (count + request.ResponseLimit + 128 > request.ContextLimit)
                throw new ModelContextLimitException(metadata with { ResponseBudget = 0 });
            Active++;
            var summary = request.ResponseSchema is { } schema && schema.TryGetProperty("properties", out var properties) &&
                properties.TryGetProperty("checkpoint", out _);
            return Task.FromResult<IPreparedModelGeneration>(new Prepared(this, metadata, summary));
        }
        private sealed class Prepared(MeasuredProvider owner, PreparedModelMetadata metadata, bool summary) : IPreparedModelGeneration
        {
            private bool quarantined;
            public PreparedModelMetadata Metadata => metadata;
            public async Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            {
                if (summary)
                {
                    owner.Summaries++;
                    if (owner.Hold)
                    {
                        owner.Entered.TrySetResult();
                        using var registration = token.Register(() => owner.Cancelled.TrySetResult());
                        await owner.Terminal.Task;
                        if (owner.UnknownCancellation)
                        {
                            quarantined = true;
                            return new(ModelGenerationFinish.Unknown, "", Error: "Fixture provider did not confirm cancellation.");
                        }
                        return new(ModelGenerationFinish.Cancelled, "", CancellationConfirmed: true);
                    }
                    return new(ModelGenerationFinish.Stop, owner.RawSummary ?? JsonSerializer.Serialize(new { checkpoint = owner.Summary }), 24, metadata.InputTokens);
                }
                return new(ModelGenerationFinish.Stop, "Fixture final output.", 16, metadata.InputTokens);
            }
            public ValueTask DisposeAsync() { if (!quarantined) owner.Active--; return ValueTask.CompletedTask; }
        }
    }
}
