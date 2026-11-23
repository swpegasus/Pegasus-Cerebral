using System.Security.Cryptography;
using System.Text.Json;

namespace Cerebral.Core;

/// <summary>Checks an extracted portable payload before Configuration selects any durable state.</summary>
public static class PortablePackageAdmission
{
    private static string? validatedRoot;

    public static bool IsValidatedAt(string directory) =>
        string.Equals(System.Threading.Volatile.Read(ref validatedRoot),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase);

    private sealed class Manifest
    {
        public string? Contract { get; set; }
        public string? Product { get; set; }
        public string? Platform { get; set; }
        public string? Mode { get; set; }
        public Dictionary<string, string>? Roots { get; set; }
        public string[]? Directories { get; set; }
        public FileRow[]? Files { get; set; }
        public FileRow[]? Bootstrap { get; set; }
    }

    private sealed class FileRow
    {
        public string? Path { get; set; }
        public long Bytes { get; set; }
        public string? Sha256 { get; set; }
    }

    private static readonly string[] ProgramRoots = ["Applications", "Plugins", "Resources"];
    private static readonly string[] BootstrapNames = ["Start-CerebralPortable.cmd", "Start-CerebralPortable.ps1", "installation.ini"];
    private const string MarkerText = "[Cerebral]\r\nProduct=PegasusCerebral\r\nPortable=1\r\nBaseDir=.\r\nDataDir=Data\r\nModelsDir=Models\r\nToolsDir=Tools\r\nProjectsDir=Projects\r\nSharedDir=Shared\r\nUserDataDir=UserData\r\n";
    private static readonly Dictionary<string, string> MutableRoots = new(StringComparer.Ordinal)
    {
        ["data"] = "Data", ["models"] = "Models", ["tools"] = "Tools", ["projects"] = "Projects",
        ["shared"] = "Shared", ["userData"] = "UserData"
    };

    public static void VerifyAt(string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(root)) throw Invalid("The portable root is missing.");
        ScopedFilePolicy.RejectLinks(root);
        var markerPath = Path.Combine(root, "installation.ini");
        if (!File.Exists(markerPath) || IsLink(markerPath) || new FileInfo(markerPath).Length > 1024 ||
            File.ReadAllText(markerPath) != MarkerText)
            throw Invalid("The portable marker does not match the packaged data roots.");
        var manifestPath = Path.Combine(root, "portable-manifest.json");
        if (!File.Exists(manifestPath) || IsLink(manifestPath)) throw Invalid("The portable manifest is missing or linked.");
        if (new FileInfo(manifestPath).Length > 2_000_000) throw Invalid("The portable manifest is too large.");
        Manifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw Invalid("The portable manifest is empty.");
        }
        catch (JsonException error) { throw Invalid("The portable manifest is malformed.", error); }
        if (manifest.Contract != "pegasus.cerebral.portable/1" || manifest.Product != "PegasusCerebral" ||
            manifest.Platform != "win-x64" || manifest.Mode != "portable")
            throw Invalid("The portable manifest contract is unsupported.");
        if (manifest.Roots is null || manifest.Roots.Count != MutableRoots.Count + 1 ||
            !manifest.Roots.TryGetValue("program", out var program) || program != "." ||
            MutableRoots.Any(pair => !manifest.Roots.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw Invalid("The portable data roots are invalid.");
        foreach (var name in MutableRoots.Values)
        {
            var path = Path.Combine(root, name);
            ScopedFilePolicy.RejectLinks(path);
            if (File.Exists(path) || Directory.Exists(path) && IsLink(path))
                throw Invalid("A portable mutable root is not an ordinary directory: " + name);
        }

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in manifest.Directories ?? [])
        {
            _ = Resolve(root, relative, program: true, directory: true);
            if (!directories.Add(relative)) throw Invalid("A portable program directory is repeated: " + relative);
        }
        foreach (var name in ProgramRoots)
            if (!directories.Contains(name)) throw Invalid("A portable program root is undeclared: " + name);
        var validatedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in directories.OrderBy(value => value.Count(ch => ch == '/')))
        {
            var parent = Parent(relative);
            if (parent is not null && !validatedDirectories.Contains(parent))
                throw Invalid("A portable program directory has an undeclared parent: " + relative);
            var path = Resolve(root, relative, program: true, directory: true);
            if (!Directory.Exists(path) || IsLink(path))
                throw Invalid("A portable program directory is missing or linked: " + relative);
            validatedDirectories.Add(relative);
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in manifest.Files ?? [])
        {
            var path = Resolve(root, row.Path, program: true, directory: false);
            if (!validatedDirectories.Contains(Parent(row.Path!)!))
                throw Invalid("A portable program file has an undeclared parent: " + row.Path);
            if (!files.Add(row.Path!)) throw Invalid("A portable program file is repeated: " + row.Path);
            VerifyFile(path, row);
        }
        var bootstrap = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in manifest.Bootstrap ?? [])
        {
            if (row.Path is null || !BootstrapNames.Contains(row.Path, StringComparer.Ordinal) ||
                !bootstrap.Add(row.Path)) throw Invalid("A portable bootstrap declaration is invalid or repeated.");
            VerifyFile(Resolve(root, row.Path, program: false, directory: false), row);
        }
        if (bootstrap.Count != BootstrapNames.Length) throw Invalid("The portable bootstrap declaration is incomplete.");

        // Enumerate after validating the declared parents. An undeclared link never becomes a traversal route.
        var pending = new Stack<string>(ProgramRoots.Select(name => Path.Combine(root, name)));
        while (pending.Count > 0)
        {
            var parent = pending.Pop();
            foreach (var item in new DirectoryInfo(parent).EnumerateFileSystemInfos())
            {
                if (IsLink(item.FullName)) throw Invalid("The portable program contains a linked path: " + item.Name);
                var relative = Path.GetRelativePath(root, item.FullName).Replace('\\', '/');
                if (item is DirectoryInfo)
                {
                    if (!directories.Contains(relative)) throw Invalid("The portable program contains an undeclared directory: " + relative);
                    pending.Push(item.FullName);
                }
                else if (!files.Contains(relative)) throw Invalid("The portable program contains an undeclared file: " + relative);
            }
        }
        System.Threading.Volatile.Write(ref validatedRoot, root);
    }

    private static string Resolve(string root, string? relative, bool program, bool directory)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 32768 || relative.Contains('\\') ||
            relative.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':')))
            throw Invalid("The portable manifest contains an invalid relative path.");
        var parts = relative.Split('/');
        if (program && (!ProgramRoots.Contains(parts[0], StringComparer.Ordinal) || !directory && parts.Length < 2))
            throw Invalid("The portable manifest contains a file outside the program roots: " + relative);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Invalid("A portable manifest path escapes the package.");
        return path;
    }

    private static void VerifyFile(string path, FileRow row)
    {
        ScopedFilePolicy.RejectLinks(path);
        if (row.Bytes < 0 || row.Sha256 is null || row.Sha256.Length != 64 ||
            row.Sha256.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            !File.Exists(path) || IsLink(path)) throw Invalid("A portable file is invalid or missing: " + row.Path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != row.Bytes ||
            !Convert.ToHexString(SHA256.HashData(stream)).Equals(row.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Invalid("A portable file changed: " + row.Path);
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static string? Parent(string relative)
    {
        var separator = relative.LastIndexOf('/');
        return separator < 0 ? null : relative[..separator];
    }
    private static InvalidDataException Invalid(string message, Exception? inner = null) => new(message, inner);
}
