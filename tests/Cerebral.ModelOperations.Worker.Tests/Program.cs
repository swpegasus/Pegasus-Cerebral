using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Worker;
using Cerebral.Worker.ModelOperations;

if (args.Contains("--inventory"))
{
    // Read-only installed-owner lookup. Credentials stay inside the product SDK;
    // serialized results use a closed metadata projection, never HostSettings.
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var phase = "installed-owner"; bool? ownerCredentialConfigured = null, peerCredentialConfigured = null;
    try
    {
        using var owner = CerebralClient.ForInstalledWorker();
        var health = await owner.CommandAsync("health", cancellationToken: deadline.Token);
        var settings = await owner.CommandAsync("settings.get", cancellationToken: deadline.Token);
        static object? Field(JsonElement value, string name) => value.TryGetProperty(name, out var item) &&
            item.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? item.Clone() : null;
        var available = settings.Success && settings.Data.ValueKind == JsonValueKind.Object &&
            settings.Data.TryGetProperty("workers", out var workerValues) && workerValues.ValueKind == JsonValueKind.Array;
        var safe = available ? settings.Data.GetProperty("workers").EnumerateArray().Select(x => new
        { id = Field(x, "id"), name = Field(x, "name"), installationId = Field(x, "installationId"), url = Field(x, "url"),
          architecture = Field(x, "architecture"), state = Field(x, "state"), requiresConfiguration = Field(x, "requiresConfiguration") }).Cast<object>().ToArray() : [];
        if (args.Contains("--remote"))
        {
            if (!available) throw new InvalidOperationException("Registered workers unavailable.");
            var matches = settings.Data.GetProperty("workers").EnumerateArray().Where(x => Field(x, "id") is JsonElement id && id.GetString() == "gb10").ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Exact GB10 registration is ambiguous or missing.");
            var registration = CerebralJson.Read<WorkerRegistration>(matches[0]);
            phase = "registered-remote-owner";
            ownerCredentialConfigured = !string.IsNullOrWhiteSpace(registration.AccessKey);
            peerCredentialConfigured = !string.IsNullOrWhiteSpace(registration.PeerToken);
            using var remote = CerebralClient.ForOwner(registration);
            phase = "remote-health";
            var remoteHealth = await remote.CommandAsync("health", cancellationToken: deadline.Token);
            var resources = await remote.CommandAsync("resources", cancellationToken: deadline.Token);
            var hold = await remote.CommandAsync("resources.local.status", cancellationToken: deadline.Token);
            static Dictionary<string, object?> Select(JsonElement value, params string[] fields) => fields.ToDictionary(x => x, x => value.ValueKind == JsonValueKind.Object ? Field(value, x) : null);
            var gpuValues = resources.Success && resources.Data.ValueKind == JsonValueKind.Object && resources.Data.TryGetProperty("gpus", out var gpus) && gpus.ValueKind == JsonValueKind.Array
                ? gpus.EnumerateArray().Select(x => Select(x, "name", "totalMb", "freeMb", "usedMb", "unified")).ToArray() : [];
            Console.WriteLine(JsonSerializer.Serialize(new { schema = "cerebral.model-operations-remote-inventory/1", registeredWorkerId = registration.Id,
                healthSucceeded = remoteHealth.Success, health = Select(remoteHealth.Data, "version", "protocol", "architecture", "os", "installationId", "name"),
                resourcesSucceeded = resources.Success, resources = Select(resources.Data, "systemAvailableMb", "systemTotalMb", "processMemoryMb"), gpus = gpuValues,
                holdStatusSucceeded = hold.Success, hold = Select(hold.Data, "state", "pauseRequested", "admissionOpen"),
                mutationPerformed = false, inferencePerformed = false }));
            return remoteHealth.Success ? 0 : 2;
        }
        Console.WriteLine(JsonSerializer.Serialize(new { schema = "cerebral.model-operations-owner-inventory/1",
            healthSucceeded = health.Success, workersSucceeded = available, workers = safe,
            mutationPerformed = false, inferencePerformed = false }));
        return health.Success && available ? 0 : 2;
    }
    catch (Exception error)
    { Console.WriteLine(JsonSerializer.Serialize(new { schema = "cerebral.model-operations-owner-inventory/1", state = "unavailable", phase,
        ownerCredentialConfigured, peerCredentialConfigured, errorType = error.GetType().Name, mutationPerformed = false })); return 2; }
}

var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
// An explicit scratch path avoids assuming the artifacts-layout build directory.
var scratchArgument = args.SkipWhile(x => x != "--scratch").Skip(1).FirstOrDefault()
    ?? throw new ArgumentException("Provide --scratch under the Cerebral build directory.");
var buildRoot = Path.GetFullPath("C:/CodexProjects/Pegasus Cerebral/build/ModelOperations");
var scratchRoot = Path.GetFullPath(scratchArgument);
if (!scratchRoot.StartsWith(buildRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Test scratch must be a child of the task's build directory.");
Directory.CreateDirectory(scratchRoot);
var scratch = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
var passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
async Task Reject(Func<Task> action, string message)
{ try { await action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException) { passed++; return; } throw new Exception(message); }
var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII=");
string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

try
{
    var state = new StateStore(Path.Combine(scratch, "state"));
    var artifacts = new ModelOperationArtifactStore(state, "worker-test", new(24 * 1024 * 1024, 32 * 1024 * 1024, 64 * 1024 * 1024, 128 * 1024 * 1024));
    var first = await artifacts.CommitAsync("first", "job-one", "effect-one", "profile-a", "preview", "image/png", "cerebral.image.preview/1", png.Length, Sha(png), new MemoryStream(png));
    Check(first.Length == png.Length && first.Sha256 == Sha(png), "Committed bytes must be identified.");
    Check(first == await artifacts.CommitAsync("first", "job-one", "effect-one", "profile-a", "preview", "image/png", "cerebral.image.preview/1", png.Length, Sha(png), Stream.Null), "Exact completed retry returns its original receipt without consuming a stream.");
    await Reject(() => artifacts.CommitAsync("first", "another-job", "effect-one", "profile-a", "preview", "image/png", "cerebral.image.preview/1", png.Length, Sha(png), Stream.Null), "Changed identity replay must refuse.");
    await Reject(() => Task.FromResult(artifacts.Describe("first", 1, "profile-b")), "Foreign profile must not read an artifact.");
    Check((await artifacts.ReadAsync("first", 1, "profile-a", 0, png.Length)).SequenceEqual(png), "Authorized range bytes match.");
    await Reject(() => artifacts.ReadAsync("first", 1, "profile-a", 0, ModelOperationArtifactStore.MaximumRangeBytes + 1), "Range must be bounded.");
    await Reject(() => artifacts.CommitAsync("changed", "job-one", "effect-two", "profile-a", "preview", "image/png", "cerebral.image.preview/1", png.Length, new string('0', 64), new MemoryStream(png)), "Bad hash must fail before publication.");
    Check(artifacts.List("job-one", "profile-a").Count == 1, "Failed writes stay invisible.");
    var oversized = (byte[])png.Clone(); BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(16), 8192);
    await Reject(() => artifacts.CommitAsync("eight-k", "job-one", "effect-eight", "profile-a", "preview", "image/png", "cerebral.image.preview/1", oversized.Length, Sha(oversized), new MemoryStream(oversized)), "8K output must refuse.");

    var glb = FixtureGlb(png);
    var validPath = Path.Combine(scratch, "valid.glb"); await File.WriteAllBytesAsync(validPath, glb);
    ModelOperationArtifactValidation.Validate(validPath, "model/gltf-binary", "cerebral.gltf.pbr/1"); Check(true, "PBR GLB embeds a legal texture without requiring vertex colors.");
    var large = FixtureGlb(png, binaryBytes: 17 * 1024 * 1024);
    var largeInfo = await artifacts.CommitAsync("large-mesh", "large-job", "large-effect", "profile-a", "mesh", "model/gltf-binary", "cerebral.gltf.pbr/1", large.Length, Sha(large), new MemoryStream(large));
    Check(largeInfo.Length > 16 * 1024 * 1024, "New profile supports a real file larger than the unchanged attachment limit.");
    var external = FixtureGlb(png, externalBuffer: true);
    await Reject(() => artifacts.CommitAsync("external", "bad-job", "bad-effect", "profile-a", "mesh", "model/gltf-binary", "cerebral.gltf.pbr/1", external.Length, Sha(external), new MemoryStream(external)), "External GLB buffers must refuse.");
    var invalidIndices = FixtureGlb(png, index: 99);
    await Reject(() => artifacts.CommitAsync("bad-index", "bad-job", "bad-effect", "profile-a", "mesh", "model/gltf-binary", "cerebral.gltf.pbr/1", invalidIndices.Length, Sha(invalidIndices), new MemoryStream(invalidIndices)), "Out-of-range indices must refuse.");
    var nonfinite = FixtureGlb(png, nonfinite: true);
    await Reject(() => artifacts.CommitAsync("nan-mesh", "bad-job", "bad-effect", "profile-a", "mesh", "model/gltf-binary", "cerebral.gltf.pbr/1", nonfinite.Length, Sha(nonfinite), new MemoryStream(nonfinite)), "Nonfinite geometry must refuse.");
    var limited = new ModelOperationArtifactStore(state, "worker-test", new(128, 128, 256, 512));
    await Reject(() => limited.CommitAsync("no-budget", "another-job", "another-effect", "profile-a", "preview", "image/png", "cerebral.image.preview/1", png.Length, Sha(png), new MemoryStream(png)), "Committed bytes consume quotas across store instances.");
    passed += await JobChecks.RunAsync(scratch);
    passed += await ArtifactSecurityChecks.RunAsync(scratch);
    passed += await CapacityChecks.RunAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { state = "passed", checks = passed, evidence = "real SQLite/artifact streams and deterministic GLB/runtime fixtures; no model, container or renderer", inferencePerformed = false }));
    return 0;
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    var target = Path.GetFullPath(scratch);
    if (!target.StartsWith(scratchRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Scratch cleanup escaped its verified root.");
    Directory.Delete(target, recursive: true);
}

static byte[] FixtureGlb(byte[] png, int binaryBytes = 0, bool externalBuffer = false, ushort index = 2, bool nonfinite = false)
{
    var binary = new byte[Math.Max(68 + ((png.Length + 3) & ~3), binaryBytes)];
    float[] positions = [0,0,0, 1,0,0, 0,1,0];
    if (nonfinite) positions[0] = float.NaN;
    Buffer.BlockCopy(positions, 0, binary, 0, 36);
    float[] uv = [0,0, 1,0, 0,1]; Buffer.BlockCopy(uv, 0, binary, 36, 24);
    BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(60), 0); BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(62), 1); BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(64), index);
    png.CopyTo(binary, 68);
    var buffer = new Dictionary<string, object> { ["byteLength"] = binary.Length };
    if (externalBuffer) buffer["uri"] = "other.bin";
    var json = JsonSerializer.SerializeToUtf8Bytes(new { asset = new { version = "2.0" }, buffers = new[] { buffer },
        bufferViews = new[] { new { buffer=0,byteOffset=0,byteLength=36 },new { buffer=0,byteOffset=36,byteLength=24 },new { buffer=0,byteOffset=60,byteLength=6 },new { buffer=0,byteOffset=68,byteLength=png.Length } },
        accessors = new object[] { new { bufferView=0,componentType=5126,count=3,type="VEC3" },new { bufferView=1,componentType=5126,count=3,type="VEC2" },new { bufferView=2,componentType=5123,count=3,type="SCALAR" } },
        images = new[] { new { bufferView=3,mimeType="image/png" } }, textures = new[] { new { source=0 } },
        materials = new[] { new { pbrMetallicRoughness = new { baseColorTexture = new { index=0 } } } },
        meshes = new[] { new { primitives = new[] { new { attributes=new { POSITION=0,TEXCOORD_0=1 },indices=2,material=0,mode=4 } } } },
        nodes = new[] { new { mesh=0 } }, scenes = new[] { new { nodes=new[] {0} } }, scene=0 });
    var jsonLength = (json.Length + 3) & ~3; var total = 12 + 8 + jsonLength + 8 + binary.Length;
    var bytes = new byte[total]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x46546c67); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 2); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), total);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), jsonLength); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x4e4f534a);
    bytes.AsSpan(20, jsonLength).Fill(32); json.CopyTo(bytes, 20);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20 + jsonLength), binary.Length); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + jsonLength), 0x004e4942);
    binary.CopyTo(bytes, 28 + jsonLength); return bytes;
}
