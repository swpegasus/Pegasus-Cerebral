using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.SkillsPlugin;

internal sealed record SkillFolderEntry(string Key, string Origin, string Directory, SkillDocument? Document,
    string? Error, string? RetainedId = null, bool Incomplete = false)
{
    public string Id => Document?.Manifest.Id ?? RetainedId ?? "";
    public string Name => Document?.Manifest.Name ?? (RetainedId ?? Path.GetFileName(Directory));
    public string Version => Document?.Manifest.Version ?? "";
}
internal sealed record SkillScan(DateTimeOffset At, IReadOnlyList<SkillFolderEntry> Entries, bool Complete, string? Error);

internal sealed class OwnedSkill
{
    public OwnedSkill() { }
    public string SourceKey { get; set; } = "";
    public int Revision { get; set; }
    public string DocumentHash { get; set; } = "";
    public string CatalogHash { get; set; } = "";
    public SkillManifest? Identity { get; set; }
    public Dictionary<string, string> ManagedFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Pending { get; set; }
    public string? PendingKind { get; set; }
    public string? PendingHash { get; set; }
    public string? PendingSourceKey { get; set; }
    public string? PendingDocumentHash { get; set; }
    public SkillManifest? PendingIdentity { get; set; }
    public Dictionary<string, string>? PendingFiles { get; set; }
    public bool? PendingEnabled { get; set; }
    public List<SkillBinding>? PendingBindings { get; set; }
    public string? Diagnostic { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
internal sealed class SkillsIndex
{
    public SkillsIndex() { }
    public int SchemaVersion { get; set; } = 1;
    public string PluginId { get; set; } = SkillsPlugin.PackageId;
    public string? OwnerInstallationId { get; set; }
    public Dictionary<string, string> Exclusions { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SuppressedIds { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, SkillManifest> SourceIdentities { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, OwnedSkill> Skills { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string[]> Groups { get; set; } = new(StringComparer.Ordinal);
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    [JsonIgnore] internal string? ReadHash { get; set; }
}

internal sealed class SkillFolderCatalog
{
    internal string DataDirectory { get; }
    internal string EditableRoot { get; }
    internal string BundledRoot { get; }
    internal string IndexPath => SkillFiles.Resolve(DataDirectory, "skills-index.v1.json");
    internal const string SourcePrefix = "pegasus.skills:";
    private const int MaximumIndexBytes = 1024 * 1024;

    internal SkillFolderCatalog(string packageDirectory, string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        EditableRoot = SkillFiles.Resolve(DataDirectory, "Skills");
        BundledRoot = SkillFiles.Resolve(Path.GetFullPath(packageDirectory), "Skills");
    }

    internal void EnsureEditableRoot()
    {
        ScopedFilePolicy.RejectLinks(EditableRoot);
        Directory.CreateDirectory(EditableRoot);
        ScopedFilePolicy.RejectLinks(EditableRoot);
    }

    internal IDisposable AcquireWriter()
    {
        EnsureEditableRoot();
        var path = SkillFiles.Resolve(DataDirectory, ".skills-manager.lock");
        var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (lease.Length != 0) throw new InvalidDataException("The manager lock file is not empty; it was preserved.");
            ScopedFilePolicy.RejectLinks(path);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal SkillsIndex ReadIndex()
    {
        if (!File.Exists(IndexPath)) return new();
        var bytes = SkillFiles.Read(IndexPath, MaximumIndexBytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        RequireUniqueProperties(document.RootElement);
        var index = JsonSerializer.Deserialize<SkillsIndex>(bytes, CerebralJson.Options)
            ?? throw new InvalidDataException("The skills index is empty.");
        if (index.SchemaVersion != 1 || index.PluginId != SkillsPlugin.PackageId || index.Skills is null ||
            index.Groups is null || index.Exclusions is null || index.SuppressedIds is null || index.SourceIdentities is null ||
            index.Exclusions.Count > 256 || index.SuppressedIds.Count > 256 || index.SourceIdentities.Count > 256 ||
            index.Skills.Count > SkillDocument.MaximumSkills || index.Groups.Count > 64 || index.OwnerInstallationId?.Length > 512)
            throw new InvalidDataException("The skills ownership index has an unsupported identity or size; it was preserved.");
        foreach (var (id, value) in index.Skills)
        {
            SkillFiles.ValidateId(id);
            if (value is null || value.SourceKey is null || value.ManagedFiles is null || value.ManagedFiles.Count > 34 ||
                value.Revision < 0) throw new InvalidDataException("An owned skill index row is invalid.");
            _ = DirectoryFor(value.SourceKey);
            foreach (var file in value.ManagedFiles)
            {
                _ = SkillFiles.Relative(file.Key);
                RequireHash(file.Value);
            }
            if (value.Revision > 0) { RequireHash(value.CatalogHash); RequireHash(value.DocumentHash); }
            if (value.Identity is { } identity) SkillFiles.ValidateId(identity.Id);
        }
        foreach (var key in index.SuppressedIds.Keys)
        {
            var pieces = key.Split(':', 2);
            if (pieces.Length != 2 || pieces[0] is not ("bundled" or "data")) throw new InvalidDataException("Invalid stable suppression origin.");
            SkillFiles.ValidateId(pieces[1]);
        }
        foreach (var identity in index.SourceIdentities)
        {
            _ = DirectoryFor(identity.Key);
            SkillFiles.ValidateId(identity.Value.Id);
        }
        foreach (var exclusion in index.Exclusions)
        {
            _ = DirectoryFor(exclusion.Key);
            if (exclusion.Value is null || exclusion.Value.Length > 400) throw new InvalidDataException("Invalid discovery exclusion.");
        }
        if (index.Skills.Values.Select(x => x.SourceKey).Distinct(StringComparer.Ordinal).Count() != index.Skills.Count)
            throw new InvalidDataException("Two catalog IDs cannot claim one source folder.");
        foreach (var (group, ids) in index.Groups)
        {
            ValidateGroupName(group);
            if (ids is null || ids.Length > SkillDocument.MaximumSkills || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                throw new InvalidDataException("A saved group is invalid.");
            foreach (var id in ids) SkillFiles.ValidateId(id);
        }
        index.ReadHash = SkillFiles.Hash(bytes);
        return index;
    }

    internal void SaveIndex(SkillsIndex index)
    {
        if (index.Skills.Count > 128 || index.Groups.Count > 64 || index.Exclusions.Count > 256 ||
            index.SuppressedIds.Count > 256 || index.SourceIdentities.Count > 256)
            throw new InvalidDataException("The private index reached its bound; existing owner exclusions were preserved.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(index, CerebralJson.Options);
        if (bytes.Length > MaximumIndexBytes) throw new InvalidDataException("The skills index exceeds 1 MiB.");
        var path = IndexPath;
        var temporary = "skills-index-" + Guid.NewGuid().ToString("N") + ".tmp";
        var temporaryPath = SkillFiles.Resolve(DataDirectory, temporary);
        SkillFiles.WriteNew(DataDirectory, temporary, bytes);
        try
        {
            // Both desktop hosts share the writer lease. This also detects an unrelated editor's change.
            var current = File.Exists(path) ? SkillFiles.FileHash(path, MaximumIndexBytes) : null;
            if (current != index.ReadHash) throw new IOException("The skills index changed. Refresh before another operation.");
            ScopedFilePolicy.RejectLinks(path);
            File.Move(temporaryPath, path, overwrite: current is not null);
            index.ReadHash = SkillFiles.Hash(bytes);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    internal SkillScan Scan(SkillsIndex index, CancellationToken token)
    {
        var rows = new List<SkillFolderEntry>();
        var complete = true;
        var errors = new List<string>();
        var visited = 0;
        long assetBytes = 0;
        var stopScan = false;
        foreach (var (origin, root) in new[] { ("bundled", BundledRoot), ("data", EditableRoot) })
        {
            if (stopScan) break;
            if (!Directory.Exists(root)) continue;
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                var (directory, depth) = pending.Pop();
                try
                {
                    ScopedFilePolicy.RejectLinks(directory);
                    if (++visited > 256 || depth > 8) throw new InvalidDataException("Folder scan exceeds 256 directories or eight levels.");
                    var children = Directory.EnumerateFileSystemEntries(directory).Take(1025).ToArray();
                    if (children.Length > 1024) throw new InvalidDataException("A skill directory exceeds 1024 entries.");
                    Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                    if (depth > 0 && File.Exists(Path.Combine(directory, "SKILL.md")))
                    {
                        if (rows.Count >= SkillDocument.MaximumSkills) throw new InvalidDataException("At most 128 skill folders may be indexed.");
                        var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
                        _ = SkillFiles.Relative(relative);
                        var key = origin + "/" + relative;
                        var identity = index.SourceIdentities.GetValueOrDefault(key) ??
                            index.Skills.Values.SingleOrDefault(x => x.SourceKey == key)?.Identity;
                        try
                        {
                            var doc = SkillDocument.Read(directory, key, identity, relativeAsset =>
                            {
                                var remaining = 32L * 1024 * 1024 - assetBytes;
                                if (remaining <= 0) { stopScan = true; throw new InvalidDataException("Aggregate asset scan budget reached 32 MiB."); }
                                var assetPath = SkillFiles.Resolve(directory, relativeAsset);
                                var declared = new FileInfo(assetPath).Length;
                                if (declared > SkillDocument.MaximumAssetBytes) throw new InvalidDataException("Asset exceeds 1 MiB.");
                                if (declared > remaining) { stopScan = true; throw new InvalidDataException("Aggregate asset scan budget would exceed 32 MiB."); }
                                var bytes = SkillFiles.Read(assetPath,
                                    checked((int)Math.Min(SkillDocument.MaximumAssetBytes, remaining)));
                                assetBytes = checked(assetBytes + bytes.LongLength); // Charge failed documents too.
                                return bytes;
                            });
                            var retained = index.Skills.FirstOrDefault(x => x.Value.SourceKey == key).Key;
                            rows.Add(retained is not null && retained != doc.Manifest.Id
                                ? new(key, origin, directory, null, "The source ID changed. Uninstall the original before explicitly installing the new ID.", retained)
                                : new(key, origin, directory, doc, null));
                        }
                        catch (Exception error) when (FileFailure(error))
                        { rows.Add(new(key, origin, directory, null, Bound(error.Message), index.Skills.FirstOrDefault(x => x.Value.SourceKey == key).Key, Incomplete: stopScan)); }
                        if (stopScan || assetBytes >= 32L * 1024 * 1024)
                        {
                            complete = false; stopScan = true;
                            errors.Add("Asset scan stopped at its aggregate 32 MiB budget; remaining sources were not inferred missing.");
                            pending.Clear(); break;
                        }
                        continue; // A skill's support directories are assets, not nested skill installations.
                    }
                    foreach (var child in children.Reverse())
                    {
                        ScopedFilePolicy.RejectLinks(child);
                        if (Directory.Exists(child) && !Path.GetFileName(child).StartsWith('.'))
                        {
                            if (pending.Count + visited >= 256) { stopScan = true; throw new InvalidDataException("Directory scan reached its 256-directory budget."); }
                            pending.Push((child, depth + 1));
                        }
                    }
                }
                catch (Exception error) when (FileFailure(error))
                { complete = false; errors.Add(Bound(error.Message)); if (stopScan) pending.Clear(); }
            }
        }
        if (complete)
            foreach (var (id, owned) in index.Skills.Where(x => !rows.Any(row => row.Key == x.Value.SourceKey)))
                rows.Add(new(owned.SourceKey, owned.SourceKey.StartsWith("bundled/", StringComparison.Ordinal) ? "bundled" : "data",
                    DirectoryFor(owned.SourceKey), null, "The source folder or SKILL.md is missing.", id));
        return new(DateTimeOffset.UtcNow, rows.ToArray(), complete, errors.Count == 0 ? null : string.Join(" ", errors.Distinct().Take(4)));
    }

    internal string DirectoryFor(string sourceKey)
    {
        if (sourceKey.StartsWith("bundled/", StringComparison.Ordinal))
            return SkillFiles.Resolve(BundledRoot, sourceKey[8..]);
        if (sourceKey.StartsWith("data/", StringComparison.Ordinal))
            return SkillFiles.Resolve(EditableRoot, sourceKey[5..]);
        throw new InvalidDataException("Ownership must reference a relative source in the selected bundled or data root.");
    }

    internal static bool IsExcluded(SkillsIndex index, SkillFolderEntry entry) =>
        index.Exclusions.ContainsKey(entry.Key) || index.SuppressedIds.ContainsKey(entry.Origin + ":" + entry.Id);

    internal static void Suppress(SkillsIndex index, SkillFolderEntry entry, string reason)
    {
        index.Exclusions[entry.Key] = Bound(reason);
        if (entry.Id.Length != 0) index.SuppressedIds[entry.Origin + ":" + entry.Id] = Bound(reason);
    }

    internal static bool MatchesOwned(SkillsIndex index, string installationId, string id, SkillInspection current) =>
        index.OwnerInstallationId == installationId && index.Skills.TryGetValue(id, out var owned) &&
        current.Summary.Id == id && !current.Summary.BuiltIn && current.Summary.Revision >= owned.Revision &&
        current.Summary.Source == Source(owned.SourceKey) &&
        CatalogHash(new SkillManifest { Id = current.Summary.Id, Name = current.Summary.Name,
            Version = current.Summary.Version, Description = current.Summary.Description,
            Triggers = current.Summary.Triggers.ToList() }, current.Content) == owned.CatalogHash;

    internal static string Source(string key) => SourcePrefix + key;
    internal static string CatalogHash(SkillManifest manifest, string content) =>
        SkillFiles.Hash(SkillFiles.Utf8.GetBytes(JsonSerializer.Serialize(manifest, CerebralJson.Options) + "\n" + content));

    internal IReadOnlyList<string> RemoveManagedFiles(OwnedSkill owned)
    {
        var preserved = new List<string>();
        if (!owned.SourceKey.StartsWith("data/", StringComparison.Ordinal)) return preserved;
        foreach (var (relative, hash) in owned.ManagedFiles)
        {
            try
            {
                var path = SkillFiles.Resolve(DirectoryFor(owned.SourceKey), relative);
                if (!File.Exists(path)) continue;
                var maximum = relative is "SKILL.md" or "skill.json" ? SkillDocument.MaximumManifestBytes : SkillDocument.MaximumAssetBytes;
                if (new FileInfo(path).Length > maximum || SkillFiles.FileHash(path, maximum) != hash)
                { preserved.Add(relative); continue; }
                ScopedFilePolicy.RejectLinks(path);
                File.Delete(path); // Never recurse or delete an unknown/modified entry.
            }
            catch (Exception error) when (FileFailure(error)) { preserved.Add(relative); }
        }
        return preserved;
    }

    internal static void SetGroup(SkillsIndex index, string name, IEnumerable<string> ids)
    {
        ValidateGroupName(name);
        var values = ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (values.Length is < 1 or > SkillDocument.MaximumSkills) throw new InvalidDataException("Select 1–128 group members.");
        foreach (var id in values) SkillFiles.ValidateId(id);
        index.Groups[name] = values;
    }

    internal static void ValidateGroupName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
            throw new InvalidDataException("Group names require 1–80 visible characters.");
    }
    internal static bool FileFailure(Exception error) => error is InvalidDataException or IOException or UnauthorizedAccessException or
        ArgumentException or JsonException or InvalidOperationException or OverflowException;
    internal static string Bound(string message) => message.Length <= 400 ? message : message[..400];
    private static void RequireHash(string hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(x => !char.IsAsciiHexDigit(x)))
            throw new InvalidDataException("An owned file hash is invalid.");
    }
    private static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = value.EnumerateObject().ToArray();
            if (fields.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length)
                throw new InvalidDataException("Repeated JSON properties are unsupported.");
            foreach (var field in fields) RequireUniqueProperties(field.Value);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RequireUniqueProperties(item);
    }
}
