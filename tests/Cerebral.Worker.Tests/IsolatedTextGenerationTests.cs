using System.Text.Json;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

// Deterministic model fakes and disposable local SQLite only. No scheduler/background service,
// engine executable, loaded native model, provider, installed Data, settings or Hold is touched.
internal static class IsolatedTextGenerationTests
{
    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        void Reject(Action action, string message)
        { try { action(); throw new Exception("Expected rejection: " + message); } catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException or WorkflowConflictException or InvalidOperationException) { checks++; } }
        var request = new IsolatedTextGenerationRequest(1, Guid.NewGuid().ToString("N"), "autoprompt-profile", 4, "autoprompt",
            "Fixed plugin policy", "Only the latest mounted public response.", 128, DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds());
        request.Validate(DateTimeOffset.UtcNow); checks++;
        Reject(() => (request with { SchemaVersion = 2 }).Validate(DateTimeOffset.UtcNow), "future schema");
        Reject(() => (request with { ExpiresAtUnixMilliseconds = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds() }).Validate(DateTimeOffset.UtcNow), "unbounded deadline");
        Reject(() => (request with { MaximumOutputTokens = 257 }).Validate(DateTimeOffset.UtcNow), "output bound");
        Reject(() => (request with { Text = "public\0extra" }).Validate(DateTimeOffset.UtcNow), "NUL input");
        var profile = new AgentProfile
        {
            Id = request.ProfileId, Revision = 4, Purpose = "autoprompt", Role = "helper", ModelId = "already-loaded",
            ContextTokens = 4096, ResponseTokens = 512, Instructions = "PRIVATE PROFILE INSTRUCTIONS",
            AllowedRoots = ["C:\\PRIVATE"], AllowedTools = ["file.write"], Capabilities = ["files.write"],
            ReasoningMode = "on", AutomaticCompaction = true,
            PluginSettings = new() { ["private"] = CerebralJson.Element(new { history = "PRIVATE HISTORY" }) }
        };
        var model = new ModelRecord { Id = "already-loaded", Path = "C:\\fixture\\not-opened.gguf", MaximumContextTokens = 4096, ContextTokens = 4096 };
        var packages = new[] { new ServicePluginPackageBinding(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, []) };
        var snapshot = IsolatedTextGenerationAdmission.Snapshot(request, profile, model, packages, "fixture-routing");
        var roundTrip = WorkerExecutionSnapshot.Read(CerebralJson.Element(snapshot));
        var input = IsolatedTextGenerationAdmission.ModelRequest(request, roundTrip);
        Check(input.Messages.Count == 2 && input.Messages[0].Role == "system" && input.Messages[1].Role == "user" &&
            ((Cerebral.Contracts.TextContentPart)input.Messages[0].Content.Single()).Text == request.Policy &&
            ((Cerebral.Contracts.TextContentPart)input.Messages[1].Content.Single()).Text == request.Text,
            "The exact policy and public text are the entire model input.");
        Check(!CerebralJson.Element(input).GetRawText().Contains("PRIVATE", StringComparison.Ordinal) && input.ReasoningTokens == 0,
            "Profile instructions, roots, tools, plugin settings/history and reasoning mode are not inherited.");
        Check(roundTrip.Tools.Count == 0 && roundTrip.Skills.Count == 0 && roundTrip.AllowedRoots.Count == 0 &&
            roundTrip.Capabilities.Count == 0 && !roundTrip.FullAccess && roundTrip.Compactor is null && roundTrip.ConversationPaths is null,
            "The host snapshot has no execution grants, skills, directory or compaction context.");
        Reject(() => IsolatedTextGenerationAdmission.RequireProfile(request with { ProfileRevision = 5 }, profile), "profile CAS");
        var remote = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile)); remote.ModelWorkerId = "remote";
        Reject(() => IsolatedTextGenerationAdmission.RequireProfile(request, remote), "remote model inheritance");
        Reject(() => IsolatedTextGenerationAdmission.ModelRequest(request, snapshot with { SharedInstructions = "PRIVATE" }), "shared instructions");
        Reject(() => IsolatedTextGenerationAdmission.ModelRequest(request, snapshot with { AllowedRoots = ["C:\\PRIVATE"] }), "root inheritance");
        JobScheduler.RequireIsolatedAdmission(false, false, false); checks++;
        foreach (var gate in new[] { (true, false, false), (false, true, false), (false, false, true) })
            Reject(() => JobScheduler.RequireIsolatedAdmission(gate.Item1, gate.Item2, gate.Item3), "resource/Hold/engine admission");
        var fake = new FakeModel(); var selections = 0;
        Task<IModelRuntimeAdapter> Select(CancellationToken token) { selections++; return Task.FromResult<IModelRuntimeAdapter>(fake); }
        var result = await IsolatedTextGenerationExecutor.RunAsync(request, () => snapshot, Select, CancellationToken.None);
        Check(result.State == WorkflowEffectState.Succeeded && fake.Preparations == 1 && fake.Generations == 1 && fake.Disposals == 1 && selections == 1,
            "One selected preparation generates once and releases its lease.");
        Check(fake.Last!.Messages.Count == 2 && fake.Last.ModelId == model.Id && fake.Last.ResponseLimit == 128,
            "The prepared fake receives only the captured model and bounded isolated input.");
        fake.Reset(); fake.ThrowPreparing = true;
        result = await IsolatedTextGenerationExecutor.RunAsync(request, () => snapshot, Select, CancellationToken.None);
        Check(result.State == WorkflowEffectState.Failed && fake.Generations == 0, "Unavailable loaded model fails before generation without fallback.");
        fake.Reset(); var validations = 0;
        result = await IsolatedTextGenerationExecutor.RunAsync(request, () => ++validations == 2 ? throw new UnauthorizedAccessException() : snapshot, Select, CancellationToken.None);
        Check(result.State == WorkflowEffectState.Failed && fake.Generations == 0 && fake.Disposals == 1,
            "Profile revocation after preparation prevents generation and releases unused capacity.");
        fake.Reset(); fake.ThrowGenerating = true;
        result = await IsolatedTextGenerationExecutor.RunAsync(request, () => snapshot, Select, CancellationToken.None);
        Check(result.State == WorkflowEffectState.Unknown && fake.Generations == 1 && fake.Disposals == 1,
            "An exception after dispatch remains Unknown; no second generation is attempted.");
        fake.Reset(); fake.ThrowDisposing = true;
        result = await IsolatedTextGenerationExecutor.RunAsync(request, () => snapshot, Select, CancellationToken.None);
        Check(result.State == WorkflowEffectState.Unknown, "Unconfirmed preparation cleanup overrides an otherwise known result.");
        fake.Reset(); fake.Finish = ModelGenerationFinish.Length;
        result = await IsolatedTextGenerationExecutor.RunAsync(request, () => snapshot, Select, CancellationToken.None);
        Check(result.State == WorkflowEffectState.Succeeded && result.Data!.Value.GetProperty("text").ValueKind == JsonValueKind.Null,
            "A known length-limited receipt retains accounting but supplies no partial suggestion.");
        fake.Reset(); fake.Finish = ModelGenerationFinish.Cancelled; fake.CancellationConfirmed = true;
        result = await IsolatedTextGenerationExecutor.RunAsync(request, () => snapshot, Select, CancellationToken.None);
        Check(result.State == WorkflowEffectState.Cancelled, "Confirmed provider cancellation remains Cancelled.");
        fake.Reset(); fake.Finish = ModelGenerationFinish.Cancelled;
        result = await IsolatedTextGenerationExecutor.RunAsync(request, () => snapshot, Select, CancellationToken.None);
        Check(result.State == WorkflowEffectState.Unknown, "Unconfirmed cancellation cannot claim a confirmed stop.");
        fake.Reset();
        result = await IsolatedTextGenerationExecutor.RunAsync(request with { ExpiresAtUnixMilliseconds = 1 }, () => snapshot, Select, CancellationToken.None);
        Check(result.State == WorkflowEffectState.Cancelled && fake.Preparations == 0, "Expired work cannot prepare or load a model after a hold/restart.");

        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-isolated-text-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var store = new StateStore(Path.Combine(root, "store")); var configuration = new Configuration(store); var events = new EventHub();
            var effects = new WorkerWorkflowEffects(store, configuration, fake, new NoTools());
            var host = new WorkerWorkflows(store, configuration, events, [effects], packages);
            var cancelled = host.SubmitIsolatedText(request, cancellationOnly: true);
            host.Store.Cancel(cancelled.Id);
            Check(host.SubmitIsolatedText(request).Status == WorkflowStatus.Cancelled && fake.Preparations == 0,
                "Cancellation that overtakes Generate durably fences the same ID before any profile/model admission.");
            Reject(() => IsolatedTextGenerationAdmission.RequireSubmission(cancelled.Submission with { IsolatedText = null }), "missing host admission marker");
            Reject(() => IsolatedTextGenerationAdmission.RequireSubmission(cancelled.Submission with { ParentRunId = "parent" }), "inherited parent lineage");
            Reject(() => IsolatedTextGenerationAdmission.RequireSubmission(cancelled.Submission with { ProjectId = "project" }), "inherited project context");
            Reject(() => host.SubmitIsolatedText(request with { Text = "different" }), "same ID with changed body");
            Reject(() => host.Submit(new(request.RequestId, "other", request.ProfileId, IsolatedTextGenerationContract.DefinitionId,
                IsolatedTextGenerationContract.DefinitionVersion, "text", null, CerebralJson.Element(request)), null), "generic workflow transport bypass");
            var handler = host.Registry.GetCommand(IsolatedTextGenerationContract.EffectId, 1);
            var before = fake.Preparations;
            result = await handler.ReconcileAsync(new("existing-effect", cancelled.Submission, WorkflowContract.EmptyObject), CancellationToken.None);
            Check(result.State == WorkflowEffectState.Unknown && fake.Preparations == before, "Reconciliation only reports uncertainty and never replays generation.");
            Check(cancelled.Submission.ConversationId == "isolated-" + request.RequestId && cancelled.Submission.ProjectId is null &&
                cancelled.Submission.AcceptedContent.Count == 0, "Isolated work cannot bind itself to the user's chat, project or accepted attachments.");
            Check(host.Journal.ReadPage(cancelled.Submission.ConversationId).Events
                .Where(e => e.Type == Cerebral.Contracts.CerebralEventTypes.Message)
                .All(e => e.Projection.Human == Cerebral.Contracts.HumanVisibility.Hidden),
                "An isolated request does not synthesize a public user chat turn.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(root).StartsWith("cerebral-isolated-text-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup boundary changed.");
            Directory.Delete(root, recursive: true);
        }
        return checks;
    }
    private sealed class FakeModel : IModelRuntimeAdapter, IPreparedModelGeneration
    {
        internal int Preparations, Generations, Disposals;
        internal bool ThrowPreparing, ThrowGenerating, ThrowDisposing, CancellationConfirmed;
        internal ModelGenerationFinish Finish = ModelGenerationFinish.Stop;
        internal ModelGenerationRequest? Last;
        public ModelAdapterDescriptor Descriptor => new("fake", "1.0.0", ["text"]);
        public PreparedModelMetadata Metadata => new("fixture-instance", "fake", "1.0.0", "already-loaded", 10, 4096, 128, "fixture");
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        { Preparations++; Last = request; if (ThrowPreparing) throw new InvalidOperationException("No loaded instance"); return Task.FromResult<IPreparedModelGeneration>(this); }
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
        { Generations++; if (ThrowGenerating) throw new IOException("Lost terminal receipt"); return Task.FromResult(new ModelGenerationResult(Finish, "Could you explain the next step?", 8, 10, CancellationConfirmed)); }
        public ValueTask DisposeAsync()
        { Disposals++; if (ThrowDisposing) throw new IOException("Cleanup unconfirmed"); return ValueTask.CompletedTask; }
        internal void Reset() { Preparations = Generations = Disposals = 0; ThrowPreparing = ThrowGenerating = ThrowDisposing = CancellationConfirmed = false; Finish = ModelGenerationFinish.Stop; Last = null; }
    }
    private sealed class NoTools : IToolDispatcher
    {
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) => throw new InvalidOperationException("No tool discovery allowed.");
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token) => throw new InvalidOperationException("No tool dispatch allowed.");
    }
}
