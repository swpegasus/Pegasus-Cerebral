using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Resources;
using Cerebral.Core;
using Cerebral.Engine.Resources;

namespace Cerebral.Worker;

/// <summary>
/// Produces disposable, bounded archive derivatives beside durable SQLite originals. Extraction is a service-owned
/// typed PAO operation; no archive member is executed, promoted to a project, or inserted into model context here.
/// </summary>
internal sealed class AttachmentArtifacts : IDisposable
{
    private const string StateKind = "attachment-artifact";
    internal const int MaximumEntries = 512;
    internal const long MaximumExpandedBytes = 128L * 1024 * 1024;
    internal const long MaximumEntryBytes = 32L * 1024 * 1024;
    internal const int MaximumDepth = 12;
    internal const int MaximumTextBytes = 1024 * 1024;
    internal const long MaximumCacheBytes = 512L * 1024 * 1024;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly AttachmentStore attachments;
    private readonly StateStore state;
    private readonly string root;
    private readonly Func<JsonElement, string?, CancellationToken, Task<JsonElement>> pao;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim cacheGate = new(1, 1);
    private readonly long maximumCacheBytes;
    private readonly ITimer pruneTimer;
    private int disposed;

    public AttachmentArtifacts(AttachmentStore attachments, StateStore state, string root,
        Func<JsonElement, string?, CancellationToken, Task<JsonElement>> pao, TimeProvider? clock = null,
        long? cacheLimitBytes = null)
    {
        this.attachments = attachments;
        this.state = state;
        this.root = Path.GetFullPath(root);
        this.pao = pao;
        this.clock = clock ?? TimeProvider.System;
        maximumCacheBytes = cacheLimitBytes ?? MaximumCacheBytes;
        if (maximumCacheBytes is < 1 or > MaximumCacheBytes) throw new ArgumentOutOfRangeException(nameof(cacheLimitBytes));
        Directory.CreateDirectory(this.root);
        if ((File.GetAttributes(this.root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The derivative root cannot be a reparse point.");
        Prune();
        pruneTimer = this.clock.CreateTimer(_ => Prune(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    public async Task<AttachmentArchiveInspection> InspectAsync(AttachmentAccessContext access, string id, long revision,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var source = await MaterializeAsync(access, id, revision, cancellationToken).ConfigureAwait(false);
        try { return ParseInspection(await ExecuteAsync("7z_list", source.Path, null, cancellationToken).ConfigureAwait(false), source.Reference); }
        finally { DeleteFile(source.Path); }
    }

    public async Task<AttachmentArtifactReceipt> ExtractAsync(AttachmentAccessContext access, string id, long revision,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var key = Key(access, id, revision);
        var gate = gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prune();
            if (Current(key) is { State: "ready" } ready && Directory.Exists(ContentDirectory(id))) return ready;
            var source = await MaterializeAsync(access, id, revision, cancellationToken).ConfigureAwait(false);
            var staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
            try
            {
                var inspection = ParseInspection(
                    await ExecuteAsync("7z_list", source.Path, null, cancellationToken).ConfigureAwait(false), source.Reference);
                ValidateInspection(inspection);
                _ = await ExecuteAsync("7z_test", source.Path, null, cancellationToken).ConfigureAwait(false);
                await cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    PruneCore();
                    RequireRoom(inspection.ExpandedBytes);
                    Directory.CreateDirectory(staging);
                    _ = await ExecuteAsync("7z_extract", source.Path, staging, cancellationToken).ConfigureAwait(false);
                    VerifyExtracted(staging, inspection.Entries);

                    // One attachment ID owns one physical derivative directory. Retire every
                    // prior receipt before replacing it, and never bury failed physical cleanup.
                    foreach (var prior in state.List<AttachmentArtifactReceipt>(StateKind)
                        .Where(value => value.AttachmentId == id && value.State == "ready"))
                        Expire(prior, clock.GetUtcNow());
                    var destination = AttachmentDirectory(id);
                    if (!TryDeleteTree(destination, out var cleanupError))
                        throw new IOException(cleanupError);
                    foreach (var cleared in state.List<AttachmentArtifactReceipt>(StateKind)
                        .Where(value => value.AttachmentId == id && value.State == "expired" && value.Error is not null))
                        state.Put(StateKind, Key(new(cleared.ProfileId, cleared.ConversationId), id,
                            cleared.AttachmentRevision), cleared with { Entries = [], ExpandedBytes = 0, Error = null });
                    Directory.CreateDirectory(destination);
                    Directory.Move(staging, ContentDirectory(id));
                    var now = clock.GetUtcNow();
                    var receipt = new AttachmentArtifactReceipt(id, revision, access.ProfileId, access.ConversationId,
                        "ready", inspection.Entries, inspection.ExpandedBytes, now, now + Retention);
                    state.Put(StateKind, key, receipt);
                    PruneCore();
                    var retained = Current(key);
                    if (retained is not { State: "ready" })
                        throw new InvalidOperationException("The derivative could not remain within the bounded cache.");
                    return retained;
                }
                finally { cacheGate.Release(); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var stagingRemoved = TryDeleteTree(staging, out var stagingError);
                var now = clock.GetUtcNow();
                var pending = Current(key);
                if (pending is { State: "expired", Error: not null })
                    state.Put(StateKind, key, pending with
                    {
                        Error = Bounded(pending.Error + " Extraction also failed: " + ex.GetType().Name + "." +
                            (stagingRemoved ? "" : " " + stagingError))
                    });
                else
                    state.Put(StateKind, key, new AttachmentArtifactReceipt(id, revision, access.ProfileId,
                        access.ConversationId, "failed", [], 0, now, now + Retention,
                        Bounded(ex.Message + (stagingRemoved ? "" : " " + stagingError))));
                throw;
            }
            finally { DeleteFile(source.Path); _ = TryDeleteTree(staging, out _); }
        }
        finally { gate.Release(); }
    }

    public AttachmentArtifactReceipt Status(AttachmentAccessContext access, string id, long revision)
    {
        ThrowIfDisposed();
        _ = attachments.Describe(access, id, revision);
        Prune();
        return Current(Key(access, id, revision)) ?? new(id, revision, access.ProfileId, access.ConversationId,
            "not-extracted", [], 0, clock.GetUtcNow(), clock.GetUtcNow());
    }

    public async Task<AttachmentArtifactText> ReadTextAsync(AttachmentAccessContext access, string id, long revision,
        string entryPath, int maximumUtf8Bytes, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (maximumUtf8Bytes is < 0 or > MaximumTextBytes) throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
        var receipt = Status(access, id, revision);
        if (receipt.State != "ready") throw new InvalidOperationException("The archive derivative is not ready. Extract or refresh it first.");
        var canonical = ValidateEntryPath(entryPath);
        var retained = receipt.Entries.SingleOrDefault(x => !x.Directory && x.Path == canonical)
            ?? throw new KeyNotFoundException("The selected archive entry is unavailable in this derivative.");
        if (retained.Length > maximumUtf8Bytes)
            throw new InvalidOperationException($"Complete archive text needs {retained.Length} bytes; increase the limit or choose a smaller entry.");
        var path = ContainedPath(ContentDirectory(id), canonical);
        if (!File.Exists(path) || new FileInfo(path).Length != retained.Length)
            throw new InvalidDataException("The archive derivative changed or expired. Extract it again.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("The selected archive entry is not valid UTF-8 text.", ex); }
            if (text.Contains('\0')) throw new InvalidDataException("The selected archive entry contains NUL bytes and is not admitted as text.");
            return new(id, revision, canonical, text);
        }
        finally { Array.Clear(bytes); }
    }

    public async Task<IReadOnlyList<AttachmentArtifactText>> ResolveModelTextAsync(AttachmentAccessContext access,
        AttachmentReference reference, int maximumUtf8Bytes, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (maximumUtf8Bytes is < 0 or > MaximumTextBytes) throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
        var receipt = await ExtractAsync(access, reference.Id, reference.Revision, cancellationToken).ConfigureAwait(false);
        var remaining = maximumUtf8Bytes;
        var result = new List<AttachmentArtifactText>();
        foreach (var entry in receipt.Entries.Where(x => !x.Directory && IsTextCandidate(x.Path)).Take(32))
        {
            if (entry.Length > remaining) break;
            try
            {
                var text = await ReadTextAsync(access, reference.Id, reference.Revision, entry.Path, remaining, cancellationToken)
                    .ConfigureAwait(false);
                result.Add(text);
                remaining -= checked((int)entry.Length);
            }
            catch (InvalidDataException) { /* A text-like extension with binary content is simply not projected. */ }
        }
        return result;
    }

    private async Task<(AttachmentReference Reference, string Path)> MaterializeAsync(AttachmentAccessContext access,
        string id, long revision, CancellationToken cancellationToken)
    {
        var reference = attachments.Describe(access, id, revision, cancellationToken);
        if (reference.Metadata is null || !reference.Metadata.ContainsKey("handler.archive"))
            throw new NotSupportedException("This immutable original is not assigned to the archive handler.");
        var sourceDirectory = Path.Combine(root, ".source"); Directory.CreateDirectory(sourceDirectory);
        var path = Path.Combine(sourceDirectory, Guid.NewGuid().ToString("N") + Path.GetExtension(reference.OriginalName));
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                AttachmentStore.MaximumRangeBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            for (long offset = 0; offset < reference.Length;)
            {
                var count = (int)Math.Min(AttachmentStore.MaximumRangeBytes, reference.Length - offset);
                var range = attachments.ReadRange(access, id, revision, offset, count, cancellationToken);
                await output.WriteAsync(range.Bytes, cancellationToken).ConfigureAwait(false);
                offset += count;
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return (reference, path);
        }
        catch { DeleteFile(path); throw; }
    }

    private async Task<JsonElement> ExecuteAsync(string operation, string source, string? destination,
        CancellationToken cancellationToken)
    {
        var arguments = destination is null
            ? CerebralJson.Element(new { op = operation, src = source, detail = true })
            : CerebralJson.Element(new { op = operation, src = source, dst = destination, pol = "clean", detail = true });
        var receipt = await pao(arguments, root, cancellationToken).ConfigureAwait(false);
        if (!receipt.TryGetProperty("ok", out var ok) || !ok.GetBoolean() ||
            !receipt.TryGetProperty("state", out var stateValue) || stateValue.GetString() != "done")
        {
            var message = receipt.TryGetProperty("err", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString() : "Cortex did not confirm the archive operation.";
            throw new InvalidOperationException(message);
        }
        return receipt;
    }

    private static AttachmentArchiveInspection ParseInspection(JsonElement receipt, AttachmentReference reference)
    {
        if (!receipt.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("entries", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Cortex returned no structured archive listing.");
        var entries = new List<AttachmentArchiveEntry>();
        foreach (var item in values.EnumerateArray())
        {
            var path = ValidateEntryPath(item.GetProperty("path").GetString() ?? "");
            var directory = item.TryGetProperty("directory", out var isDirectory) && isDirectory.GetBoolean();
            var length = item.TryGetProperty("size", out var size) ? size.GetInt64() : 0;
            entries.Add(new(path, length, directory));
        }
        var expanded = data.TryGetProperty("expanded_bytes", out var total) ? total.GetInt64() : entries.Sum(x => x.Length);
        var truncated = data.TryGetProperty("truncated", out var truncation) && truncation.GetBoolean();
        var count = data.TryGetProperty("count", out var countValue) ? countValue.GetInt32() : entries.Count;
        if (count != entries.Count) truncated = true;
        return new(reference, entries, expanded, truncated);
    }

    private static void ValidateInspection(AttachmentArchiveInspection inspection)
    {
        if (inspection.Truncated || inspection.Entries.Count > MaximumEntries)
            throw new InvalidDataException($"Archive contains more than the {MaximumEntries}-entry inspection limit.");
        if (inspection.ExpandedBytes < 0 || inspection.ExpandedBytes > MaximumExpandedBytes)
            throw new InvalidDataException($"Archive expands beyond the {MaximumExpandedBytes}-byte derivative limit.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long sum = 0;
        foreach (var entry in inspection.Entries)
        {
            _ = ValidateEntryPath(entry.Path);
            if (!seen.Add(entry.Path)) throw new InvalidDataException("Archive contains colliding member paths.");
            if (entry.Length < 0 || entry.Length > MaximumEntryBytes)
                throw new InvalidDataException($"Archive member exceeds the {MaximumEntryBytes}-byte entry limit.");
            if (!entry.Directory) sum = checked(sum + entry.Length);
        }
        if (sum != inspection.ExpandedBytes) throw new InvalidDataException("Archive expanded byte count does not match its bounded listing.");
    }

    private static void VerifyExtracted(string directory, IReadOnlyList<AttachmentArchiveEntry> entries)
    {
        var expected = entries.Where(x => !x.Directory).ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
        if (files.Length != expected.Count) throw new InvalidDataException("Extracted file count does not match the admitted archive listing.");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Archive extraction produced a link or reparse point.");
            _ = ContainedPath(directory, Path.GetRelativePath(directory, path));
        }
        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (!expected.TryGetValue(relative, out var entry) || new FileInfo(path).Length != entry.Length)
                throw new InvalidDataException("Extracted member does not match the admitted archive listing.");
        }
    }

    private void Prune()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        cacheGate.Wait();
        try { if (Volatile.Read(ref disposed) == 0) PruneCore(); }
        finally { cacheGate.Release(); }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(AttachmentArtifacts));
    }

    private void PruneCore()
    {
        var now = clock.GetUtcNow();
        var receipts = state.List<AttachmentArtifactReceipt>(StateKind).OrderBy(x => x.CreatedAt).ToList();
        foreach (var receipt in receipts)
        {
            if (receipt.State == "expired" && receipt.Error is not null ||
                receipt.State == "ready" && (receipt.ExpiresAt <= now ||
                    !TryTreeBytes(ContentDirectory(receipt.AttachmentId), out var bytes) || bytes == 0 &&
                    !Directory.Exists(ContentDirectory(receipt.AttachmentId))))
                Expire(receipt, now);
        }
        foreach (var staging in Directory.EnumerateDirectories(root, ".staging-*", SearchOption.TopDirectoryOnly))
            if (Directory.GetCreationTimeUtc(staging) < now.UtcDateTime.AddHours(-1)) _ = TryDeleteTree(staging, out _);
        var sources = Path.Combine(root, ".source");
        if (Directory.Exists(sources)) foreach (var source in Directory.EnumerateFiles(sources))
            if (File.GetCreationTimeUtc(source) < now.UtcDateTime.AddHours(-1)) DeleteFile(source);
        _ = TryMakeRoom(0);
    }

    private void Expire(AttachmentArtifactReceipt receipt, DateTimeOffset now)
    {
        var deleted = TryDeleteTree(AttachmentDirectory(receipt.AttachmentId), out var cleanupError);
        state.Put(StateKind, Key(new(receipt.ProfileId, receipt.ConversationId), receipt.AttachmentId, receipt.AttachmentRevision),
            receipt with { State = "expired", Entries = [], ExpandedBytes = deleted ? 0 : receipt.ExpandedBytes,
                ExpiresAt = now, Error = deleted ? null : receipt.Error ?? cleanupError });
    }

    private void RequireRoom(long incoming)
    {
        if (incoming < 0 || incoming > maximumCacheBytes || !TryMakeRoom(incoming))
            throw new InvalidOperationException("The derivative cache is full or cleanup is pending. Release locked derivatives and retry.");
    }

    // Caller holds cacheGate. Count the physical derivative root, including failed staging and
    // temporary source copies, before and after each attempted eviction. An unreadable or linked
    // path cannot be treated as free space.
    private bool TryMakeRoom(long incoming)
    {
        if (!TryTreeBytes(root, out var total)) return false;
        if (total <= maximumCacheBytes - incoming) return true;
        foreach (var receipt in state.List<AttachmentArtifactReceipt>(StateKind)
            .Where(value => value.State == "ready").OrderBy(value => value.CreatedAt))
        {
            Expire(receipt, clock.GetUtcNow());
            if (!TryTreeBytes(root, out total)) return false;
            if (total <= maximumCacheBytes - incoming) return true;
        }
        return false;
    }

    private AttachmentArtifactReceipt? Current(string key) => state.Get<AttachmentArtifactReceipt>(StateKind, key);
    private string AttachmentDirectory(string id) => Path.Combine(root, id);
    private string ContentDirectory(string id) => Path.Combine(AttachmentDirectory(id), "contents");
    private static string Key(AttachmentAccessContext access, string id, long revision) =>
        access.ProfileId + "|" + access.ConversationId + "|" + id + "|" + revision;
    private static string Bounded(string message) => message.Length <= 512 ? message : message[..512];

    private static string ValidateEntryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.StartsWith('/') || value.StartsWith('\\') || value.Contains(':'))
            throw new InvalidDataException("Archive contains an unsafe member path.");
        var segments = value.Replace('\\', '/').Split('/');
        if (segments.Length > MaximumDepth || segments.Any(x => x.Length == 0 || x is "." or ".." ||
            x.EndsWith(' ') || x.EndsWith('.') || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Reserved(x)))
            throw new InvalidDataException("Archive contains an unsafe member path.");
        return string.Join('/', segments);
    }

    private static bool Reserved(string segment)
    {
        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(stem, "^(COM|LPT)[1-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool IsTextCandidate(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".txt" or ".md" or ".markdown" or ".csv" or ".tsv" or ".log" or ".json" or ".xml" or
        ".yaml" or ".yml" or ".cs" or ".py" or ".js" or ".ts" or ".css" or ".sql" or ".html" or
        ".htm" or ".xaml" or ".ini" or ".toml" or ".properties" or ".java" or ".cpp" or ".c" or
        ".h" or ".hpp" or ".rs" or ".go" or ".swift" or ".kt";

    private static string ContainedPath(string directory, string relative)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive member escaped its derivative directory.");
        return path;
    }

    private static bool TryTreeBytes(string directory, out long bytes)
    {
        try { bytes = TreeBytes(directory); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            OverflowException or System.Security.SecurityException)
        { bytes = long.MaxValue; return false; }
    }

    private static long TreeBytes(string directory, Func<string, FileAttributes>? readAttributes = null)
    {
        readAttributes ??= File.GetAttributes;
        try
        {
            if ((readAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A derivative contains a link or reparse point.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return 0; }
        long total = 0;
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out var current))
            foreach (var entry in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = readAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A derivative contains a link or reparse point.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else total = checked(total + new FileInfo(entry).Length);
            }
        return total;
    }

    private static void DeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    internal static bool TryDeleteTree(string path, out string? error,
        Func<string, FileAttributes>? readAttributes = null)
    {
        try
        {
            _ = TreeBytes(path, readAttributes); // Refuse any reparse point before recursive deletion.
            try { _ = File.GetAttributes(path); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { error = null; return true; }
            Directory.Delete(path, true);
            try { _ = File.GetAttributes(path); throw new IOException("Derivative deletion was not confirmed."); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { error = null; return true; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Security.SecurityException)
        {
            error = ex is InvalidDataException ? "Derivative cleanup pending: link or reparse point detected." :
                ex is UnauthorizedAccessException or System.Security.SecurityException ? "Derivative cleanup pending: access denied." :
                "Derivative cleanup pending: file in use or I/O failure.";
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        pruneTimer.Dispose();
        // A timer callback may already be waiting for cacheGate, and an accepted request may
        // still hold either gate. Keeping these managed semaphores alive lets those operations
        // release normally; the object and its gates are reclaimed when their users finish.
    }
}
