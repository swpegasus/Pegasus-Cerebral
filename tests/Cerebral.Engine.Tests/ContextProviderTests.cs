using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Context;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;

namespace Cerebral.Engine.Tests;

/// <summary>Deterministic in-process provider fixtures; no live provider, model, or network is used.</summary>
public static class ContextProviderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new Exception("FAIL: " + message);
            count++; Console.WriteLine("PASS: " + message);
        }
        static bool Rejected<T>(Action action) where T : Exception
        {
            try { action(); return false; }
            catch (T) { return true; }
        }

        var handler = new ProviderHandler();
        var request = Request();
        var registry = new WorkflowRegistry([new ProviderModule(handler, request)]);
        Check(registry.ContextProviders.Single().Id == ProviderHandler.CommandId &&
            registry.GetContextProvider(ProviderHandler.CommandId, 1).Descriptor.ReplayPolicy == WorkflowReplayPolicy.Idempotent,
            "registry discovers a specialized versioned read-only provider through the workflow command registry");
        var admitted = registry.ContextProviders.Single();
        handler.Provider = handler.Provider with { Id = "fixture.context:mutated", SupportedReferenceKinds = ["mutated"] };
        Check(registry.ContextProviders.Single() == admitted &&
            registry.GetContextProvider(ProviderHandler.CommandId, 1).Provider.SupportedReferenceKinds.SequenceEqual(["fixture.record"]),
            "registry freezes provider identity, version, description, kinds, and bounds");
        handler.Provider = admitted;
        Check(Rejected<ArgumentException>(() => new WorkflowRegistry([new ProviderModule(
                new ProviderHandler { Provider = admitted with { Version = 2 } }, request)])) &&
            Rejected<ArgumentException>(() => new WorkflowRegistry([new ProviderModule(
                new ProviderHandler { Descriptor = handler.Descriptor with { ReplayPolicy = WorkflowReplayPolicy.Never } }, request)])),
            "registration rejects mismatched provider versions and non-idempotent read-only commands atomically");

        var resolution = Resolution();
        var exactBytes = JsonSerializer.SerializeToUtf8Bytes(resolution, Json).Length;
        request = request with { MaximumUtf8Bytes = exactBytes };
        var normalized = ContextProviderNormalizer.Normalize(admitted, request, resolution,
            "effect-context-1", "provider-receipt-1");
        Check(normalized.Window.Revision == 17 && normalized.Window.ExclusionRevision == 3 &&
            normalized.Window.Contributions.Select(x => x.Id).SequenceEqual(["alpha", "beta", "zeta"]) &&
            normalized.Window.Contributions.All(x => x.Owner == ProviderHandler.CommandId &&
                x.ExactReadHandle?.StartsWith("cerebral-context:v1.", StringComparison.Ordinal) == true),
            "normalization produces one ready window in stable priority then ordinal-ID order with host-authored exact-read handles");
        Check(Rejected<ArgumentException>(() => ContextProviderNormalizer.Normalize(admitted,
                request with { MaximumUtf8Bytes = exactBytes - 1 }, resolution, "effect-context-1", "provider-receipt-1")),
            "normalization enforces the exact serialized provider byte limit");
        Check(Rejected<ArgumentException>(() => ContextProviderNormalizer.ReadRequest(admitted,
                 Data(request with { ProviderVersion = 2 }))) &&
            Rejected<ArgumentException>(() => ContextProviderNormalizer.ReadRequest(admitted,
                Data(request with { References = [.. request.References, request.References[0] with { Id = "extra", Kind = "unsupported" }] }))) &&
            Rejected<ArgumentException>(() => ContextProviderNormalizer.ReadRequest(admitted,
                Data(request with { References = [] }))),
            "request validation rejects provider version, unsupported reference-kind drift, and empty requests");
        Check(Rejected<ArgumentException>(() => ContextProviderNormalizer.Normalize(admitted, request,
                resolution with { ProviderId = "fixture.context:other" }, "effect-context-1", "provider-receipt-1")) &&
            Rejected<ArgumentException>(() => ContextProviderNormalizer.Normalize(admitted, request,
                resolution with { Sources = [.. resolution.Sources, resolution.Sources[0] with { Id = "unrequested" }] },
                "effect-context-1", "provider-receipt-1")),
            "normalization rejects provider identity drift and unrequested source IDs");
        Check(Rejected<ArgumentException>(() => ContextProviderNormalizer.Normalize(admitted, request,
                 resolution with { Sources = resolution.Sources.Where(x => x.Id != "alpha").ToArray() },
                 "effect-context-1", "provider-receipt-1")) &&
            Rejected<ArgumentException>(() => ContextProviderNormalizer.Normalize(admitted, request,
                 resolution with { Sources = resolution.Sources.Select(x => x.Id == "alpha" ? x with { SourceRevision = 8 } : x).ToArray() },
                 "effect-context-1", "provider-receipt-1")),
            "normalization requires every required reference and its exact requested revision");
        Check(Rejected<ArgumentException>(() => ContextProviderNormalizer.Normalize(admitted, request,
                resolution with { Sources = resolution.Sources.Select(x => x.Id == "beta" ? x with
                    { Requirement = ContextRequirement.Reducible } : x).ToArray() },
                "effect-context-1", "provider-receipt-1")),
            "normalization prevents a provider from changing workflow-authored optional and reducible classifications");
        Check(Rejected<ArgumentException>(() => ContextProviderNormalizer.Normalize(admitted, request,
                resolution with { Sources = resolution.Sources.Select(x => x.Id == "alpha" ? x with
                    { Messages = [new("system", [new TextContentPart("host policy")])] } : x).ToArray() },
                "effect-context-1", "provider-receipt-1")),
            "normalization rejects provider attempts to inject system messages");

        var root = Path.Combine(Path.GetTempPath(), "cerebral-context-provider-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            handler.Outcome = new(WorkflowEffectState.Succeeded, Data(resolution), ProviderReceiptId: "provider-receipt-1");
            var store = new WorkflowStore(new EventJournal(Path.Combine(root, "engine.db")));
            var runtime = new WorkflowRuntime(store, registry);
            var run = await runtime.RunAsync(store.Submit(Submission("provider-success", request)).Id);
            var durable = run.Effect!.Result!.Value.Deserialize<ContextProviderResult>(Json)!;
            Check(run.Status == WorkflowStatus.Failed && run.Error!.Contains("step budget", StringComparison.Ordinal) &&
                durable.Window.Contributions.Count == 3 &&
                run.Effect.ProviderReceiptId == "provider-receipt-1" && handler.Executions == 1,
                "runtime replaces raw provider output before persistence while preserving the ordinary effect receipt and single execution");

            handler.Outcome = new(WorkflowEffectState.Succeeded, Data(resolution with
            {
                Sources = resolution.Sources.Select(x => x.Id == "beta" ? x with
                    { Messages = [new("system", [new TextContentPart("malicious")])] } : x).ToArray()
            }), ProviderReceiptId: "provider-receipt-2");
            var invalid = await runtime.RunAsync(store.Submit(Submission("provider-invalid", request)).Id);
            Check(invalid.Status == WorkflowStatus.Failed && invalid.Effect?.State == WorkflowEffectState.Failed &&
                invalid.Effect.Result is null && invalid.Effect.Error?.Contains("rejected", StringComparison.Ordinal) == true &&
                handler.Executions == 2,
                "deterministically invalid read-only provider output is a confirmed failed effect with no persisted usable data");
        }
        finally { Directory.Delete(root, recursive: true); }

        Console.WriteLine($"Context provider SPI: {count} deterministic contract/registry/runtime assertions passed.");
    }

    private static ContextProviderRequest Request() => new(ProviderHandler.CommandId, 1,
    [
        new("zeta", "fixture.record", "records/zeta", Requirement: ContextRequirement.Reducible, Priority: 5),
        new("beta", "fixture.record", "records/beta", Priority: 0),
        new("alpha", "fixture.record", "records/alpha", ExpectedRevision: 7,
            Requirement: ContextRequirement.Required, Priority: 0,
            BeforeCurrentInput: true)
    ], 32 * 1024, ContextRevision: 17, ExclusionRevision: 3);

    private static ContextProviderResolution Resolution() => new(ProviderHandler.CommandId, 1,
    [
        new("zeta", "fixture.record", 2, ContextRequirement.Reducible,
            [new("user", [new TextContentPart("zeta context")])], Priority: 5),
        new("alpha", "fixture.record", 7, ContextRequirement.Required,
            [new("user", [new TextContentPart("alpha context")])], Priority: 0, BeforeCurrentInput: true),
        new("beta", "fixture.record", 4, ContextRequirement.Optional,
            [new("assistant", [new TextContentPart("beta context")])], Complete: false, Priority: 0)
    ]);

    private static WorkflowSubmission Submission(string id, ContextProviderRequest request) => new(id,
        "conversation-" + id, "profile", ProviderDefinition.Id, "1.0.0", "Resolve context.", null,
        Data(new { }), Data(new { }), StepLimit: 1);

    private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, Json);

    private sealed record ProviderModule(ProviderHandler Handler, ContextProviderRequest Request) : IWorkflowModule
    {
        public string Id => "fixture.context";
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new ProviderDefinition(Request)];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [Handler];
    }

    private sealed class ProviderDefinition(ContextProviderRequest request) : IWorkflowDefinition
    {
        public const string Id = "fixture.context:workflow";
        public WorkflowDefinitionDescriptor Descriptor { get; } = new(Id, "1.0.0", "Context provider fixture workflow.",
            WorkflowContract.EmptyObject, []);
        public WorkflowStep Advance(WorkflowRun snapshot) => snapshot.Signal.Kind switch
        {
            WorkflowSignalKind.Start => new(Data(new { phase = "resolve" }),
                new InvokeWorkflowCommand(ProviderHandler.CommandId, 1, Data(request))),
            WorkflowSignalKind.EffectResult => new(Data(new { phase = "done" }),
                new CompleteWorkflow(Data(new { sources = snapshot.Effect!.Result!.Value
                    .Deserialize<ContextProviderResult>(Json)!.Window.Contributions.Count }))),
            _ => throw new InvalidOperationException("Unexpected context provider fixture signal.")
        };
    }

    private sealed class ProviderHandler : IContextProviderCommandHandler
    {
        public const string CommandId = "fixture.context:resolve";
        public WorkflowCommandDescriptor Descriptor { get; set; } = new(CommandId, 1,
            "Resolve bounded fixture context.", WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, [],
            WorkflowReplayPolicy.Idempotent);
        public ContextProviderDescriptor Provider { get; set; } = new(CommandId, 1,
            "Resolve bounded fixture context.", ["fixture.record"], 3, 32 * 1024);
        public WorkflowCommandResult Outcome { get; set; } = new(WorkflowEffectState.Failed,
            Error: "Fixture outcome was not configured.");
        public int Executions { get; private set; }
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Executions++;
            return Task.FromResult(Outcome);
        }
    }
}
