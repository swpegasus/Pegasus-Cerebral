using System.Diagnostics;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;

namespace Cerebral.Worker.Tests;

/// <summary>Opt-in real local CPU plugin predictions with a synthetic workflow and real SQLite.
/// The injected journal/steering/cancellation races are deterministic host mutations, not user tasks.
/// No fake model or advisor substitutes for the installed native provider.</summary>
internal static class LayaNativeWorkflowAcceptance
{
    internal static async Task RunAsync(string packages, string dataDirectory)
    {
        var data = Path.GetFullPath(dataDirectory);
        if (!Path.GetFileName(data).StartsWith("laya-native-", StringComparison.Ordinal) ||
            !Path.GetFullPath(packages).StartsWith(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp")) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This opt-in check requires task-owned package and data paths.");
        Directory.CreateDirectory(data);
        var checks = 0;
        void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); checks++; }
        using var plugins = new ServicePluginRuntime(packages, Path.Combine(data, "PluginData"), new EventHub());
        var statusCommand = plugins.CommandFor("laya.status") ?? throw new InvalidOperationException("The native Laya package did not contribute its status command.");
        async Task<JsonElement> Status()
            => JsonSerializer.SerializeToElement(await statusCommand.ExecuteAsync("laya.status", WorkflowContract.EmptyObject, CancellationToken.None), CerebralJson.Options);
        var initial = await Status();
        Console.WriteLine(JsonSerializer.Serialize(new { admission = initial }, CerebralJson.Options));
        Check(initial.GetProperty("ready").GetBoolean(), "The local CPU runner must be actually ready.");
        Check(plugins.Status.Single(item => item.Id == "pegasus.laya-auditor").Loaded, "The native service package must be admitted.");
        var native = plugins.WorkflowAuditors.Single(item => item.Descriptor.Id == "laya.stock.criterion");
        Check(native.Descriptor.Purposes.Contains("component_evidence_check", StringComparer.Ordinal), "The native adapter must support the actual host completion purpose.");
        var journal = new EventJournal(Path.Combine(data, "engine.db")); var store = new WorkflowStore(journal);
        var registry = new WorkflowRegistry([new Module()]);
        WorkflowRun Submit(string id) => store.Submit(new(id, id + "-context", "native-test",
            "native.audit:complete", "1.0.0", "Compute two plus three; the answer must be five.",
            null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject));
        IReadOnlyList<CerebralEvent> Receipts(string id) => journal.ReadPage(id + "-context", limit: 100).Events
            .Where(item => item.Type == "workflow.audit").ToArray();
        bool NativeAssessment(CerebralEvent receipt) =>
            receipt.Data!.Value.GetProperty("result").GetProperty("adapterId").GetString() == native.Descriptor.Id &&
            receipt.Data.Value.GetProperty("result").GetProperty("disposition").GetString() is "accepted" or "gap" or "insufficient_evidence";
        async Task<WorkflowRun> Execute(string id, Wrapped adapter, CancellationToken token = default)
        {
            Submit(id);
            return await new WorkflowRuntime(store, registry, auditPipeline: new([adapter]),
                auditPolicy: _ => new(adapter.Descriptor.Id, false, 30000)).RunAsync(id, token);
        }
        var timer = Stopwatch.StartNew();
        var clean = new Wrapped(native);
        Check((await Execute("native-clean", clean)).Status == WorkflowStatus.Completed, "Native advisory must preserve ordinary deterministic completion.");
        Check(clean.Requests.Count == 1 && NativeAssessment(Receipts("native-clean").Single()), "A real native assessment must be attributed and retained once.");
        var steered = new Wrapped(native);
        steered.BeforeReturn = request => { if (steered.Requests.Count == 1) store.Steer(new("native-new-direction", request.RunId, "Keep the calculation scoped to two plus three.")); };
        Check((await Execute("native-steered", steered)).Status == WorkflowStatus.Completed && steered.Requests.Count == 2,
            "Steering after an actual prediction must force a fresh audit, not accept the old witness.");
        Check(Receipts("native-steered").Count == 1 && NativeAssessment(Receipts("native-steered").Single()) &&
            !Receipts("native-steered").Single().Data!.Value.GetRawText().Contains(steered.Requests[0].RequestId, StringComparison.Ordinal),
            "Only the fresh native steering receipt may be retained.");

        var advanced = new Wrapped(native);
        advanced.BeforeReturn = request =>
        {
            if (advanced.Requests.Count != 1) return;
            journal.Append(new("native-extra-evidence", request.ConversationId, request.RunId, 0, "test.new.evidence",
                new(EventActorKind.Service, "native.test"), [new TextContentPart("The recorded calculation remains two plus three equals five.")],
                new(ModelInclusion.Evidence, HumanVisibility.Activity), new(EventSourceKind.Service, "native.test"), DateTimeOffset.UtcNow),
                request.ConversationRevision);
        };
        Check((await Execute("native-journal", advanced)).Status == WorkflowStatus.Completed && advanced.Requests.Count == 2,
            "A journal-only revision after actual prediction must reject old conversation evidence.");
        Check(Receipts("native-journal").Count == 1 && !Receipts("native-journal").Single().Data!.Value.GetRawText()
            .Contains(advanced.Requests[0].RequestId, StringComparison.Ordinal), "No stale native journal assessment may be retained.");

        using var ownerCancellation = new CancellationTokenSource();
        var cancelled = new Wrapped(native);
        cancelled.BeforeReturn = request => { store.Cancel(request.RunId); ownerCancellation.Cancel(); };
        var cancelledRun = await Execute("native-cancelled", cancelled, ownerCancellation.Token);
        Check(cancelled.Requests.Count == 1 && cancelledRun.Status == WorkflowStatus.Cancelled && cancelledRun.Result is null &&
            Receipts("native-cancelled").Count == 0, "Owner cancellation before delivery must reject the real late native result.");

        var replaced = new Wrapped(native);
        replaced.BeforeReturn = _ => replaced.GenerationDelta = 1;
        await Execute("native-replaced", replaced);
        Check(Receipts("native-replaced").All(receipt => !NativeAssessment(receipt)),
            "Capability generation replacement before delivery must prevent native assessment adoption.");
        timer.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new { checks, elapsedMilliseconds = timer.ElapsedMilliseconds,
            evidence = "Actual stock Laya CPU predictions; synthetic host races; real SQLite; no chat-model inference",
            initial, final = await Status() }, CerebralJson.Options));
    }

    private sealed class Wrapped(IWorkflowAuditAdapter native) : IWorkflowAuditAdapter
    {
        internal List<WorkflowAuditRequest> Requests { get; } = [];
        internal Action<WorkflowAuditRequest>? BeforeReturn;
        internal int GenerationDelta;
        public WorkflowAuditDescriptor Descriptor => native.Descriptor with { Generation = native.Descriptor.Generation + GenerationDelta };
        public async Task<WorkflowAuditResult> ReviewAsync(WorkflowAuditRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var result = await native.ReviewAsync(request, cancellationToken);
            BeforeReturn?.Invoke(request);
            return result;
        }
    }
    private sealed class Module : IWorkflowModule
    {
        public string Id => "native.audit";
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new Definition()];
    }
    private sealed class Definition : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new("native.audit:complete", "1.0.0", "Native audit acceptance",
            CerebralJson.Element(new { type = "object" }), []) { SupportsSteering = true };
        public WorkflowStep Advance(WorkflowRun run) => new(WorkflowContract.EmptyObject,
            new CompleteWorkflow(CerebralJson.Element(new { text = "Two plus three is five." })));
    }
}
