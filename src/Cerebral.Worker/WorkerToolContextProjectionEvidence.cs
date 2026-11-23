using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;

namespace Cerebral.Worker;

/// <summary>Immutable journal/receipt proof shared by execution and model-history projection.
/// Current registration, current run revision and model-facing tool views are not historical proof.</summary>
internal static class WorkerToolContextProjectionEvidence
{
    private const int MaximumIntentPages = 32;

    internal sealed record Origin(WorkflowToolContextProjectionCommandRequest Command,
        WorkflowToolContextProjectionLink Link, ContextProviderRequest PublicationRequest,
        WorkflowToolContextSavedEffect Source);

    internal static (ToolCallData Call, int? CommandVersion) ReadCall(CerebralEvent item)
    {
        if (item.Type != CerebralEventTypes.ToolCall || item.Data is not { ValueKind: JsonValueKind.Object } data)
            throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
            if (!names.Add(property.Name) || property.Name is not ("callId" or "toolName" or "arguments" or "commandVersion"))
                throw Invalid();
        if (!names.Contains("callId") || !names.Contains("toolName") || !names.Contains("arguments"))
            throw Invalid();
        var arguments = data.GetProperty("arguments");
        if (arguments.ValueKind == JsonValueKind.Undefined) throw Invalid();
        int? version = null;
        if (data.TryGetProperty("commandVersion", out var rawVersion))
        {
            if (rawVersion.ValueKind != JsonValueKind.Number || !rawVersion.TryGetInt32(out var value) || value < 1)
                throw Invalid();
            version = value;
        }
        return (new(Text(data, "callId"), Text(data, "toolName"), arguments.Clone()), version);
    }

    internal static CerebralEvent FindIntent(EventJournal journal, WorkflowRun run, string effectId,
        long upper, CancellationToken token)
    {
        if (run.Id != run.Submission.RequestId || string.IsNullOrWhiteSpace(effectId) ||
            effectId.Length > 256 || upper <= run.Submission.ConversationSequence || upper == long.MaxValue)
            throw Invalid();
        CerebralEvent? found = null;
        long? before = upper + 1;
        for (var index = 0; index < MaximumIntentPages; index++)
        {
            token.ThrowIfCancellationRequested();
            var page = journal.ReadPreviousPage(run.Submission.ConversationId, before,
                EventJournal.MaximumPageEvents, upper, token);
            foreach (var item in page.Events.Reverse())
            {
                if (item.Sequence <= run.Submission.ConversationSequence) return found ?? throw Invalid();
                if (item.Type != CerebralEventTypes.ToolCall || item.CorrelationId != effectId) continue;
                var (call, _) = ReadCall(item);
                RequireOwner(item, run, call.ToolName);
                if (call.CallId != effectId || found is not null) throw Invalid();
                found = item;
            }
            if (page.NextBeforeSequence is null) return found ?? throw Invalid();
            if (page.NextBeforeSequence >= before) throw Invalid();
            before = page.NextBeforeSequence;
        }
        throw Invalid(); // Exhaustion never authorizes a guessed intent/version.
    }

    internal static Origin ReadOrigin(EventJournal journal, WorkflowStore receipts, WorkflowRun run,
        CerebralEvent projectionIntent, WorkerContextContributionAdmission admission, CancellationToken token) =>
        ReadOrigin(journal, receipts, run, projectionIntent, admission, token, verifyPrevious: true);

    private static Origin ReadOrigin(EventJournal journal, WorkflowStore receipts, WorkflowRun run,
        CerebralEvent projectionIntent, WorkerContextContributionAdmission admission, CancellationToken token,
        bool verifyPrevious)
    {
        token.ThrowIfCancellationRequested();
        if (run.Id != run.Submission.RequestId ||
            !WorkerToolContextProjectionAdmission.SupportsConsumer(run.Submission,
                WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot)) ||
            admission.Descriptor.SchemaVersion != WorkflowToolContextProjectionContract.RequiredContributionDescriptorSchemaVersion ||
            admission.Selection.Disposition != WorkflowContextDisposition.Ready || admission.ProjectionDescriptor is null)
            throw Invalid();
        var (call, version) = ReadCall(projectionIntent);
        RequireOwner(projectionIntent, run, WorkerToolContextProjectionProtocol.CommandId);
        if (call.ToolName != WorkerToolContextProjectionProtocol.CommandId ||
            version != WorkerToolContextProjectionProtocol.CommandVersion) throw Invalid();
        var command = WorkerToolContextProjectionProtocol.ReadCommandRequest(call.Arguments);
        if (command.ContributionId != admission.Descriptor.Id || command.ToolEffectId == call.CallId ||
            command.PreviousProjectionEffectId == call.CallId ||
            command.Slot.Id != admission.ProjectionDescriptor.PublicationTemplate.References.Single().Id)
            throw Invalid();
        var source = ReadSource(journal, receipts, run, command.ToolEffectId, projectionIntent.Sequence - 1, token);
        var tool = WorkerWorkflows.Read<WorkerWorkflowEffects.ToolArguments>(source.Arguments);
        if (string.IsNullOrWhiteSpace(tool.Name) || tool.Name.Length > 256 ||
            tool.Arguments.ValueKind != JsonValueKind.Object ||
            !admission.ProjectionDescriptor.ToolNames.Contains(tool.Name, StringComparer.Ordinal))
            throw Invalid();
        var link = new WorkflowToolContextProjectionLink(WorkflowToolContextProjectionContract.SchemaVersion,
            command.ContributionId, run.Id, call.CallId, source.EffectId,
            WorkerToolContextProjectionProtocol.SavedEffectDigest(source),
            command.PreviousProjectionEffectId, command.Slot);
        var publication = WorkerToolContextProjectionProtocol.DerivePublicationRequest(
            admission.ProjectionDescriptor.PublicationTemplate, command);
        if (verifyPrevious) RequirePrevious(journal, receipts, run, projectionIntent, admission, command, token);
        return new(command, link, publication, source);
    }

    internal static WorkflowToolContextSavedEffect ReadSource(EventJournal journal, WorkflowStore receipts,
        WorkflowRun run, string effectId, long upper, CancellationToken token)
    {
        var intent = FindIntent(journal, run, effectId, upper, token);
        var (call, version) = ReadCall(intent);
        if (call.ToolName != WorkerWorkflowEffects.ToolCommand || version != 1) throw Invalid();
        var result = receipts.GetEffectResult(run.Id, effectId);
        if (result.State is not (WorkflowEffectState.Succeeded or WorkflowEffectState.Failed or WorkflowEffectState.Cancelled) ||
            result.ConversationCheckpoint is not null) throw Invalid();
        return new(WorkflowToolContextProjectionContract.SchemaVersion, run.Id, effectId,
            call.ToolName, version.Value, call.Arguments.Clone(), result.State, result.Data?.Clone(),
            result.Error, result.ProviderReceiptId);
    }

    private static void RequirePrevious(EventJournal journal, WorkflowStore receipts, WorkflowRun run,
        CerebralEvent currentIntent, WorkerContextContributionAdmission admission,
        WorkflowToolContextProjectionCommandRequest command, CancellationToken token)
    {
        if (command.PreviousProjectionEffectId is null)
        {
            if (command.PriorState is not null || command.Slot.CurrentSourceRevision is not null) throw Invalid();
            return;
        }
        var priorIntent = FindIntent(journal, run, command.PreviousProjectionEffectId,
            currentIntent.Sequence - 1, token);
        // This is a bounded one-hop join to a trusted host receipt, not recursive history replay.
        // Its own original source/digest is verified; its earlier ledger chain was checked at execution.
        var prior = ReadOrigin(journal, receipts, run, priorIntent, admission, token, verifyPrevious: false);
        var receipt = receipts.GetEffectResult(run.Id, command.PreviousProjectionEffectId);
        if (receipt.State != WorkflowEffectState.Succeeded || receipt.Data is null ||
            receipt.ConversationCheckpoint is not null || string.IsNullOrWhiteSpace(receipt.ProviderReceiptId))
            throw Invalid();
        var result = WorkerToolContextProjectionProtocol.ValidateResult(admission.Descriptor.Provider,
            prior.PublicationRequest, prior.Link,
            WorkerToolContextProjectionProtocol.ReadResult(receipt.Data.Value), receipt.ProviderReceiptId);
        if (result.Disposition == WorkflowToolContextProjectionDisposition.Blocked ||
            result.Link.ToolEffectId == command.ToolEffectId || !SameState(result.NextState, command.PriorState))
            throw Invalid();
        var revision = result.Disposition == WorkflowToolContextProjectionDisposition.Publish
            ? result.SlotUpdate!.SourceRevision : result.Link.Slot.CurrentSourceRevision;
        if (command.Slot.Id != result.Link.Slot.Id || command.Slot.CurrentSourceRevision != revision) throw Invalid();
    }

    internal static bool SameState(JsonElement? left, JsonElement? right) =>
        left is null ? right is null : right is { } value && JsonElement.DeepEquals(left.Value, value);

    internal static void RequireOwner(CerebralEvent item, WorkflowRun run, string operation)
    {
        var (call, _) = ReadCall(item);
        if (item.TurnId != run.Id || item.Provenance.JobId != run.Id ||
            item.ConversationId != run.Submission.ConversationId ||
            item.Sequence <= run.Submission.ConversationSequence || item.CorrelationId != call.CallId ||
            call.ToolName != operation || item.Actor != new EventActor(EventActorKind.Agent, run.Submission.ProfileId) ||
            item.Provenance.Source != EventSourceKind.Service || item.Provenance.SourceId != "cerebral.engine")
            throw Invalid();
    }

    private static string Text(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String ||
            field.GetString() is not { Length: > 0 and <= 256 } text || text.Any(char.IsControl))
            throw Invalid();
        return text;
    }

    private static InvalidDataException Invalid() => new("The exact saved tool projection evidence is unavailable or changed.");
}
