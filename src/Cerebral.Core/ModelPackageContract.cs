namespace Cerebral.Core;

/// <summary>Portable package v1 contains weights only; v2 adds one explicitly selected image component.</summary>
public static class ModelPackageContract
{
    public const string Format = "pegasus.cerebral.model";
    public const string PrimaryExtension = ".CerebralLLM";
    public const string LegacyExtension = ".cerebral-model.zip";
    public const string ManifestName = "manifest.json";
    public const string ModelName = "model.gguf";
    public const string ProjectorName = "projector.gguf";
    public const long MaximumProjectorBytes = 32L * 1024 * 1024 * 1024;

    public static bool IsPackagePath(string path) =>
        path.EndsWith(PrimaryExtension, StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(LegacyExtension, StringComparison.OrdinalIgnoreCase);

    public static void ValidateLayout(ModelPackage package, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Contract != Format || package.Version is not (1 or 2) || package.Model is null || package.Package is null)
            throw new InvalidDataException("Unsupported model package contract, version or metadata.");
        if (package.Package.SettingsMode is not ("current" or "defaults"))
            throw new InvalidDataException("Model package settingsMode must be current or defaults.");
        if (package.Version == 1 && package.Model.VisionProjectorPath is not null)
            throw new InvalidDataException("Version 1 cannot carry an image component. Export a version 2 package with the component bytes.");
        if (package.Version == 2 && package.Model.VisionProjectorPath != ProjectorName)
            throw new InvalidDataException("Version 2 requires its image component at the fixed projector.gguf entry.");
        string[] expected = package.Version == 1 ? [ManifestName, ModelName] : [ManifestName, ModelName, ProjectorName];
        var entries = names.ToArray();
        if (entries.Length != expected.Length || !entries.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Model package entries must exactly match the supported manifest, weights and optional projector. Paths and extra entries are not accepted.");
    }
}
