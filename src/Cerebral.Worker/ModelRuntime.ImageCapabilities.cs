using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed class ModelCapabilityReadinessException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>Read-only report. Only the private live instance cache is used to authorize readiness.</summary>
public sealed record ModelImageVerification(string ModelId, string InstanceId, string AdapterId,
    string AdapterVersion, DateTimeOffset VerifiedAt, int InputTokens, int? OutputTokens);

/// <summary>One loaded instance's pinned components and serialized, bounded verification.</summary>
internal sealed class NativeImageVerification : IDisposable
{
    private readonly string binding;
    private readonly string serverPath;
    private readonly List<PinnedFile> pins = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private ModelImageVerification? verified;
    private string? failure;
    private int retired;

    internal NativeImageVerification(ModelRecord model, string executable)
    {
        if (model.VisionProjectorPath is null)
            throw new ModelCapabilityReadinessException("Configure the matching image projector before verifying an image-required role.");
        binding = JsonSerializer.Serialize(WorkerExecutionSnapshot.Bind(model), CerebralJson.Options);
        serverPath = Path.GetFullPath(executable);
        try
        {
            // These handles prevent Windows writes/replacement for the entire loaded instance,
            // including the interval between verification and a later task. They hold no file bytes.
            foreach (var path in new[] { model.Path, model.VisionProjectorPath, executable })
                pins.Add(new PinnedFile(Path.GetFullPath(path)));
        }
        catch { Dispose(); throw; }
    }

    internal bool Matches(ModelRecord model, string executable) => Volatile.Read(ref retired) == 0 &&
        string.Equals(serverPath, Path.GetFullPath(executable), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        binding == JsonSerializer.Serialize(WorkerExecutionSnapshot.Bind(model), CerebralJson.Options) && pins.All(x => x.Matches());

    internal ModelImageVerification? Current => Volatile.Read(ref retired) == 0 ? Volatile.Read(ref verified) : null;

    internal async Task<ModelImageVerification> EnsureAsync(Func<CancellationToken, Task<ModelImageVerification>> probe,
        Func<bool> stillCurrent, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
                throw new ModelCapabilityReadinessException("Image-required role verification currently needs Windows pinned file identity. Generic native image input remains separate from this role-readiness gate.");
            EnsureCurrent();
            if (verified is not null) return verified;
            if (failure is not null) throw new ModelCapabilityReadinessException(failure);
            try
            {
                var result = await probe(token);
                token.ThrowIfCancellationRequested();
                EnsureCurrent();
                Volatile.Write(ref verified, result);
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            {
                failure = "Native image verification timed out. Check runtime readiness, then unload and reload before retrying; the role remains configured.";
                throw new ModelCapabilityReadinessException(failure, ex);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or JsonException)
            {
                failure = "Native image verification did not complete successfully. Check the matching projector, runtime and chat template, then unload and reload before retrying. " +
                    (ex is NativeImageVerificationException ? ex.Message : "The runtime could not establish image readiness.");
                throw new ModelCapabilityReadinessException(failure, ex);
            }
        }
        finally { gate.Release(); }

        void EnsureCurrent()
        {
            if (Volatile.Read(ref retired) != 0 || !stillCurrent())
                throw new ModelCapabilityReadinessException("The model, image component or runtime changed during verification. Unload and reload the current configuration.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref retired, 1) != 0) return;
        Volatile.Write(ref verified, null);
        foreach (var file in pins) file.Dispose();
        // Keep the immutable pin list intact for readers racing with retirement.
        // A forced unload may still have waiters. The gate has no kernel handle and is reclaimed
        // with this instance after those callers finish, rather than disposing it under a waiter.
    }

    private sealed class PinnedFile : IDisposable
    {
        private readonly FileStream stream;
        private readonly FileIdentity identity;
        internal PinnedFile(string path)
        {
            stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try { identity = OperatingSystem.IsWindows() ? Identify(stream) : default; }
            catch { stream.Dispose(); throw; }
        }
        internal bool Matches()
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var current = new FileStream(stream.Name, FileMode.Open, FileAccess.Read, FileShare.Read);
                return identity.Equals(Identify(current));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { return false; }
        }
        public void Dispose() => stream.Dispose();
        private static FileIdentity Identify(FileStream stream)
        {
            if (!GetFileInformationByHandleEx(stream.SafeFileHandle, 18, out var result, Marshal.SizeOf<FileIdentity>()))
                throw new IOException("The host could not establish pinned model component file identity.");
            return result;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileIdentity : IEquatable<FileIdentity>
    {
        private readonly ulong volume;
        private readonly ulong low;
        private readonly ulong high;
        public FileIdentity(ulong volume, ulong low, ulong high)
        { this.volume = volume; this.low = low; this.high = high; }
        public bool Equals(FileIdentity other) => volume == other.volume && low == other.low && high == other.high;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileIdentity information, int size);

    internal static async Task<ModelImageVerification> ProbeAsync(HttpClient http, LlamaSlotPool pool,
        ModelRecord model, string instanceId, Func<bool> exited, CancellationToken token,
        ModelExecutionAccounting? accounting = null)
    {
        var ownership = accounting?.Begin(ModelOwnershipKind.ImageVerification, model.Id);
        ownership?.Bind(instanceId: instanceId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var image = Convert.ToBase64String(ProbeImage());
        using var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"left":{"type":"string","enum":["red","blue","green","yellow","unknown"]},"right":{"type":"string","enum":["red","blue","green","yellow","unknown"]}},"required":["left","right"],"additionalProperties":false}
            """);
        const string prompt = "Identify the colored rectangles in the supplied image. Return only JSON with exactly left and right color names. Do not explain.";
        var request = new ModelGenerationRequest(model.Id, [new("user", [new TextContentPart(prompt)])],
            Math.Min(model.ContextTokens, 4096), 64, Temperature: 0, ResponseSchema: schema.RootElement);
        var messages = JsonSerializer.SerializeToElement(new[]
        {
            new { role = "user", content = new object[]
            {
                new { type = "text", text = prompt },
                new { type = "image_url", image_url = new { url = "data:image/png;base64," + image } }
            }}
        });
        LlamaSlotPool.Lease slot;
        try { slot = await pool.AcquireAsync(() => ownership?.Complete(), timeout.Token,
            cause => ownership?.Unknown("image-verification-slot-quarantined")); }
        catch { ownership?.Complete(); throw; }
        // The probe uses an ordinary physical lease. Publish its exact coordinate so
        // ownership views can label this slot as a vision check while it is retained.
        ownership?.Bind(leaseId: slot.LeaseId);
        ownership?.NativeSlot(slot.LeaseId, slot.Id);
        ownership?.Phase(ModelOwnershipPhase.Preparing, effectStarted: true);
        await using var prepared = await LlamaPreparedGeneration.PrepareMultimodalAsync(http,
            slot, instanceId, request, messages, [image], model.StopSequences, exited, timeout.Token);
        ownership?.Phase(ModelOwnershipPhase.Generating, effectStarted: true);
        var result = await prepared.GenerateAsync(null, timeout.Token);
        timeout.Token.ThrowIfCancellationRequested();
        if (result.Finish != ModelGenerationFinish.Stop || result.Error is not null ||
            result.EvaluatedTokens != prepared.Metadata.InputTokens)
            throw new NativeImageVerificationException("A complete result with the prepared native image count is required.");
        using var answer = JsonDocument.Parse(result.Text);
        var value = answer.RootElement;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2 ||
            !value.TryGetProperty("left", out var left) || left.ValueKind != JsonValueKind.String || left.GetString() != "red" ||
            !value.TryGetProperty("right", out var right) || right.ValueKind != JsonValueKind.String || right.GetString() != "blue")
            throw new NativeImageVerificationException("The visual probe did not identify the expected left and right colors.");
        return new(model.Id, instanceId, prepared.Metadata.AdapterId, prepared.Metadata.AdapterVersion,
            DateTimeOffset.UtcNow, prepared.Metadata.InputTokens, result.GeneratedTokens);
    }

    private sealed class NativeImageVerificationException(string message) : InvalidOperationException(message);

    private static byte[] ProbeImage()
    {
        using var result = new MemoryStream();
        result.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, 256);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 256);
        header[8] = 8; header[9] = 2;
        Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            for (var y = 0; y < 256; y++)
            {
                zip.WriteByte(0);
                for (var x = 0; x < 256; x++)
                {
                    ReadOnlySpan<byte> color = x is >= 24 and < 112 && y is >= 40 and < 216 ? [230, 25, 25] :
                        x is >= 144 and < 232 && y is >= 40 and < 216 ? [25, 50, 230] : [250, 250, 250];
                    zip.Write(color);
                }
            }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        return result.ToArray();

        void Chunk(string name, byte[] bytes)
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(number, bytes.Length); result.Write(number);
            var kind = Encoding.ASCII.GetBytes(name); result.Write(kind); result.Write(bytes);
            uint crc = uint.MaxValue;
            foreach (var b in kind.Concat(bytes))
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
            }
            BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); result.Write(number);
        }
    }
}

public sealed partial class ModelRuntime
{
    private static readonly IReadOnlySet<string> VerifiedImageCapabilities = new HashSet<string>(StringComparer.Ordinal)
        { ModelCapabilityIds.ImageInput };

    public IReadOnlyList<ModelCapabilityStatus> AssessCapabilities(ModelRecord model) =>
        ModelCapabilityPolicy.Assess(model, CurrentImageVerification(model) is null ? null : VerifiedImageCapabilities);

    public ModelImageVerification? CurrentImageVerification(ModelRecord model)
    {
        if (!instances.TryGetValue(model.Id, out var instance) || HasExited(instance) ||
            instance.Slots.Quarantined.Count != 0 || instance.Images is not { } images ||
            !images.Matches(model, settings().LlamaServerPath)) return null;
        return images.Current;
    }

    public void EnsureCurrentlyCompatible(AgentProfile profile, ModelRecord model) =>
        ModelCapabilityPolicy.EnsureCompatible(profile, model,
            CurrentImageVerification(model) is null ? null : VerifiedImageCapabilities);

    public async Task EnsureRequiredCapabilitiesAsync(AgentProfile profile, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var model = profile.WorkerId == "local" && profile.ModelWorkerId == "local"
            ? store.Get<ModelRecord>("model", profile.ModelId) : null;
        ModelCapabilityPolicy.EnsureConfigured(profile, model);
        if (ModelCapabilityPolicy.Requirements(profile).Contains(ModelCapabilityIds.ImageInput))
        {
            using var lease = await AcquireRequiredCapabilitiesAsync(profile, token);
        }
    }

    /// <summary>Keep the verified instance busy through the caller's task generation.</summary>
    public async Task<IDisposable?> AcquireRequiredCapabilitiesAsync(AgentProfile profile, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var model = profile.WorkerId == "local" && profile.ModelWorkerId == "local"
            ? store.Get<ModelRecord>("model", profile.ModelId) : null;
        ModelCapabilityPolicy.EnsureConfigured(profile, model);
        if (!ModelCapabilityPolicy.Requirements(profile).Contains(ModelCapabilityIds.ImageInput)) return null;
        if (model?.VisionProjectorPath is null)
            throw new ModelCapabilityReadinessException("Configure the matching image projector before using an image-required role.");
        var ownership = Accounting.Begin(ModelOwnershipKind.CapabilityLease, profile.ModelId);
        Instance instance;
        try { instance = await AcquireAsync(profile.ModelId, token); ownership.Bind(instanceId: instance.Id); }
        catch { ownership.Complete(); throw; }
        try
        {
            await VerifyAcquiredImageAsync(instance, profile.ModelId, token);
            ownership.Phase(ModelOwnershipPhase.Ready);
            return new CapabilityLease(() => { ReleaseCapability(instance); ownership.Complete(); });
        }
        catch { ReleaseCapability(instance); ownership.Complete(); throw; }
    }

    private sealed class CapabilityLease(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }

    private static void ReleaseCapability(Instance instance)
    {
        instance.Idle.Touch();
        Interlocked.Decrement(ref instance.Busy);
    }

    private async Task<ModelImageVerification> VerifyAcquiredImageAsync(Instance instance, string modelId, CancellationToken token)
    {
        var images = instance.Images ?? throw new ModelCapabilityReadinessException("The loaded model has no pinned image components. Unload and reload it.");
        bool Current()
        {
            var current = store.Get<ModelRecord>("model", modelId);
            return !HasExited(instance) && instance.Slots.Quarantined.Count == 0 && current is not null &&
                instances.TryGetValue(modelId, out var loaded) && ReferenceEquals(instance, loaded) &&
                images.Matches(current, settings().LlamaServerPath);
        }
        return await images.EnsureAsync(ct =>
        {
            var current = store.Get<ModelRecord>("model", modelId) ?? throw new ModelCapabilityReadinessException("Model registration was removed.");
            return NativeImageVerification.ProbeAsync(instance.Http, instance.Slots, current, instance.Id,
                () => HasExited(instance), ct, Accounting);
        }, Current, token);
    }

    public async Task<ModelImageVerification> VerifyImageInputAsync(string modelId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var configured = store.Get<ModelRecord>("model", modelId) ?? throw new ArgumentException("Model not found.");
        if (configured.VisionProjectorPath is null)
            throw new ModelCapabilityReadinessException("Configure the matching image projector before using an image-required role.");
        using var ownership = Accounting.Begin(ModelOwnershipKind.CapabilityLease, modelId);
        Instance instance;
        try { instance = await AcquireAsync(modelId, token); ownership.Bind(instanceId: instance.Id); }
        catch { ownership.Complete(); throw; }
        try
        {
            var result = await VerifyAcquiredImageAsync(instance, modelId, token);
            events.Publish("model.capability.verified", null, result);
            return result;
        }
        finally { ReleaseCapability(instance); ownership.Complete(); }
    }
}
