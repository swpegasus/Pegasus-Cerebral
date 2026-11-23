namespace Cerebral.Core;

public sealed class ModelRepositoryScan
{
    public string Directory { get; set; } = "";
    public List<DiscoveredModel> Models { get; set; } = [];
    public List<string> Issues { get; set; } = [];
    public bool Truncated { get; set; }
}
public sealed class DiscoveredModel
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? RegisteredId { get; set; }
    public string? Error { get; set; }
    public string Architecture { get; set; } = "";
    public string? DeclaredType { get; set; }
    public string Quantization { get; set; } = "";
    public int? NativeContextTokens { get; set; }
    public string Purpose { get; set; } = "General";
    public bool HasEmbeddedChatTemplate { get; set; }
    public string? MetadataWarning { get; set; }
    public string Status => Error ?? (RegisteredId is null ? "Available to import" : "Registered");
    public string Size => $"{SizeBytes / 1073741824d:N2} GiB";
}
public sealed class HuggingFaceModelList
{
    public string Repository { get; set; } = "";
    public string Revision { get; set; } = "";
    public string? License { get; set; }
    public List<HuggingFaceFile> Files { get; set; } = [];
    public bool Truncated { get; set; }
}
public sealed class HuggingFaceFile
{
    public string FileName { get; set; } = "";
    public long? SizeBytes { get; set; }
    public string Url { get; set; } = "";
    public string Label => FileName + (SizeBytes is { } size ? $" · {size / 1073741824d:N2} GiB" : "");
}
public sealed class ModelPackage
{
    public string Contract { get; set; } = "pegasus.cerebral.model";
    public int Version { get; set; } = 1;
    public ModelPackageMetadata Package { get; set; } = new();
    public ModelRecord Model { get; set; } = new();
}
public sealed class ModelPackageMetadata
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "1.0";
    public string Purpose { get; set; } = "Optional model";
    /// <summary>Portable runtime tuning carried by this package: current or defaults.</summary>
    public string SettingsMode { get; set; } = "current";
    public bool Starter { get; set; }
    public string? CreatedBy { get; set; }
    public string? Notes { get; set; }
}
