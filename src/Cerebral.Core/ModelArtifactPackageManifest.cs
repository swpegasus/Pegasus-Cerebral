using Cerebral.Contracts.ModelOperations;

namespace Cerebral.Core;

/// <summary>Data inventory only. Role never selects code, an executable, a mount, or a privileged path.</summary>
public sealed record ModelArtifactPackageFile(string Path, string Role, long Bytes, string Sha256);
/// <summary>Reference to an independently admitted trusted runtime. Model packages contain no runtime code.</summary>
public sealed record ModelArtifactPackageRuntime(string RuntimeId, string RuntimeVersion,
    string RuntimeSha256, ModelOperationProfile Profile);
public sealed record ModelArtifactPackageManifest(string Contract, int Version, string PackageId,
    string PackageVersion, string ModelId, string Publisher, string License, string SourceRevision,
    IReadOnlyList<ModelArtifactPackageFile> Files, IReadOnlyList<ModelArtifactPackageRuntime> Runtimes);

/// <summary>Caller may lower these hard package limits. They are installation I/O bounds, not fit/admission promises.</summary>
public sealed record ModelArtifactPackageLimits(int MaximumFiles = 4096,
    long MaximumFileBytes = 64L * 1024 * 1024 * 1024, long MaximumTotalBytes = 256L * 1024 * 1024 * 1024);
public sealed record ModelArtifactPackageInspection(ModelArtifactPackageManifest Manifest,
    string ManifestSha256, string PackageSha256, long PayloadBytes);
public sealed record ModelArtifactPackageExtraction(ModelArtifactPackageInspection Package, string Directory);
