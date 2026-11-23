using System.Diagnostics;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    private WorkflowGenerationPreview? CreateGenerationPreview(WorkflowCommandContext context, Stopwatch elapsed)
    {
        if (diagnosticEvents?.HasServiceSubscribers != true) return null;
        try
        {
            var (snapshot, effective) = Resolve(context.Submission);
            var arguments = Read<GenerateArguments>(context.Arguments);
            if (!PreviewIdentity(context.Submission.ConversationId) || !PreviewIdentity(context.Submission.RequestId) ||
                !PreviewIdentity(context.RunId) || !PreviewIdentity(context.EffectId) || !PreviewIdentity(effective.Id))
                return null;
            var operations = snapshot.Tools.Where(tool => WorkerExecutionSnapshot.AllowsTool(effective, tool.Name) &&
                effective.Capabilities.Contains(tool.Capability, StringComparer.Ordinal) &&
                !HostOwnsNativeCortexTool(snapshot, tool.Name) &&
                (arguments.ToolNames is null || arguments.ToolNames.Contains(tool.Name, StringComparer.Ordinal)))
                .Select(tool => tool.Name).ToArray();
            var identity = new WorkflowGenerationPreviewIdentity(context.Submission.ConversationId,
                context.Submission.RequestId, context.RunId!, context.EffectId, effective.Id);
            return new(identity, operations, value =>
            {
                // This is the same owner-authenticated pipe/event feed as workflow.updated.
                // Preview observation is optional and can never fail or dispatch the effect.
                try { diagnosticEvents.Publish(WorkflowGenerationPreview.EventType, context.RunId, value); }
                catch (Exception error) when (IsOrdinary(error)) { }
            }, () => elapsed.ElapsedMilliseconds);
        }
        catch (Exception error) when (IsOrdinary(error)) { return null; }
    }

    private bool TryBindGenerationDraft(WorkflowCommandContext context, IPreparedModelGeneration prepared,
        WorkflowGenerationPreview? preview)
    {
        if (preview is null || prepared is not IBoundGenerationDraft source) return false;
        try
        {
            var (_, effective) = Resolve(context.Submission);
            if (!PreviewIdentity(effective.ModelWorkerId)) return false;
            var binding = new GenerationDraftBinding(context.Submission.ConversationId,
                context.Submission.RequestId, context.RunId!, context.EffectId, effective.Id,
                effective.ModelWorkerId);
            var gate = new object();
            var decisionComplete = false;
            var accepted = false;
            var enabled = true;
            long sequence = 0;
            void ObserveDraft(GenerationDraftSample sample)
            {
                lock (gate)
                {
                    if (!decisionComplete || !accepted || !enabled) return;
                    if (sample is null || sample.Binding != binding || sample.Sequence != sequence + 1 ||
                        sample.Kind is not (GenerationDraftKind.Content or GenerationDraftKind.Unavailable) ||
                        sample.Kind == GenerationDraftKind.Content && sample.Content is null)
                    {
                        enabled = false; InvalidateGenerationPreview(preview); return;
                    }
                    sequence = sample.Sequence;
                    if (sample.Kind == GenerationDraftKind.Unavailable)
                    {
                        enabled = false; InvalidateGenerationPreview(preview); return;
                    }
                    // Raw provider-channel bytes stop at the bounded public-envelope parser.
                    // They never enter diagnostics or the existing terminal onText callback.
                    ObserveGenerationPreview(preview, sample.Content!);
                }
            }
            // Do not hold our observer gate while invoking an optional external binder.
            var bound = source.TryBindDraft(binding, ObserveDraft);
            lock (gate)
            {
                accepted = bound; decisionComplete = true;
                // True remains owned even if the channel later disables itself. False leaves
                // legitimate legacy/direct adapter callback semantics unchanged.
                return accepted;
            }
        }
        catch (Exception error) when (IsOrdinary(error))
        { InvalidateGenerationPreview(preview); return false; }
    }

    // The outer effect owns successful settlement after preparation and readiness cleanup.
    // Only public projection state and the provider finish cross that cleanup boundary.
    private sealed class GenerationPreviewCompletion
    {
        internal WorkflowGenerationPreview? Preview { get; set; }
        internal ModelGenerationFinish Finish { get; set; } = ModelGenerationFinish.Unknown;
    }

    private static void CompleteGenerationPreview(GenerationPreviewCompletion completion,
        WorkflowCommandResult? result)
    {
        if (completion.Finish != ModelGenerationFinish.Stop ||
            result is { State: WorkflowEffectState.Succeeded, Error: null })
            SettleGenerationPreview(completion.Preview, completion.Finish);
        else InvalidateGenerationPreview(completion.Preview);
    }

    private static void InvalidateGenerationPreview(WorkflowGenerationPreview? preview)
    {
        try { preview?.Invalidate(); }
        catch (Exception error) when (IsOrdinary(error)) { }
    }

    private static bool PreviewIdentity(string? value) =>
        value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);

    private static void ObserveGenerationPreview(WorkflowGenerationPreview? preview, string fragment)
    {
        try { preview?.Append(fragment); }
        catch (Exception error) when (IsOrdinary(error)) { }
    }

    private static void SettleGenerationPreview(WorkflowGenerationPreview? preview,
        Cerebral.Contracts.Models.ModelGenerationFinish finish)
    {
        try { preview?.Finish(finish); }
        catch (Exception error) when (IsOrdinary(error)) { }
    }
}
