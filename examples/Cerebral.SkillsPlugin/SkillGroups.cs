using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.SkillsPlugin;

internal enum SkillConflictChoice { Keep, Replace, SideBySide }
internal sealed record SkillGroupMember(string Directory, SkillDocument Document);
internal sealed record SkillImportItem(SkillGroupMember Member, SkillManifest Manifest, SkillSummary? Existing,
    bool Skip, string Decision);
internal sealed record SkillImportPlan(string GroupName, IReadOnlyList<SkillImportItem> Items);
internal sealed record MaterializedSkill(SkillFolderEntry Entry, IReadOnlyDictionary<string, string> Files);

internal sealed class SkillGroupArchive : IDisposable
{
    private readonly BoundedContentZipArchive archive;
    private readonly IReadOnlyDictionary<string, (long Bytes, string Hash)> files;
    private string? temporaryRoot;
    internal string Name { get; }
    internal IReadOnlyList<SkillGroupMember> Members { get; }
    internal string? CleanupWarning { get; private set; }

    internal SkillGroupArchive(BoundedContentZipArchive archive, string name, IReadOnlyList<SkillGroupMember> members,
        IReadOnlyDictionary<string, (long Bytes, string Hash)> files)
    { this.archive = archive; Name = name; Members = members; this.files = files; }

    internal void Extract(string dataDirectory)
    {
        if (temporaryRoot is not null) throw new InvalidOperationException("This reviewed archive was already extracted.");
        var candidate = SkillFiles.Resolve(dataDirectory, ".skills-import-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(candidate) || File.Exists(candidate)) throw new IOException("Import temporary directory collision.");
        Directory.CreateDirectory(candidate);
        temporaryRoot = candidate;
        // The handle performs its own complete destination preflight before any entry write.
        archive.ExtractToNewDirectory(candidate);
    }

    internal MaterializedSkill Materialize(SkillImportItem item, SkillFolderCatalog catalog)
    {
        if (temporaryRoot is null) throw new InvalidOperationException("Confirm the preview before extracting content.");
        var groupKey = SkillFiles.Hash(SkillFiles.Utf8.GetBytes(Name))[..12];
        var relative = "Imported/" + groupKey + "/" + item.Manifest.Id + "-" + Guid.NewGuid().ToString("N")[..8];
        var destination = SkillFiles.Resolve(catalog.EditableRoot, relative);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Import folder collision; existing files were preserved.");
        Directory.CreateDirectory(destination);
        var owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Write(string path, byte[] bytes)
        {
            SkillFiles.WriteNew(destination, path, bytes);
            owned.Add(path, SkillFiles.Hash(bytes));
        }
        try
        {
            Write("skill.json", JsonSerializer.SerializeToUtf8Bytes(item.Manifest, CerebralJson.Options));
            Write("SKILL.md", SkillFiles.Utf8.GetBytes(item.Member.Document.Content));
            foreach (var asset in item.Member.Document.Assets)
            {
                var bytes = SkillFiles.Read(SkillFiles.Resolve(temporaryRoot, item.Member.Directory + "/" + asset.Path),
                    SkillDocument.MaximumAssetBytes);
                if (bytes.LongLength != asset.Bytes || SkillFiles.Hash(bytes) != asset.Sha256)
                    throw new InvalidDataException("An extracted asset no longer matches its reviewed content.");
                Write(asset.Path, bytes);
            }
            var key = "data/" + relative;
            var document = SkillDocument.Read(destination, key);
            if (SkillFolderCatalog.CatalogHash(document.Manifest, document.Content) !=
                SkillFolderCatalog.CatalogHash(item.Manifest, item.Member.Document.Content))
                throw new InvalidDataException("The materialized skill differs from its preview.");
            return new(new(key, "data", destination, document, null), owned);
        }
        catch
        {
            Cleanup(destination, owned.ToDictionary(x => x.Key, x => (Bytes: (long)SkillDocument.MaximumAssetBytes, Hash: x.Value)));
            throw;
        }
    }

    public void Dispose()
    {
        archive.Dispose();
        if (temporaryRoot is not null)
        {
            try { Cleanup(temporaryRoot, files); }
            catch (Exception error) when (SkillFolderCatalog.FileFailure(error))
            { CleanupWarning = "Modified or inaccessible import staging files were preserved: " + SkillFolderCatalog.Bound(error.Message); }
            temporaryRoot = null;
        }
    }

    private static void Cleanup(string ownedRoot, IReadOnlyDictionary<string, (long Bytes, string Hash)> expected)
    {
        var root = Path.GetFullPath(ownedRoot);
        ScopedFilePolicy.RejectLinks(root);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        foreach (var file in expected)
        {
            var path = SkillFiles.Resolve(root, file.Key);
            var parent = Path.GetDirectoryName(path);
            while (parent is not null && ScopedFilePolicy.IsWithin(parent, root))
            { directories.Add(parent); parent = Path.GetDirectoryName(parent); }
            if (File.Exists(path) && SkillFiles.FileHash(path, checked((int)Math.Min(SkillDocument.MaximumGroupBytes, file.Value.Bytes))) == file.Value.Hash)
                File.Delete(path);
        }
        foreach (var directory in directories.OrderByDescending(x => x.Length))
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory, recursive: false);
    }
}

internal static class SkillGroups
{
    private sealed class GroupManifest
    {
        public GroupManifest() { }
        public int SchemaVersion { get; set; } = 1;
        public string GroupName { get; set; } = "";
        public List<GroupMemberManifest> Skills { get; set; } = [];
    }
    private sealed class GroupMemberManifest
    {
        public GroupMemberManifest() { }
        public string Id { get; set; } = "";
        public string Directory { get; set; } = "";
    }

    internal static SkillGroupArchive Open(string path)
    {
        ScopedFilePolicy.RejectLinks(Path.GetFullPath(path));
        var archive = BoundedContentZipArchive.OpenRead(path);
        try
        {
            var entries = archive.Files;
            if (entries.Count is < 3 or > 512 || entries.Sum(x => x.Length) > SkillDocument.MaximumGroupBytes)
                throw new InvalidDataException("Skill groups contain at most 512 files and 16 MiB expanded content.");
            foreach (var entry in entries) _ = SkillFiles.Relative(entry.FullName);
            var byName = entries.ToDictionary(x => x.FullName, StringComparer.Ordinal);
            var root = byName.GetValueOrDefault("group.json") ?? throw new InvalidDataException("A skill group requires group.json.");
            var groupBytes = archive.ReadEntryBytes(root, SkillDocument.MaximumManifestBytes);
            using (var json = JsonDocument.Parse(groupBytes, new JsonDocumentOptions { MaxDepth = 8 }))
            {
                ExactFields(json.RootElement, ["schemaVersion", "groupName", "skills"]);
                foreach (var member in json.RootElement.GetProperty("skills").EnumerateArray())
                    ExactFields(member, ["id", "directory"]);
            }
            var group = JsonSerializer.Deserialize<GroupManifest>(groupBytes, CerebralJson.Options)
                ?? throw new InvalidDataException("The group manifest is empty.");
            SkillFolderCatalog.ValidateGroupName(group.GroupName);
            if (group.SchemaVersion != 1 || group.Skills is null || group.Skills.Count is < 1 or > SkillDocument.MaximumSkills || group.Skills.Any(x => x is null) ||
                group.Skills.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != group.Skills.Count)
                throw new InvalidDataException("A group needs schema 1 and 1–128 case-distinct unique skill IDs.");
            var admitted = new Dictionary<string, (long Bytes, string Hash)>(StringComparer.Ordinal)
                { ["group.json"] = (groupBytes.LongLength, SkillFiles.Hash(groupBytes)) };
            byte[] Read(string name, int maximum)
            {
                var entry = byName.GetValueOrDefault(name) ?? throw new InvalidDataException("A declared group file is missing: " + name);
                var bytes = archive.ReadEntryBytes(entry, maximum);
                if (!admitted.TryAdd(name, (bytes.LongLength, SkillFiles.Hash(bytes))))
                    throw new InvalidDataException("A group file is claimed more than once.");
                return bytes;
            }
            var members = new List<SkillGroupMember>();
            foreach (var item in group.Skills)
            {
                SkillFiles.ValidateId(item.Id);
                if (item.Directory != "skills/" + item.Id) throw new InvalidDataException("Each group member directory must be skills/<its exact ID>.");
                var document = SkillDocument.Parse(Read(item.Directory + "/SKILL.md", SkillDocument.MaximumDocumentBytes),
                    Read(item.Directory + "/skill.json", SkillDocument.MaximumManifestBytes), item.Directory,
                    asset => Read(item.Directory + "/" + asset, SkillDocument.MaximumAssetBytes));
                if (document.Manifest.Id != item.Id) throw new InvalidDataException("Skill identity differs from group membership.");
                members.Add(new(item.Directory, document));
            }
            if (!admitted.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(byName.Keys))
                throw new InvalidDataException("Group archives may contain only declared skill manifests, Markdown and passive assets. Unknown files were not extracted.");
            return new(archive, group.GroupName, members, admitted);
        }
        catch { archive.Dispose(); throw; }
    }

    internal static SkillImportPlan Plan(SkillGroupArchive group, IReadOnlyList<SkillSummary> existing, SkillConflictChoice choice)
    {
        if (!Enum.IsDefined(choice)) throw new ArgumentOutOfRangeException(nameof(choice));
        var names = existing.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var member in group.Members) names.Add(member.Document.Manifest.Id);
        var items = new List<SkillImportItem>();
        foreach (var member in group.Members)
        {
            var original = member.Document.Manifest;
            var found = existing.SingleOrDefault(x => x.Id.Equals(original.Id, StringComparison.OrdinalIgnoreCase));
            var manifest = SkillDocument.Copy(original);
            var skip = found is not null && choice == SkillConflictChoice.Keep;
            if (found is not null && found.Id != original.Id && choice == SkillConflictChoice.Replace)
                throw new InvalidDataException("Case-only ID collisions require Keep or SideBySide; they cannot be replaced.");
            if (found is { BuiltIn: true } && choice == SkillConflictChoice.Replace)
                throw new InvalidDataException("A group cannot replace a built-in skill; choose Keep or SideBySide.");
            if (found is not null && choice == SkillConflictChoice.SideBySide)
            {
                var stem = original.Id.Length <= 70 ? original.Id : original.Id[..70];
                var number = 1;
                do { manifest.Id = stem + "-copy-" + number++; } while (!names.Add(manifest.Id));
                SkillDocument.Validate(manifest, member.Document.Content);
                found = null;
            }
            items.Add(new(member, manifest, found, skip, skip ? "Keep existing" :
                found is null ? "Install disabled" : "Replace content; preserve current enablement and bindings"));
        }
        return new(group.Name, items);
    }

    internal static void Export(string groupName, IReadOnlyList<SkillFolderEntry> selected,
        string destination, string? expectedDestinationHash)
    {
        SkillFolderCatalog.ValidateGroupName(groupName);
        if (selected.Count is < 1 or > SkillDocument.MaximumSkills || selected.Any(x => x.Document is null) ||
            selected.Select(x => x.Document!.Manifest.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Count)
            throw new InvalidDataException("Export requires 1–128 valid, unique selected skill definitions.");
        var members = selected.Select(row => new GroupMemberManifest
            { Id = row.Document!.Manifest.Id, Directory = "skills/" + row.Document.Manifest.Id }).ToList();
        var groupText = JsonSerializer.Serialize(new GroupManifest { GroupName = groupName, Skills = members }, CerebralJson.Options);
        var generated = new Dictionary<string, string>(StringComparer.Ordinal);
        var prospectiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long prospectiveBytes = 0;
        void PlanFile(string name, long bytes)
        {
            _ = SkillFiles.Relative(name);
            if (bytes < 0 || !prospectiveNames.Add(name) || prospectiveNames.Count > 512 ||
                bytes > SkillDocument.MaximumGroupBytes - prospectiveBytes)
                throw new InvalidDataException("Selected definitions/assets exceed 512 files or 16 MiB.");
            prospectiveBytes += bytes;
        }
        var groupByteCount = SkillFiles.Utf8.GetByteCount(groupText);
        if (groupByteCount > SkillDocument.MaximumManifestBytes) throw new InvalidDataException("Group manifest exceeds 64 KiB.");
        PlanFile("group.json", groupByteCount);
        // Complete the declared-budget preflight before opening any selected source or allocating asset arrays.
        foreach (var row in selected)
        {
            var doc = row.Document!;
            SkillDocument.Validate(doc.Manifest, doc.Content);
            if (doc.Assets.Count > SkillDocument.MaximumAssets) throw new InvalidDataException("Too many declared assets.");
            var prefix = "skills/" + doc.Manifest.Id;
            var manifestText = JsonSerializer.Serialize(doc.Manifest, CerebralJson.Options);
            var manifestBytes = SkillFiles.Utf8.GetByteCount(manifestText);
            if (manifestBytes > SkillDocument.MaximumManifestBytes) throw new InvalidDataException("Skill manifest exceeds 64 KiB.");
            generated.Add(prefix + "/skill.json", manifestText);
            PlanFile(prefix + "/skill.json", manifestBytes);
            PlanFile(prefix + "/SKILL.md", SkillFiles.Utf8.GetByteCount(doc.Content));
            foreach (var asset in doc.Assets)
            {
                if (asset.Bytes is < 0 or > SkillDocument.MaximumAssetBytes) throw new InvalidDataException("Asset exceeds 1 MiB.");
                PlanFile(prefix + "/" + asset.Path, asset.Bytes);
            }
        }
        var files = new List<(string Path, byte[] Bytes)>();
        long retainedBytes = 0;
        void RequireRoom(long bytes)
        {
            if (bytes < 0 || files.Count >= 512 || bytes > SkillDocument.MaximumGroupBytes - retainedBytes)
                throw new InvalidDataException("Selected definitions/assets exceed 512 files or 16 MiB.");
        }
        void AddText(string name, string text)
        {
            var length = SkillFiles.Utf8.GetByteCount(text);
            RequireRoom(length); // Check generated Markdown/JSON before allocating their UTF-8 buffers.
            var bytes = SkillFiles.Utf8.GetBytes(text);
            retainedBytes += bytes.LongLength; files.Add((name, bytes));
        }
        AddText("group.json", groupText);
        foreach (var row in selected)
        {
            var admitted = row.Document!;
            var byPath = admitted.Assets.ToDictionary(x => x.Path, StringComparer.Ordinal);
            var current = SkillDocument.Read(row.Directory, row.Key, admitted.Manifest, relative =>
            {
                var asset = byPath.GetValueOrDefault(relative) ?? throw new IOException("Asset declarations changed after preview.");
                // A changed document cannot cause verification to read new or larger undeclared assets.
                return SkillFiles.Read(SkillFiles.Resolve(row.Directory, relative), checked((int)asset.Bytes));
            });
            if (current.Fingerprint != admitted.Fingerprint) throw new IOException("A selected skill changed. Refresh the export preview.");
            var prefix = "skills/" + admitted.Manifest.Id;
            AddText(prefix + "/skill.json", generated[prefix + "/skill.json"]);
            AddText(prefix + "/SKILL.md", admitted.Content);
            foreach (var asset in admitted.Assets)
            {
                RequireRoom(asset.Bytes); // Apply cumulative limits before every retained asset allocation.
                var bytes = SkillFiles.Read(SkillFiles.Resolve(row.Directory, asset.Path), checked((int)asset.Bytes));
                if (bytes.LongLength != asset.Bytes || SkillFiles.Hash(bytes) != asset.Sha256)
                    throw new IOException("A declared asset changed after preview.");
                RequireRoom(bytes.LongLength);
                retainedBytes += bytes.LongLength;
                files.Add((prefix + "/" + asset.Path, bytes)); // Includes mandatory license assets.
            }
        }
        var target = Path.GetFullPath(destination);
        ScopedFilePolicy.RejectLinks(target);
        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Choose an existing export directory.");
        var temporary = SkillFiles.Resolve(parent, ".skills-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
                foreach (var file in files)
                {
                    var entry = zip.CreateEntry(file.Path, CompressionLevel.NoCompression);
                    using var stream = entry.Open();
                    stream.Write(file.Bytes);
                }
            var currentHash = File.Exists(target)
                ? SkillFiles.FileHash(target, checked((int)SkillDocument.MaximumGroupBytes + 1024 * 1024)) : null;
            if (currentHash != expectedDestinationHash) throw new IOException("Export destination changed after selection; it was preserved.");
            ScopedFilePolicy.RejectLinks(target);
            File.Move(temporary, target, overwrite: currentHash is not null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ExactFields(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Group manifest objects are required.");
        var fields = element.EnumerateObject().ToArray();
        if (fields.Length != names.Length || !fields.Select(x => x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names))
            throw new InvalidDataException("Group manifests contain only their declared schema fields, once each.");
    }
}
