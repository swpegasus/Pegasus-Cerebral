using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.ModelOperations;
using Cerebral.Worker;
using Cerebral.Worker.ModelOperations;

/// <summary>CPU fixtures only. No Docker process, renderer, model or installed Worker is invoked.</summary>
public static class ArtifactSecurityChecks
{
    public static async Task<int> RunAsync(string scratchRoot)
    {
        var root = Path.Combine(scratchRoot, "artifact-security");
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or JsonException or KeyNotFoundException)
            { checks++; return; }
            throw new Exception(message);
        }
        async Task<T> Expect<T>(Func<Task> action, string message) where T : Exception
        {
            try { await action(); }
            catch (T error) { checks++; return error; }
            throw new Exception(message);
        }
        var sequence = 0;
        void Validate(Action<JsonObject>? mutate = null)
        {
            var path = Path.Combine(root, "mesh-" + sequence++ + ".glb");
            File.WriteAllBytes(path, Mesh(mutate));
            ModelOperationArtifactValidation.Validate(path, "model/gltf-binary", "cerebral.gltf.pbr/1");
        }

        Validate(); Check(true, "A static reachable float mesh with normalized byte color remains admitted.");
        Reject(() => Validate(x => x["nodes"]![0]!["children"] = new JsonArray(0)), "Scene cycles must reject.");
        Reject(() => Validate(x => x["nodes"]!.AsArray().Add(new JsonObject { ["mesh"] = 0 })), "Unreachable nodes must reject.");
        Reject(() => Validate(x => x["scene"] = 1), "Invalid default scene must reject.");
        Reject(() => Validate(x => x["nodes"]![0]!["translation"] = new JsonArray(0, 1)), "Malformed transform shape must reject.");
        Reject(() => Validate(x => x["nodes"]![0]!["rotation"] = new JsonArray(0, 0, 0, 0)), "Nonunit quaternion must reject.");
        Reject(() => Validate(x => x["nodes"]![0]!["matrix"] = new JsonArray(1,0,0,1, 0,1,0,0, 0,0,1,0, 0,0,0,1)), "Nonaffine matrix must reject.");
        Reject(() => Validate(x => { x["nodes"]![0]!["translation"] = new JsonArray(0,0,0); x["nodes"]![0]!["matrix"] = new JsonArray(1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1); }), "Matrix plus TRS must reject.");
        Reject(() => Validate(x => x["accessors"]![2]!["componentType"] = 5123), "Integer normals need an unsupported quantization extension and must reject.");
        Reject(() => Validate(x => x["accessors"]![0]!["normalized"] = true), "Float POSITION cannot be normalized.");
        Reject(() => Validate(x => x["accessors"]![1]!["normalized"] = true), "Index accessor cannot be normalized.");
        Reject(() => Validate(x => { x["bufferViews"]![1]!["byteStride"] = 4; x["bufferViews"]![1]!["byteLength"] = 12; }), "Index accessor cannot use a vertex stride.");
        Reject(() => Validate(x => x["accessors"]![3]!["normalized"] = false), "Unsigned color must be normalized.");
        Reject(() => Validate(x => x["bufferViews"]![3]!["byteOffset"] = 79), "Byte vertex attribute starts must be four-byte aligned.");
        Reject(() => Validate(x => { x["accessors"]![3]!["type"] = "VEC3"; x["bufferViews"]![3]!.AsObject().Remove("byteStride"); }), "Packed byte VEC3 vertex rows need explicit four-byte alignment.");
        Reject(() => Validate(x => { x["materials"] = new JsonArray(new JsonObject { ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = new JsonObject { ["index"] = 0 } } }); x["meshes"]![0]!["primitives"]![0]!["material"] = 0; }), "Textured mesh without TEXCOORD_0 must reject.");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Expect<OperationCanceledException>(() => Task.Run(() => ModelOperationArtifactValidation.Validate(Path.Combine(root, "missing.glb"), "model/gltf-binary", "cerebral.gltf.pbr/1", cancelled.Token)), "Pre-cancel must precede any artifact read.");
        }

        var bytes = "{}"u8.ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var state = new StateStore(Path.Combine(root, "state"));
        var artifacts = new ModelOperationArtifactStore(state, "worker-security", new(2, 2, 8, 16));
        Task<ModelArtifactInfo> Commit(string id, string job, Stream stream, CancellationToken token = default) =>
            artifacts.CommitAsync(id, job, "effect-" + job, "profile", "provenance", "application/json", "cerebral.model.provenance/1", bytes.Length, digest, stream, token);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var untouched = new CallbackReadStream(bytes, () => throw new Exception("Pre-cancel consumed the source."));
            await Expect<OperationCanceledException>(() => Commit("pre-cancel", "pre-cancel", untouched, cancelled.Token), "Pre-cancel must not reserve an identity.");
            Check(untouched.ReadCalls == 0 && (await Commit("pre-cancel", "pre-cancel", new MemoryStream(bytes))).Sha256 == digest, "Pre-cancel leaves no reservation or stream consumption.");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            var interrupted = new CallbackReadStream(bytes, () => { cancelled.Cancel(); throw new OperationCanceledException(cancelled.Token); });
            await Expect<OperationCanceledException>(() => Commit("clean-cancel", "clean-cancel", interrupted, cancelled.Token), "Confirmed partial cleanup preserves cancellation.");
            Check((await Commit("clean-cancel", "clean-cancel", new MemoryStream(bytes))).Length == 2, "Confirmed cancellation cleanup releases its original reservation.");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            var uncertainFinal = Path.Combine(state.DirectoryPath, "Artifacts", "ModelOperations", "uncertain.blob");
            var interrupted = new CallbackReadStream(bytes, () =>
            {
                Directory.CreateDirectory(uncertainFinal); cancelled.Cancel(); throw new OperationCanceledException(cancelled.Token);
            });
            var error = await Expect<ModelArtifactCleanupUncertainException>(() => Commit("uncertain", "uncertain-job", interrupted, cancelled.Token), "Unconfirmed cleanup must override ordinary cancellation with explicit uncertainty.");
            Check(error.InnerException is AggregateException aggregate && aggregate.InnerExceptions.Any(x => x is OperationCanceledException) && artifacts.List("uncertain-job", "profile").Count == 0, "Unknown cleanup retains its cause and never exposes an artifact.");
            await Expect<InvalidOperationException>(() => Commit("uncertain", "uncertain-job", new MemoryStream(bytes)), "Unknown artifact identity cannot be replayed.");
            await Expect<InvalidOperationException>(() => Commit("quota-refused", "uncertain-job", new MemoryStream(bytes)), "Unknown partial reservation remains charged to the exact job.");
        }

        var batchState = new StateStore(Path.Combine(root, "batch-state"));
        var batchJobs = new ModelOperationJobs(batchState, new QuotaAdmission(), new EventHub());
        var input = new ModelOperationInput("image", new("worker-security", "input-job", "input-effect", "input-artifact", 1,
            "image/png", "cerebral.image.preview/1", "image", 2, digest));
        var submission = new ModelOperationSubmissionRequest(1, "quota-request", "quota-model", ModelOperationContract.ImageTo3D,
            [new("first", [input], new()), new("second", [input], new())]);
        var parentBatch = batchJobs.Submit(submission, "profile");
        var batchArtifacts = new ModelOperationArtifactStore(batchState, "worker-security", new(2, 2, 8, 16));
        Task<ModelArtifactInfo> CommitItem(string id, int index, Stream source) => batchArtifacts.CommitAsync(id,
            parentBatch.Items[index].JobId, parentBatch.Items[index].EffectId, "profile", "provenance", "application/json",
            "cerebral.model.provenance/1", bytes.Length, digest, source);
        await CommitItem("parent-first", 0, new MemoryStream(bytes));
        var notConsumed = new CallbackReadStream(bytes, () => { });
        await Expect<InvalidOperationException>(() => CommitItem("parent-second", 1, notConsumed), "Distinct per-item job IDs must share the host parent batch's quota.");
        Check(parentBatch.Items[0].JobId != parentBatch.Items[1].JobId && notConsumed.ReadCalls == 0 && batchArtifacts.List(parentBatch.Items[1].JobId, "profile").Count == 0,
            "Parent quota refusal is before source consumption and publication.");
        await Expect<InvalidDataException>(() => batchArtifacts.CommitAsync("wrong-effect", parentBatch.Items[0].JobId, "foreign-effect", "profile", "provenance",
            "application/json", "cerebral.model.provenance/1", bytes.Length, digest, new MemoryStream(bytes)), "Artifact effect must match its host-owned item binding.");
        var ownerState = new StateStore(Path.Combine(root, "owner-state"));
        var ownerJobs = new ModelOperationJobs(ownerState, new QuotaAdmission(), new EventHub());
        var ownerBatch = ownerJobs.Submit(submission with { RequestId = "owner-quota-request", Items = [submission.Items[0]] }, null);
        var ownerArtifacts = new ModelOperationArtifactStore(ownerState, "worker-security", new(2, 2, 8, 16));
        Task<ModelArtifactInfo> CommitOwner(string id, string? owner) => ownerArtifacts.CommitAsync(id,
            ownerBatch.Items[0].JobId, ownerBatch.Items[0].EffectId, owner, "provenance", "application/json", "cerebral.model.provenance/1", bytes.Length, digest, new MemoryStream(bytes));
        await Expect<InvalidDataException>(() => CommitOwner("owner-wrong-scope", "owner"), "Literal profile 'owner' cannot claim a null owner-operator batch.");
        var ownerArtifact = await CommitOwner("owner-artifact", null);
        await Expect<UnauthorizedAccessException>(() => Task.FromResult(ownerArtifacts.Describe(ownerArtifact.Id, 1, "owner")), "Literal profile 'owner' cannot read null-owned operator artifacts.");
        Check((await ownerArtifacts.ReadAsync(ownerArtifact.Id, 1, null, 0, bytes.Length)).SequenceEqual(bytes), "Authenticated owner reads remain permitted without an invented profile sentinel.");
        // Deliberately corrupt only this inert test DB to prove ambiguity cannot select a permissive scope.
        batchState.Put("model-operation-batch", "ambiguous", new { Status = parentBatch with { Id = "ambiguous" }, Request = submission });
        await Expect<InvalidDataException>(() => CommitItem("ambiguous-refused", 1, new MemoryStream(bytes)), "Ambiguous durable job ownership must fail closed.");

        // Check that a linked ancestor is rejected before Directory.CreateDirectory can write through it.
        var linkedState = new StateStore(Path.Combine(root, "linked-state"));
        var outside = Path.Combine(root, "link-target"); Directory.CreateDirectory(outside);
        var link = Path.Combine(linkedState.DirectoryPath, "Artifacts"); var linkCreated = false;
        try { Directory.CreateSymbolicLink(link, outside); linkCreated = true; }
        catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        { Console.WriteLine("SKIP artifact-security linked-ancestor fixture: local link creation unavailable."); }
        if (linkCreated)
        {
            try
            {
                Reject(() => new ModelOperationArtifactStore(linkedState, "worker-security"), "Linked artifact ancestor must reject before effects.");
                Check(!Directory.Exists(Path.Combine(outside, "ModelOperations")), "Rejected linked ancestor caused no out-of-root directory creation.");
            }
            finally { Directory.Delete(link); }
        }

        var pin = new string('a', 64); var cid = new string('b', 64);
        var spec = new OciModelContainerSpec(Path.Combine(root, "docker"), pin, "sha256:" + pin, "worker-security", "instance-security",
            Path.Combine(root, "models"), Path.Combine(root, "inputs"), Path.Combine(root, "outputs"), pin);
        var arguments = OciModelContainer.BuildCreateArguments(spec, "1000:1000");
        Check(arguments[0] == "create" && arguments.Contains("--interactive") && arguments[^1] == spec.ImageDigest && arguments.Count(x => x == "--mount") == 3 && !arguments.Contains("--privileged"), "Fixed create-time stdin and closed mount inventory are explicit.");
        Check(arguments.Contains("type=bind,src=" + spec.ModelDirectory + ",dst=/models,readonly") && arguments.Contains("type=bind,src=" + spec.InputDirectory + ",dst=/data/jobs,readonly"), "Model and input mounts are read-only.");
        Reject(() => OciModelContainer.BuildCreateArguments(spec, "0:1000"), "Root container identity must reject.");
        Reject(() => OciModelContainer.BuildCreateArguments(spec with { OutputDirectory = spec.InputDirectory }, "1000:1000"), "Writable mount equality must reject.");
        Reject(() => OciModelContainer.BuildCreateArguments(spec with { OutputDirectory = Path.GetDirectoryName(spec.ModelDirectory)! }, "1000:1000"), "Writable ancestor of model mount must reject.");
        Reject(() => OciModelContainer.BuildCreateArguments(spec with { OutputDirectory = Path.Combine(spec.InputDirectory, "nested") }, "1000:1000"), "Writable child of input mount must reject.");
        Reject(() => OciModelContainer.BuildCreateArguments(spec with { DockerExecutable = Path.Combine(spec.OutputDirectory, "docker") }, "1000:1000"), "Writable output cannot overwrite the pinned host control executable.");
        var intent = JsonSerializer.Serialize(new { schema = "cerebral.model-container/1", state = "creating", spec.WorkerInstallationId, spec.InstanceId, spec.ImageDigest, spec.ModelManifestSha256 });
        OciModelContainer.ValidateCreatingIntent(intent, spec); Check(true, "Exact creating intent admits read-only name reconciliation.");
        Reject(() => OciModelContainer.ValidateCreatingIntent(intent.Replace("instance-security", "foreign-instance", StringComparison.Ordinal), spec), "Foreign creating intent must reject.");
        Reject(() => OciModelContainer.ValidateCreatingIntent(intent[..^1] + ",\"InstanceId\":\"instance-security\"}", spec), "Duplicate intent property must reject.");
        Reject(() => OciModelContainer.ValidateCreatingIntent(intent[..^1] + ",\"extra\":true}", spec), "Unknown intent field must reject.");
        Reject(() => OciModelContainer.ValidateCreatingIntent(new string(' ', 16 * 1024 + 1), spec), "Oversized intent must reject before parsing.");
        string Observe(string status, bool running, string? worker = null, string? name = null, string? image = null, string? observedCid = null) => JsonSerializer.Serialize(new[] { new
        {
            Id = observedCid ?? cid, Name = name ?? "/cerebral-model-instance-security", Image = image ?? spec.ImageDigest,
            Config = new { Labels = new Dictionary<string, string> { ["pegasus.cerebral.worker"] = worker ?? spec.WorkerInstallationId, ["pegasus.cerebral.instance"] = spec.InstanceId, ["pegasus.cerebral.model-manifest"] = spec.ModelManifestSha256 } },
            State = new { Status = status, Running = running, ExitCode = 0 }
        } });
        var created = OciModelContainer.ParseObservation(spec, Observe("created", false));
        Check(created.ContainerId == cid && created.IdentityVerified && created.SafeToRemove && !created.Terminal && created.ExitCode is null, "A verified created CID can be cleaned without inventing an exit result.");
        var running = OciModelContainer.ParseObservation(spec, Observe("running", true), cid);
        Check(running.Running && !running.SafeToRemove && !running.Terminal, "A running CID cannot be removed as stopped.");
        Check(OciModelContainer.ParseObservation(spec, Observe("exited", false), cid).SafeToRemove, "A verified exited CID can be cleaned.");
        Reject(() => OciModelContainer.ParseObservation(spec, Observe("created", false, worker: "foreign-worker")), "Foreign labels cannot reconcile.");
        Reject(() => OciModelContainer.ParseObservation(spec, Observe("created", false, name: "/foreign-name")), "Wrong deterministic name cannot reconcile.");
        Reject(() => OciModelContainer.ParseObservation(spec, Observe("created", false, image: "sha256:" + new string('c', 64))), "Wrong local image identity cannot reconcile.");
        Reject(() => OciModelContainer.ParseObservation(spec, Observe("created", false, observedCid: "short")), "Abbreviated CID cannot reconcile.");
        Reject(() => OciModelContainer.ParseObservation(spec, Observe("created", false), new string('c', 64)), "Known CID cannot rebind to another object.");
        Reject(() => OciModelContainer.ParseObservation(spec, Observe("created", true)), "Inconsistent created/running observation must reject.");
        Reject(() => OciModelContainer.ParseObservation(spec, Observe("created", false)[..^1] + "," + Observe("created", false)[1..]), "Ambiguous inspect array cannot reconcile.");
        return checks;
    }

    private static byte[] Mesh(Action<JsonObject>? mutate)
    {
        // Inert unit fixture, not generated-model evidence. Single triangle with core float normals
        // and normalized RGBA8 vertex colors; every node and mesh is in the default static scene.
        var binary = new byte[92]; float[] positions = [0,0,0, 1,0,0, 0,1,0]; Buffer.BlockCopy(positions, 0, binary, 0, 36);
        BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(36), 0); BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(38), 1); BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(40), 2);
        float[] normals = [0,0,1, 0,0,1, 0,0,1]; Buffer.BlockCopy(normals, 0, binary, 44, 36);
        for (var i = 80; i < 92; i += 4) { binary[i] = 255; binary[i + 1] = 128; binary[i + 3] = 255; }
        var root = JsonNode.Parse("""
        {"asset":{"version":"2.0"},"buffers":[{"byteLength":92}],
        "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":6},{"buffer":0,"byteOffset":44,"byteLength":36},{"buffer":0,"byteOffset":80,"byteLength":12,"byteStride":4}],
        "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},{"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"},{"bufferView":2,"componentType":5126,"count":3,"type":"VEC3"},{"bufferView":3,"componentType":5121,"count":3,"type":"VEC4","normalized":true}],
        "meshes":[{"primitives":[{"attributes":{"POSITION":0,"NORMAL":2,"COLOR_0":3},"indices":1}]}],"nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0}
        """)!.AsObject();
        mutate?.Invoke(root);
        var json = JsonSerializer.SerializeToUtf8Bytes(root); var jsonLength = (json.Length + 3) & ~3;
        var bytes = new byte[28 + jsonLength + binary.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x46546c67); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 2); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), jsonLength); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x4e4f534a);
        bytes.AsSpan(20, jsonLength).Fill(32); json.CopyTo(bytes, 20);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20 + jsonLength), binary.Length); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + jsonLength), 0x004e4942);
        binary.CopyTo(bytes, 28 + jsonLength); return bytes;
    }

    private sealed class CallbackReadStream(byte[] bytes, Action atEnd) : MemoryStream(bytes, writable: false)
    {
        public int ReadCalls { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            ReadCalls++;
            if (Position == Length) atEnd();
            return base.ReadAsync(buffer, token);
        }
    }

    private sealed class QuotaAdmission : IModelOperationJobAdmission
    {
        public void ValidateSubmission(ModelOperationSubmissionRequest request, string? ownerProfileId) => ModelOperationContract.Validate(request);
        public Task<IModelOperationExecutionLease> PrepareAsync(ModelOperationSubmissionRequest batch, ModelOperationSubmissionItem item,
            ModelOperationItemStatus identity, string? ownerProfileId, CancellationToken token) => throw new NotSupportedException("Quota fixtures never dispatch a model runtime.");
    }
}
