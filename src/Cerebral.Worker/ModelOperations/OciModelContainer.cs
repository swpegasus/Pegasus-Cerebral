using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cerebral.Worker.ModelOperations;

/// <summary>Trusted activation data, supplied by the admitted runtime package, never by a model-job request.</summary>
public sealed record OciModelContainerSpec(string DockerExecutable, string DockerSha256, string ImageDigest,
    string WorkerInstallationId, string InstanceId, string ModelDirectory, string InputDirectory,
    string OutputDirectory, string ModelManifestSha256);
public sealed record OciModelContainerIdentity(string ContainerId, string WorkerInstallationId,
    string InstanceId, string ImageDigest, DateTimeOffset CreatedAt);
public sealed record OciModelContainerObservation(string ContainerId, bool Running, bool Terminal,
    int? ExitCode, string State, bool IdentityVerified)
{
    /// <summary>A never-started container also has a confirmed stopped state. It has no exit result.</summary>
    public bool SafeToRemove => IdentityVerified && !Running && State is "created" or "exited" or "dead";
}

/// <summary>Docker CLI termination is not container termination. All destructive calls use an exact
/// recorded CID, pinned image and product labels; unknown outcomes retain their ownership record.</summary>
public sealed class OciModelContainer
{
    private const int MaximumCommandOutput = 64 * 1024;
    private const int MaximumIntentBytes = 16 * 1024;
    private readonly OciModelContainerSpec spec;
    private readonly string journalPath;
    private OciModelContainerIdentity? identity;
    public OciModelContainerSpec Specification => spec;

    public OciModelContainer(OciModelContainerSpec spec, string journalPath)
    {
        this.spec = spec;
        this.journalPath = Path.GetFullPath(journalPath);
        ValidateSpec(spec);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The initial OCI model runtime is qualified only for Linux Workers.");
        foreach (var path in new[] { spec.DockerExecutable, spec.ModelDirectory, spec.InputDirectory, spec.OutputDirectory, this.journalPath })
        {
            if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl) || path.Contains(','))
                throw new ArgumentException("Use ordinary absolute runtime paths without control characters or mount delimiters.");
            ModelOperationArtifactStore.Ordinary(path);
        }
        if (Within(this.journalPath, spec.OutputDirectory)) throw new ArgumentException("Container ownership journal cannot be writable by the container.");
        if (!Directory.Exists(spec.ModelDirectory) || !Directory.Exists(spec.InputDirectory) || !Directory.Exists(spec.OutputDirectory))
            throw new DirectoryNotFoundException("Host must prepare the admitted model and instance directories.");
        VerifyExecutable();
    }

    public async Task<OciModelContainerIdentity> CreateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (identity is not null || File.Exists(journalPath))
            throw new InvalidOperationException("Container activation exists. Reconcile it before another create.");
        // Persist intended immutable ownership before the daemon can create an independently
        // surviving object. A lost create acknowledgement must never become automatic replay.
        ModelOperationArtifactStore.Ordinary(journalPath);
        using (var stream = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            JsonSerializer.Serialize(stream, new { schema = "cerebral.model-container/1", state = "creating",
                spec.WorkerInstallationId, spec.InstanceId, spec.ImageDigest, spec.ModelManifestSha256 });
            stream.Flush(true);
        }
        var result = await CommandAsync(BuildCreateArguments(spec, PosixIdentity()), token).ConfigureAwait(false);
        var cid = result.Trim();
        if (!ContainerId(cid)) throw new IOException("Container create returned an unconfirmed identity. Preserve activation journal.");
        var observed = ParseObservation(spec, await CommandAsync(["inspect", "--type", "container", cid], token).ConfigureAwait(false), cid);
        return Acknowledge(observed.ContainerId);
    }

    /// <summary>Pure fixed-argument construction. Does not confer resource admission or run Docker.
    /// ImageDigest is the admitted local Docker image/config ID, not an OCI registry manifest digest.
    /// The initial all-GPU route requires a separately qualified single-GPU Worker.</summary>
    public static IReadOnlyList<string> BuildCreateArguments(OciModelContainerSpec spec, string posixIdentity)
    {
        ValidateSpec(spec);
        var user = posixIdentity.Split(':');
        if (user.Length != 2 || !uint.TryParse(user[0], out var uid) || uid == 0 ||
            !uint.TryParse(user[1], out _) || user.Any(x => x.Length == 0 || x.Any(c => !char.IsAsciiDigit(c))))
            throw new ArgumentException("Use the non-root numeric Worker uid:gid.");
        string[] arguments = ["create", "--interactive", "--pull", "never", "--name", ContainerName(spec), "--label", "pegasus.cerebral.worker=" + spec.WorkerInstallationId,
            "--label", "pegasus.cerebral.instance=" + spec.InstanceId,
            "--label", "pegasus.cerebral.model-manifest=" + spec.ModelManifestSha256,
            "--read-only", "--network", "none", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
            "--user", posixIdentity, "--pids-limit", "256", "--gpus", "all", "--shm-size", "1g",
            "--tmpfs", "/tmp:rw,nosuid,nodev,size=1073741824",
            "--mount", "type=bind,src=" + spec.ModelDirectory + ",dst=/models,readonly",
            "--mount", "type=bind,src=" + spec.InputDirectory + ",dst=/data/jobs,readonly",
            "--mount", "type=bind,src=" + spec.OutputDirectory + ",dst=/data/results",
            "--env", "HF_HUB_OFFLINE=1", "--env", "TRANSFORMERS_OFFLINE=1", "--env", "PYTHONDONTWRITEBYTECODE=1",
            spec.ImageDigest];
        return Array.AsReadOnly(arguments);
    }

    /// <summary>Recovers a lost create acknowledgement by the exact intended name and product labels.
    /// Missing, foreign or ambiguous objects remain unknown. This never creates, starts or removes one.</summary>
    public async Task<OciModelContainerIdentity> ReconcileCreatingAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (identity is not null) throw new InvalidOperationException("An acknowledged CID must be inspected by its saved identity.");
        ValidateCreatingIntent(ReadIntent(), spec);
        var text = await CommandAsync(["inspect", "--type", "container", ContainerName(spec)], token).ConfigureAwait(false);
        var observed = ParseObservation(spec, text);
        return Acknowledge(observed.ContainerId);
    }

    /// <summary>The returned attached process carries JSON IPC only; the daemon still owns the CID.</summary>
    public async Task<Process> StartAttachedAsync(CancellationToken token)
    {
        var observed = await InspectAsync(token).ConfigureAwait(false);
        if (observed.Running || observed.State != "created") throw new InvalidOperationException("Container is not in its exact fresh created state.");
        var info = StartInfo(["start", "--attach", "--interactive", RequiredIdentity().ContainerId]);
        token.ThrowIfCancellationRequested();
        var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("Container attach was not acknowledged.");
        return process;
    }

    public async Task<OciModelContainerObservation> InspectAsync(CancellationToken token)
    {
        var current = RequiredIdentity();
        var text = await CommandAsync(["inspect", "--type", "container", current.ContainerId], token).ConfigureAwait(false);
        return ParseObservation(spec, text, current.ContainerId);
    }

    /// <summary>Pure bounded Docker-observation admission; no object is adopted by an unverified name.</summary>
    public static OciModelContainerObservation ParseObservation(OciModelContainerSpec spec, string text, string? expectedContainerId = null)
    {
        ValidateSpec(spec);
        if (text.Length > MaximumCommandOutput) throw new InvalidDataException("Container inspect exceeded its bound.");
        using var document = ParseJson(text);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1)
            throw new InvalidDataException("Container inspect did not return one exact object.");
        var value = document.RootElement[0];
        var cid = value.GetProperty("Id").GetString();
        var labels = value.GetProperty("Config").GetProperty("Labels");
        if (!ContainerId(cid) || expectedContainerId is not null && cid != expectedContainerId ||
            value.GetProperty("Name").GetString() != "/" + ContainerName(spec) || value.GetProperty("Image").GetString() != spec.ImageDigest ||
            labels.GetProperty("pegasus.cerebral.worker").GetString() != spec.WorkerInstallationId ||
            labels.GetProperty("pegasus.cerebral.instance").GetString() != spec.InstanceId ||
            labels.GetProperty("pegasus.cerebral.model-manifest").GetString() != spec.ModelManifestSha256)
            throw new InvalidDataException("Container ownership or image identity differs. No stop/remove is admitted.");
        var state = value.GetProperty("State"); var status = state.GetProperty("Status").GetString()!;
        var running = state.GetProperty("Running").GetBoolean();
        if (status is not ("created" or "running" or "paused" or "restarting" or "removing" or "exited" or "dead") ||
            running && status is "created" or "exited" or "dead" || !running && status is "running" or "paused")
            throw new InvalidDataException("Container observation has an invalid or inconsistent state.");
        var terminal = !running && status is "exited" or "dead";
        return new(cid!, running, terminal, terminal ? state.GetProperty("ExitCode").GetInt32() : null, status, true);
    }

    public async Task<OciModelContainerObservation> StopAsync(CancellationToken token)
    {
        var state = await InspectAsync(token).ConfigureAwait(false);
        if (state.SafeToRemove) return state;
        await CommandAsync(["stop", "--time", "10", RequiredIdentity().ContainerId], token).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            state = await InspectAsync(token).ConfigureAwait(false);
            if (state.SafeToRemove) return state;
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        throw new IOException("Container stop is unconfirmed; retain resident reservation and reconcile exact CID.");
    }

    public async Task RemoveStoppedAsync(CancellationToken token)
    {
        var state = await InspectAsync(token).ConfigureAwait(false);
        if (!state.SafeToRemove) throw new InvalidOperationException("Only a confirmed stopped or never-started model container may be removed.");
        await CommandAsync(["rm", RequiredIdentity().ContainerId], token).ConfigureAwait(false);
        // Keep the durable activation journal as provenance, even after removing this owned CID.
    }

    public void RestoreIdentity(OciModelContainerIdentity saved)
    {
        if (identity is not null || saved.WorkerInstallationId != spec.WorkerInstallationId || saved.InstanceId != spec.InstanceId ||
            saved.ImageDigest != spec.ImageDigest || !ContainerId(saved.ContainerId))
            throw new InvalidDataException("Cannot rebind an activation to another container or Worker.");
        identity = saved;
    }

    /// <summary>Only the exact bounded creating intent authorizes name-based read-only reconciliation.</summary>
    public static void ValidateCreatingIntent(string text, OciModelContainerSpec spec)
    {
        ValidateSpec(spec);
        if (Encoding.UTF8.GetByteCount(text) > MaximumIntentBytes) throw new InvalidDataException("Container intent exceeds its bound.");
        using var document = ParseJson(text);
        var value = document.RootElement;
        string[] names = ["schema", "state", "WorkerInstallationId", "InstanceId", "ImageDigest", "ModelManifestSha256"];
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length ||
            value.EnumerateObject().Any(x => !names.Contains(x.Name, StringComparer.Ordinal)) ||
            value.GetProperty("schema").GetString() != "cerebral.model-container/1" || value.GetProperty("state").GetString() != "creating" ||
            value.GetProperty("WorkerInstallationId").GetString() != spec.WorkerInstallationId ||
            value.GetProperty("InstanceId").GetString() != spec.InstanceId || value.GetProperty("ImageDigest").GetString() != spec.ImageDigest ||
            value.GetProperty("ModelManifestSha256").GetString() != spec.ModelManifestSha256)
            throw new InvalidDataException("Container intent does not match this exact activation.");
    }

    private string ReadIntent()
    {
        ModelOperationArtifactStore.Ordinary(journalPath);
        using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 2 or > MaximumIntentBytes) throw new InvalidDataException("Container intent exceeds its bound.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private OciModelContainerIdentity Acknowledge(string cid)
    {
        // Keep the original intent if either acknowledgement write or atomic replacement is uncertain.
        // An existing acknowledgement scratch file is never silently overwritten or replayed.
        ValidateCreatingIntent(ReadIntent(), spec);
        var acknowledged = new OciModelContainerIdentity(cid, spec.WorkerInstallationId, spec.InstanceId, spec.ImageDigest, DateTimeOffset.UtcNow);
        var temporary = journalPath + ".created";
        ModelOperationArtifactStore.Ordinary(temporary); ModelOperationArtifactStore.Ordinary(journalPath);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { JsonSerializer.Serialize(stream, acknowledged); stream.Flush(true); }
        File.Move(temporary, journalPath, overwrite: true);
        identity = acknowledged;
        return acknowledged;
    }

    private static void ValidateSpec(OciModelContainerSpec spec)
    {
        ModelOperationArtifactStore.Identifier(spec.WorkerInstallationId); ModelOperationArtifactStore.Identifier(spec.InstanceId);
        if (!ModelOperationArtifactStore.IsDigest(spec.DockerSha256) ||
            !spec.ImageDigest.StartsWith("sha256:", StringComparison.Ordinal) || !ModelOperationArtifactStore.IsDigest(spec.ImageDigest[7..]) ||
            !ModelOperationArtifactStore.IsDigest(spec.ModelManifestSha256)) throw new ArgumentException("Runtime executable, local image and model manifest must be pinned.");
        foreach (var path in new[] { spec.DockerExecutable, spec.ModelDirectory, spec.InputDirectory, spec.OutputDirectory })
            if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl) || path.Contains(','))
                throw new ArgumentException("Use ordinary absolute runtime paths without control characters or mount delimiters.");
        if (Overlap(spec.OutputDirectory, spec.ModelDirectory) || Overlap(spec.OutputDirectory, spec.InputDirectory))
            throw new ArgumentException("Writable output must be disjoint from every read-only model/input mount.");
        if (Within(spec.DockerExecutable, spec.OutputDirectory))
            throw new ArgumentException("Container output cannot contain its pinned host control executable.");
    }

    private static bool Within(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); var child = Path.GetFullPath(path);
        return string.Equals(child, parent, comparison) || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, comparison);
    }
    private static bool Overlap(string a, string b) => Within(a, b) || Within(b, a);
    private static string ContainerName(OciModelContainerSpec spec) => "cerebral-model-" + spec.InstanceId;
    private static bool ContainerId(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonDocument ParseJson(string text)
    {
        var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        try { Unique(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
        static void Unique(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in value.EnumerateObject())
                { if (!names.Add(field.Name)) throw new InvalidDataException("Duplicate container JSON property."); Unique(field.Value); }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
        }
    }

    private void VerifyExecutable()
    {
        ModelOperationArtifactStore.Ordinary(spec.DockerExecutable);
        using var executable = new FileStream(spec.DockerExecutable, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexStringLower(SHA256.HashData(executable)) != spec.DockerSha256)
            throw new InvalidDataException("Installed container executor differs from its admitted identity.");
    }

    private OciModelContainerIdentity RequiredIdentity() => identity ?? throw new InvalidOperationException("Container identity has not been acknowledged. Reconcile the creating journal.");

    private ProcessStartInfo StartInfo(IEnumerable<string> arguments)
    {
        VerifyExecutable();
        var result = new ProcessStartInfo(spec.DockerExecutable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var name in new[] { "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH", "DOCKER_API_VERSION" }) result.Environment.Remove(name);
        result.ArgumentList.Add("--host"); result.ArgumentList.Add("unix:///var/run/docker.sock");
        foreach (var argument in arguments) result.ArgumentList.Add(argument);
        return result;
    }

    private async Task<string> CommandAsync(IEnumerable<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = StartInfo(arguments) };
        if (!process.Start()) throw new IOException("Container control process did not start.");
        process.StandardInput.Close();
        async Task<string> ReadBoundedAsync(StreamReader reader)
        {
            var buffer = new char[4096]; var text = new System.Text.StringBuilder();
            while (true)
            {
                var count = await reader.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (count == 0) return text.ToString();
                if (text.Length + count > MaximumCommandOutput) throw new IOException("Container control output exceeded its bound.");
                text.Append(buffer, 0, count);
            }
        }
        var stdout = ReadBoundedAsync(process.StandardOutput); var stderr = ReadBoundedAsync(process.StandardError);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Container control failed (exit " + process.ExitCode + "). The exact activation requires inspection; no automatic replay.");
            return await stdout.ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch { /* This only reaps the CLI. The CID's state is deliberately not inferred. */ }
            }
        }
    }

    private static string PosixIdentity()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var uid = Native.GetUid();
        if (uid == 0) throw new UnauthorizedAccessException("Model containers require a non-root Worker identity.");
        return uid + ":" + Native.GetGid();
    }
    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")] internal static extern uint GetUid();
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getegid")] internal static extern uint GetGid();
    }
}
