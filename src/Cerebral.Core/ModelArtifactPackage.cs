using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using Cerebral.Contracts.ModelOperations;

namespace Cerebral.Core;

/// <summary>Neutral v3 model-data packages. Legacy GGUF package contracts are unchanged.
/// This layer reads/verifies/copies bytes only; tensor semantics, trusted runtime selection,
/// authentication, installation registration and hardware admission remain host responsibilities.</summary>
public static class ModelArtifactPackage
{
    public const string Format = "pegasus.cerebral.model";
    public const int Version = 3;
    public const string Extension = ".CerebralModel";
    public const string ManifestName = "manifest.json";
    public const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumCentralDirectoryBytes = 8 * 1024 * 1024;
    private static readonly ModelArtifactPackageLimits HardLimits = new();
    private static readonly string[] Roles = ["weights", "configuration", "tokenizer", "license", "metadata"];
    private static readonly HashSet<string> DataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".safetensors", ".gguf", ".onnx", ".bin", ".pt", ".pth", ".ckpt", ".npy", ".npz",
        ".json", ".yaml", ".yml", ".txt", ".md", ".model", ".tiktoken", ".vocab", ".merges", ".csv", ".license"
    };

    public static bool IsPackagePath(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Bounded manifest inspection only. Call Verify before treating payload bytes as installed.</summary>
    public static ModelArtifactPackageManifest ReadManifest(string packagePath,
        ModelArtifactPackageLimits? limits = null)
    {
        limits = Limits(limits);
        using var file = OpenPackage(packagePath, limits);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        var (manifest, _) = ReadManifest(archive, limits);
        _ = Entries(archive, manifest);
        return manifest;
    }

    public static ModelArtifactPackageInspection Verify(string packagePath,
        ModelArtifactPackageLimits? limits = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits = Limits(limits);
        using var file = OpenPackage(packagePath, limits);
        return VerifyOpen(file, limits, cancellationToken);
    }

    /// <summary>Publishes only a fully verified fresh directory. Existing destinations are never overwritten.
    /// A private sibling staging directory is owned by this call until atomic publication.</summary>
    public static ModelArtifactPackageExtraction Extract(string packagePath, string destinationDirectory,
        ModelArtifactPackageLimits? limits = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits = Limits(limits);
        var destination = Ordinary(destinationDirectory);
        Require(!Directory.Exists(destination) && !File.Exists(destination), "Package destination already exists.");
        var parent = Path.GetDirectoryName(destination) ?? throw Invalid("Package destination requires a parent.");
        Require(Directory.Exists(parent), "Package destination parent must already exist.");
        using var file = OpenPackage(packagePath, limits);
        var inspection = VerifyOpen(file, limits, cancellationToken);
        file.Position = 0;
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var (_, raw) = ReadManifest(archive, limits);
        Require(Hex(SHA256.HashData(raw)) == inspection.ManifestSha256,
            "Package manifest changed after payload verification.");
        var entries = Entries(archive, inspection.Manifest);
        var stage = Ordinary(Path.Combine(parent, ".cerebral-model-stage-" + Guid.NewGuid().ToString("N")));
        Require(IsChild(parent, stage), "Package stage escaped its parent.");
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var item in inspection.Manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Resolve(stage, item.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Ordinary(target);
                using var input = entries[item.Path].Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyAndVerify(input, output, item, cancellationToken);
            }
            using (var manifestFile = new FileStream(Resolve(stage, ManifestName, manifest: true),
                       FileMode.CreateNew, FileAccess.Write, FileShare.None)) manifestFile.Write(raw);
            cancellationToken.ThrowIfCancellationRequested();
            Ordinary(destination); Ordinary(stage);
            Directory.Move(stage, destination); // Atomic create-only publication, including concurrent contenders.
            return new(inspection, destination);
        }
        catch (Exception error)
        {
            try { RemoveOwnedStage(parent, stage, inspection.Manifest); }
            catch (Exception cleanup) { throw new AggregateException("Package extraction failed and stage cleanup is unconfirmed.", error, cleanup); }
            throw;
        }
    }

    /// <summary>Exports only declared ordinary model data. The output is create-only; a failed output
    /// remains claimed for inspection rather than being overwritten by a retry.</summary>
    public static ModelArtifactPackageInspection Export(string sourceDirectory, string destinationPackage,
        ModelArtifactPackageManifest manifest, ModelArtifactPackageLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits = Limits(limits);
        var raw = WriteManifest(manifest, limits);
        manifest = ParseManifest(raw, limits); // Freeze caller-owned collections before any writes.
        var source = Ordinary(sourceDirectory);
        var destination = Ordinary(destinationPackage);
        Require(Directory.Exists(source) && IsPackagePath(destination), "Export requires an ordinary source and .CerebralModel destination.");
        Require(!IsChild(source, destination) && source != destination, "Export must not write into the immutable source tree.");
        Require(Directory.Exists(Path.GetDirectoryName(destination)), "Export parent must already exist.");
        VerifyDirectory(source, manifest, limits);
        using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var target = archive.CreateEntry(ManifestName, CompressionLevel.NoCompression).Open()) target.Write(raw);
                foreach (var item in manifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = Resolve(source, item.Path);
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    Require(input.Length == item.Bytes, "Export source length changed.");
                    using var target = archive.CreateEntry(item.Path, CompressionLevel.NoCompression).Open();
                    CopyAndVerify(input, target, item, cancellationToken);
                }
            }
            file.Flush(flushToDisk: true);
            file.Position = 0;
            PreflightZip(file, limits);
            return VerifyOpen(file, limits, cancellationToken);
        }
    }

    public static void Validate(ModelArtifactPackageManifest manifest, ModelArtifactPackageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        limits = Limits(limits);
        Require(manifest.Contract == Format && manifest.Version == Version, "Unsupported neutral model package contract/version.");
        Id(manifest.PackageId); Id(manifest.PackageVersion); Id(manifest.ModelId);
        Text(manifest.Publisher, 256); Text(manifest.License, 2048); Text(manifest.SourceRevision, 256);
        Require(manifest.Files is not null && manifest.Files.Count is > 0 && manifest.Files.Count <= limits.MaximumFiles,
            "Model package file inventory exceeds its bound.");
        var files = manifest.Files.Take(limits.MaximumFiles + 1).ToArray();
        Require(files.Length == manifest.Files.Count && files.Length <= limits.MaximumFiles, "Model file inventory changed.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in files)
        {
            Require(item is not null, "Null model file."); Relative(item.Path);
            Require(paths.Add(item.Path), "Duplicate or case-colliding model path.");
            Require(Roles.Contains(item.Role, StringComparer.Ordinal), "Unsupported model-data role.");
            Require(DataExtensions.Contains(Path.GetExtension(item.Path)) ||
                new[] { "LICENSE", "NOTICE", "COPYING" }.Contains(Path.GetFileName(item.Path), StringComparer.OrdinalIgnoreCase),
                "Code and unrecognized file kinds are not model data.");
            Require(item.Bytes > 0 && item.Bytes <= limits.MaximumFileBytes, "Model file length exceeds its bound.");
            Digest(item.Sha256); total = checked(total + item.Bytes);
        }
        Require(total <= limits.MaximumTotalBytes && files.Any(item => item.Role == "weights"), "Model package requires bounded weights data.");
        foreach (var path in paths)
            for (var separator = path.IndexOf('/'); separator >= 0; separator = path.IndexOf('/', separator + 1))
                Require(!paths.Contains(path[..separator]), "A model file is also used as a parent directory.");
        Require(manifest.Runtimes is not null && manifest.Runtimes.Count is > 0 and <= 64, "Runtime binding count exceeds its bound.");
        var runtimes = manifest.Runtimes.Take(65).ToArray();
        Require(runtimes.Length == manifest.Runtimes.Count && runtimes.Length <= 64, "Runtime bindings changed.");
        var identities = new HashSet<(string, string)>();
        foreach (var runtime in runtimes)
        {
            Require(runtime is not null, "Null runtime binding."); Id(runtime.RuntimeId); Id(runtime.RuntimeVersion); Digest(runtime.RuntimeSha256);
            ModelOperationContract.Validate(runtime.Profile);
            Require(identities.Add((runtime.RuntimeId, runtime.Profile.Id)), "Duplicate model runtime/profile binding.");
        }
    }

    private static FileStream OpenPackage(string path, ModelArtifactPackageLimits limits)
    {
        path = Ordinary(path);
        Require(IsPackagePath(path), "Neutral model packages require .CerebralModel.");
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { PreflightZip(file, limits); return file; }
        catch { file.Dispose(); throw; }
    }

    private static ModelArtifactPackageInspection VerifyOpen(FileStream file, ModelArtifactPackageLimits limits, CancellationToken token)
    {
        file.Position = 0;
        var packageHash = Hash(file, token);
        file.Position = 0;
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var (manifest, raw) = ReadManifest(archive, limits);
        var entries = Entries(archive, manifest);
        foreach (var item in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            using var input = entries[item.Path].Open();
            CopyAndVerify(input, Stream.Null, item, token);
        }
        // FileShare excludes concurrent writers on Windows. Rehash also detects an
        // ordinary source modified across this verification on platforms with advisory locks.
        file.Position = 0;
        Require(Hash(file, token) == packageHash, "Package container changed during verification.");
        return new(manifest, Hex(SHA256.HashData(raw)), packageHash, manifest.Files.Sum(item => item.Bytes));
    }

    private static (ModelArtifactPackageManifest Manifest, byte[] Raw) ReadManifest(ZipArchive archive, ModelArtifactPackageLimits limits)
    {
        var entries = archive.Entries.Where(item => item.FullName == ManifestName).ToArray();
        Require(entries.Length == 1 && entries[0].Length is > 0 and <= MaximumManifestBytes, "Package requires one bounded manifest.");
        using var input = entries[0].Open();
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        for (var count = input.Read(buffer); count > 0; count = input.Read(buffer))
        {
            Require(output.Length + count <= MaximumManifestBytes, "Manifest grew beyond its bound.");
            output.Write(buffer, 0, count);
        }
        var raw = output.ToArray();
        return (ParseManifest(raw, limits), raw);
    }

    private static ModelArtifactPackageManifest ParseManifest(byte[] raw, ModelArtifactPackageLimits limits)
    {
        Require(raw.Length is > 0 and <= MaximumManifestBytes, "Manifest exceeds its byte bound.");
        var reader = new Utf8JsonReader(raw, new JsonReaderOptions { MaxDepth = 24 });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.OrdinalIgnoreCase));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                throw Invalid("Duplicate model package manifest property.");
        }
        var manifest = JsonSerializer.Deserialize<ModelArtifactPackageManifest>(raw, ModelOperationContract.Json)
            ?? throw Invalid("Null model package manifest.");
        Validate(manifest, limits);
        return manifest with
        {
            Files = Array.AsReadOnly(manifest.Files.ToArray()),
            Runtimes = Array.AsReadOnly(manifest.Runtimes.Select(runtime => runtime with
            {
                Profile = runtime.Profile with { Operations = Array.AsReadOnly(runtime.Profile.Operations.ToArray()) }
            }).ToArray())
        };
    }

    private static byte[] WriteManifest(ModelArtifactPackageManifest manifest, ModelArtifactPackageLimits limits)
    {
        Validate(manifest, limits);
        var raw = JsonSerializer.SerializeToUtf8Bytes(manifest, ModelOperationContract.Json);
        Require(raw.Length <= MaximumManifestBytes, "Manifest exceeds its byte bound.");
        return raw;
    }

    private static Dictionary<string, ZipArchiveEntry> Entries(ZipArchive archive, ModelArtifactPackageManifest manifest)
    {
        Require(archive.Entries.Count == manifest.Files.Count + 1, "Package entry inventory differs from its manifest.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declared = manifest.Files.ToDictionary(item => item.Path, StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            Require(insensitive.Add(entry.FullName) && entries.TryAdd(entry.FullName, entry), "Repeated/colliding package entry.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            Require(unixType is 0 or 0x8000 && (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) == 0,
                "Linked or non-file package entry.");
            if (entry.FullName == ManifestName) continue;
            Relative(entry.FullName);
            Require(declared.TryGetValue(entry.FullName, out var item) && entry.Length == item.Bytes,
                "Undeclared package entry or mismatching length.");
        }
        return entries;
    }

    private static void CopyAndVerify(Stream input, Stream output, ModelArtifactPackageFile item, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long bytes = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = input.Read(buffer);
            if (count == 0) break;
            bytes = checked(bytes + count);
            Require(bytes <= item.Bytes, "Model data exceeds its declared length.");
            hash.AppendData(buffer, 0, count); output.Write(buffer, 0, count);
        }
        Require(bytes == item.Bytes && Hex(hash.GetHashAndReset()) == item.Sha256, "Model data digest/length mismatch.");
    }

    private static string Hash(Stream input, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        for (var count = input.Read(buffer); count > 0; count = input.Read(buffer))
        { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Hex(hash.GetHashAndReset());
    }

    private static void VerifyDirectory(string source, ModelArtifactPackageManifest manifest, ModelArtifactPackageLimits limits)
    {
        var expected = manifest.Files.Select(item => item.Path).ToHashSet(StringComparer.Ordinal);
        var actual = Inventory(source);
        if (actual.Remove(ManifestName))
        {
            var path = Resolve(source, ManifestName, manifest: true);
            Require(new FileInfo(path).Length <= MaximumManifestBytes, "Source manifest exceeds its bound.");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[MaximumManifestBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = input.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            Require(count <= MaximumManifestBytes, "Source manifest grew beyond its bound.");
            var stored = ParseManifest(bytes[..count], limits);
            Require(WriteManifest(stored, limits).SequenceEqual(WriteManifest(manifest, limits)), "Stored source manifest differs.");
        }
        Require(actual.SetEquals(expected), "Source contains undeclared data or executable artifacts.");
    }

    private static HashSet<string> Inventory(string root)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(root);
        var count = 0;
        while (pending.TryPop(out var directory))
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                Require(++count <= 16384, "Source directory inventory exceeds its bound.");
                Ordinary(entry.FullName);
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
                else result.Add(Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'));
            }
        return result;
    }

    private static void RemoveOwnedStage(string parent, string stage, ModelArtifactPackageManifest manifest)
    {
        Ordinary(parent); Ordinary(stage);
        Require(IsChild(parent, stage) && Path.GetFileName(stage).StartsWith(".cerebral-model-stage-", StringComparison.Ordinal), "Invalid owned stage.");
        var allowed = manifest.Files.Select(item => item.Path).Append(ManifestName).ToHashSet(StringComparer.Ordinal);
        Require(Inventory(stage).All(allowed.Contains), "Unrecognized extraction stage contents; inspect before cleanup.");
        Directory.Delete(stage, recursive: true);
    }

    private static string Resolve(string root, string relative, bool manifest = false)
    {
        if (!manifest) Relative(relative);
        else Require(relative == ManifestName, "Invalid manifest entry.");
        var path = Ordinary(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(IsChild(root, path), "Package path escapes its root.");
        return path;
    }

    private static string Ordinary(string path)
    {
        Require(Path.IsPathFullyQualified(path), "Package paths must be absolute host-selected paths.");
        path = Path.GetFullPath(path);
        for (string? cursor = path; cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            if (File.Exists(cursor) || Directory.Exists(cursor))
                Require((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, "Linked package paths are not admitted.");
            else
            {
                // File.Exists hides dangling links. Attributes still expose an existing link entry.
                try { Require((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, "Linked package paths are not admitted."); }
                catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            }
        }
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static bool IsChild(string parent, string path) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void Relative(string path)
    {
        Require(path is not null && path.Length <= 1024 && path.StartsWith("data/", StringComparison.Ordinal), "Model data requires a data/ relative path.");
        var parts = path!.Split('/');
        Require(parts.Length is >= 2 and <= 16, "Model data path depth exceeds its bound.");
        foreach (var part in parts)
        {
            Require(part.Length is > 0 and <= 128 && !part.EndsWith('.') &&
                Regex.IsMatch(part, "\\A[A-Za-z0-9][A-Za-z0-9._-]*\\z", RegexOptions.CultureInvariant), "Invalid portable model-data path.");
            var stem = part.Split('.')[0].ToUpperInvariant();
            Require(stem is not ("CON" or "PRN" or "AUX" or "NUL") &&
                !Regex.IsMatch(stem, "^(COM|LPT)[0-9]$", RegexOptions.CultureInvariant), "Reserved portable path component.");
        }
    }

    private static ModelArtifactPackageLimits Limits(ModelArtifactPackageLimits? limits)
    {
        limits ??= HardLimits;
        Require(limits.MaximumFiles is >= 1 && limits.MaximumFiles <= HardLimits.MaximumFiles &&
            limits.MaximumFileBytes is >= 1 && limits.MaximumFileBytes <= HardLimits.MaximumFileBytes &&
            limits.MaximumTotalBytes >= limits.MaximumFileBytes && limits.MaximumTotalBytes <= HardLimits.MaximumTotalBytes,
            "Package limits must remain within the fixed hard bounds.");
        return limits;
    }

    // Bound central-directory allocation before ZipArchive materializes its entry list, including ZIP64
    // packages with large tensor files. Split archives and self-extracting executable wrappers are rejected.
    private static void PreflightZip(FileStream file, ModelArtifactPackageLimits limits)
    {
        Require(file.Length >= 22 && file.Length <= limits.MaximumTotalBytes + MaximumManifestBytes + MaximumCentralDirectoryBytes,
            "Package container size exceeds its bound.");
        Span<byte> signature = stackalloc byte[4]; file.Position = 0; file.ReadExactly(signature);
        Require(BinaryPrimitives.ReadUInt32LittleEndian(signature) == 0x04034b50, "Package must be an ordinary ZIP container.");
        var tail = new byte[(int)Math.Min(file.Length, 65557)]; file.Position = file.Length - tail.Length; file.ReadExactly(tail);
        var found = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) == 0x06054b50 &&
                index + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, 2)) == tail.Length)
            { found = index; break; }
        Require(found >= 0, "ZIP end record is missing.");
        var end = tail.AsSpan(found);
        Require(BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) == 0 && BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) == 0,
            "Split ZIP packages are unsupported.");
        ulong entries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        ulong size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        ulong offset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        var endPosition = file.Length - tail.Length + found;
        if (entries == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            Require(endPosition >= 20, "ZIP64 locator is missing.");
            Span<byte> locator = stackalloc byte[20]; file.Position = endPosition - 20; file.ReadExactly(locator);
            Require(BinaryPrimitives.ReadUInt32LittleEndian(locator) == 0x07064b50 &&
                BinaryPrimitives.ReadUInt32LittleEndian(locator[4..]) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(locator[16..]) == 1,
                "Invalid ZIP64 locator.");
            var position = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
            Require(position <= (ulong)(endPosition - 76), "ZIP64 end record is outside its container.");
            Span<byte> record = stackalloc byte[56]; file.Position = (long)position; file.ReadExactly(record);
            Require(BinaryPrimitives.ReadUInt32LittleEndian(record) == 0x06064b50 &&
                BinaryPrimitives.ReadUInt64LittleEndian(record[4..]) is >= 44 and <= 1024 &&
                BinaryPrimitives.ReadUInt32LittleEndian(record[16..]) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(record[20..]) == 0,
                "Invalid ZIP64 end record.");
            entries = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
            Require(BinaryPrimitives.ReadUInt64LittleEndian(record[24..]) == entries, "Split ZIP64 entries are unsupported.");
            size = BinaryPrimitives.ReadUInt64LittleEndian(record[40..]); offset = BinaryPrimitives.ReadUInt64LittleEndian(record[48..]);
        }
        else Require(BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) == entries, "Split ZIP entries are unsupported.");
        Require(entries is > 0 && entries <= (ulong)limits.MaximumFiles + 1 && size <= MaximumCentralDirectoryBytes &&
            offset <= (ulong)endPosition && size <= (ulong)endPosition - offset, "ZIP directory exceeds its bound.");
        // Do not trust the end-record count to bound framework allocations: verify each
        // central entry framing/count before ZipArchive parses its object inventory.
        var directory = new byte[(int)size]; file.Position = (long)offset; file.ReadExactly(directory);
        var cursor = 0;
        ulong observed = 0;
        while (cursor < directory.Length)
        {
            Require(directory.Length - cursor >= 46 &&
                BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(cursor, 4)) == 0x02014b50,
                "Invalid ZIP central entry.");
            var entry = directory.AsSpan(cursor);
            var length = 46 + BinaryPrimitives.ReadUInt16LittleEndian(entry[28..]) +
                BinaryPrimitives.ReadUInt16LittleEndian(entry[30..]) + BinaryPrimitives.ReadUInt16LittleEndian(entry[32..]);
            Require(length <= directory.Length - cursor && ++observed <= (ulong)limits.MaximumFiles + 1,
                "ZIP entry inventory exceeds its bound.");
            cursor += length;
        }
        Require(observed == entries, "ZIP declared entry count differs from its actual directory.");
        file.Position = 0;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private static void Id(string value) => Require(value is not null && value.Length <= 128 &&
        Regex.IsMatch(value, "\\A[A-Za-z0-9][A-Za-z0-9._+-]*\\z", RegexOptions.CultureInvariant), "Invalid package identity.");
    private static void Text(string value, int maximum) => Require(!string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Contains('\0'), "Invalid package metadata.");
    private static void Digest(string value) => Require(value is { Length: 64 } && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'), "Invalid package SHA-256.");
    private static void Require([DoesNotReturnIf(false)] bool value, string message) { if (!value) throw Invalid(message); }
    private static InvalidDataException Invalid(string message) => new(message);
}
