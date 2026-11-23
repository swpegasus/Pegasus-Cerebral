using System.IO.Compression;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private async Task<ModelRecord> ImportPackageAsync(string source, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(source)) throw new ArgumentException("Use an absolute package path on the service machine.");
        using var archive = ZipFile.OpenRead(source);
        if (archive.Entries.Count is < 2 or > 3 || archive.Entries.Count(x => x.FullName == "manifest.json") != 1)
            throw new InvalidDataException("A model package requires a single manifest and at most two component files.");
        var metadata = archive.GetEntry("manifest.json")!;
        if (metadata.Length > 65536) throw new InvalidDataException("Model package manifest exceeds 64 KiB.");
        ModelPackage package;
        await using (var input = metadata.Open())
        {
            using var buffer = new MemoryStream(); var chunk = new byte[8192];
            while (true)
            {
                var read = await input.ReadAsync(chunk, ct); if (read == 0) break;
                if (buffer.Length + read > 65536) throw new InvalidDataException("Expanded model manifest exceeds 64 KiB.");
                buffer.Write(chunk, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1) ||
                !root.TryGetProperty("contract", out var contract) || contract.ValueKind != JsonValueKind.String || contract.GetString() != "pegasus.cerebral.model" ||
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number is not (1 or 2) ||
                !root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Unsupported or missing model package contract, version or model metadata.");
            if (model.EnumerateObject().GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1))
                throw new InvalidDataException("Duplicate model metadata fields are not supported.");
            package = CerebralJson.Read<ModelPackage>(root);
        }
        ModelPackageContract.ValidateLayout(package, archive.Entries.Select(x => x.FullName));
        ValidatePackage(package);
        var weights = archive.GetEntry("model.gguf")!;
        if (weights.Length is < 4 or > 274877906944L) throw new InvalidDataException("Invalid model package weight size.");
        // Paths and identifiers from another installation never select a local destination.
        package.Model.Id = Guid.NewGuid().ToString("N");
        package.Model.PackageId = package.Package.Id;
        package.Model.PackageVersion = package.Package.Version;
        package.Model.PackagePurpose = package.Package.Purpose;
        package.Model.StarterPackage = package.Package.Starter;
        var projector = package.Version == 2 ? archive.GetEntry(ModelPackageContract.ProjectorName) : null;
        if (projector is not null && projector.Length is < 4 or > ModelPackageContract.MaximumProjectorBytes)
            throw new InvalidDataException("Invalid image component size.");
        // Neither imported path may select a local destination. Publish one registration only
        // after both complete files pass inspection; failure cleans only this fresh import.
        package.Model.VisionProjectorPath = null;
        await modelFiles.WaitAsync(ct);
        string? weightPath = null, componentPath = null; var committed = false;
        try
        {
            var manifest = CerebralJson.Element(package.Model);
            var model = NewModel(manifest, "");
            model.Path = ModelDestination(model.Id + ".gguf");
            if (projector is not null)
            {
                var destination = ModelDestination(model.Id + ".projector.gguf");
                await using var component = projector.Open();
                await ReceiveFileAsync(component, destination, projector.Length, ct);
                componentPath = destination;
                ModelRuntime.ValidateProjector(destination);
                model.VisionProjectorPath = destination;
            }
            await using (var content = weights.Open())
                await ReceiveFileAsync(content, model.Path, weights.Length, ct);
            weightPath = model.Path; model.SizeBytes = weights.Length;
            model = await ApplyModelMetadataAsync(model, manifest.TryGetProperty("contextTokens", out _), preserveDisplayName: true, ct);
            ct.ThrowIfCancellationRequested();
            store.Put("model", model.Id, model); committed = true;
            events.Publish("model.updated", null, model);
            return model;
        }
        finally
        {
            try
            {
                if (!committed)
                {
                    if (weightPath is not null) File.Delete(weightPath);
                    if (componentPath is not null) File.Delete(componentPath);
                }
            }
            finally { modelFiles.Release(); }
        }
    }

    private async Task<object> ExportPackageAsync(string id, string destination, string settingsMode, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(destination) || !ModelPackageContract.IsPackagePath(destination))
            throw new ArgumentException($"Use an absolute destination ending in {ModelPackageContract.PrimaryExtension} on the service machine.");
        settingsMode = settingsMode.Trim().ToLowerInvariant();
        if (settingsMode is not ("current" or "defaults"))
            throw new ArgumentException("Model package settingsMode must be current or defaults.");
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("Destination already exists. Choose a new package name.");
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Choose an existing export folder.");
        await modelFiles.WaitAsync(ct);
        try
        {
            var model = store.Get<ModelRecord>("model", id) ?? throw new ArgumentException("Model not found.");
            var copy = CerebralJson.Read<ModelRecord>(CerebralJson.Element(model));
            copy.Id = ""; copy.Path = ""; copy.State = "unloaded"; copy.Error = null;
            if (settingsMode == "defaults")
            {
                copy.ChatTemplate = null;
                copy.StopSequences = [];
                copy.AutomaticContextSizing = true;
                copy.MaximumContextTokens = 0;
                copy.ContextTokens = 4096;
                copy.ParallelRequests = 1;
                copy.GpuLayers = -1;
                copy.IdleMinutes = 5;
            }
            var projector = ModelComponentConfiguration.NormalizePath(model.VisionProjectorPath);
            if (projector is not null)
            {
                ModelRuntime.ValidateProjector(projector);
                if (new FileInfo(projector).Length is < 4 or > ModelPackageContract.MaximumProjectorBytes)
                    throw new InvalidDataException("Image component exceeds the portable package size limit.");
            }
            copy.VisionProjectorPath = projector is null ? null : ModelPackageContract.ProjectorName;
            copy.SourceUrl = PortablePackageUrl(copy.SourceUrl);
            copy.UpstreamModelUrl = PortablePackageUrl(copy.UpstreamModelUrl);
            copy.LicenseUrl = PortablePackageUrl(copy.LicenseUrl);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                {
                    using var zip = new ZipArchive(output, ZipArchiveMode.Create, true);
                    await using (var metadata = zip.CreateEntry("manifest.json", CompressionLevel.Fastest).Open())
                        await JsonSerializer.SerializeAsync(metadata, new ModelPackage
                        {
                            Version = projector is null ? 1 : 2,
                            Package = new()
                            {
                                Id = string.IsNullOrWhiteSpace(copy.PackageId) ? "pegasus.cerebral.export." + SanitizePackageId(copy.Name) : copy.PackageId,
                                Version = string.IsNullOrWhiteSpace(copy.PackageVersion) ? "1.0" : copy.PackageVersion,
                                Purpose = string.IsNullOrWhiteSpace(copy.PackagePurpose) ? "Portable model export" : copy.PackagePurpose,
                                SettingsMode = settingsMode,
                                Starter = copy.StarterPackage,
                                CreatedBy = "Pegasus Cerebral " + typeof(CommandDispatcher).Assembly.GetName().Version?.ToString(3)
                            },
                            Model = copy
                        }, CerebralJson.Options, ct);
                    // GGUF weights gain little from recompression; export streams one copy at the selected destination.
                    await using (var weights = zip.CreateEntry("model.gguf", CompressionLevel.NoCompression).Open())
                    await using (var input = new FileStream(model.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
                        await input.CopyToAsync(weights, 1024 * 1024, ct);
                    if (projector is not null)
                    {
                        await using var component = zip.CreateEntry(ModelPackageContract.ProjectorName, CompressionLevel.NoCompression).Open();
                        await using var input = new FileStream(projector, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
                        await input.CopyToAsync(component, 1024 * 1024, ct);
                    }
                }
                File.Move(temporary, destination, false);
                return new { path = destination, bytes = new FileInfo(destination).Length };
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { modelFiles.Release(); }
    }

    private static void ValidatePackage(ModelPackage package)
    {
        static void Text(string? value, int maximum, string label)
        {
            if (value is not null && (value.Length > maximum || value.Contains('\0') || value.Any(char.IsControl)))
                throw new InvalidDataException($"Model package {label} is invalid or exceeds {maximum} characters.");
        }
        static void Url(string? value, string label)
        {
            if (value is null) return;
            if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException($"Model package {label} must be HTTP(S) without credentials, queries or fragments.");
        }

        Text(package.Package.Id, 160, "ID");
        Text(package.Package.Version, 64, "version");
        Text(package.Package.Purpose, 256, "purpose");
        Text(package.Package.CreatedBy, 256, "creator");
        Text(package.Package.Notes, 4000, "notes");
        Text(package.Model.Name, 256, "model name");
        Text(package.Model.License, 128, "license identifier");
        Text(package.Model.Publisher, 256, "publisher");
        Text(package.Model.UpstreamModelId, 256, "upstream model ID");
        Text(package.Model.UpstreamRevision, 256, "upstream revision");
        Text(package.Model.Description, 4000, "description");
        if (package.Model.Tags.Count > 32 || package.Model.Tags.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64 || x.Contains('\0') || x.Any(char.IsControl)))
            throw new InvalidDataException("Model package tags must contain at most 32 short non-empty values.");
        Url(package.Model.SourceUrl, "artifact source URL");
        Url(package.Model.UpstreamModelUrl, "upstream model URL");
        Url(package.Model.LicenseUrl, "license URL");
    }

    private static string? PortablePackageUrl(string? value) =>
        value is not null && value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? value : null;

    private static string SanitizePackageId(string name)
    {
        var value = new string(name.ToLowerInvariant().Select(x => char.IsLetterOrDigit(x) ? x : '-').ToArray()).Trim('-');
        while (value.Contains("--", StringComparison.Ordinal)) value = value.Replace("--", "-", StringComparison.Ordinal);
        return string.IsNullOrEmpty(value) ? "model" : value[..Math.Min(value.Length, 80)];
    }
}
