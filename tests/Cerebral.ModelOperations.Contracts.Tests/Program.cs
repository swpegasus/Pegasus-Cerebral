using System.IO.Compression;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.ModelOperations;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Engine.Contracts;

// All model bytes, artifacts and runtimes here are inert fixtures. No provider,
// container, network, renderer, model load or installed-state operation is performed.
var tests = new (string Name, Action Run)[]
{
    ("typed_request_roundtrip_and_four_pipeline_names", ContractTests.Roundtrip),
    ("frozen_request_does_not_follow_caller_collection", ContractTests.Freeze),
    ("texture_and_retexture_resolution_bounds", ContractTests.TextureBounds),
    ("sampler_finiteness_intervals_and_ui_bounds", ContractTests.SamplerBounds),
    ("samples_tokens_and_export_policy_bounds", ContractTests.OptionBounds),
    ("strict_json_rejects_duplicate_unknown_and_numeric_enum", ContractTests.StrictJson),
    ("batch_maximum_and_duplicate_item_identity", ContractTests.BatchBounds),
    ("input_operation_media_and_role_contract", ContractTests.InputRoles),
    ("result_binds_exact_worker_job_effect_and_opaque_refs", ContractTests.ResultIdentity),
    ("output_aggregate_cap_and_unknown_uncertainty", ContractTests.ResultBounds),
    ("runtime_descriptor_profiles_are_passive_and_closed", ContractTests.Descriptor),
    ("legacy_interfaces_and_gguf_package_contract_unchanged", ContractTests.Legacy),
    ("neutral_package_export_verify_extract_roundtrip", PackageTests.Roundtrip),
    ("create_only_export_and_extract_preserve_prior_outputs", PackageTests.Collision),
    ("hash_mismatch_rejected_before_destination_creation", PackageTests.HashMismatch),
    ("relative_path_code_role_and_collision_admission", PackageTests.ManifestPaths),
    ("exact_archive_inventory_duplicate_case_and_symlink", PackageTests.ArchiveInventory),
    ("strict_manifest_unknown_duplicate_and_legacy_version", PackageTests.StrictManifest),
    ("package_file_and_total_limits_enforced", PackageTests.Limits),
    ("central_directory_count_checked_before_framework_parse", PackageTests.DirectoryCount),
    ("zip64_end_record_bounded_small_fixture", PackageTests.Zip64),
    ("export_source_inventory_and_output_placement", PackageTests.SourceInventory),
    ("canceled_package_work_creates_no_output", PackageTests.Precancel),
    ("runtime_binding_mode_digest_and_duplicate_profile", PackageTests.RuntimeBindings)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + " " + error); }
}
Console.WriteLine($"RESULT total={tests.Length} passed={tests.Length - failed} failed={failed} evidence=CPU-contract-and-inert-package-fixtures-only");
return failed == 0 ? 0 : 1;

static class Check
{
    internal static void True(bool value, string message = "Assertion failed.") { if (!value) throw new Exception(message); }
    internal static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException) { return; }
        throw new Exception("Expected structural refusal.");
    }
    internal static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    internal static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

static class ContractTests
{
    internal static readonly string Digest = new('a', 64);
    internal static ModelOperationRuntimeBinding Binding => new("worker-a", "fixture-runtime", "1.0", Digest,
        "fixture-model", "fixture-package", "1.0", Digest, "linux-arm64-cuda-fixture", Digest, "instance-a");
    internal static ModelOperationArtifactReference Artifact(string media = "image/png", string id = "input-a") =>
        new("worker-a", "source-job", "source-effect", id, 1, media, "fixture.media/1", "source", 8, Digest);
    internal static ModelOperationRequest Request => new(1, "request-a", "job-a", "effect-a", ModelOperationContract.ImageTo3D,
        Binding, [new("image", Artifact())], new());
    internal static ModelOperationProfile Profile => new("linux-arm64-cuda-fixture", "container", "linux-arm64", "cuda", Digest,
        [ModelOperationContract.ImageTo3D, ModelOperationContract.MeshRetexture]);
    internal static ModelOperationResult Result => new(1, "request-a", "job-a", "effect-a", Binding,
        ModelOperationState.Succeeded, false, [new("mesh", Artifact("model/gltf-binary", "output-a") with
            { JobId = "job-a", EffectId = "effect-a", Role = "mesh" })]);

    internal static void Roundtrip()
    {
        foreach (var pipeline in Enum.GetValues<ModelOperationPipelineType>())
        {
            var request = Request with { Options = new() { PipelineType = pipeline, TextureResolution = 4096 } };
            var raw = ModelOperationContract.Write(request);
            var read = ModelOperationContract.Read<ModelOperationRequest>(raw);
            Check.True(read.Binding == Binding && read.Options.PipelineType == pipeline && read.Options.TextureResolution == 4096);
        }
        Check.True(Encoding.UTF8.GetString(ModelOperationContract.Write(Request with
            { Options = new() { PipelineType = ModelOperationPipelineType.Resolution1024 } })).Contains("\"pipelineType\":\"1024\""));
    }
    internal static void Freeze()
    {
        var inputs = new List<ModelOperationInput>(Request.Inputs);
        var frozen = ModelOperationContract.Freeze(Request with { Inputs = inputs });
        inputs[0] = new("mesh", Artifact("model/gltf-binary"));
        Check.True(frozen.Inputs[0].Role == "image");
        ModelOperationContract.Validate(frozen);
    }
    internal static void TextureBounds()
    {
        foreach (var resolution in new[] { 1024, 2048, 3072, 4096 }) ModelOperationContract.Validate(new ModelOperationOptions { TextureResolution = resolution });
        foreach (var resolution in new[] { -1, 0, 512, 4097, 8192 }) Check.Reject(() => ModelOperationContract.Validate(new ModelOperationOptions { TextureResolution = resolution }));
        foreach (var resolution in new[] { 512, 1024, 1536 }) ModelOperationContract.Validate(new ModelOperationOptions { Resolution = resolution });
        Check.Reject(() => ModelOperationContract.Validate(new ModelOperationOptions { Resolution = 4096 }));
    }
    internal static void SamplerBounds()
    {
        ModelOperationContract.Validate(new ModelOperationOptions { SparseStructure = new(50, 10, 1, new(0, 1), 6) });
        foreach (var sampler in new[] { new ModelOperationSamplerOptions(Steps: 51), new(GuidanceStrength: 0),
                     new(GuidanceRescale: double.NaN), new(TimeRescale: double.PositiveInfinity),
                     new(GuidanceInterval: new(0.8, 0.2)), new(GuidanceInterval: new(-0.1, 1)) })
            Check.Reject(() => ModelOperationContract.Validate(new ModelOperationOptions { Texture = sampler }));
    }
    internal static void OptionBounds()
    {
        ModelOperationContract.Validate(new ModelOperationOptions { NumSamples = 4, MaxNumTokens = 49152, Export = new(1000000, true, 1, 0.9) });
        foreach (var options in new[] { new ModelOperationOptions { NumSamples = 5 }, new() { MaxNumTokens = 49153 },
                     new() { Seed = -1 }, new() { Export = new(99999) }, new() { Export = new(RemeshBand: 2) },
                     new() { Export = new(RemeshProjection: double.NegativeInfinity) } }) Check.Reject(() => ModelOperationContract.Validate(options));
    }
    internal static void StrictJson()
    {
        var valid = Encoding.UTF8.GetString(ModelOperationContract.Write(Request));
        foreach (var invalid in new[] { valid.Replace("\"version\":1", "\"version\":1,\"version\":1"),
                     valid.Replace("\"version\":1", "\"version\":1,\"Version\":1"),
                     valid[..^1] + ",\"executable\":\"not-admitted\"}",
                     valid.Replace("\"pipelineType\":null", "\"pipelineType\":0"),
                     valid.Replace("\"numSamples\":null", "\"numSamples\":1,\"kwargs\":{}") })
            Check.Reject(() => ModelOperationContract.Read<ModelOperationRequest>(Encoding.UTF8.GetBytes(invalid)));
    }
    internal static void BatchBounds()
    {
        var items = Enumerable.Range(0, 64).Select(index => new ModelOperationSubmissionItem("item-" + index, Request.Inputs, new())).ToArray();
        var request = new ModelOperationSubmissionRequest(1, "batch", "fixture-model", ModelOperationContract.ImageTo3D, items);
        ModelOperationContract.Validate(request);
        Check.Reject(() => ModelOperationContract.Validate(request with { Items = [.. items, new("extra", Request.Inputs, new())] }));
        Check.Reject(() => ModelOperationContract.Validate(request with { Items = [items[0], items[0]] }));
    }
    internal static void InputRoles()
    {
        ModelOperationContract.Validate(Request with { Operation = ModelOperationContract.MeshRetexture,
            Inputs = [new("mesh", Artifact("model/gltf-binary")), new("appearance", Artifact())] });
        Check.Reject(() => ModelOperationContract.Validate(Request with { Inputs = [new("image", Artifact("model/gltf-binary"))] }));
        Check.Reject(() => ModelOperationContract.Validate(Request with { Operation = ModelOperationContract.MeshRetexture }));
        Check.Reject(() => ModelOperationContract.Validate(Request with { Operation = "shell" }));
        Check.Reject(() => ModelOperationContract.Validate(Request with { JobId = "job-a\n" }));
    }
    internal static void ResultIdentity()
    {
        ModelOperationContract.Validate(Result);
        foreach (var artifact in new[] { Result.Outputs[0].Artifact with { WorkerInstallationId = "foreign" },
                     Result.Outputs[0].Artifact with { JobId = "foreign" },
                     Result.Outputs[0].Artifact with { EffectId = "foreign" }, Result.Outputs[0].Artifact with { Revision = 0 },
                     Result.Outputs[0].Artifact with { Bytes = ModelOperationContract.MaximumArtifactBytes + 1 } })
            Check.Reject(() => ModelOperationContract.Validate(Result with { Outputs = [new("mesh", artifact)] }));
    }
    internal static void ResultBounds()
    {
        var outputs = Enumerable.Range(0, 4).Select(index => new ModelOperationOutput("mesh", Result.Outputs[0].Artifact with
            { ArtifactId = "mesh-" + index, Bytes = ModelOperationContract.MaximumArtifactBytes })).ToArray();
        ModelOperationContract.Validate(Result with { Outputs = outputs });
        Check.Reject(() => ModelOperationContract.Validate(Result with { Outputs = [.. outputs, new("texture", outputs[0].Artifact with { ArtifactId = "extra" })] }));
        Check.Reject(() => ModelOperationContract.Validate(Result with { State = ModelOperationState.Unknown, Partial = false, Outputs = [] }));
        ModelOperationContract.Validate(Result with { State = ModelOperationState.Unknown, Partial = true, Outputs = [] });
        Check.Reject(() => ModelOperationContract.Validate(Result with { State = ModelOperationState.Failed }));
    }
    internal static void Descriptor()
    {
        var descriptor = new ModelOperationRuntimeDescriptor("fixture-runtime", "1.0",
            [new(ModelOperationContract.ImageTo3D, 1, ["image/png"], ["fixture.mesh/1"]),
             new(ModelOperationContract.MeshRetexture, 1, ["image/png", "model/gltf-binary"], ["fixture.mesh/1"])], [Profile]);
        ModelOperationContract.Validate(descriptor);
        Check.Reject(() => ModelOperationContract.Validate(Profile with { Mode = "shell" }));
        Check.Reject(() => ModelOperationContract.Validate(Profile with { Operations = ["arbitrary"] }));
        Check.Reject(() => ModelOperationContract.Validate(descriptor with { Profiles = [Profile, Profile] }));
    }
    internal static void Legacy()
    {
        ModelPackageContract.ValidateLayout(new ModelPackage(), ["manifest.json", "model.gguf"]);
        var image = new ModelPackage { Version = 2 }; image.Model.VisionProjectorPath = "projector.gguf";
        ModelPackageContract.ValidateLayout(image, ["manifest.json", "model.gguf", "projector.gguf"]);
        Check.True(ModelPackageContract.IsPackagePath("fixture.CerebralLLM") && !ModelPackageContract.IsPackagePath("fixture.CerebralModel"));
        Check.True(ModelArtifactPackage.IsPackagePath("fixture.CerebralModel") && !ModelArtifactPackage.IsPackagePath("fixture.CerebralLLM"));
        Check.True(new LegacyAdapter().Descriptor.AdapterId == "fixture" && new LegacyEngine().Descriptor.ProtocolVersion == 1);
    }
    private sealed class LegacyAdapter : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor => new("fixture", "1", ["text"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class LegacyEngine : IEnginePlugin
    {
        public EngineDescriptor Descriptor => new("fixture", "1", 1, "stateless/1", []);
        public Task InitializeAsync(IEngineFeatureHost host, EngineConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CheckHealthAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<EngineDecision> EvaluateAsync(EngineRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

static class PackageTests
{
    private static readonly byte[] Weights = Encoding.UTF8.GetBytes("inert synthetic tensor fixture; not model or inference evidence");
    private static ModelArtifactPackageManifest Manifest => new(ModelArtifactPackage.Format, 3,
        "fixture-package", "1.0", "fixture-model", "Fixture publisher", "Fixture-only license", "fixture-revision",
        [new("data/weights.safetensors", "weights", Weights.Length, Check.Hash(Weights))],
        [new("fixture-runtime", "1.0", ContractTests.Digest, ContractTests.Profile)]);
    private static string WriteArchive(Temporary root, ModelArtifactPackageManifest? manifest = null,
        string? rawManifest = null, Action<ZipArchive>? extra = null, byte[]? weights = null)
    {
        var path = root.PathFor(Guid.NewGuid().ToString("N") + ".CerebralModel");
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        using (var target = archive.CreateEntry("manifest.json").Open()) target.Write(Encoding.UTF8.GetBytes(rawManifest ?? JsonSerializer.Serialize(manifest ?? Manifest, ModelOperationContract.Json)));
        using (var target = archive.CreateEntry("data/weights.safetensors").Open()) target.Write(weights ?? Weights);
        extra?.Invoke(archive);
        return path;
    }
    private static string Source(Temporary root)
    {
        var source = root.PathFor("source"); Directory.CreateDirectory(Path.Combine(source, "data"));
        File.WriteAllBytes(Path.Combine(source, "data/weights.safetensors"), Weights); return source;
    }
    internal static void Roundtrip()
    {
        using var root = new Temporary(); var source = Source(root); var package = root.PathFor("export.CerebralModel");
        var exported = ModelArtifactPackage.Export(source, package, Manifest);
        var verified = ModelArtifactPackage.Verify(package);
        Check.True(exported.PackageSha256 == verified.PackageSha256 && verified.PayloadBytes == Weights.Length);
        Check.Throws<NotSupportedException>(() => ((IList<ModelArtifactPackageFile>)verified.Manifest.Files).Add(Manifest.Files[0]));
        Check.True(ModelArtifactPackage.ReadManifest(package).PackageId == Manifest.PackageId);
        var destination = root.PathFor("installed"); ModelArtifactPackage.Extract(package, destination);
        Check.True(File.ReadAllBytes(Path.Combine(destination, "data/weights.safetensors")).SequenceEqual(Weights));
        ModelArtifactPackage.Export(destination, root.PathFor("roundtrip.CerebralModel"), Manifest);
        Check.True(File.ReadAllBytes(Path.Combine(source, "data/weights.safetensors")).SequenceEqual(Weights));
    }
    internal static void Collision()
    {
        using var root = new Temporary(); var source = Source(root); var package = root.PathFor("package.CerebralModel");
        ModelArtifactPackage.Export(source, package, Manifest); var original = File.ReadAllBytes(package);
        Check.Throws<IOException>(() => ModelArtifactPackage.Export(source, package, Manifest));
        Check.True(File.ReadAllBytes(package).SequenceEqual(original));
        var destination = root.PathFor("installed"); Directory.CreateDirectory(destination); File.WriteAllText(Path.Combine(destination, "keep.txt"), "preserve");
        Check.Reject(() => ModelArtifactPackage.Extract(package, destination));
        Check.True(File.ReadAllText(Path.Combine(destination, "keep.txt")) == "preserve");
    }
    internal static void HashMismatch()
    {
        using var root = new Temporary(); var bad = WriteArchive(root, weights: Enumerable.Repeat((byte)7, Weights.Length).ToArray());
        Check.Reject(() => ModelArtifactPackage.Verify(bad));
        var destination = root.PathFor("not-created"); Check.Reject(() => ModelArtifactPackage.Extract(bad, destination));
        Check.True(!Directory.Exists(destination));
    }
    internal static void ManifestPaths()
    {
        foreach (var path in new[] { "../outside.bin", "data/../outside.bin", "C:/outside.bin", "data\\outside.bin", "data/a.bin:stream", "data/CON.bin", "data/a./b.bin", "data/evil\n/weights.bin", "data/script.py", "data/runner.exe" })
            Check.Reject(() => ModelArtifactPackage.Validate(Manifest with { Files = [Manifest.Files[0] with { Path = path }] }));
        Check.Reject(() => ModelArtifactPackage.Validate(Manifest with { Files = [Manifest.Files[0], Manifest.Files[0] with { Path = "data/WEIGHTS.safetensors" }] }));
        Check.Reject(() => ModelArtifactPackage.Validate(Manifest with { Files = [Manifest.Files[0] with { Role = "executable" }] }));
        Check.Reject(() => ModelArtifactPackage.Validate(Manifest with { Files = [Manifest.Files[0] with { Path = "data/a.bin" }, Manifest.Files[0] with { Path = "data/a.bin/b.json" }] }));
    }
    internal static void ArchiveInventory()
    {
        using var root = new Temporary();
        foreach (var extra in new Action<ZipArchive>[] {
                     archive => archive.CreateEntry("data/extra.py"),
                     archive => archive.CreateEntry("data/weights.safetensors"),
                     archive => archive.CreateEntry("MANIFEST.JSON") })
            Check.Reject(() => ModelArtifactPackage.Verify(WriteArchive(root, extra: extra)));
        var path = WriteArchive(root, extra: archive => { });
        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Update)) archive.GetEntry("data/weights.safetensors")!.ExternalAttributes = unchecked((int)0xA1FF0000);
        Check.Reject(() => ModelArtifactPackage.Verify(path));
    }
    internal static void StrictManifest()
    {
        using var root = new Temporary(); var raw = JsonSerializer.Serialize(Manifest, ModelOperationContract.Json);
        foreach (var invalid in new[] { raw.Replace("\"version\":3", "\"version\":3,\"version\":3"),
                     raw.Replace("\"version\":3", "\"version\":2"), raw[..^1] + ",\"entrypoint\":\"untrusted\"}" })
            Check.Reject(() => ModelArtifactPackage.ReadManifest(WriteArchive(root, rawManifest: invalid)));
        Check.Reject(() => ModelArtifactPackage.ReadManifest(WriteArchive(root, rawManifest: new string(' ', ModelArtifactPackage.MaximumManifestBytes + 1))));
    }
    internal static void Limits()
    {
        using var root = new Temporary(); var path = WriteArchive(root);
        Check.Reject(() => ModelArtifactPackage.Verify(path, new(1, Weights.Length - 1, Weights.Length - 1)));
        Check.Reject(() => ModelArtifactPackage.Validate(Manifest with { Files = [Manifest.Files[0] with { Bytes = 0 }] }));
        Check.Reject(() => ModelArtifactPackage.Validate(Manifest, new(MaximumFiles: 4097)));
    }
    internal static void DirectoryCount()
    {
        using var root = new Temporary(); var path = WriteArchive(root); var bytes = File.ReadAllBytes(path);
        // Ordinary fixture has an uncommented 22-byte end record. Lie about its count.
        bytes[^12] = 1; bytes[^11] = 0; bytes[^14] = 1; bytes[^13] = 0;
        File.WriteAllBytes(path, bytes); Check.Reject(() => ModelArtifactPackage.ReadManifest(path));
    }
    internal static void SourceInventory()
    {
        using var root = new Temporary(); var source = Source(root);
        Check.Reject(() => ModelArtifactPackage.Export(source, Path.Combine(source, "inside.CerebralModel"), Manifest));
        File.WriteAllText(Path.Combine(source, "unadmitted.py"), "not executed");
        Check.Reject(() => ModelArtifactPackage.Export(source, root.PathFor("not-created.CerebralModel"), Manifest));
        Check.True(!File.Exists(root.PathFor("not-created.CerebralModel")));
    }
    internal static void Zip64()
    {
        using var root = new Temporary(); var path = WriteArchive(root); var bytes = File.ReadAllBytes(path);
        var end = bytes.AsSpan(bytes.Length - 22);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        var record = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(4), 44);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), 45);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), 45);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), count);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), count);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(40), size);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(48), offset);
        var locator = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator.AsSpan(8), (ulong)(bytes.Length - 22));
        BinaryPrimitives.WriteUInt32LittleEndian(locator.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], uint.MaxValue);
        File.WriteAllBytes(path, [.. bytes[..^22], .. record, .. locator, .. bytes[^22..]]);
        Check.True(ModelArtifactPackage.Verify(path).Manifest.ModelId == Manifest.ModelId);
    }
    internal static void Precancel()
    {
        using var root = new Temporary(); var source = Source(root); var package = WriteArchive(root);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check.Throws<OperationCanceledException>(() => ModelArtifactPackage.Export(source, root.PathFor("canceled.CerebralModel"), Manifest, cancellationToken: canceled.Token));
        Check.Throws<OperationCanceledException>(() => ModelArtifactPackage.Extract(package, root.PathFor("canceled"), cancellationToken: canceled.Token));
        Check.True(!File.Exists(root.PathFor("canceled.CerebralModel")) && !Directory.Exists(root.PathFor("canceled")));
    }
    internal static void RuntimeBindings()
    {
        var runtime = Manifest.Runtimes[0];
        Check.Reject(() => ModelArtifactPackage.Validate(Manifest with { Runtimes = [runtime, runtime] }));
        Check.Reject(() => ModelArtifactPackage.Validate(Manifest with { Runtimes = [runtime with { RuntimeSha256 = "missing" }] }));
        Check.Reject(() => ModelArtifactPackage.Validate(Manifest with { Runtimes = [runtime with { Profile = runtime.Profile with { Mode = "command" } }] }));
    }
}

sealed class Temporary : IDisposable
{
    private static readonly string Scratch = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch"));
    private readonly string directory = Path.Combine(Scratch, Guid.NewGuid().ToString("N"));
    public Temporary() => Directory.CreateDirectory(directory);
    public string PathFor(string relative) => Path.Combine(directory, relative);
    public void Dispose()
    {
        var absolute = Path.GetFullPath(directory);
        if (!absolute.StartsWith(Scratch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(absolute, recursive: true);
    }
}
