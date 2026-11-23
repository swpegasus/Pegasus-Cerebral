using System.Text.Json;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private async Task<object> IsolatedTextCommandAsync(string operation, JsonElement arguments, CancellationToken token)
    {
        if (workflows?.Engine is null) throw new InvalidOperationException("This Worker does not support isolated suggestions.");
        if (operation == IsolatedTextGenerationContract.CapabilityOperation)
        {
            if (arguments.EnumerateObject().Any()) throw new ArgumentException("Capability discovery takes no arguments.");
            return new { contract = IsolatedTextGenerationContract.Id, supported = true, loadedOnly = true, maximumOutputTokens = 256 };
        }
        var fields = new[] { "schemaVersion", "requestId", "profileId", "profileRevision", "purpose", "policy", "text", "maximumOutputTokens", "expiresAtUnixMilliseconds" };
        if (!arguments.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal)))
            throw new ArgumentException("Isolated generation requires exactly its versioned request fields, once each.");
        var input = WorkerWorkflows.Read<IsolatedTextGenerationRequest>(arguments);
        if (operation == IsolatedTextGenerationContract.CancelOperation)
            return IsolatedTextResult(scheduler.CancelIsolatedText(input));
        if (operation != IsolatedTextGenerationContract.GenerateOperation) throw new ArgumentException("Unsupported isolated generation operation.");
        var run = scheduler.SubmitIsolatedText(input);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(input.ExpiresAtUnixMilliseconds - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 1, 30000)));
        try
        {
            while (!run.IsTerminal && run.Status != WorkflowStatus.Waiting && !run.CancelRequested)
            {
                await Task.Delay(50, timeout.Token).ConfigureAwait(false);
                run = workflows.Store.Get(input.RequestId);
            }
            return IsolatedTextResult(run);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return IsolatedTextResult(scheduler.CancelIsolatedText(input));
        }
        finally
        {
            // The request owner is bounded even if a client disappears without sending Cancel.
            // Persist cancellation before signalling execution; Unknown receipts remain durable.
            if (workflows.Store.Get(input.RequestId) is { IsTerminal: false })
                scheduler.CancelIsolatedText(input);
        }
    }

    internal static IsolatedTextGenerationResult IsolatedTextResult(WorkflowRun run)
    {
        _ = IsolatedTextGenerationAdmission.RequireSubmission(run.Submission);
        var uncertain = run.Effect?.State is WorkflowEffectState.Unknown or WorkflowEffectState.Dispatched ||
            run.Status is WorkflowStatus.Waiting or WorkflowStatus.Cancelling;
        string? text = null;
        if (!uncertain && run.Status == WorkflowStatus.Completed && run.Result is { ValueKind: JsonValueKind.Object } result &&
            result.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
            text = value.GetString();
        var state = uncertain ? "uncertain" : run.Status == WorkflowStatus.Completed ? "completed" :
            run.Status == WorkflowStatus.Cancelled ? "cancelled" : "rejected";
        return new(IsolatedTextGenerationContract.Id, run.Id, state, text,
            uncertain ? "The previous suggestion outcome is unconfirmed. Its operation is retained and will not be replayed." :
                state == "rejected" ? "Suggestions require a current dedicated profile and its explicitly loaded local model; held work remains held." : null);
    }

    private static IReadOnlyList<WorkerWorkflows.OperationDescriptor> IsolatedTextOperations { get; } =
    [
        new(IsolatedTextGenerationContract.CapabilityOperation, 1, "Read isolated owner text-generation support; no model preparation.",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }), true),
        new(IsolatedTextGenerationContract.GenerateOperation, 1, "One isolated, loaded-model-only generation through ordinary scheduler Hold/resource admission. Exact profile revision; no context expansion or tools.", IsolatedTextRequestSchema(), true),
        new(IsolatedTextGenerationContract.CancelOperation, 1, "Cancel only the exact immutable isolated request, including cancellation arriving before submit. Unknown receipts remain retained; no replay.", IsolatedTextRequestSchema(), true)
    ];
    private static JsonElement IsolatedTextRequestSchema()
    {
        using var document = JsonDocument.Parse("""
        {"type":"object","properties":{"schemaVersion":{"type":"integer","enum":[1]},"requestId":{"type":"string","pattern":"^[0-9a-fA-F]{32}$"},
        "profileId":{"type":"string","minLength":1,"maxLength":128},"profileRevision":{"type":"integer","minimum":1},
        "purpose":{"type":"string","minLength":1,"maxLength":100},"policy":{"type":"string","minLength":1,"maxLength":8192},
        "text":{"type":"string","minLength":1,"maxLength":64000},"maximumOutputTokens":{"type":"integer","minimum":1,"maximum":256},
        "expiresAtUnixMilliseconds":{"type":"integer","minimum":1}},"required":["schemaVersion","requestId","profileId","profileRevision","purpose","policy","text","maximumOutputTokens","expiresAtUnixMilliseconds"],"additionalProperties":false}
        """);
        return document.RootElement.Clone();
    }
}
