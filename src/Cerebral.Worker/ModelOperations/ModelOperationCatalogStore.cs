using System.Text.Json;
using Cerebral.Contracts.ModelOperations;
using Cerebral.Core;

namespace Cerebral.Worker.ModelOperations;

internal sealed record InstalledOperationModel(string Id, string Name, string WorkerInstallationId,
    ModelArtifactPackageInspection Package, string Directory, DateTimeOffset InstalledAt);

/// <summary>Package installation is data admission only. It cannot load code, select an executable
/// or turn a declared capability into a runnable profile.</summary>
public sealed class ModelOperationCatalogStore
{
    private const string Kind = "model-operation-model";
    private readonly StateStore store;
    private readonly string installationId;
    private readonly string packageRoot;
    private readonly object importGate = new();
    public ModelOperationCatalogStore(StateStore store, string installationId, string modelDirectory)
    {
        this.store = store;
        this.installationId = ModelOperationArtifactStore.Identifier(installationId);
        if (!Path.IsPathFullyQualified(modelDirectory)) throw new ArgumentException("Model directory must be an owner-configured absolute path.");
        packageRoot = Path.Combine(Path.GetFullPath(modelDirectory), "OperationPackages");
        ModelOperationArtifactStore.Ordinary(packageRoot);
    }

    public ModelOperationCatalogEntry Import(string source, string expectedSha256, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(source) || !ModelArtifactPackage.IsPackagePath(source) ||
            !ModelOperationArtifactStore.IsDigest(expectedSha256)) throw new ArgumentException("Import requires an absolute .CerebralModel and exact SHA-256.");
        ModelOperationArtifactStore.Ordinary(source);
        var inspection = ModelArtifactPackage.Verify(source, cancellationToken: token);
        if (inspection.PackageSha256 != expectedSha256) throw new InvalidDataException("Model package differs from its admitted digest.");
        // This synchronous gate serializes data installation. Model or runtime activation does
        // not happen here, and foreign model IDs never select local directories or record IDs.
        lock (importGate)
        {
            var existing = store.List<InstalledOperationModel>(Kind).SingleOrDefault(x => x.Package.PackageSha256 == expectedSha256);
            if (existing is not null) return Passive(existing);
            ModelOperationArtifactStore.Ordinary(packageRoot); Directory.CreateDirectory(packageRoot); ModelOperationArtifactStore.Ordinary(packageRoot);
            var destination = Path.Combine(packageRoot, expectedSha256);
            if (Directory.Exists(destination)) throw new InvalidOperationException("Package bytes exist without an accepted registration; reconcile the original import.");
            var extracted = ModelArtifactPackage.Extract(source, destination, cancellationToken: token);
            if (extracted.Package.PackageSha256 != expectedSha256) throw new InvalidDataException("Package changed between verification and extraction.");
            token.ThrowIfCancellationRequested();
            var id = Guid.NewGuid().ToString("N");
            var model = new InstalledOperationModel(id, inspection.Manifest.ModelId, installationId,
                extracted.Package, destination, DateTimeOffset.UtcNow);
            store.Put(Kind, id, model);
            return Passive(model);
        }
    }

    public ModelOperationCatalog List(Func<ModelOperationCatalogEntry, ModelOperationCatalogEntry>? observe = null)
        => new(ModelOperationContract.Version, store.List<InstalledOperationModel>(Kind).Select(Passive)
            .Select(x => observe is null ? x : observe(x)).ToArray());

    public ModelOperationDescription Describe(string id, Func<ModelOperationCatalogEntry, ModelOperationCatalogEntry>? observe = null)
    {
        var model = Passive(GetInstalled(id));
        return new(ModelOperationContract.Version, observe is null ? model : observe(model), SettingsSchema());
    }

    internal InstalledOperationModel GetInstalled(string id)
    {
        var value = store.Get<InstalledOperationModel>(Kind, ModelOperationArtifactStore.Identifier(id)) ?? throw new KeyNotFoundException("Model operation package is not installed.");
        if (value.WorkerInstallationId != installationId) throw new InvalidDataException("Model package belongs to another Worker installation.");
        ModelOperationArtifactStore.Ordinary(value.Directory);
        return value;
    }

    private ModelOperationCatalogEntry Passive(InstalledOperationModel model)
    {
        if (model.WorkerInstallationId != installationId) throw new InvalidDataException("Model catalog installation identity differs.");
        var profiles = model.Package.Manifest.Runtimes.Select(x => new ModelOperationProfileReadiness(x.Profile,
            false, UnavailableReason: "Runtime handler, exact package and hardware profile have not been qualified on this Worker.")).ToArray();
        return new(model.Id, model.Name, installationId, "installed", "A model package declaration is not runtime readiness.", null, profiles);
    }

    public static JsonElement SettingsSchema()
    {
        // Public settings are a closed, versioned surface. The runtime profile and strict
        // contract validator remain authoritative over combinations and supported defaults.
        using var document = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"properties":{
          "pipelineType":{"enum":["512","1024","1024_cascade","1536_cascade"]},
          "resolution":{"enum":[512,1024,1536]},"numSamples":{"type":"integer","minimum":1,"maximum":4},
          "preprocessImage":{"type":"boolean"},"maxNumTokens":{"type":"integer","minimum":1,"maximum":49152},
          "seed":{"type":"integer","minimum":0,"maximum":2147483647},
          "textureResolution":{"enum":[1024,2048,3072,4096]},
          "sparseStructure":{"$ref":"#/$defs/sampler"},"shape":{"$ref":"#/$defs/sampler"},"texture":{"$ref":"#/$defs/sampler"},
          "export":{"type":"object","additionalProperties":false,"properties":{
            "decimationTarget":{"type":"integer","minimum":100000,"maximum":1000000},"remesh":{"type":"boolean"},
            "remeshBand":{"enum":[1]},"remeshProjection":{"type":"number","minimum":0,"maximum":1}}}
        },"$defs":{"sampler":{"type":"object","additionalProperties":false,"properties":{
          "steps":{"type":"integer","minimum":1,"maximum":50},"guidanceStrength":{"type":"number","minimum":1,"maximum":10},
          "guidanceRescale":{"type":"number","minimum":0,"maximum":1},"timeRescale":{"type":"number","minimum":1,"maximum":6},
          "guidanceInterval":{"type":"object","additionalProperties":false,"required":["start","end"],"properties":{
            "start":{"type":"number","minimum":0,"maximum":1},"end":{"type":"number","minimum":0,"maximum":1}}}
        }}}}
        """);
        return document.RootElement.Clone();
    }
}
