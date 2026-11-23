using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Bounded discovery only: scanning never moves files or loads a model.</summary>
public sealed class ModelRepository(string directory)
{
    public async Task<ModelRepositoryScan> ScanAsync(IReadOnlyList<ModelRecord> registered, CancellationToken ct)
    {
        var root = Path.GetFullPath(directory);
        var result = new ModelRepositoryScan { Directory = root };
        if (!Directory.Exists(root)) { result.Issues.Add("Repository folder does not exist yet. Importing or downloading creates it."); return result; }
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Select the actual model repository folder, not a link.");
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
                if (result.Models.Count >= 512) { result.Truncated = true; break; }
                var candidate = new DiscoveredModel { Path = path, Name = Path.GetFileNameWithoutExtension(path), SizeBytes = new FileInfo(path).Length,
                    RegisteredId = registered.FirstOrDefault(x => Path.GetFullPath(x.Path).Equals(path, comparer))?.Id };
                try
                {
                    if (IsSplitFile(Path.GetFileName(path))) throw new InvalidDataException("Split GGUF packages are not supported by this importer. Choose a single-file GGUF.");
                    var metadata = await Task.Run(() => GgufMetadataInspector.Inspect(path, ct), ct);
                    ModelMetadataDefaults.Apply(candidate, metadata);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { candidate.Error = ex.Message; }
                result.Models.Add(candidate);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Issues.Add(ex.Message); }
        return result;
    }

    public static bool IsSplitFile(string name) => Regex.IsMatch(name, @"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static string RepositoryId(string input)
    {
        var value = input.Trim().TrimEnd('/');
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || uri.Host != "huggingface.co" || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Use a public Hugging Face repository ID or its HTTPS repository URL.");
            value = uri.AbsolutePath.Trim('/');
        }
        if (!Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant) || value.Length > 200)
            throw new ArgumentException("Enter an owner/model repository ID, for example unsloth/Qwen3.5-9B-GGUF.");
        return value;
    }

    public static string DownloadUrl(string repository, string revision, string file)
    {
        repository = RepositoryId(repository);
        if (revision.Length is < 1 or > 200 || revision.Any(char.IsControl)) throw new ArgumentException("Invalid model revision.");
        var segments = file.Split('/');
        if (segments.Any(x => x.Length == 0 || x is "." or ".." || x.Contains('\\') || x.Any(char.IsControl)) || !file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || IsSplitFile(file))
            throw new ArgumentException("Choose a single-file GGUF path from the repository.");
        return "https://huggingface.co/" + repository + "/resolve/" + Uri.EscapeDataString(revision) + "/" + string.Join('/', segments.Select(Uri.EscapeDataString)) + "?download=true";
    }

    public static async Task<HuggingFaceModelList> HuggingFaceAsync(string repository, string revision, CancellationToken ct)
    {
        repository = RepositoryId(repository);
        _ = DownloadUrl(repository, revision, "validate.gguf");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.GetAsync("https://huggingface.co/api/models/" + repository + "/revision/" + Uri.EscapeDataString(revision) + "?blobs=true", HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode is 401 or 403) throw new IOException("This repository requires publisher access or authentication. Import an already-authorized local GGUF file.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, ct); if (read == 0) break;
            if (buffer.Length + read > 4 * 1024 * 1024) throw new IOException("Repository listing is too large. Use a direct GGUF download URL.");
            buffer.Write(bytes, 0, read);
        }
        using var doc = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        var root = doc.RootElement;
        var resolved = root.TryGetProperty("sha", out var sha) && sha.ValueKind == JsonValueKind.String ? sha.GetString()! : revision;
        string? license = root.TryGetProperty("cardData", out var card) && card.ValueKind == JsonValueKind.Object && card.TryGetProperty("license", out var term) && term.ValueKind == JsonValueKind.String ? term.GetString() : null;
        var result = new HuggingFaceModelList { Repository = repository, Revision = resolved, License = license };
        if (!root.TryGetProperty("siblings", out var siblings) || siblings.ValueKind != JsonValueKind.Array) throw new IOException("Publisher returned no model file list.");
        foreach (var entry in siblings.EnumerateArray())
        {
            if (!entry.TryGetProperty("rfilename", out var name) || name.GetString() is not { } file || !file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || IsSplitFile(file)) continue;
            if (result.Files.Count == 256) { result.Truncated = true; break; }
            long? size = entry.TryGetProperty("size", out var length) && length.TryGetInt64(out var count) ? count : null;
            if (size is null && entry.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object && lfs.TryGetProperty("size", out length) && length.TryGetInt64(out count)) size = count;
            result.Files.Add(new HuggingFaceFile { FileName = file, SizeBytes = size, Url = DownloadUrl(repository, resolved, file) });
        }
        return result;
    }
}
