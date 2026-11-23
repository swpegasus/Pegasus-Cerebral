using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Cerebral.ChatIconsPlugin;

/// <summary>Portable passive catalog: no WPF, network, executable assets or Cortex identity assumptions.</summary>
public sealed record ChatIconAsset(string Key, string Alias, string Label, string Description,
    string RelativeFile, string Sha256, string Text, string Source);

public sealed class AssetPack
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Dictionary<string, Dictionary<string, string>> sections;
    private readonly SortedDictionary<string, string> fileHashes;
    private readonly byte[] catalogBytes;
    public string Root { get; }
    public string Id { get; }
    public string Version { get; }
    public string CatalogSha256 { get; }
    public string ContentSha256 { get; }
    public IReadOnlyList<ChatIconAsset> Assets { get; }
    public const int MaximumCatalogBytes = 64 * 1024;
    public const int MaximumAssetBytes = 128 * 1024;
    public const int MaximumIcons = 64;

    private AssetPack(string root)
    {
        Root = Path.GetFullPath(root);
        var catalog = Read("catalog.ini", MaximumCatalogBytes);
        catalogBytes = catalog;
        CatalogSha256 = Digest(catalog);
        sections = Parse(Utf8.GetString(catalog));
        if (!sections.TryGetValue("pack", out var header)) throw new InvalidDataException("Missing pack header.");
        Exact(header, ["contract", "id", "version", "namespace", "license", "license-file", "notice-file",
            "publisher", "upstream", "revision", "viewport"]);
        if (header["contract"] != "pegasus.asset-pack/1" || header["license"] != "MIT" || header["viewport"] != "20")
            throw new InvalidDataException("Unsupported passive icon pack contract, license or viewport.");
        Id = header["id"]; Version = header["version"];
        if (!Regex.IsMatch(Id, "^[a-z][a-z0-9.-]{0,62}$", RegexOptions.CultureInvariant) ||
            !System.Version.TryParse(Version, out _) || header["namespace"] != Id + ".")
            throw new InvalidDataException("Invalid pack identity.");
        _ = RequiredText(header, "publisher", 128); _ = RequiredText(header, "revision", 128);
        Https(header["upstream"]);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["catalog.ini"] = CatalogSha256 };
        foreach (var name in new[] { "license-file", "notice-file" })
        {
            var relative = header[name];
            if (!relative.StartsWith("Licensing/", StringComparison.Ordinal) || !relative.EndsWith(".txt", StringComparison.Ordinal))
                throw new InvalidDataException("Required license files must remain in Licensing.");
            var bytes = Read(relative, MaximumCatalogBytes);
            if (bytes.Length == 0) throw new InvalidDataException("Empty licensing file.");
            _ = Utf8.GetString(bytes);
            files[relative] = Digest(bytes);
        }
        var assets = new List<ChatIconAsset>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, item) in sections.Where(pair => pair.Key != "pack"))
        {
            if (!key.StartsWith(Id + ".", StringComparison.Ordinal) ||
                !Regex.IsMatch(key, "^[a-z][a-z0-9.-]{0,126}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Icon key must remain in its declared namespace.");
            Exact(item, ["alias", "label", "description", "file", "sha256", "text", "source"]);
            var alias = RequiredText(item, "alias", 64);
            if (!aliases.Add(alias)) throw new InvalidDataException("Duplicate compatibility alias.");
            var file = item["file"];
            if (!file.StartsWith("svg/", StringComparison.Ordinal) || !file.EndsWith(".svg", StringComparison.Ordinal))
                throw new InvalidDataException("Icons must reference contained SVG assets.");
            var bytes = Read(file, MaximumAssetBytes);
            if (!Regex.IsMatch(item["sha256"], "^[a-f0-9]{64}$", RegexOptions.CultureInvariant) || Digest(bytes) != item["sha256"])
                throw new InvalidDataException("Icon content does not match its SHA-256.");
            ValidateSvg(bytes);
            Https(item["source"]);
            var text = RequiredText(item, "text", 16);
            assets.Add(new(key, alias, RequiredText(item, "label", 128), RequiredText(item, "description", 512),
                file, item["sha256"], text, item["source"]));
            files[file] = item["sha256"];
        }
        if (assets.Any(asset => assets.Any(other => other.Key == asset.Alias)))
            throw new InvalidDataException("Compatibility aliases cannot collide with namespaced icon keys.");
        if (assets.Count is < 1 or > MaximumIcons) throw new InvalidDataException("A pack must contain 1–64 icons.");
        Assets = assets.AsReadOnly();
        // Path-bound digest is portable: installed root and machine timestamps never participate.
        fileHashes = files;
        ContentSha256 = Digest(Utf8.GetBytes(string.Join("\n", files.Select(pair => pair.Key + "\0" + pair.Value)) + "\n"));
    }

    public static AssetPack Load(string root) => new(root);
    public ChatIconAsset Find(string key) => Assets.SingleOrDefault(asset => asset.Key == key || asset.Alias == key)
        ?? throw new KeyNotFoundException("The exact icon key or alias is not in this pack.");

    public string Reference(ChatIconAsset asset)
    {
        if (!Assets.Contains(asset)) throw new ArgumentException("Reference must identify this validated pack's asset.", nameof(asset));
        return $"pack={Id}; version={Version}; key={asset.Key}; file={asset.RelativeFile}; sha256={asset.Sha256}; catalogSha256={CatalogSha256}; packSha256={ContentSha256}";
    }

    /// <summary>Exports validated content to a new directory. Licensing follows both whole packs and individual assets.</summary>
    public void Export(string destination, string? key = null)
    {
        var target = Path.GetFullPath(destination);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Export destination must be absent.");
        var parent = Path.GetDirectoryName(target) ?? throw new IOException("Export requires a parent directory.");
        RejectLinks(parent); Directory.CreateDirectory(parent); RejectLinks(parent);
        var stage = Path.Combine(parent, ".icon-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var selected = key is null ? Assets : [Find(key)];
            var entries = selected.Select(asset => asset.Key).Prepend("pack").ToArray();
            var catalog = string.Join("\n\n", entries.Select(name => "[" + name + "]\n" +
                string.Join("\n", sections[name].Select(property => property.Key + "=" + property.Value)))) + "\n";
            if (key is null) File.WriteAllBytes(Path.Combine(stage, "catalog.ini"), catalogBytes);
            else File.WriteAllText(Path.Combine(stage, "catalog.ini"), catalog, Utf8);
            foreach (var name in new[] { "license-file", "notice-file" })
                Copy(sections["pack"][name], stage, fileHashes[sections["pack"][name]]);
            foreach (var asset in selected) Copy(asset.RelativeFile, stage, asset.Sha256);
            File.WriteAllText(Path.Combine(stage, "SOURCE-REFERENCE.txt"),
                "Passive exported asset reference; no access grant or live Cortex ID.\n" +
                string.Join("\n", selected.Select(Reference)) + "\n", Utf8);
            _ = Load(stage);
            Directory.Move(stage, target);
        }
        finally
        {
            // stage was created here with an exact task-owned name; external destinations are never removed.
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    private void Copy(string relative, string stage, string? expected)
    {
        var bytes = Read(relative, relative.EndsWith(".svg", StringComparison.Ordinal) ? MaximumAssetBytes : MaximumCatalogBytes);
        if (expected is not null && Digest(bytes) != expected) throw new IOException("Asset changed before export.");
        var path = Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private byte[] Read(string relative, int maximum)
    {
        if (relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Contained forward-slash relative file required.");
        var path = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Asset path escapes the pack root.");
        RejectLinks(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > maximum) throw new InvalidDataException("Asset exceeds its byte limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Asset changed while reading.");
        return bytes;
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Link or reparse targets are not asset roots.");
    }

    private static void ValidateSvg(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumAssetBytes });
        var document = XDocument.Load(reader);
        if (document.DescendantNodes().OfType<XProcessingInstruction>().Any())
            throw new InvalidDataException("SVG processing instructions are not passive icon assets.");
        XNamespace svg = "http://www.w3.org/2000/svg";
        if (document.Root?.Name != svg + "svg") throw new InvalidDataException("SVG root required.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "svg", "path", "g", "title", "desc" };
        var attributes = new HashSet<string>(StringComparer.Ordinal)
            { "width", "height", "viewBox", "fill", "fill-rule", "clip-rule", "d", "id" };
        foreach (var element in document.Descendants())
        {
            if (element.Name.Namespace != svg || !allowed.Contains(element.Name.LocalName))
                throw new InvalidDataException("Active or unsupported SVG element.");
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration) continue;
                if (attribute.Name.NamespaceName.Length != 0 || !attributes.Contains(attribute.Name.LocalName) ||
                    attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase) || attribute.Value.Length > 32768)
                    throw new InvalidDataException("Active or unsupported SVG attribute.");
                if (attribute.Name.LocalName == "fill" &&
                    !Regex.IsMatch(attribute.Value, "^(?:none|currentColor|#[a-fA-F0-9]{3}|#[a-fA-F0-9]{4}|#[a-fA-F0-9]{6}|#[a-fA-F0-9]{8})$", RegexOptions.CultureInvariant))
                    throw new InvalidDataException("SVG paint must use an explicit passive fill literal.");
                if (attribute.Name.LocalName is "fill-rule" or "clip-rule" && attribute.Value is not ("nonzero" or "evenodd"))
                    throw new InvalidDataException("Unsupported SVG fill rule.");
            }
        }
        if (!document.Descendants(svg + "path").Any()) throw new InvalidDataException("SVG has no path.");
    }

    private static Dictionary<string, Dictionary<string, string>> Parse(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;
        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new(StringComparer.Ordinal);
                if (!result.TryAdd(line[1..^1], current)) throw new InvalidDataException("Duplicate catalog section.");
            }
            else
            {
                var equals = line.IndexOf('=');
                if (current is null || equals < 1 || !current.TryAdd(line[..equals].Trim(), line[(equals + 1)..].Trim()))
                    throw new InvalidDataException("Invalid or duplicate catalog property.");
            }
        }
        return result;
    }
    private static string RequiredText(Dictionary<string, string> item, string name, int maximum)
    {
        var value = item[name];
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new InvalidDataException("Invalid bounded icon metadata: " + name);
        return value;
    }
    private static void Exact(Dictionary<string, string> item, string[] fields)
    {
        if (!item.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(fields)) throw new InvalidDataException("Unexpected catalog fields.");
    }
    private static void Https(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Publisher references must be credential-free HTTPS URLs.");
    }
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
