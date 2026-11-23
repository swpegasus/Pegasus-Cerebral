using System.IO;
using System.Text;
using System.Text.Json;

namespace Cerebral.ChatIconsPlugin;

/// <summary>Plugin-owned, selected-only opaque bytes. Cache integrity never implies renderer admission.</summary>
public sealed class CortexIconCache
{
    public const int MaximumEntries = 16;
    public const long MaximumTotalBytes = 8 * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string root;
    private sealed record Binding(string Provider, string Namespace, string Ref, string Id,
        string Sha256, string MimeType, long Size, string MetadataSha256);

    public CortexIconCache(string pluginDataDirectory)
    {
        if (!Path.IsPathFullyQualified(pluginDataDirectory)) throw new ArgumentException("An exact plugin data root is required.");
        root = Path.Combine(Path.GetFullPath(pluginDataDirectory), "cortex-icons-v2");
        RejectLinks(root);
    }

    private static Binding Identity(string provider, CortexIconMetadata metadata) =>
        new(provider, metadata.Namespace, metadata.Preview.Ref, metadata.Preview.Id, metadata.Sha256,
            metadata.Preview.MimeType, metadata.Preview.Size,
            CortexIconLibrary.Digest(Encoding.UTF8.GetBytes(metadata.RawJson)));

    private string Entry(string provider, CortexIconMetadata metadata) =>
        Path.Combine(root, CortexIconLibrary.Digest(Encoding.UTF8.GetBytes(provider + "\0" + metadata.Preview.Ref)));

    public async Task<byte[]?> ReadAsync(string provider, CortexIconMetadata metadata, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            var directory = Entry(provider, metadata); RejectLinks(directory);
            if (!Directory.Exists(directory)) return null;
            var bindingPath = Path.Combine(directory, "binding.json");
            var contentPath = Path.Combine(directory, "content.bin");
            RejectLinks(bindingPath); RejectLinks(contentPath);
            if (new FileInfo(bindingPath).Length > 4096 || new FileInfo(contentPath).Length != metadata.Preview.Size ||
                metadata.Preview.Size is < 1 or > CortexIconLibrary.MaximumAssetBytes)
                throw new InvalidDataException("Selected cache entry exceeds its binding limits.");
            var binding = JsonSerializer.Deserialize<Binding>(await File.ReadAllTextAsync(bindingPath, token));
            if (binding != Identity(provider, metadata)) throw new InvalidDataException("Selected cache metadata binding changed.");
            var bytes = await File.ReadAllBytesAsync(contentPath, token);
            token.ThrowIfCancellationRequested();
            if (bytes.Length != metadata.Preview.Size || CortexIconLibrary.Digest(bytes) != metadata.Sha256)
                throw new InvalidDataException("Selected cached content failed its full digest.");
            return bytes;
        }
        finally { Gate.Release(); }
    }

    public async Task SaveAsync(string provider, CortexIconMetadata metadata, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length is < 1 or > CortexIconLibrary.MaximumAssetBytes || bytes.Length != metadata.Preview.Size ||
            CortexIconLibrary.Digest(bytes) != metadata.Sha256)
            throw new InvalidDataException("Only fully verified selected bytes may enter the cache.");
        await Gate.WaitAsync(token);
        string? stage = null;
        try
        {
            RejectLinks(root); Directory.CreateDirectory(root); RejectLinks(root);
            var target = Entry(provider, metadata);
            if (Directory.Exists(target)) return; // Existing identities are never silently repaired or replaced.
            var entries = Directory.EnumerateDirectories(root).Take(MaximumEntries + 1).ToArray();
            if (entries.Length > MaximumEntries || entries.Any(path => !IsDigest(Path.GetFileName(path))))
                throw new InvalidDataException("Cache contains unexpected entries; cache writing is disabled.");
            var sizes = entries.Select(path =>
            {
                RejectLinks(path);
                var files = Directory.EnumerateFiles(path).Take(3).ToArray();
                if (files.Length != 2 || files.Any(file => Path.GetFileName(file) is not ("binding.json" or "content.bin")))
                    throw new InvalidDataException("Cache entry contains unexpected files.");
                foreach (var file in files) RejectLinks(file);
                var size = new FileInfo(Path.Combine(path, "content.bin")).Length;
                if (size is < 1 or > CortexIconLibrary.MaximumAssetBytes) throw new InvalidDataException("Cache entry size is invalid.");
                return (Path: path, Size: size, Time: Directory.GetLastWriteTimeUtc(path));
            }).OrderBy(item => item.Time).ToList();
            while (sizes.Count >= MaximumEntries || sizes.Sum(item => item.Size) + bytes.Length > MaximumTotalBytes)
            {
                var oldest = sizes[0];
                File.Delete(Path.Combine(oldest.Path, "binding.json"));
                File.Delete(Path.Combine(oldest.Path, "content.bin"));
                Directory.Delete(oldest.Path); sizes.RemoveAt(0);
            }
            token.ThrowIfCancellationRequested();
            stage = Path.Combine(root, ".stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            await File.WriteAllBytesAsync(Path.Combine(stage, "content.bin"), bytes, token);
            await File.WriteAllTextAsync(Path.Combine(stage, "binding.json"), JsonSerializer.Serialize(Identity(provider, metadata)), token);
            token.ThrowIfCancellationRequested();
            Directory.Move(stage, target); stage = null;
        }
        finally
        {
            if (stage is not null && Directory.Exists(stage))
            {
                File.Delete(Path.Combine(stage, "binding.json")); File.Delete(Path.Combine(stage, "content.bin"));
                Directory.Delete(stage);
            }
            Gate.Release();
        }
    }

    private static bool IsDigest(string value) => value.Length == 64 &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cortex icon storage cannot use a link or reparse target.");
    }
}

/// <summary>Selection epochs protect UI/cache admission even if an upstream request ignores cancellation.</summary>
public sealed class CortexIconSelection : IDisposable
{
    private CancellationTokenSource? lifetime;
    private long generation;
    public CancellationToken Token => lifetime?.Token ?? new CancellationToken(true);
    public (long Generation, CancellationToken Token) Begin(CancellationToken parent)
    {
        lifetime?.Cancel(); lifetime?.Dispose();
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(parent);
        return (++generation, lifetime.Token);
    }
    public bool IsCurrent(long expected) => expected == generation && lifetime is { IsCancellationRequested: false };
    public void Dispose() { ++generation; lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; }
}
