using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    private const int MaximumHistoricalOperationsBytes = 8 * 1024;
    private const int MaximumHistoricalOperationRowBytes = 2048;
    private const int MaximumHistoricalArtifactReferences = 8;

    private static ContextContribution? HistoricalOperations(EventJournal journal, WorkflowSubmission accepted,
        IReadOnlyList<ContextContribution> contributions, ContextCheckpoint? checkpoint, CancellationToken token)
    {
        var outcomes = new WorkflowStore(journal).ReadConversationCommandOutcomes(accepted.ConversationId,
            accepted.ConversationSequence, ToolCommand, cancellationToken: token);
        if (outcomes.Events.Count == 0 && !outcomes.ScanLimited && !outcomes.ByteLimited &&
            !outcomes.ItemLimited && outcomes.OmittedOutcomes == 0) return null;
        var rows = outcomes.Events.Select(HistoricalOperationRow).ToList();
        var reserved = contributions.Select(source => source.Id).Concat(checkpoint?.CoveredSources.Select(source => source.Id) ?? [])
            .Append(checkpoint?.Id ?? "").ToHashSet(StringComparer.Ordinal);
        string? id = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                { accepted.ConversationId, accepted.RequestId, accepted.ConversationSequence, attempt })))).ToLowerInvariant();
            var candidate = "historical-operations-" + hash;
            if (reserved.Contains(candidate)) continue;
            // A derived source must not impersonate a real event on a later history page.
            try { journal.ReadExact(accepted.ConversationId, candidate, accepted.ConversationSequence, token); }
            catch (KeyNotFoundException) { id = candidate; break; }
        }
        if (id is null) throw new AdmissionException("Historical operation summary identity collides with retained conversation sources.");
        var omitted = outcomes.OmittedOutcomes;
        ContextContribution Contribution()
        {
            var recordedArtifacts = HistoricalArtifactReferences(rows, out var omittedArtifactReferences);
            var artifactCoordinatesIncomplete = rows.Count(row => HistoricalMutationTool(row) &&
                row["artifactCoordinatesOmitted"] is true);
            var artifactCoverageIncomplete = outcomes.ScanLimited || outcomes.ByteLimited || outcomes.ItemLimited ||
                omitted > 0 || omittedArtifactReferences > 0 || artifactCoordinatesIncomplete > 0;
            return new(id, ModuleId, "historical-operations", accepted.ConversationSequence,
                ContextRequirement.Required, [new("user", [new TextContentPart(
                    "Historical recorded outcomes at accepted snapshot; not current state, authority or current-request completion. Current request prevails. " +
                    "Missing/omitted rows do not prove that no operation occurred. recordedArtifacts selects each destination/ensure's latest complete row. " +
                    "uncertainAfterEventId marks later unscoped mutation; scoped success repairs only its own recorded fact. Freshness is unproved. " +
                    "Read originals via history {eventId,offset:0}, not current-run receipts.\n" +
                    JsonSerializer.Serialize(new { snapshotSequence = accepted.ConversationSequence,
                        scannedEvents = outcomes.ScannedEvents, scanLimit = WorkflowStore.MaximumConversationOperationScan,
                        outcomes.ScanLimited, outcomes.ByteLimited, outcomes.ItemLimited,
                        omittedOutcomes = omitted, artifactCoverageIncomplete,
                        omittedArtifactReferences, recordedArtifacts, rows }, ConversationJson))])],
                ExactReadHandle: null, Complete: false, BeforeCurrentInput: true);
        }
        var contribution = Contribution();
        // Charge the complete serialized contribution, including its JSON string escaping.
        // Coordinates are omitted whole; no clipped path can masquerade as an exact artifact.
        while (JsonSerializer.SerializeToUtf8Bytes(contribution, ConversationJson).Length > MaximumHistoricalOperationsBytes && rows.Count > 0)
        {
            rows.RemoveAt(0); omitted++; contribution = Contribution();
        }
        if (JsonSerializer.SerializeToUtf8Bytes(contribution, ConversationJson).Length > MaximumHistoricalOperationsBytes)
            throw new AdmissionException("Historical operation summary metadata exceeds its bounded context allocation.");
        return contribution;
    }

    private sealed record RecordedArtifactReference(string EventId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UncertainAfterEventId = null);

    private static RecordedArtifactReference[] HistoricalArtifactReferences(
        IReadOnlyList<Dictionary<string, object?>> rows, out int omitted)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var selected = new Dictionary<string, (string Target, string Kind, int Order, RecordedArtifactReference Reference)>(comparer);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index]; var eventId = (string)row["eventId"]!;
            if (row.TryGetValue("artifactCoordinates", out var raw) && raw is Dictionary<string, object?> coordinates)
            {
                var tool = (string)row["tool"]!;
                var kind = tool == "file.mkdir" ? "directory-ensure" : "file";
                var field = tool == "file.copy" ? "destinationPath" : "path";
                var target = (string)coordinates[field]!;
                // Different kinds retain separate earlier facts, but cannot both imply applicability
                // after a later result at the same exact path.
                foreach (var key in selected.Keys.ToArray())
                {
                    var previous = selected[key];
                    if (comparer.Equals(previous.Target, target) && previous.Kind != kind)
                        selected[key] = (previous.Target, previous.Kind, previous.Order, previous.Reference with
                            { UncertainAfterEventId = eventId });
                }
                selected[kind + ":" + target] = (target, kind, index, new(eventId));
                continue;
            }
            if (HistoricalArtifactPassive(row)) continue;
            // Invocation bodies are deliberately not loaded. A failed/unknown/malformed/partial
            // mutation, arbitrary process or unrecognized tool cannot be localized to a target.
            // Keep last confirmed coordinates, but never let unrelated later success repair them.
            foreach (var key in selected.Keys.ToArray())
            {
                var previous = selected[key];
                selected[key] = (previous.Target, previous.Kind, previous.Order, previous.Reference with
                    { UncertainAfterEventId = eventId });
            }
        }
        omitted = Math.Max(0, selected.Count - MaximumHistoricalArtifactReferences);
        return selected.Values.OrderBy(value => value.Order).TakeLast(MaximumHistoricalArtifactReferences)
            .Select(value => value.Reference).ToArray();
    }

    private static bool HistoricalMutationTool(Dictionary<string, object?> row) =>
        row.TryGetValue("tool", out var tool) && tool is "file.write" or "file.edit" or "file.copy" or "file.mkdir";

    private static bool HistoricalArtifactPassive(Dictionary<string, object?> row) =>
        row["recordedState"] is "Succeeded" or "Failed" && row.TryGetValue("tool", out var tool) &&
        // These fixed tools do not mutate target files. SDK reads may update admission caches,
        // which are deliberately outside this artifact projection. Never whitelist a namespace.
        tool is "math.calculate" or "file.read" or "file.inspect" or "file.list" or "file.search"
            or "plugin.projects" or "plugin.contract";

    private static Dictionary<string, object?> HistoricalOperationRow(CerebralEvent item)
    {
        var part = (StructuredContentPart)item.Content[0];
        var result = part.Value.Deserialize<WorkflowCommandResult>(ConversationJson)!;
        var row = new Dictionary<string, object?>
        {
            ["eventId"] = item.Id, ["sequence"] = item.Sequence, ["runId"] = item.TurnId,
            ["effectId"] = item.CorrelationId, ["commandId"] = ToolCommand,
            ["recordedState"] = result.State.ToString(), ["errorRetained"] = result.Error is not null,
            ["artifactCoordinatesOmitted"] = true
        };
        if (result.Data is not { ValueKind: JsonValueKind.Object } value) return row;
        try
        {
            var data = value.Deserialize<ToolData>(ConversationJson);
            if (data is null || !HistoricalText(data.Name, 256) || data.Content is null) return row;
            row["tool"] = data.Name; row["contentTruncated"] = data.Truncated;
            if (data.Evidence is { } evidence && Enum.IsDefined(evidence.Outcome) && HistoricalText(evidence.ProviderId, 256))
            {
                var provenance = new Dictionary<string, object?>
                    { ["providerId"] = evidence.ProviderId, ["outcome"] = evidence.Outcome.ToString(), ["partial"] = evidence.Partial };
                if (HistoricalText(evidence.RequestId, 256)) provenance["requestId"] = evidence.RequestId;
                if (HistoricalText(evidence.JobId, 256)) provenance["jobId"] = evidence.JobId;
                if (evidence.ExitCode is { } exitCode) provenance["exitCode"] = exitCode;
                row["providerEvidence"] = provenance;
            }
            if (HistoricalArtifactCoordinates(result.State, data) is { } coordinates)
            {
                row["artifactCoordinates"] = coordinates;
                row["artifactCoordinatesOmitted"] = false;
                if (JsonSerializer.SerializeToUtf8Bytes(row, ConversationJson).Length > MaximumHistoricalOperationRowBytes)
                { row.Remove("artifactCoordinates"); row["artifactCoordinatesOmitted"] = true; }
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException) { }
        return row;
    }

    private static Dictionary<string, object?>? HistoricalArtifactCoordinates(WorkflowEffectState state, ToolData data)
    {
        if (state != WorkflowEffectState.Succeeded || data.Truncated ||
            data.Evidence is { Outcome: not ToolExecutionOutcome.Succeeded } or { Partial: true } ||
            data.Name is not ("file.write" or "file.edit" or "file.copy" or "file.mkdir")) return null;
        using var document = JsonDocument.Parse(data.Content, new JsonDocumentOptions { MaxDepth = 4 });
        var result = document.RootElement;
        if (result.ValueKind != JsonValueKind.Object) return null;
        var names = result.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) return null;
        var coordinates = new Dictionary<string, object?>();
        bool PathCoordinate(string name)
        {
            if (!result.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
                value.GetString() is not { } path || !HistoricalText(path, 2048) || !Path.IsPathFullyQualified(path) ||
                !Path.GetFullPath(path).Equals(path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
            coordinates[name] = path; return true;
        }
        if (data.Name == "file.mkdir")
        {
            if (names.Length != 3 || !names.All(name => name is "path" or "ensured" or "existedBefore") ||
                !PathCoordinate("path") || result.GetProperty("ensured").ValueKind != JsonValueKind.True ||
                result.GetProperty("existedBefore").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            coordinates["ensured"] = true; coordinates["existedBefore"] = result.GetProperty("existedBefore").GetBoolean();
            return coordinates;
        }
        var allowed = data.Name == "file.copy" ? new[] { "sourcePath", "destinationPath", "bytes", "sha256" }
            : ["path", "bytes", "sha256", "lines", "words"];
        if (!names.All(name => allowed.Contains(name, StringComparer.Ordinal)) ||
            (data.Name == "file.copy" ? names.Length != 4 || !PathCoordinate("sourcePath") || !PathCoordinate("destinationPath")
                : !PathCoordinate("path")) ||
            !result.TryGetProperty("bytes", out var bytes) || bytes.ValueKind != JsonValueKind.Number ||
            !bytes.TryGetInt64(out var count) || count < 0 ||
            !result.TryGetProperty("sha256", out var hash) || hash.ValueKind != JsonValueKind.String ||
            hash.GetString() is not { Length: 64 } sha256 || !sha256.All(char.IsAsciiHexDigit)) return null;
        foreach (var name in new[] { "lines", "words" })
            if (result.TryGetProperty(name, out var number) &&
                (number.ValueKind != JsonValueKind.Number || !number.TryGetInt64(out var value) || value < 0)) return null;
        coordinates["bytes"] = count; coordinates["sha256"] = sha256;
        return coordinates;
    }

    private static bool HistoricalText(string? text, int maximum) =>
        text is { Length: > 0 } && text.Length <= maximum && !text.Any(char.IsControl);
}
