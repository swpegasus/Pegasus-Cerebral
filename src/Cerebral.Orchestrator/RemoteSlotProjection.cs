using System.Globalization;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

// A remote registration is a possible compute location, not an allocated or available slot.
internal static class RemoteSlotProjection
{
    internal sealed record Registration(string Id, string Name, bool ProbeReady);
    internal sealed record Observation(string InstallationId, int FreeSlots, int LoadedModels,
        DateTimeOffset SampledAt, DateTimeOffset ReadAt, long DiagnosticRoundTripMs);
    internal sealed record Presentation(string Caption, string Details);

    internal const int MaximumProbes = 8;
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    internal static (IReadOnlyList<Registration> Page, int NextCursor) SelectProbePage(
        IReadOnlyList<Registration> registrations, int cursor)
    {
        var eligible = registrations.Where(registration => registration.ProbeReady).ToArray();
        if (eligible.Length == 0) return ([], 0);
        var start = (int)((uint)cursor % (uint)eligible.Length);
        var count = Math.Min(MaximumProbes, eligible.Length);
        return (Enumerable.Range(0, count).Select(offset => eligible[(start + offset) % eligible.Length]).ToArray(),
            (start + count) % eligible.Length);
    }

    internal static IReadOnlyList<Registration>? Capture(HostSettings settings)
    {
        if (!settings.Coordinator || settings.Workers is null || settings.Workers.Count > 256) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<Registration>();
        foreach (var worker in settings.Workers)
        {
            if (worker is null || !ValidId(worker.Id) || worker.Id == "local" || !seen.Add(worker.Id)) return null;
            var name = CleanLabel(worker.Name, worker.Id);
            // This existing read route requires the owner key; peer-only dispatch is a distinct capability.
            var probeReady = !worker.RequiresConfiguration &&
                worker.AccessKey is { Length: >= 24 and <= 512 } key && !key.Any(char.IsWhiteSpace);
            rows.Add(new(worker.Id, name, probeReady));
        }
        return rows;
    }

    internal static Observation? ReadDiagnostics(JsonElement value, DateTimeOffset readAt,
        long diagnosticRoundTripMs = 0)
    {
        try
        {
            if (diagnosticRoundTripMs is < 0 or > 30000 ||
                value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > 256 * 1024 ||
                !UniqueProperties(value) || !value.TryGetProperty("workerInstallationId", out var owner) ||
                owner.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(owner.GetString(), "N", out var installation) || installation == Guid.Empty ||
                !value.TryGetProperty("sampledAt", out var stamp) ||
                stamp.ValueKind != JsonValueKind.String || !stamp.TryGetDateTimeOffset(out var sampled) ||
                sampled == default || sampled.Offset != TimeSpan.Zero ||
                sampled > readAt.AddSeconds(30) || sampled < readAt.AddSeconds(-30) ||
                !value.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array ||
                models.GetArrayLength() > 256) return null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var free = 0;
            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object || !UniqueProperties(model) ||
                    !model.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    !ValidId(id.GetString()) || !seen.Add(id.GetString()!) ||
                    !model.TryGetProperty("processId", out var process) || !process.TryGetInt32(out var pid) || pid <= 0 ||
                    !model.TryGetProperty("availableSlots", out var availability) ||
                    !availability.TryGetInt32(out var count) || count is < 0 or > 16 ||
                    !model.TryGetProperty("busy", out var users) || !users.TryGetInt32(out var busy) ||
                    busy is < 0 or > 10000) return null;
                free += count;
            }
            return new(installation.ToString("N"), free, models.GetArrayLength(), sampled, readAt,
                diagnosticRoundTripMs);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { return null; }
    }

    internal static Presentation Show(IReadOnlyList<Registration>? registrations,
        IReadOnlyDictionary<string, Observation> observations, DateTimeOffset now, bool connected)
    {
        if (registrations is null) return new("Remote: — free · workers unknown",
            "Remote registration capture is unavailable or invalid; neither the worker count nor slot capacity is known.");
        if (registrations.Count == 0) return new("Remote: none configured",
            "No remote worker registration was captured for this coordinator connection.");
        var valid = registrations.Where(registration =>
            connected && registration.ProbeReady && observations.TryGetValue(registration.Id, out var sample) &&
            sample.ReadAt <= now && now - sample.ReadAt <= MaximumAge).ToArray();
        // Different registration IDs can resolve to the same physical Worker. Count its pool once.
        var physical = valid.GroupBy(registration => observations[registration.Id].InstallationId,
                StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(registration => observations[registration.Id].ReadAt)
                .ThenBy(registration => registration.Id, StringComparer.Ordinal).First()).ToArray();
        var counted = physical.ToDictionary(registration => observations[registration.Id].InstallationId,
            registration => registration.Id, StringComparer.Ordinal);
        var aliases = valid.Length - physical.Length;
        var free = physical.Sum(registration => observations[registration.Id].FreeSlots);
        var unknown = registrations.Count - valid.Length;
        var caption = !connected ? $"Remote: — free · {registrations.Count} registrations last known" :
            valid.Length == 0 ? $"Remote: — free · {registrations.Count} registrations configured" :
            unknown == 0 ? $"Remote: {free} observed free · {physical.Length} {(physical.Length == 1 ? "worker" : "workers")} online" :
            $"Remote: ≥{free} observed free · {physical.Length} online, {unknown} registration{(unknown == 1 ? "" : "s")} unknown";
        var details = new List<string>
        {
            connected ? $"Remote registrations: {registrations.Count}; current owner-diagnostic observations: {valid.Length}; distinct observed Workers: {physical.Length}; aliases: {aliases}." :
                $"Last-known remote registrations: {registrations.Count}; Cerebral is disconnected. No remote availability is current.",
            "Configured workers and profile assignments are possible routes, not live free slots or ready inference.",
            "Remote observations use owner-managed diagnostics. Peer-only workers need a separate read route. Free counters do not reserve work or prove model admission."
        };
        foreach (var registration in registrations)
        {
            var label = registration.Name + " (" + registration.Id + ")";
            if (!connected) details.Add(label + ": last-known registration; reachability and slots unobserved.");
            else if (!registration.ProbeReady) details.Add(label + ": configured; owner diagnostic route unavailable; slots unknown.");
            else if (observations.TryGetValue(registration.Id, out var sample) &&
                sample.ReadAt <= now && now - sample.ReadAt <= MaximumAge)
                details.Add(label + $": owner observed at {sample.ReadAt:O}; {sample.FreeSlots.ToString(CultureInfo.InvariantCulture)} free across {sample.LoadedModels} loaded models; Worker {sample.InstallationId}; " +
                    (counted[sample.InstallationId] == registration.Id ? "counted once; " : $"alias of {counted[sample.InstallationId]}; capacity not added twice; ") +
                    $"diagnostic round trip {sample.DiagnosticRoundTripMs} ms (includes management processing, not inference overhead).");
            else details.Add(label + ": unavailable or stale; current slots unknown.");
        }
        if (registrations.Count(registration => registration.ProbeReady) > MaximumProbes)
            details.Add($"At most {MaximumProbes} eligible registrations are sampled per {PollInterval.TotalSeconds:N0}-second cycle; later registrations rotate through the next cycles.");
        return new(caption, string.Join("\n", details));
    }

    private static bool ValidId(string? id) => id is { Length: >= 1 and <= 100 } &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool UniqueProperties(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static string CleanLabel(string? candidate, string fallback)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return fallback;
        var clean = new string(candidate.Where(character => !char.IsControl(character)).Take(80).ToArray()).Trim();
        return clean.Length == 0 ? fallback : clean;
    }
}
