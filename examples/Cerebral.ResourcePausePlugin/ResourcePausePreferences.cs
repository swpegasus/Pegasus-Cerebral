using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cerebral.ResourcePausePlugin;

internal sealed record ResourcePausePreferences(string? PreferredLocalModelId);
internal sealed record ResourcePausePreferenceDocument(ResourcePausePreferences Preferences, string Revision);

internal sealed class ResourcePausePreferenceStore
{
    internal const string FileName = "resource-pause.json";
    internal const int MaximumFileBytes = 4 * 1024;
    private const string MissingRevision = "missing";
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string directory;
    internal string Path { get; }

    internal ResourcePausePreferenceStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || !System.IO.Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("An absolute Resource Pause data directory is required.", nameof(dataDirectory));
        directory = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dataDirectory));
        Path = System.IO.Path.Combine(directory, FileName);
    }

    internal ResourcePausePreferenceDocument Load()
    {
        lock (Sync)
        {
            if (!File.Exists(Path)) return new(new(null), MissingRevision);
            var bytes = ReadCurrentBytes();
            if (bytes.Length is 0 or > MaximumFileBytes)
                throw new InvalidDataException($"{FileName} must contain 1-{MaximumFileBytes} bytes.");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Resource Pause settings must be an object.");
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 2 || properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
                properties.Any(item => item.Name is not ("schemaVersion" or "preferredLocalModelId")))
                throw new InvalidDataException("Resource Pause settings contain missing, duplicate, or unsupported fields.");
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var version) || version != 1)
                throw new InvalidDataException("Resource Pause settings use an unsupported schema version.");
            var preferred = root.GetProperty("preferredLocalModelId");
            var id = preferred.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => preferred.GetString(),
                _ => throw new InvalidDataException("preferredLocalModelId must be a string or null.")
            };
            if (id is not null) ValidateId(id);
            return new(new(id), Digest(bytes));
        }
    }

    internal ResourcePausePreferenceDocument Save(ResourcePausePreferences preferences, string expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.PreferredLocalModelId is not null) ValidateId(preferences.PreferredLocalModelId);
        if (string.IsNullOrWhiteSpace(expectedRevision)) throw new ArgumentException("Reload Resource Pause settings before saving.");
        lock (Sync)
        {
            var current = File.Exists(Path) ? Digest(ReadCurrentBytes()) : MissingRevision;
            if (!current.Equals(expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("Resource Pause settings changed since they were loaded. Refresh before saving; the current preference was preserved.");
            Directory.CreateDirectory(directory);
            RejectReparse(directory);
            if (File.Exists(Path)) RejectReparse(Path);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                preferredLocalModelId = preferences.PreferredLocalModelId
            }, WriteOptions);
            if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Resource Pause settings exceed their bound.");
            var temporary = System.IO.Path.Combine(directory, "." + FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                { stream.Write(bytes); stream.Flush(true); }
                File.Move(temporary, Path, true);
            }
            finally { try { File.Delete(temporary); } catch { } }
            return new(preferences, Digest(bytes));
        }
    }

    internal static void ValidateId(string id)
    {
        if (id.Length is < 1 or > 100 || id.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('.' or '_' or '-')))
            throw new InvalidDataException("Model IDs use 1-100 ASCII letters, digits, dots, underscores, or hyphens.");
    }

    private byte[] ReadCurrentBytes()
    {
        RejectReparse(directory); RejectReparse(Path);
        using var stream = File.OpenRead(Path);
        if (stream.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException($"{FileName} must contain 1-{MaximumFileBytes} bytes.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Resource Pause preference file changed size while reading.");
        return bytes;
    }

    private static void RejectReparse(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Resource Pause settings cannot use a filesystem link.");
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
