using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Client;

namespace Cerebral.Desktop;

public sealed record WorkflowGenerationRateSnapshot(bool Visible, string? RunId = null, string? EffectId = null,
    string? Phase = null, double? InputTokensPerSecond = null, double? OutputTokensPerSecond = null);

public sealed partial class WorkflowWorkspace
{
    private const int MaximumRetainedGenerationMetrics = 64;
    private GenerationMetricSample? generationMetrics;
    private readonly Dictionary<string, GenerationMetricSample> generationMetricsByEffect = new(StringComparer.Ordinal);
    private long generationMetricsSequence;
    private long generationMetricsSnapshotRevision;

    public WorkflowGenerationRateSnapshot GenerationRateSnapshot => generationMetrics is { Binding: { } binding } value &&
        CurrentGenerationMetrics(value) ? new(true, binding.RunId, binding.EffectId, value.Phase,
            value.InputTokensPerSecond, value.OutputTokensPerSecond) : new(false);

    private ActivityView? CurrentGenerationActivity() => conversation?.Snapshot.RunId is { } run
        ? activityViews.LastOrDefault(view => view.Items.Any(item => item.TurnId == run)) : null;

    private void MountGenerationPreviewInActivity()
    {
        if (generationPreviewHost is null) return;
        var target = generationPreview?.Kind == "final" ? conversationFlow : CurrentGenerationActivity()?.Scroller.Content as StackPanel;
        if (ReferenceEquals(generationPreviewHost.Parent, target)) return;
        if (generationPreviewHost.Parent is Panel parent) parent.Children.Remove(generationPreviewHost);
        if (ReferenceEquals(target, conversationFlow))
            conversationFlow.Children.Insert(Math.Min(1, conversationFlow.Children.Count), generationPreviewHost);
        else target?.Children.Add(generationPreviewHost);
    }
    private void ResetGenerationMetricsFeed()
    {
        generationMetricsSequence = 0;
        generationMetricsByEffect.Clear();
        ClearGenerationMetrics();
    }
    private void ClearGenerationMetrics()
    { generationMetrics = null; generationMetricsSnapshotRevision = 0; }

    private void NotifyGenerationMetrics(ServiceEvent item)
    {
        if (item.Type != "workflow.generation.metrics" || disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            var selected = generation; var connection = connectionEpoch; var feed = generationPreviewFeedGeneration;
            PassiveDesktopInteraction.Defer(Dispatcher, "generation measurements", () =>
            {
                if (selected == generation && connection == connectionEpoch && feed == generationPreviewFeedGeneration)
                    NotifyGenerationMetrics(item);
            }, message => report(message, true));
            return;
        }
        if (!IsLoaded || item.Sequence <= generationMetricsSequence || item.Data.ValueKind != JsonValueKind.Object ||
            item.Data.GetRawText().Length > 8192) return;
        try
        {
            if (GenerationThroughput.HasDuplicateFields(item.Data)) return;
            var value = item.Data.Deserialize<GenerationMetricSample>(GenerationThroughput.Json);
            if (!GenerationThroughput.Valid(value) || value!.Binding is not { } binding || item.JobId != binding.RunId ||
                !CurrentGenerationMetrics(value)) return;
            if (generationMetrics is { Binding: { } previous } prior)
            {
                if (previous.EffectId == binding.EffectId)
                {
                    if (prior.LaneId != value.LaneId || prior.State != "active" && value.State == "active" ||
                        value.ElapsedMilliseconds < prior.ElapsedMilliseconds) return;
                }
                else if (prior.State == "active" && (Snapshot!.Run!.EffectId != binding.EffectId ||
                    Snapshot.Run.EffectState != WorkflowEffectState.Dispatched)) return;
            }
            generationMetricsSequence = item.Sequence;
            generationMetrics = value;
            generationMetricsByEffect[binding.EffectId] = value;
            while (generationMetricsByEffect.Count > MaximumRetainedGenerationMetrics)
            {
                var oldest = generationMetricsByEffect.MinBy(entry => entry.Value.At).Key;
                generationMetricsByEffect.Remove(oldest);
            }
            generationMetricsSnapshotRevision = Snapshot!.Run!.Revision;
            RefreshActivityHeaders(Snapshot.Run);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { }
    }
    private bool CurrentGenerationMetrics(GenerationMetricSample value) => value.Binding is { } binding &&
        conversation?.Snapshot is { MutationUncertain: false, Run: { } run } snapshot &&
        snapshot.ConversationId == binding.ConversationId && snapshot.RunId == binding.RunId &&
        run.Id == binding.RequestId && run.Id == binding.RunId && run.ProfileId == binding.AgentId &&
        run.EffectId == binding.EffectId && !run.CancelRequested && run.EffectState != WorkflowEffectState.Unknown &&
        (value.State == "active" ? run.Status is WorkflowStatus.Running or WorkflowStatus.Queued :
            run.Status is WorkflowStatus.Running or WorkflowStatus.Queued or WorkflowStatus.Completed or WorkflowStatus.Failed);

    private void ReconcileGenerationMetrics()
    {
        if (generationMetrics is not { Binding: { } binding } value) return;
        var run = Snapshot?.Run;
        if (!CurrentGenerationMetrics(value) || value.State == "active" && run!.Revision > generationMetricsSnapshotRevision &&
            (run.EffectId != binding.EffectId || run.EffectState != WorkflowEffectState.Dispatched)) ClearGenerationMetrics();
    }

    private static string? GenerationEffectId(IEnumerable<CerebralEvent> items) => items
        .Where(item => item.Type == CerebralEventTypes.ToolCall &&
            TryActivityOperationIdentity(item, out var operation) && operation.ToolName == "cerebral.host:generate")
        .OrderBy(item => item.Sequence)
        .Select(item => TryActivityOperationIdentity(item, out var operation) ? operation.CallId : null)
        .LastOrDefault();

    private string GenerationRateCaption(ActivityView view, WorkflowConversationRun run, bool currentIteration)
    {
        var limits = view.Items.Where(item => item.TurnId == run.Id && item.Type == CerebralEventTypes.ToolResult)
            .TakeLast(8).Count(item => GenerationLimitState(item) is not null);
        var evidence = limits >= 2 ? $" · {limits} recent response limits" : "";
        var effectId = GenerationEffectId(view.Items);
        if (effectId is null || !generationMetricsByEffect.TryGetValue(effectId, out var value))
            return evidence + " · — tok/s";
        var current = currentIteration && value.State == "active" && CurrentGenerationMetrics(value);
        var fresh = !current || DateTimeOffset.UtcNow - value.At <= TimeSpan.FromSeconds(2);
        static string Number(double? rate) => rate is { } measured ? measured.ToString("0.#", CultureInfo.InvariantCulture) : "—";
        var phase = value.Phase switch { "prefill" => "Prefilling", "output" => "Generating", _ => "Provider metrics pending" };
        var input = fresh ? value.InputTokensPerSecond : null;
        var output = fresh ? value.OutputTokensPerSecond : null;
        return evidence + " · " + (current ? phase : "Last generation") + " · " + Number(input) + " in / " + Number(output) + " out tok/s";
    }

    internal static string? GenerationLimitState(CerebralEvent item)
    {
        var receipt = item.Content.OfType<StructuredContentPart>().FirstOrDefault(part => part.Schema == "workflow.command.result")?.Value;
        if (receipt is not { ValueKind: JsonValueKind.Object } outer || !outer.TryGetProperty("state", out var state) ||
            state.ValueKind != JsonValueKind.String || state.GetString() is not ("Succeeded" or "succeeded") ||
            !outer.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("generation", out var output) ||
            output.ValueKind != JsonValueKind.Object || !output.TryGetProperty("finish", out var finish) ||
            finish.ValueKind != JsonValueKind.String || finish.GetString() is not ("length" or "Length")) return null;
        return "Response limit reached";
    }
}
