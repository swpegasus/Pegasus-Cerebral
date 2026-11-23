using System.Reflection;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

internal static partial class ConfiguredModelAdaptersChecks
{
    private static async Task AdaptiveReasoningAsync(Fixture f, Action<int, bool, string> check)
    {
        var accepted = f.Profile("adaptive-helper", "helper", "off");
        accepted.AllowReasoningAdjustment = true;
        accepted.ReasoningTokenCeiling = 2048;
        var current = CerebralJson.Read<AgentProfile>(CerebralJson.Element(accepted));
        check(5, WorkerReasoningAdjustment.Resolve(accepted, current, null, 8192) == 0 &&
            WorkerReasoningAdjustment.Resolve(accepted, current, 1024, 8192) == 1024,
            "an opted-in helper can select off or positive reasoning under its owner ceiling");
        check(5, Reject<InvalidOperationException>(() => WorkerReasoningAdjustment.Resolve(accepted, current, 2049, 8192)) &&
            Reject<InvalidOperationException>(() => WorkerReasoningAdjustment.Resolve(accepted, current, 1, 8192)),
            "out-of-ceiling and subminimum reasoning requests fail closed");
        var mandatory = CerebralJson.Read<AgentProfile>(CerebralJson.Element(accepted));
        mandatory.ReasoningMode = "on";
        check(5, WorkerReasoningAdjustment.Resolve(mandatory, mandatory, 1536, 8192) == 1536 &&
            Reject<InvalidOperationException>(() => WorkerReasoningAdjustment.Resolve(mandatory, mandatory, 0, 8192)),
            "owner-required reasoning can increase but cannot be turned off");
        current.AllowReasoningAdjustment = false;
        check(5, Reject<InvalidOperationException>(() => WorkerReasoningAdjustment.Resolve(accepted, current, 512, 8192)),
            "owner revocation blocks a retained adaptive run");
        current.AllowReasoningAdjustment = true;
        current.ReasoningTokenCeiling = 512;
        check(5, WorkerReasoningAdjustment.Resolve(accepted, current, 512, 8192) == 512 &&
            Reject<InvalidOperationException>(() => WorkerReasoningAdjustment.Resolve(accepted, current, 1024, 8192)),
            "current owner ceiling narrows the frozen ceiling");
        current.ReasoningTokenCeiling = 2048;
        current.ModelId = "other-model";
        check(5, Reject<InvalidOperationException>(() => WorkerReasoningAdjustment.Resolve(accepted, current, 512, 8192)),
            "changed model binding cannot inherit the reasoning grant");
        current.ModelId = accepted.ModelId;
        current.Revision = accepted.Revision - 1;
        check(5, Reject<InvalidOperationException>(() => WorkerReasoningAdjustment.Resolve(accepted, current, 512, 8192)),
            "a stale or foreign profile generation is rejected");
        current.Revision = accepted.Revision;
        current.Id = "different-helper";
        check(5, Reject<InvalidOperationException>(() => WorkerReasoningAdjustment.Resolve(accepted, current, 512, 8192)),
            "a child cannot use another profile's reasoning ceiling");
        current.Id = accepted.Id;
        check(5, Reject<InvalidOperationException>(() => WorkerReasoningAdjustment.Resolve(accepted, current, 128, 512)),
            "reasoning cannot remove the minimum reliable response room");
        check(5, Reject<ArgumentException>(() => Configuration.ValidateProfile(WithCeiling(accepted, 127))) &&
            Reject<ArgumentException>(() => Configuration.ValidateProfile(WithCeiling(accepted, 32769))),
            "configuration bounds the explicit owner ceiling");

        var state = new ProviderState();
        await using var provider = state.Provider();
        f.Store.Put("agent", accepted.Id, accepted);
        var snapshot = await WorkerExecutionSnapshot.CaptureAsync(f.Store, accepted, f.Configuration, f.Tools, [], default);
        var frozen = WorkerExecutionSnapshot.Read(snapshot).Profile;
        check(5, frozen.AllowReasoningAdjustment && frozen.ReasoningTokenCeiling == 2048 &&
            frozen.Id == accepted.Id && frozen.Revision == accepted.Revision,
            "admission freezes the exact owner adjustment policy and profile revision");
        var selected = f.Adapters(provider).ForSubmission(new("adaptive-request", "adaptive-chat", accepted.Id,
            "fixture.configured", "1.0.0", "fixture", null, WorkflowContract.EmptyObject, snapshot));
        check(5, selected.Descriptor.SupportsReasoning,
            "opted-in local text route advertises the reasoning-capable facet");
        await using var off = await selected.PrepareAsync(new(f.Model.Id,
            [new("user", [new TextContentPart("off")])], 8192, 512), default);
        await using var on = await selected.PrepareAsync(new(f.Model.Id,
            [new("user", [new TextContentPart("on")])], 8192, 512)
            { ReasoningTokens = 512 }, default);
        check(5, off.Metadata.ReasoningReserveTokens == 0 && on.Metadata.ReasoningReserveTokens == 512 &&
            state.Prepared == 2,
            "one accepted profile selects separate off/on preparations with inert providers");

        var effects = new WorkerWorkflowEffects(f.Store, f.Configuration, f.Runtime, f.Tools);
        var submission = new WorkflowSubmission("adaptive-request", "adaptive-chat", accepted.Id,
            "fixture.configured", "1.0.0", "fixture", null, WorkflowContract.EmptyObject, snapshot);
        var adaptiveBuild = typeof(WorkerWorkflowEffects).GetMethod("BuildAdaptiveGenerationRequest",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var unsupported = new NoReasoningAdapter();
        var unsupportedFailure = Failure(() => adaptiveBuild.Invoke(effects,
            [submission, new WorkerWorkflowEffects.GenerateArguments([]) { ReasoningTokens = 512 },
             unsupported, null]));
        check(5, unsupportedFailure is TargetInvocationException { InnerException: InvalidOperationException reason } &&
            reason.Message.Contains("does not support prepared reasoning", StringComparison.Ordinal) &&
            unsupported.Preparations == 0,
            "unsupported adapter rejects positive reasoning before preparation");
        var versions = effects.Commands.Where(x => x.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand)
            .OrderBy(x => x.Descriptor.Version).ToArray();
        check(5, versions.Select(x => x.Descriptor.Version).SequenceEqual([1, 2, 3]) &&
            !versions[0].Descriptor.InputSchema.GetProperty("properties").TryGetProperty("reasoningTokens", out _) &&
            !versions[1].Descriptor.InputSchema.GetProperty("properties").TryGetProperty("reasoningTokens", out _) &&
            versions[2].Descriptor.InputSchema.GetProperty("properties").TryGetProperty("reasoningTokens", out _),
            "earlier command versions retain their closed input contracts");
        var wrongRun = await versions[2].ExecuteAsync(new("adaptive-wrong-run",
            new("adaptive-request", "adaptive-chat", accepted.Id, "fixture.configured", "1.0.0",
                "fixture", null, WorkflowContract.EmptyObject, snapshot),
            CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([]) { ReasoningTokens = 512 }))
            { RunId = "unadmitted-run" }, default);
        check(5, wrongRun.State == WorkflowEffectState.Failed && state.Generated == 0,
            "a foreign run cannot dispatch adaptive generation");

        static AgentProfile WithCeiling(AgentProfile source, int ceiling)
        {
            var copy = CerebralJson.Read<AgentProfile>(CerebralJson.Element(source));
            copy.ReasoningTokenCeiling = ceiling;
            return copy;
        }
        static Exception? Failure(Action action)
        {
            try { action(); return null; }
            catch (Exception error) { return error; }
        }
    }

    private sealed class NoReasoningAdapter : IModelRuntimeAdapter
    {
        internal int Preparations;
        public ModelAdapterDescriptor Descriptor { get; } =
            LlamaPreparedGeneration.Adapter with { SupportsReasoning = false };

        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            Preparations++;
            throw new InvalidOperationException("An unsupported adapter must be refused before preparation.");
        }
    }
}
