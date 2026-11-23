using System.Globalization;
using System.Text.Json;

namespace Cerebral.PersonalityServicePlugin;

/// <summary>Bounded durable source store. Retained versions are never implicitly evicted.</summary>
internal sealed class PersonalityStore
{
    internal const string FileName = "personality-context.json";
    internal const int MaximumFileBytes = 96 * 1024;
    private readonly object gate = new();
    private readonly string directory;
    private readonly string path;
    private readonly string lockPath;
    internal Func<(int Ids, int Versions)>? PrivateRetention { get; set; }

    internal PersonalityStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("An absolute plugin data directory is required.");
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        GuardPath(directory);
        Directory.CreateDirectory(directory);
        GuardPath(directory);
        path = Path.Combine(directory, FileName);
        lockPath = Path.Combine(directory, "personality-context.lock");
        using var lease = Acquire(create: true);
        _ = Read();
    }

    internal PersonalityDocumentSnapshot Load()
    {
        lock (gate)
        {
            using var lease = Acquire();
            var result = Read();
            var counts = PrivateRetention?.Invoke() ?? (0, 0);
            return result with { PrivateIds = counts.Item1, PrivateVersions = counts.Item2 };
        }
    }

    internal PersonalityDocumentSnapshot Select(long expectedRevision, string expectedSha256,
        bool enabled, string presetId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            using var lease = Acquire();
            var current = Read();
            Compare(current, expectedRevision, expectedSha256);
            if (!PersonalityContract.ValidId(presetId)) throw new ArgumentException("Choose a valid personality preset ID.");
            _ = current.Latest(presetId);
            if (current.Document.Enabled == enabled && current.Document.SelectedPresetId == presetId) return current;
            var next = current.Document with
            {
                Revision = checked(current.Document.Revision + 1), Enabled = enabled, SelectedPresetId = presetId
            };
            return Publish(next, cancellationToken);
        }
    }

    internal (PersonalityDocumentSnapshot Settings, PersonalityPreset Preset) Save(long expectedRevision,
        string expectedSha256, string id, string name, string text, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            using var lease = Acquire();
            var current = Read();
            Compare(current, expectedRevision, expectedSha256);
            if (!PersonalityContract.ValidId(id)) throw new ArgumentException("Use a bounded lowercase personality preset ID.");
            if (PersonalityContract.BuiltIns.Any(preset => preset.Id == id))
                throw new ArgumentException("Built-in presets are immutable. Save a custom preset under another ID.");
            var previous = current.Document.Versions.Where(preset => preset.Id == id).MaxBy(preset => preset.Revision);
            // Retried owner mutations must reload after an uncertain response. A repeated exact save against
            // the current CAS is a no-op; old CAS tokens still fail rather than silently overwrite newer work.
            if (previous is not null && previous.Name == name && previous.Text == text) return (current, previous);
            var privateRetention = PrivateRetention?.Invoke() ?? (0, 0);
            if (current.Document.Versions.Length + privateRetention.Item2 >= PersonalityContract.MaximumRetainedVersions)
                throw new InvalidOperationException("Personality retained-version limit reached (32). No source was removed. Existing prepared styles remain available; adding a revision requires a separately supported history migration.");
            if (previous is null && current.Document.Versions.Select(preset => preset.Id).Distinct(StringComparer.Ordinal).Count() + privateRetention.Item1 >= PersonalityContract.MaximumCustomPresets)
                throw new InvalidOperationException("Personality custom-preset limit reached (8). Existing presets and accepted-work sources were preserved.");
            var preset = PersonalityContract.Preset(id, name, checked((previous?.Revision ?? 0) + 1), text);
            PersonalityContract.ValidatePreset(preset);
            var next = current.Document with
            {
                Revision = checked(current.Document.Revision + 1), Versions = [.. current.Document.Versions, preset]
            };
            return (Publish(next, cancellationToken), preset);
        }
    }

    internal PersonalityPreset Resolve(string sourceId, long? expectedRevision)
    {
        if (sourceId is null || sourceId.Length > 256) throw new ArgumentException("An exact personality source is required.");
        var parts = sourceId.Split('/');
        if (parts.Length != 5 || parts[0] != "personality" || parts[1] != "v1" ||
            !PersonalityContract.ValidId(parts[2]) ||
            !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 1 ||
            expectedRevision != revision || parts[4].Length != 64 ||
            parts[4].Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("An exact canonical personality revision and hash are required.");
        lock (gate)
        {
            using var lease = Acquire();
            var source = Read().AllVersions.SingleOrDefault(preset => preset.Id == parts[2] && preset.Revision == revision);
            if (source is null || PersonalityContract.SourceId(source) != sourceId)
                throw new InvalidDataException("The exact personality source revision or hash is unavailable.");
            // Current selection is deliberately not used: already accepted turns retain their exact source.
            return source;
        }
    }

    internal T WithRetention<T>(Func<PersonalityDocumentSnapshot, T> action)
    {
        lock (gate) { using var lease = Acquire(); return action(Read()); }
    }

    private PersonalityDocumentSnapshot Read()
    {
        GuardPath(directory);
        GuardPath(path);
        if (Directory.Exists(path)) throw new IOException("Personality context storage must be a regular file.");
        if (!File.Exists(path))
            return Snapshot(new(PersonalityContract.SchemaVersion, 0, false, "cerebral", []));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("Personality context storage exceeds its 96 KiB bound.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Personality context storage changed while being read.");
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        RejectDuplicateProperties(parsed.RootElement);
        RequireFields(parsed.RootElement, ["schemaVersion", "revision", "enabled", "selectedPresetId", "versions"]);
        var versions = parsed.RootElement.GetProperty("versions");
        if (versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() > PersonalityContract.MaximumRetainedVersions)
            throw new InvalidDataException("Personality retained versions must be a bounded array.");
        foreach (var version in versions.EnumerateArray())
            RequireFields(version, ["id", "name", "revision", "text", "sha256"]);
        var document = parsed.RootElement.Deserialize<PersonalityDocument>(PersonalityContract.Json)
            ?? throw new InvalidDataException("Personality context storage is invalid.");
        Validate(document);
        return Snapshot(document);
    }

    private PersonalityDocumentSnapshot Publish(PersonalityDocument document, CancellationToken cancellationToken)
    {
        Validate(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, PersonalityContract.Json);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Personality context storage exceeds its 96 KiB bound; existing sources were preserved.");
        cancellationToken.ThrowIfCancellationRequested();
        GuardPath(directory); GuardPath(path);
        var temporary = Path.Combine(directory, "." + FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            GuardPath(directory); GuardPath(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return new(document, PersonalityContract.Digest(bytes));
    }

    private FileStream Acquire(bool create = false)
    {
        GuardPath(directory); GuardPath(lockPath);
        // FileShare.None serializes cooperating package instances across load contexts and processes.
        // Busy storage is an explicit retryable IO failure, never a bypass of the CAS.
        // Reads never repair a missing sidecar or materialize state during host capture/provider resolution.
        return new FileStream(lockPath, create ? FileMode.OpenOrCreate : FileMode.Open,
            create ? FileAccess.ReadWrite : FileAccess.Read, FileShare.None);
    }

    private static void Compare(PersonalityDocumentSnapshot current, long revision, string sha256)
    {
        if (revision < 0 || sha256 is null || sha256.Length != 64 ||
            sha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Reload personality settings and supply their exact revision and lowercase SHA-256.");
        if (current.Document.Revision != revision || current.Sha256 != sha256)
            throw new InvalidOperationException("Personality settings changed. Reload before saving; current settings and sources were preserved.");
    }

    private static PersonalityDocumentSnapshot Snapshot(PersonalityDocument document) =>
        new(document, PersonalityContract.Digest(JsonSerializer.SerializeToUtf8Bytes(document, PersonalityContract.Json)));

    private static void Validate(PersonalityDocument document)
    {
        if (document.SchemaVersion != PersonalityContract.SchemaVersion || document.Revision < 0 ||
            !PersonalityContract.ValidId(document.SelectedPresetId) || document.Versions is null ||
            document.Versions.Length > PersonalityContract.MaximumRetainedVersions)
            throw new InvalidDataException("Personality context storage has an unsupported schema or invalid bounds.");
        var builtInIds = PersonalityContract.BuiltIns.Select(preset => preset.Id).ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<(string, long)>();
        foreach (var preset in document.Versions)
        {
            PersonalityContract.ValidatePreset(preset);
            if (builtInIds.Contains(preset.Id) || !ids.Add((preset.Id, preset.Revision)))
                throw new InvalidDataException("Personality source identities must be unique and cannot replace built-in presets.");
        }
        var groups = document.Versions.GroupBy(preset => preset.Id).ToArray();
        if (groups.Length > PersonalityContract.MaximumCustomPresets ||
            groups.Any(group => !group.OrderBy(preset => preset.Revision).Select(preset => preset.Revision)
                .SequenceEqual(Enumerable.Range(1, group.Count()).Select(revision => (long)revision))) ||
            (!builtInIds.Contains(document.SelectedPresetId) && !groups.Any(group => group.Key == document.SelectedPresetId)))
            throw new InvalidDataException("Personality retained source history or selection is invalid.");
    }

    private static void RequireFields(JsonElement value, string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(names))
            throw new InvalidDataException("Personality context storage fields do not match its schema.");
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Personality context JSON contains duplicate fields.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void GuardPath(string value)
    {
        for (var current = value; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Personality storage cannot traverse filesystem links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
