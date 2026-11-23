using System.Globalization;

namespace Cerebral.Orchestrator;

internal sealed record SlotStatusOccupant(string ModelId, string InstanceId, int NativeSlotId,
    string LeaseId, string AttemptId, string? ProfileId, int? ProfileRevision, string? RunId,
    string Phase, bool Uncertain, string Progress, string ProgressState,
    long? ProgressAgeMilliseconds, DateTimeOffset ObservedAt, string Kind = "unobserved", string Purpose = "unattributed");

internal sealed record SlotAgentLabel(string Id, int Revision, string? Name);

internal sealed record SlotRetainedContext(string ModelId, string? InstanceId, string? ProfileId,
    int? ProfileRevision, string? RunId, string Phase, bool Uncertain, DateTimeOffset ObservedAt);

internal sealed record SlotImageVerificationObservation(string ModelId, string? InstanceId,
    string Phase, bool Uncertain, DateTimeOffset ObservedAt);

/// <summary>Presentation of validated observations; neither allocator subtraction nor missing rows establish an occupant.</summary>
internal static class SlotStatusPresentation
{
    internal const string UnknownAllocatorCaption = "— free / — total / — quarantined";
    internal const int MaximumPrimaryCharacters = 360;
    internal const int MaximumAgentLabels = 256;
    internal static string AllocatorCaption(int? available, int? allocated, int? quarantined) =>
        available is { } free && allocated is { } total && quarantined is { } held
            ? $"{free.ToString(CultureInfo.InvariantCulture)} free / {total.ToString(CultureInfo.InvariantCulture)} total / {held.ToString(CultureInfo.InvariantCulture)} quarantined"
            : UnknownAllocatorCaption;

    internal static string PrimaryCaption(IReadOnlyList<SlotStatusOccupant> occupants,
        IReadOnlyList<SlotRetainedContext> contexts, int unmapped, bool partial,
        IReadOnlyList<SlotImageVerificationObservation>? imageVerifications = null,
        IReadOnlyList<SlotAgentLabel>? agentLabels = null)
    {
        imageVerifications ??= [];
        var qualifiers = new List<string>();
        if (contexts.Count > 0) qualifiers.Add($"{contexts.Count} retained context records · activity/slot unobserved");
        if (imageVerifications.Count > 0) qualifiers.Add($"{imageVerifications.Count} image verification observations · slot unobserved");
        if (unmapped > 0) qualifiers.Add($"{unmapped} unmapped lease rows");
        if (partial) qualifiers.Add("partial observation");
        var qualifierLength = string.Join("  ·  ", qualifiers).Length;
        var slotBudget = Math.Max(0, MaximumPrimaryCharacters - qualifierLength - 64);
        var groups = Groups(occupants).ToArray();
        var parts = new List<string>();
        if (groups.Length > 0) parts.Add($"{groups.Length} observed occupied {(groups.Length == 1 ? "slot" : "slots")}");
        var used = 0;
        var shown = 0;
        foreach (var group in groups)
        {
            var rows = group.ToArray();
            var coordinate = $"Slot {group.Key.NativeSlotId} [{Label(group.Key.InstanceId, 8)}]";
            var text = rows.Length == 1
                ? coordinate + ": " + Work(rows[0], agentLabels) + " · " + Phase(rows[0])
                : coordinate + ": ambiguous occupants";
            // Leave room for every omission/uncertainty qualifier below.
            if (shown >= 4 || used + text.Length > slotBudget) break;
            parts.Add(text); used += text.Length + 3; shown++;
        }
        if (parts.Count == 0) parts.Add(groups.Length == 0
            ? "Slot occupants unobserved" : "Observed slot occupants in details");
        if (shown < groups.Length) parts.Add($"+{groups.Length - shown} observed slots in details");
        parts.AddRange(qualifiers);
        var result = string.Join("  ·  ", parts);
        // Identifiers have already been bounded, but do not let future label changes expand the status strip.
        return result.Length <= MaximumPrimaryCharacters ? result
            : Label(result, MaximumPrimaryCharacters - 1);
    }

    internal static IReadOnlyList<string> OccupantLines(IReadOnlyList<SlotStatusOccupant> occupants,
        int unmapped, bool partial, DateTimeOffset sampledAt, IReadOnlyList<SlotRetainedContext>? contexts = null,
        IReadOnlyList<SlotImageVerificationObservation>? imageVerifications = null,
        IReadOnlyList<SlotAgentLabel>? agentLabels = null)
    {
        contexts ??= [];
        imageVerifications ??= [];
        var lines = new List<string>
        {
            $"Observed slot occupants — sample {sampledAt.ToString("O", CultureInfo.InvariantCulture)}.",
            "Availability comes from the separate allocator read. Missing ownership rows do not mean free slots."
        };
        if (partial) lines.Add("Ownership coverage is partial; only the listed captured rows are known.");
        if (occupants.Count == 0)
            lines.Add("No current lease could be mapped to a known instance and native slot in this sample; occupancy is not inferred.");
        var used = lines.Sum(line => line.Length + 1);
        var omitted = 0;
        foreach (var group in Groups(occupants))
        {
            var rows = group.ToArray();
            var line = rows.Length == 1 ? Describe(rows[0], agentLabels) :
                $"Slot {group.Key.NativeSlotId} · instance {Label(group.Key.InstanceId, 24)}: conflicting current lease/attempt observations ({rows.Length}); occupant is ambiguous.";
            if (used + line.Length + 1 > 10000) { omitted += rows.Length; continue; }
            lines.Add(line); used += line.Length + 1;
        }
        if (unmapped > 0)
            lines.Add($"{unmapped.ToString(CultureInfo.InvariantCulture)} current lease rows lack a complete instance/slot coordinate; those occupants remain unknown.");
        if (omitted > 0)
            lines.Add($"{omitted.ToString(CultureInfo.InvariantCulture)} captured occupant rows omitted at the display bound; no missing slot is classified as free.");
        lines.Add("Retained context records — activity/slot unobserved; these are not an idle, free-slot or generating count.");
        var omittedContexts = 0;
        foreach (var row in contexts)
        {
            var line = $"Retained context · model {Label(row.ModelId, 48)} · instance {Label(row.InstanceId, 24)} · {Agent(row.ProfileId, row.ProfileRevision, agentLabels)} · profile {ProfileCoordinate(row.ProfileId, row.ProfileRevision)} · run {Label(row.RunId, 32)} · observed phase {row.Phase}" +
                (row.Uncertain ? " (ownership uncertain)" : "") +
                $". Activity and native-slot ownership are unobserved; row observed {row.ObservedAt.ToString("O", CultureInfo.InvariantCulture)}.";
            if (used + line.Length + 1 > 12000) { omittedContexts++; continue; }
            lines.Add(line); used += line.Length + 1;
        }
        if (contexts.Count == 0) lines.Add("No retained context records were captured in this page; missing or partial rows do not prove none exist.");
        if (omittedContexts > 0) lines.Add($"{omittedContexts} retained context records omitted at the display bound; activity/slot remains unobserved.");
        if (imageVerifications.Count > 0)
        {
            lines.Add("Image verification observations — instance evidence only; slot ownership is unobserved.");
            var omittedImages = 0;
            foreach (var row in imageVerifications)
            {
                var line = $"Image verification observed · model {Label(row.ModelId, 48)} · instance {Label(row.InstanceId, 24)} · observed phase {row.Phase}" +
                    (row.Uncertain ? " (ownership uncertain)" : "") +
                    $". Native slot and physical lease are unobserved; row observed {row.ObservedAt.ToString("O", CultureInfo.InvariantCulture)}.";
                if (used + line.Length + 1 > 13000) { omittedImages++; continue; }
                lines.Add(line); used += line.Length + 1;
            }
            if (omittedImages > 0) lines.Add($"{omittedImages} image verification observations omitted at the display bound; slots remain unobserved.");
        }
        lines.Add("Lease ownership can include preparing, cancellation and release work. A terminal result alone does not return a slot.");
        lines.Add("Image verification labels require the captured ImageVerification kind. Other text/image modality, context totals, KV residency and native provider activity are not inferred.");
        return lines;
    }

    private static IOrderedEnumerable<IGrouping<(string InstanceId, int NativeSlotId), SlotStatusOccupant>> Groups(
        IReadOnlyList<SlotStatusOccupant> occupants) => occupants.GroupBy(item => (item.InstanceId, item.NativeSlotId))
        .OrderBy(group => group.Key.InstanceId, StringComparer.Ordinal).ThenBy(group => group.Key.NativeSlotId);

    private static string Work(SlotStatusOccupant row, IReadOnlyList<SlotAgentLabel>? agentLabels) => row.Kind switch
    {
        "ImageVerification" => "Vision check",
        "LocalPreparation" when row.Purpose == "task-generation" => "Agent turn · " + Agent(row.ProfileId, row.ProfileRevision, agentLabels),
        "LocalPreparation" when row.Purpose == "compaction" => "Compaction · " + Agent(row.ProfileId, row.ProfileRevision, agentLabels),
        "LocalContext" => "Context lease · " + Agent(row.ProfileId, row.ProfileRevision, agentLabels),
        _ => row.Kind + " · " + Agent(row.ProfileId, row.ProfileRevision, agentLabels)
    };
    private static string Phase(SlotStatusOccupant row) =>
        "observed " + row.Phase + (row.Uncertain ? " (uncertain)" : "");
    private static string Agent(string? id, int? revision, IReadOnlyList<SlotAgentLabel>? agentLabels)
    {
        if (id is null) return "agent unobserved";
        string? name = null;
        if (revision is not null && agentLabels is { Count: <= MaximumAgentLabels })
        {
            var matches = agentLabels.Where(item => item.Id == id && item.Revision == revision).Take(2).ToArray();
            if (matches.Length == 1 && matches[0].Name is { } candidate &&
                !string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 256 && !candidate.Any(char.IsControl))
                name = candidate.Trim();
        }
        return "agent " + (name is null ? Label(id, 8) : Label(name, 32)) + "@" +
            (revision?.ToString(CultureInfo.InvariantCulture) ?? "—");
    }
    private static string ProfileCoordinate(string? id, int? revision) =>
        (id ?? "unobserved") + "@" + (revision?.ToString(CultureInfo.InvariantCulture) ?? "—");
    private static string Describe(SlotStatusOccupant row, IReadOnlyList<SlotAgentLabel>? agentLabels)
    {
        var age = row.ProgressAgeMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "unobserved";
        return $"Slot {row.NativeSlotId} · instance {Label(row.InstanceId, 24)} · {Work(row, agentLabels)} · profile {ProfileCoordinate(row.ProfileId, row.ProfileRevision)} · run {Label(row.RunId, 32)} · {Phase(row)}. " +
            $"Model {Label(row.ModelId, 48)}; lease {Label(row.LeaseId, 24)}; attempt {Label(row.AttemptId, 24)}; captured kind {row.Kind}; purpose {row.Purpose}. " +
            $"Provider progress {row.Progress} ({row.ProgressState}, age {age} ms); row observed {row.ObservedAt.ToString("O", CultureInfo.InvariantCulture)}.";
    }

    private static string Label(string? value, int maximum)
    {
        if (value is null) return "unobserved";
        if (value.Length <= maximum) return value;
        if (maximum > 0 && char.IsHighSurrogate(value[maximum - 1]) && char.IsLowSurrogate(value[maximum])) maximum--;
        return value[..maximum] + "…";
    }
}
