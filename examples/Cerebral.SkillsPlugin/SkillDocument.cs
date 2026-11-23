using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.SkillsPlugin;

internal sealed record SkillAsset(string Path, long Bytes, string Sha256);
internal sealed record SkillDocument(SkillManifest Manifest, string Content, IReadOnlyList<SkillAsset> Assets,
    bool HasManifest, string Fingerprint, string Attribution)
{
    internal const int MaximumDocumentBytes = 32 * 1024;
    internal const int MaximumManifestBytes = 64 * 1024;
    internal const int MaximumSkills = 128;
    internal const int MaximumAssetBytes = 1024 * 1024;
    internal const int MaximumAssets = 32;
    internal const long MaximumGroupBytes = 16L * 1024 * 1024;
    private static readonly HashSet<string> HeaderKeys = new(StringComparer.Ordinal)
        { "name", "description", "id", "version", "triggers", "assets", "license", "author", "origin" };
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".txt", ".json", ".csv", ".tsv", ".yaml", ".yml", ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    internal static SkillDocument Read(string directory, string sourceKey, SkillManifest? identity = null,
        Func<string, byte[]>? readAsset = null) =>
        Parse(SkillFiles.Read(SkillFiles.Resolve(directory, "SKILL.md"), MaximumDocumentBytes),
            File.Exists(SkillFiles.Resolve(directory, "skill.json"))
                ? SkillFiles.Read(SkillFiles.Resolve(directory, "skill.json"), MaximumManifestBytes) : null,
            sourceKey, readAsset ?? (relative => SkillFiles.Read(SkillFiles.Resolve(directory, relative), MaximumAssetBytes)), identity);

    internal static SkillDocument Parse(byte[] markdown, byte[]? manifestBytes, string sourceKey,
        Func<string, byte[]> readAsset, SkillManifest? identity = null)
    {
        var content = SkillFiles.Decode(markdown, MaximumDocumentBytes);
        var header = Frontmatter(content);
        SkillManifest manifest;
        if (manifestBytes is not null)
        {
            if (manifestBytes.Length > MaximumManifestBytes) throw new InvalidDataException("skill.json exceeds 64 KiB.");
            using var json = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 8 });
            var fields = json.RootElement.ValueKind == JsonValueKind.Object
                ? json.RootElement.EnumerateObject().ToArray() : throw new InvalidDataException("skill.json must be an object.");
            string[] names = ["schemaVersion", "id", "name", "version", "description", "triggers"];
            if (fields.Length != names.Length || !fields.Select(x => x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names))
                throw new InvalidDataException("skill.json must contain each existing SkillManifest field exactly once.");
            manifest = JsonSerializer.Deserialize<SkillManifest>(manifestBytes, CerebralJson.Options)
                ?? throw new InvalidDataException("skill.json is empty.");
        }
        else
        {
            var name = header.GetValueOrDefault("name") ?? throw new InvalidDataException("SKILL.md needs scalar name and description frontmatter, or skill.json.");
            var description = header.GetValueOrDefault("description") ?? throw new InvalidDataException("SKILL.md needs description frontmatter.");
            var id = identity?.Id ?? header.GetValueOrDefault("id") ??
                "skill-" + SkillFiles.Hash(Encoding.UTF8.GetBytes(sourceKey))[..20];
            var triggers = identity?.Triggers.ToList() ?? Split(header.GetValueOrDefault("triggers") ?? name).ToList();
            manifest = new()
            {
                Id = id, Name = name, Description = description,
                Version = identity?.Version ?? header.GetValueOrDefault("version") ?? "1.0.0", Triggers = triggers
            };
        }
        Validate(manifest, content);
        var requiresCredit = manifest.Id.StartsWith("cerebral.base.", StringComparison.Ordinal) ||
            (header.GetValueOrDefault("origin")?.StartsWith("https://github.com/mattpocock/skills/", StringComparison.Ordinal) == true);
        var paths = Split(header.GetValueOrDefault("assets") ?? "").ToArray();
        if (paths.Length > MaximumAssets || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new InvalidDataException("A skill may declare at most 32 unique passive assets.");
        var assets = new List<SkillAsset>();
        foreach (var relative in paths)
        {
            _ = SkillFiles.Relative(relative);
            if (relative.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                relative.Equals("skill.json", StringComparison.OrdinalIgnoreCase) ||
                !AssetExtensions.Contains(System.IO.Path.GetExtension(relative)))
                throw new InvalidDataException("Assets must be declared passive documents or images, without executable content types.");
            var bytes = readAsset(relative);
            if (bytes.Length > MaximumAssetBytes) throw new InvalidDataException("A declared asset exceeds 1 MiB.");
            assets.Add(new(relative, bytes.LongLength, SkillFiles.Hash(bytes)));
        }
        if (requiresCredit && (!assets.Any(x => x.Path == "LICENSE.txt" && x.Sha256 == "0e7ac423bf2c6e223b7c5b156f8cf72da49d748e56a1641402c31f22ad07dbb5") ||
            !content.Contains("Matt Pocock", StringComparison.Ordinal) || !content.Contains("adapt", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Derived Cerebral base skills require their complete pinned MIT LICENSE.txt asset and source/adaptation attribution.");
        var attribution = string.Join(" · ", new[] { header.GetValueOrDefault("author"),
            header.GetValueOrDefault("license"), header.GetValueOrDefault("origin") }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return Create(manifest, content, assets, manifestBytes is not null, attribution);
    }

    internal SkillDocument WithManifest(SkillManifest manifest)
    {
        Validate(manifest, Content);
        return Create(manifest, Content, Assets, HasManifest, Attribution);
    }

    private static SkillDocument Create(SkillManifest manifest, string content, IReadOnlyList<SkillAsset> assets,
        bool hasManifest, string attribution)
    {
        var copy = Copy(manifest);
        var identity = JsonSerializer.Serialize(copy, CerebralJson.Options) + "\n" + content + "\n" +
            JsonSerializer.Serialize(assets, CerebralJson.Options);
        return new(copy, content, assets.ToArray(), hasManifest, SkillFiles.Hash(Encoding.UTF8.GetBytes(identity)), attribution);
    }

    internal static SkillManifest Copy(SkillManifest value) => new()
    {
        SchemaVersion = value.SchemaVersion, Id = value.Id, Name = value.Name, Version = value.Version,
        Description = value.Description, Triggers = value.Triggers.ToList()
    };

    // The Worker remains authoritative. These local bounds reject bad files before an owner command is offered.
    internal static void Validate(SkillManifest manifest, string content)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Only SkillManifest schemaVersion 1 is supported.");
        SkillFiles.ValidateId(manifest.Id);
        _ = PluginVersion.Parse(manifest.Version);
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 120 || manifest.Name.Contains('\0') ||
            string.IsNullOrWhiteSpace(manifest.Description) || manifest.Description.Length > 500 || manifest.Description.Contains('\0'))
            throw new InvalidDataException("Skill name/description must be bounded nonempty text (120/500 characters).");
        if (manifest.Triggers is null || manifest.Triggers.Count is < 1 or > 32 ||
            manifest.Triggers.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 80 || x.Contains('\0')) ||
            manifest.Triggers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Triggers.Count)
            throw new InvalidDataException("Skills need 1–32 unique trigger phrases, each at most 80 characters.");
        if (string.IsNullOrWhiteSpace(content) || content.Contains('\0') ||
            SkillFiles.Utf8.GetByteCount(content) > MaximumDocumentBytes)
            throw new InvalidDataException("SKILL.md must contain valid instruction text of at most 32 KiB.");
    }

    internal static IReadOnlyDictionary<string, string> Frontmatter(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0] != "---") return result;
        var end = Array.FindIndex(lines, 1, x => x == "---");
        if (end < 1 || end > 128 || SkillFiles.Utf8.GetByteCount(string.Join("\n", lines.Take(end))) > 8192)
            throw new InvalidDataException("Frontmatter must close within 128 lines and 8 KiB.");
        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            if (char.IsWhiteSpace(line[0])) throw new InvalidDataException("Nested YAML, arrays and indentation outside a scalar block are unsupported.");
            var colon = line.IndexOf(':');
            if (colon < 1) throw new InvalidDataException("Frontmatter supports key: scalar pairs only.");
            var key = line[..colon];
            if (!HeaderKeys.Contains(key) || result.ContainsKey(key))
                throw new InvalidDataException("Unsupported or duplicate frontmatter key: " + key);
            var value = line[(colon + 1)..].Trim();
            if (value is "|" or ">")
            {
                var literal = value == "|";
                var block = new List<string>();
                while (i + 1 < end && (string.IsNullOrEmpty(lines[i + 1]) || lines[i + 1].StartsWith("  ", StringComparison.Ordinal)))
                {
                    i++;
                    if (lines[i].Contains('\t')) throw new InvalidDataException("Tabs are unsupported in scalar blocks.");
                    block.Add(lines[i].Length >= 2 ? lines[i][2..] : "");
                }
                value = string.Join(literal ? "\n" : " ", block).Trim();
            }
            else if (value.StartsWith('"'))
            {
                try { value = JsonSerializer.Deserialize<string>(value) ?? ""; }
                catch (JsonException error) { throw new InvalidDataException("Double-quoted frontmatter uses JSON-compatible escapes.", error); }
            }
            else if (value.StartsWith('\''))
            {
                if (value.Length < 2 || !value.EndsWith('\'')) throw new InvalidDataException("Unclosed quoted scalar.");
                value = value[1..^1].Replace("''", "'");
            }
            else
            {
                if (value.Length == 0 || "[{!&*".Contains(value[0]) || value.Contains(": ") || value.Contains('\t'))
                    throw new InvalidDataException("Only plain/quoted text and | or > scalar blocks are supported; YAML tags, aliases and collections are unsupported.");
                var comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0) value = value[..comment].TrimEnd();
            }
            if (value.Length is < 1 or > 4096 || value.Contains('\0'))
                throw new InvalidDataException("Frontmatter scalars must contain 1–4096 characters.");
            result.Add(key, value);
        }
        return result;
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal static class SkillFiles
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    // Mirrors the existing Worker identifier preflight; owner commands remain the admission authority.
    internal static void ValidateId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 ||
            value.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '-' or '_')))
            throw new InvalidDataException("IDs require 1–100 ASCII letters, digits, dots, hyphens or underscores.");
    }

    internal static string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') || path.Contains(':') ||
            path.StartsWith('/') || path.EndsWith('/') || path.Contains('\0'))
            throw new InvalidDataException("Skill paths must be bounded forward-slash relative file names.");
        var parts = path.Split('/');
        if (parts.Length > 8 || parts.Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("Skill path contains traversal or excessive depth.");
        foreach (var part in parts)
        {
            if (part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(ch => ch < ' ' || ch is '<' or '>' or '"' or '|' or '?' or '*'))
                throw new InvalidDataException("Skill paths contain Windows-aliased or invalid filenames.");
            int bytes;
            try { bytes = Utf8.GetByteCount(part); }
            catch (EncoderFallbackException error) { throw new InvalidDataException("Invalid filename Unicode.", error); }
            if (bytes > 255) throw new InvalidDataException("A filename segment exceeds 255 UTF-8 bytes.");
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                    stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'))
                throw new InvalidDataException("Skill paths contain a reserved Windows device name.");
        }
        return path;
    }

    internal static string Resolve(string root, string relative)
    {
        var result = NativeDesktopPluginPackage.ContainedPath(Path.GetFullPath(root),
            Relative(relative).Replace('/', Path.DirectorySeparatorChar));
        ScopedFilePolicy.RejectLinks(result);
        return result;
    }

    internal static byte[] Read(string path, int maximum)
    {
        ScopedFilePolicy.RejectLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw new InvalidDataException("File exceeds its declared size limit.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("File changed while reading.");
        ScopedFilePolicy.RejectLinks(path);
        return bytes;
    }

    internal static string Decode(byte[] bytes, int maximum)
    {
        if (bytes.Length > maximum) throw new InvalidDataException("Text exceeds its byte limit.");
        try
        {
            var text = Utf8.GetString(bytes).TrimStart('\uFEFF');
            if (text.Contains('\0')) throw new InvalidDataException("Text cannot contain NUL.");
            return text;
        }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Files must contain valid UTF-8.", error); }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string FileHash(string path, int maximum) => Hash(Read(path, maximum));

    internal static void WriteNew(string root, string relative, byte[] bytes)
    {
        var path = Resolve(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ScopedFilePolicy.RejectLinks(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes); file.Flush(true);
    }
}
