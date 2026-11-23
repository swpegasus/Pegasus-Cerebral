using System.Text;
using System.Text.Json;

namespace Cerebral.MetricsPlugin.Service;

/// <summary>Single-writer bounded scalar journal. A corrupt/foreign file is preserved and the
/// store becomes read-only; it never repairs unknown bytes by silently discarding evidence.</summary>
internal sealed class MetricsHistory : IDisposable
{
    private readonly object gate = new();
    private readonly string root, historyPath, settingsPath, coveragePath;
    private readonly string? legacyHistoryPath, legacyCoveragePath;
    private readonly int journalVersion;
    private readonly Func<DateTimeOffset> clock;
    // A bounded keyed slot retains each stream’s latest row. The winning view
    // replaces revisions; fields and cumulative progress are never merged or added.
    private sealed class Slot
    {
        internal MetricObservation? Legacy, Writable;
        internal MetricObservation Winner => Legacy is null ? Writable! : Writable is null || Legacy.Revision >= Writable.Revision ? Legacy : Writable;
    }
    private readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private FileStream? ownership, legacyLease, legacyCoverageLease;
    private long bytes, legacyBytes, duplicates, rejected, dropped, legacyDropped, sourceDropCursor;
    private string? fault, budgetReason;
    private bool disposed, hasGap, legacyHasGap;
    private DateTimeOffset? started, lastSource, legacyLastSource;
    private MetricsSettings settings = new(0, new());
    internal Action? AfterSettingsCommit { get; init; }
    internal MetricsHistory(string root, Func<DateTimeOffset>? clock = null, int journalVersion = 1)
    {
        if (journalVersion is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(journalVersion));
        this.journalVersion = journalVersion;
        this.root = Path.GetFullPath(root); this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        historyPath = Path.Combine(this.root, journalVersion == 1 ? "history.jsonl" : "history.v2.jsonl");
        coveragePath = Path.Combine(this.root, journalVersion == 1 ? "coverage.json" : "coverage.v2.json");
        legacyHistoryPath = journalVersion == 2 ? Path.Combine(this.root, "history.jsonl") : null;
        legacyCoveragePath = journalVersion == 2 ? Path.Combine(this.root, "coverage.json") : null;
        settingsPath = Path.Combine(this.root, "settings.json");
        try
        {
            GuardRoot(); Directory.CreateDirectory(this.root); GuardRoot();
            GuardFile(Path.Combine(this.root, "history.lock"));
            ownership = new(Path.Combine(this.root, "history.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            ReadSettings(); Load(); ReadCoverage();
        }
        catch (Exception e) when (Ordinary(e)) { fault = "history-open-failed"; }
    }
    internal MetricsSettings Settings { get { lock (gate) return settings; } }
    internal void SourceStarted() { lock (gate) { if (lastSource is not null) hasGap = true; started = clock(); sourceDropCursor = 0; PersistCoverage(); } }
    internal void SourceGap(long sourceDropped, bool force = false)
    {
        lock (gate)
        {
            if (sourceDropped is < 0 or > 9_000_000_000_000) { hasGap = true; PersistCoverage(); return; }
            var delta = sourceDropped >= sourceDropCursor ? sourceDropped - sourceDropCursor : sourceDropped;
            if (delta > 0 || force || sourceDropped < sourceDropCursor)
            { hasGap = true; dropped = checked(dropped + delta); sourceDropCursor = sourceDropped; PersistCoverage(); }
        }
    }
    internal void SourceObserved() { lock (gate) lastSource = clock(); }
    internal bool Append(MetricObservation row, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (fault is not null) return false;
            if (!MetricsValidation.Observation(row, clock()) || row.SchemaVersion != journalVersion)
            { rejected++; hasGap = true; return false; }
            if (row.ObservedAt <= clock().AddDays(-settings.Values.RetentionDays)) return false;
            if (slots.TryGetValue(row.Key, out var existing))
            {
                var old = existing.Winner;
                if (row.Revision < old.Revision) { duplicates++; return false; }
                if (row.Revision == old.Revision)
                {
                    if (Equivalent(row, old)) duplicates++;
                    else { rejected++; hasGap = true; }
                    return false;
                }
                if (SampleTimeConflict(row, old)) { rejected++; hasGap = true; return false; }
            }
            var serialized = JsonSerializer.SerializeToUtf8Bytes(row, MetricsContract.TypeInfo<MetricObservation>());
            if (serialized.Length > MetricsContract.MaximumRecordBytes) { rejected++; hasGap = true; return false; }
            var requiredBytes = serialized.Length + 1;
            try
            {
                GuardOwnedFiles(); token.ThrowIfCancellationRequested();
                if (!LegacyBudgetAllows(requiredBytes, row.Key)) { rejected++; hasGap = true; return false; }
                if (bytes + legacyBytes + requiredBytes > settings.Values.MaximumBytes ||
                    !slots.ContainsKey(row.Key) && slots.Count >= MetricsContract.MaximumRecords)
                    return Rewrite(token, row);
                using var file = new FileStream(historyPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                file.Write(serialized); file.WriteByte((byte)'\n'); file.Flush(true);
                bytes += requiredBytes;
                if (!slots.TryGetValue(row.Key, out var slot)) slots.Add(row.Key, slot = new());
                slot.Writable = row; budgetReason = null; lastSource = clock(); return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (Ordinary(e)) { fault = "history-write-failed"; hasGap = true; return false; }
        }
    }
    // Immutable legacy bytes and entities keep consuming the SAME budget. A
    // budget block permits compatible settings changes; corruption still fails closed.
    private bool LegacyBudgetAllows(int pendingBytes = 0, string? pendingKey = null)
    {
        if (legacyBytes + pendingBytes > settings.Values.MaximumBytes)
            budgetReason = "legacy-history-budget";
        else if (pendingKey is not null && !slots.ContainsKey(pendingKey) &&
            slots.Values.Count(slot => slot.Legacy is not null) >= MetricsContract.MaximumRecords)
            budgetReason = "legacy-history-record-budget";
        else { budgetReason = null; return true; }
        return false;
    }
    internal MetricsSettings SaveSettings(MetricsSettingsChange change, CancellationToken token)
    {
        MetricsValidation.Preferences(change.Values); token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (fault is not null) throw new InvalidOperationException("History storage is read-only; inspect status before retrying.");
            if (change.ExpectedRevision != settings.Revision) throw new InvalidOperationException("Reload metrics settings before saving; the revision changed.");
            if (change.Values == settings.Values) return settings;
            var next = new MetricsSettings(checked(settings.Revision + 1), change.Values);
            try { GuardOwnedFiles(); AtomicWrite(settingsPath, JsonSerializer.SerializeToUtf8Bytes(next, MetricsContract.TypeInfo<MetricsSettings>()), token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (Ordinary(e)) { fault = "settings-write-uncertain"; throw new IOException("Inspect settings before retrying the save."); }
            settings = next; budgetReason = null;
            AfterSettingsCommit?.Invoke();
            // Quota maintenance cannot turn a confirmed preference save into an ambiguous save.
            Maintain(CancellationToken.None); return settings;
        }
    }
    internal void Maintain(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (disposed || fault is not null) return;
            try
            {
                GuardOwnedFiles();
                if (!LegacyBudgetAllows()) { hasGap = true; return; }
                if (slots.Values.Any(slot => slot.Writable is { } row && row.ObservedAt <= clock().AddDays(-settings.Values.RetentionDays)) ||
                    bytes + legacyBytes > settings.Values.MaximumBytes) Rewrite(token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (Ordinary(e)) { fault = "history-maintenance-failed"; hasGap = true; }
        }
    }
    internal MetricsCoverage Coverage()
    {
        lock (gate)
        {
            var rows = Visible();
            return new(rows.Length == 0 ? null : rows.Min(x => x.ObservedAt), rows.Length == 0 ? null : rows.Max(x => x.ObservedAt),
                rows.Length, checked(bytes + legacyBytes), duplicates, rejected, checked(dropped + legacyDropped),
                hasGap || legacyHasGap || fault is not null || budgetReason is not null,
                fault is not null ? "read-only" : budgetReason is not null ? "limited" : "available", fault ?? budgetReason, started,
                lastSource is { } current && legacyLastSource is { } old ? (current >= old ? current : old) : lastSource ?? legacyLastSource);
        }
    }
    internal MetricsQueryResult Query(MetricsQuery query, CancellationToken token)
    {
        MetricsValidation.Query(query); token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var rows = Visible().Where(row => row.ObservedAt >= query.From && row.ObservedAt < query.To &&
                (query.ModelId is null || row.ModelId == query.ModelId) && (query.AgentId is null || row.AgentId == query.AgentId) &&
                (query.ConversationId is null || row.ConversationId == query.ConversationId) && (query.ProjectId is null || row.ProjectId == query.ProjectId) &&
                (query.Origin is null || row.Origin == query.Origin) && (query.WorkerId is null || row.WorkerId == query.WorkerId))
                .OrderByDescending(row => row.ObservedAt).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
            token.ThrowIfCancellationRequested();
            return MetricsAggregation.Project(rows, query, Coverage(), clock());
        }
    }
    private MetricObservation[] Visible() => slots.Values.Select(slot => slot.Winner)
        .Where(row => row.ObservedAt > clock().AddDays(-settings.Values.RetentionDays)).ToArray();
    private void ReadSettings()
    {
        if (!File.Exists(settingsPath)) return;
        GuardFile(settingsPath);
        if (new FileInfo(settingsPath).Length > 4096) throw new InvalidDataException();
        settings = JsonSerializer.Deserialize(File.ReadAllBytes(settingsPath), MetricsContract.TypeInfo<MetricsSettings>()) ?? throw new InvalidDataException();
        if (settings.Revision < 0) throw new InvalidDataException();
        MetricsValidation.Preferences(settings.Values);
    }
    private void Load()
    {
        GuardFile(historyPath);
        bytes = File.Exists(historyPath) ? new FileInfo(historyPath).Length : 0;
        if (legacyHistoryPath is not null)
        {
            GuardFile(legacyHistoryPath);
            legacyBytes = File.Exists(legacyHistoryPath) ? new FileInfo(legacyHistoryPath).Length : 0;
        }
        // One global physical read budget, including expired legacy bytes. Never
        // allocate, migrate or compact externally oversized/unknown evidence.
        if (legacyBytes > 268_435_456 || bytes > 268_435_456 - legacyBytes)
        { fault = "history-oversized"; hasGap = true; return; }
        if (legacyHistoryPath is not null && File.Exists(legacyHistoryPath))
        {
            legacyLease = new FileStream(legacyHistoryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            LoadStream(legacyLease, legacy: true);
        }
        if (fault is null && File.Exists(historyPath))
        {
            using var file = new FileStream(historyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            LoadStream(file, legacy: false);
        }
        if (fault is null && !LegacyBudgetAllows()) hasGap = true;
    }
    private void LoadStream(Stream file, bool legacy)
    {
        var line = new List<byte>(1024); bool overflow = false;
        int b;
        while ((b = file.ReadByte()) >= 0)
        {
            if (b != '\n') { if (line.Count < MetricsContract.MaximumRecordBytes) line.Add((byte)b); else overflow = true; continue; }
            if (overflow || line.Count == 0) { fault = "history-corrupt"; hasGap = true; }
            else
            {
                try
                {
                    var row = MetricsValidation.DeserializeObservation(line.ToArray());
                    if (row.SchemaVersion != (legacy ? 1 : journalVersion) || !MetricsValidation.Observation(row, clock()))
                        throw new InvalidDataException();
                    if (!slots.TryGetValue(row.Key, out var slot))
                    {
                        if (slots.Count >= MetricsContract.MaximumRecords)
                        { fault = "history-record-limit"; hasGap = true; return; }
                        slots.Add(row.Key, slot = new());
                    }
                    var previous = legacy ? slot.Legacy : slot.Writable;
                    var other = legacy ? slot.Writable : slot.Legacy;
                    if ((previous is not null && Conflicts(row, previous)) || (other is not null && Conflicts(row, other)))
                    { fault = "history-conflict"; hasGap = true; }
                    else if (previous is null || row.Revision > previous.Revision)
                    { if (legacy) slot.Legacy = row; else slot.Writable = row; }
                }
                catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException)
                { fault = "history-corrupt"; hasGap = true; }
            }
            line.Clear(); overflow = false;
        }
        if (line.Count != 0 || overflow) { fault = "history-truncated-tail"; hasGap = true; }
    }
    private static bool Conflicts(MetricObservation a, MetricObservation b) =>
        SampleTimeConflict(a, b) || a.Revision == b.Revision && !Equivalent(a, b);
    private void ReadCoverage()
    {
        if (legacyCoveragePath is not null && ReadCheckpoint(legacyCoveragePath, holdLegacy: true) is { } legacy)
        { legacyHasGap = legacy.HasGap; legacyDropped = legacy.SourceDropped; legacyLastSource = legacy.LastSource; }
        if (ReadCheckpoint(coveragePath) is { } value)
        { hasGap |= value.HasGap; dropped = value.SourceDropped; started = value.Started; lastSource = value.LastSource; }
        var writable = slots.Values.Where(slot => slot.Writable is not null).Select(slot => slot.Writable!).ToArray();
        if (writable.Length > 0) { hasGap = true; lastSource ??= writable.Max(row => row.ObservedAt); }
        var legacyRows = slots.Values.Where(slot => slot.Legacy is not null).Select(slot => slot.Legacy!).ToArray();
        if (legacyRows.Length > 0) { legacyHasGap = true; legacyLastSource ??= legacyRows.Max(row => row.ObservedAt); }
    }
    private MetricsCoverageCheckpoint? ReadCheckpoint(string path, bool holdLegacy = false)
    {
        GuardFile(path);
        if (!File.Exists(path)) return null;
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (input.Length > 4096) throw new InvalidDataException();
            var value = JsonSerializer.Deserialize(input, MetricsContract.TypeInfo<MetricsCoverageCheckpoint>()) ?? throw new InvalidDataException();
            if (value.SourceDropped is < 0 or > 9_000_000_000_000) throw new InvalidDataException();
            if (holdLegacy) { legacyCoverageLease = input; input = null!; }
            return value;
        }
        finally { input?.Dispose(); }
    }
    private void PersistCoverage()
    {
        if (fault is not null) return;
        try
        {
            GuardOwnedFiles(); AtomicWrite(coveragePath,
                JsonSerializer.SerializeToUtf8Bytes(new MetricsCoverageCheckpoint(hasGap, dropped, started, lastSource), MetricsContract.TypeInfo<MetricsCoverageCheckpoint>()), CancellationToken.None);
        }
        catch (Exception e) when (Ordinary(e)) { fault = "coverage-write-failed"; hasGap = true; }
    }
    private bool Rewrite(CancellationToken token, MetricObservation? pending = null)
    {
        var pendingBytes = pending is null ? 0 : JsonSerializer.SerializeToUtf8Bytes(pending, MetricsContract.TypeInfo<MetricObservation>()).Length + 1;
        if (!LegacyBudgetAllows(pendingBytes, pending?.Key)) { hasGap = true; return false; }
        var existing = slots.Values.Select(slot => slot.Writable).Where(row => row is not null &&
            row.ObservedAt > clock().AddDays(-settings.Values.RetentionDays) && row.Key != pending?.Key)
            .Select(row => row!).OrderByDescending(row => row.ObservedAt).ThenBy(row => row.Key, StringComparer.Ordinal);
        // A replacement and its eviction/compaction are ONE atomic image. Until
        // rename succeeds the previous accepted file remains; after it succeeds
        // the pending row is already durable, even if coverage persistence fails.
        var selected = pending is null ? existing : existing.Prepend(pending);
        var kept = new List<MetricObservation>();
        var keys = slots.Where(pair => pair.Value.Legacy is not null).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var temporary = Path.Combine(root, ".metrics-" + Guid.NewGuid().ToString("N") + ".tmp");
        long written = 0; bool created = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                foreach (var row in selected)
                {
                    token.ThrowIfCancellationRequested(); var data = JsonSerializer.SerializeToUtf8Bytes(row, MetricsContract.TypeInfo<MetricObservation>());
                    if (written + data.Length + 1 > settings.Values.MaximumBytes - legacyBytes ||
                        !keys.Contains(row.Key) && keys.Count >= MetricsContract.MaximumRecords)
                    {
                        if (ReferenceEquals(row, pending)) throw new IOException("The pending metrics record does not fit its admitted storage budget.");
                        hasGap = true; continue;
                    }
                    output.Write(data); output.WriteByte((byte)'\n'); written += data.Length + 1; kept.Add(row); keys.Add(row.Key);
                }
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested(); GuardOwnedFiles(); File.Move(temporary, historyPath, true); created = false;
        }
        finally { if (created) try { File.Delete(temporary); } catch (IOException) { } }
        foreach (var key in slots.Keys.ToArray())
        { slots[key].Writable = null; if (slots[key].Legacy is null) slots.Remove(key); }
        foreach (var row in kept)
        { if (!slots.TryGetValue(row.Key, out var slot)) slots.Add(row.Key, slot = new()); slot.Writable = row; }
        bytes = written; if (pending is not null) lastSource = clock();
        // Metadata failure is visible/read-only but cannot revoke a confirmed row.
        PersistCoverage(); return true;
    }
    private void AtomicWrite(string path, byte[] data, CancellationToken token)
    {
        var temporary = Path.Combine(root, ".metrics-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { created = true; file.Write(data); file.Flush(true); }
            token.ThrowIfCancellationRequested(); GuardFile(path); File.Move(temporary, path, true); created = false;
        }
        finally { if (created) try { File.Delete(temporary); } catch (IOException) { } }
    }
    private void GuardRoot()
    {
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked metrics storage is unsupported.");
    }
    private void GuardFile(string path)
    {
        GuardRoot(); if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked metrics storage is unsupported.");
    }
    private void GuardOwnedFiles()
    {
        if (ownership is null) throw new IOException();
        GuardFile(historyPath); GuardFile(settingsPath);
        if (legacyHistoryPath is not null)
        {
            GuardFile(legacyHistoryPath);
            if ((File.Exists(legacyHistoryPath) ? new FileInfo(legacyHistoryPath).Length : 0) != legacyBytes)
                throw new IOException("Legacy metrics history changed outside its owner.");
        }
        if ((File.Exists(historyPath) ? new FileInfo(historyPath).Length : 0) != bytes) throw new IOException("Metrics history changed outside its owner.");
    }
    private static bool Ordinary(Exception e) => e is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    private static bool SampleTimeConflict(MetricObservation a, MetricObservation b) =>
        a.Kind == MetricKind.Capacity && a.ObservedAt != b.ObservedAt;
    private static bool Equivalent(MetricObservation a, MetricObservation b) =>
        a.Kind is MetricKind.Workflow or MetricKind.Tool ? a with { SourceGeneration = b.SourceGeneration } == b : a == b;
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            Exception? failure = null;
            foreach (var handle in new[] { legacyLease, legacyCoverageLease, ownership })
            { try { handle?.Dispose(); } catch (Exception error) when (Ordinary(error)) { failure ??= error; } }
            legacyLease = legacyCoverageLease = ownership = null;
            if (failure is not null) throw new IOException("Metrics storage handles did not retire cleanly.", failure);
        }
    }
}
