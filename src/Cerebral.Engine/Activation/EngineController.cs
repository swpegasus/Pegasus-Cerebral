using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cerebral.Engine.Contracts;

namespace Cerebral.Engine.Activation;

public sealed record EngineSlotStatus(string Slot, bool Available, bool BuildAllowed, EnginePackageInfo? Package, string? Error);
public sealed record EngineStatus(bool Paused, long Epoch, int ActiveEvaluations, long CommittedDecisions,
    bool CandidatePending, EnginePackageInfo? Active, string? KnownGoodSlot, string? Error,
    IReadOnlyList<EngineSlotStatus> Slots, int UnreleasedContexts, string? Recovery);
public sealed class EnginePausedException(string message) : InvalidOperationException(message);
public sealed class EngineEvaluationException(string message) : InvalidOperationException(message);

/// <summary>One strategy generation; the host owns effects independently. All activation and decision
/// commits use the same exclusion lock. No plugin object crosses this boundary.</summary>
public sealed class EngineController(string programRoot, string dataRoot) : IAsyncDisposable
{
    private readonly string programRoot = Path.GetFullPath(programRoot);
    private readonly string selectionPath = Path.Combine(Path.GetFullPath(dataRoot), "selection.ini");
    private readonly object gate = new();
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly FeatureBroker broker = new();
    private readonly List<WeakReference> retired = [];
    private Generation? active;
    private Selection selection = new();
    private bool paused = true;
    private string? error;
    private string? recovery;
    private bool resumeAfterRecovery;
    private bool recoveryAttempted;
    private int evaluations;
    private long committed;
    private bool disposed;

    private sealed record Selection(long Epoch = 0, string? Slot = null, string? Package = null, string? Configuration = null,
        string? KnownSlot = null, string? KnownPackage = null, string? KnownConfiguration = null, bool Pending = false, bool Paused = false);
    internal sealed record Generation(EnginePackageInfo Info, IEnginePlugin Plugin, EngineLoadContext Context, EngineDescriptor Descriptor);

    public bool IsPaused { get { lock (gate) return paused || active is null; } }
    public bool SupportsWork(string contract, int version)
    {
        lock (gate) return active?.Descriptor.Work.Any(x => x.InputContract == contract && x.InputVersion == version) == true;
    }
    public EngineStatus Status()
    {
        lock (gate)
        {
            var slots = new List<EngineSlotStatus>();
            foreach (var slot in new[] { "A", "B" })
            {
                var writable = slot != selection.Slot && (!selection.Pending || slot != selection.KnownSlot);
                try { slots.Add(new(slot, true, writable, EnginePackage.ReadSlot(programRoot, slot).Info, null)); }
                catch (Exception ex) when (Ordinary(ex)) { slots.Add(new(slot, false, writable, null, Message(ex))); }
            }
            return new(paused || active is null, selection.Epoch, evaluations, committed, selection.Pending,
                active?.Info, selection.KnownSlot, error, slots.AsReadOnly(), retired.Count(x => x.IsAlive), recovery);
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await changes.WaitAsync(ct).ConfigureAwait(false);
        Generation? initial = null;
        try
        {
            lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); if (active is not null) return; }
            try
            {
                if (File.Exists(selectionPath)) selection = ReadSelection();
                var slot = selection.Pending ? selection.KnownSlot : selection.Slot;
                var packagePin = selection.Pending ? selection.KnownPackage : selection.Package;
                var configPin = selection.Pending ? selection.KnownConfiguration : selection.Configuration;
                Generation generation;
                var restored = selection.Pending;
                try { generation = await LoadAsync(slot ?? "A", packagePin, configPin, ct).ConfigureAwait(false); }
                catch (Exception ex) when (Ordinary(ex) && selection.KnownSlot is not null && selection.KnownSlot != slot)
                {
                    generation = await LoadAsync(selection.KnownSlot, selection.KnownPackage, selection.KnownConfiguration, ct).ConfigureAwait(false);
                    restored = true;
                    recovery = "Active engine failed; restored the pinned known-good engine: " + Message(ex);
                }
                initial = generation;
                if (selection.Pending) recovery = "An unconfirmed engine was restored to the pinned known-good slot during startup.";
                ct.ThrowIfCancellationRequested();
                var next = new Selection(Math.Max(1, checked(selection.Epoch + (restored ? 1 : 0))),
                    generation.Info.Slot, generation.Info.PackageSha256, generation.Info.ConfigurationSha256,
                    generation.Info.Slot, generation.Info.PackageSha256, generation.Info.ConfigurationSha256, false, selection.Paused);
                WriteSelection(next);
                lock (gate) { active = generation; initial = null; selection = next; paused = next.Paused; }
            }
            catch (Exception ex) when (Ordinary(ex))
            {
                // A missing/broken engine does not prevent the host transport or Configurator from starting.
                lock (gate) { paused = true; error = "Engine unavailable: " + Message(ex); }
            }
        }
        finally { if (initial is not null) await RetireAsync(initial).ConfigureAwait(false); changes.Release(); }
    }

    public EngineStatus Pause(long expectedEpoch)
    {
        lock (gate)
        {
            Expected(expectedEpoch); var next = selection with { Paused = true }; resumeAfterRecovery = false;
            WriteSelection(next); selection = next; paused = true; return Status();
        }
    }
    public EngineStatus Resume(long expectedEpoch)
    {
        lock (gate)
        {
            Expected(expectedEpoch);
            if (active is null || error is not null) throw new InvalidOperationException("Restore a healthy engine before resuming.");
            var next = selection with { Paused = false };
            WriteSelection(next); selection = next; paused = false; return Status();
        }
    }

    public async Task<EngineStatus> ActivateAsync(string slot, long expectedEpoch, CancellationToken ct = default)
    {
        await changes.WaitAsync(ct).ConfigureAwait(false);
        Generation? candidate = null;
        try
        {
            lock (gate)
            {
                Expected(expectedEpoch); RequireQuiescent();
                if (selection.Pending) throw new InvalidOperationException("Test and promote or roll back the current candidate before replacing the fallback.");
                if (slot == selection.Slot) throw new InvalidOperationException("The selected slot is already active.");
            }
            candidate = await LoadAsync(slot, null, null, ct).ConfigureAwait(false);
            Generation? previous;
            lock (gate)
            {
                Expected(expectedEpoch); RequireQuiescent();
                ct.ThrowIfCancellationRequested();
                var next = new Selection(checked(selection.Epoch + 1), candidate.Info.Slot, candidate.Info.PackageSha256,
                    candidate.Info.ConfigurationSha256, selection.Slot, selection.Package, selection.Configuration,
                    selection.Slot is not null, true);
                WriteSelection(next);
                previous = active; active = candidate; candidate = null; selection = next; committed = 0; error = null;
                recovery = null; recoveryAttempted = false;
            }
            await RetireAsync(previous).ConfigureAwait(false);
            return Status();
        }
        finally { if (candidate is not null) await RetireAsync(candidate).ConfigureAwait(false); changes.Release(); }
    }

    public Task<EngineStatus> RollbackAsync(long expectedEpoch, CancellationToken ct = default) => RestoreAsync(expectedEpoch, false, ct);

    /// <summary>One automatic attempt for a failed candidate, after its evaluation leases settle.
    /// Ordinary feature/tool failures never enter this path. An owner pause overrides automatic resume.</summary>
    public async Task<bool> RecoverAsync(CancellationToken ct = default)
    {
        long epoch;
        lock (gate)
        {
            if (disposed || !selection.Pending || error is null || evaluations != 0 || recoveryAttempted) return false;
            epoch = selection.Epoch;
        }
        await RestoreAsync(epoch, true, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<EngineStatus> RestoreAsync(long expectedEpoch, bool automatic, CancellationToken ct)
    {
        await changes.WaitAsync(ct).ConfigureAwait(false);
        Generation? candidate = null;
        try
        {
            string slot; string? package; string? configuration; string? reason;
            lock (gate)
            {
                if (automatic && (selection.Epoch != expectedEpoch || !selection.Pending || error is null || evaluations != 0 || recoveryAttempted)) return Status();
                Expected(expectedEpoch); RequireQuiescent();
                if (!selection.Pending || selection.KnownSlot is null) throw new InvalidOperationException("No protected fallback is available.");
                slot = selection.KnownSlot; package = selection.KnownPackage; configuration = selection.KnownConfiguration;
                reason = error;
                if (automatic) recoveryAttempted = true;
            }
            candidate = await LoadAsync(slot, package, configuration, ct).ConfigureAwait(false);
            Generation? previous;
            lock (gate)
            {
                Expected(expectedEpoch); RequireQuiescent();
                ct.ThrowIfCancellationRequested();
                var next = new Selection(checked(selection.Epoch + 1), slot, package, configuration, slot, package, configuration, false, !automatic || !resumeAfterRecovery);
                WriteSelection(next); previous = active; active = candidate; candidate = null;
                selection = next; paused = next.Paused; committed = 0; error = null;
                recovery = automatic ? "Restored the pinned known-good engine after candidate failure: " + reason : "Rolled back to the pinned known-good engine.";
            }
            await RetireAsync(previous).ConfigureAwait(false);
            return Status();
        }
        catch (Exception ex) when (automatic && Ordinary(ex) && ex is not OperationCanceledException)
        {
            lock (gate) { paused = true; error = "Engine fallback failed; work remains paused: " + Message(ex); }
            return Status();
        }
        catch (OperationCanceledException) when (automatic)
        {
            lock (gate)
                if (selection.Epoch == expectedEpoch && selection.Pending) recoveryAttempted = false;
            throw;
        }
        finally { if (candidate is not null) await RetireAsync(candidate).ConfigureAwait(false); changes.Release(); }
    }

    public EngineStatus Promote(long expectedEpoch)
    {
        lock (gate)
        {
            Expected(expectedEpoch);
            if (!selection.Pending || active is null || committed < 1 || error is not null)
                throw new InvalidOperationException("A candidate must complete a host-committed decision and be explicitly confirmed before promotion.");
            var next = selection with { KnownSlot = selection.Slot, KnownPackage = selection.Package,
                KnownConfiguration = selection.Configuration, Pending = false };
            WriteSelection(next); selection = next; return Status();
        }
    }

    /// <summary>Gate only the host's committed dispatch intent; an already dispatched provider keeps
    /// its host lease and can report its exact receipt after an engine switch.</summary>
    public T Dispatch<T>(Func<T> commit)
    {
        lock (gate) { if (paused || active is null) throw new EnginePausedException("The engine is paused."); return commit(); }
    }

    public async Task<DecisionLease> EvaluateAsync(string workId, long revision, string contract, int version,
        JsonElement payload, Func<CancellationToken, Task<JsonElement>> pureFeature, CancellationToken ct)
    {
        Generation? generation; EngineRequest request; EngineWorkSupport support;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (paused || active is null) throw new EnginePausedException("The engine is paused or unavailable.");
            generation = active;
            support = generation.Descriptor.Work.SingleOrDefault(x => x.InputContract == contract && x.InputVersion == version)
                ?? throw new EngineEvaluationException("The selected engine does not support this work contract.");
            Bound(payload);
            request = new(Guid.NewGuid().ToString("N"), workId, revision, selection.Epoch,
                generation.Info.ConfigurationSha256, contract, version, payload.Clone());
            broker.Add(request, pureFeature); evaluations++;
        }
        try
        {
            var decision = await generation.Plugin.EvaluateAsync(request, ct).ConfigureAwait(false);
            if (decision is null || decision.OperationId != request.OperationId || decision.WorkId != request.WorkId ||
                decision.ExpectedRevision != request.ExpectedRevision || decision.ActivationEpoch != request.ActivationEpoch ||
                decision.ConfigurationFingerprint != request.ConfigurationFingerprint || decision.Contract != support.OutputContract || decision.Version != support.OutputVersion)
                throw new EngineEvaluationException("Engine returned a stale or mismatched decision envelope.");
            Bound(decision.Payload);
            return new(this, generation, request, decision with { Payload = decision.Payload.Clone() });
        }
        catch (EngineFeatureFailureException ex) { Release(request.OperationId); throw new InvalidOperationException(ex.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Release(request.OperationId); throw; }
        catch (Exception ex) when (Ordinary(ex))
        {
            Fault(Message(ex)); Release(request.OperationId); throw new EngineEvaluationException(Message(ex));
        }
        finally { generation = null; } // Completed task boxes must not retain collectible strategy code.
    }

    public sealed class DecisionLease : IDisposable
    {
        private EngineController? owner;
        private Generation? generation;
        private readonly EngineRequest request;
        private bool committed;
        public EngineDecision Decision { get; }
        internal DecisionLease(EngineController owner, Generation generation, EngineRequest request, EngineDecision decision)
        { this.owner = owner; this.generation = generation; this.request = request; Decision = decision; }
        public T Commit<T>(Func<EngineDecision, T> commit)
        {
            var controller = owner ?? throw new ObjectDisposedException(nameof(DecisionLease));
            lock (controller.gate)
            {
                if (committed) throw new InvalidOperationException("This engine decision was already committed.");
                if (controller.error is not null) throw new EngineEvaluationException("The selected engine has faulted; this uncommitted decision was discarded.");
                if (controller.active != generation || controller.selection.Epoch != request.ActivationEpoch ||
                    controller.selection.Configuration != request.ConfigurationFingerprint)
                    throw new EngineEvaluationException("Decision belongs to a retired engine generation.");
                var result = commit(Decision); committed = true; controller.committed++; return result;
            }
        }
        public void Reject(string message) => (owner ?? throw new ObjectDisposedException(nameof(DecisionLease))).Fault(message);
        public void Dispose()
        {
            var controller = Interlocked.Exchange(ref owner, null); generation = null;
            controller?.Release(request.OperationId);
        }
    }

    private void Fault(string message)
    {
        lock (gate)
        {
            if (error is null) resumeAfterRecovery = !paused;
            paused = true; error = "Engine evaluation failed: " + message;
            var next = selection with { Paused = true };
            try { WriteSelection(next); selection = next; }
            catch (Exception ex) when (Ordinary(ex)) { error += "; pause persistence failed: " + Message(ex); }
        }
    }
    private void Release(string operationId) { lock (gate) { broker.Remove(operationId); evaluations--; } }
    private void Expected(long epoch)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (epoch != selection.Epoch) throw new InvalidOperationException("Engine activation changed; refresh before retrying.");
    }
    private void RequireQuiescent()
    {
        if (!paused || evaluations != 0) throw new InvalidOperationException("Pause and wait for all engine evaluations before switching. Host effects retain their own receipts.");
        TrimRetired();
        if (retired.Count >= 32) throw new InvalidOperationException("Too many retired engine contexts remain reachable. Release engine-owned references before another replacement.");
    }
    private async Task<Generation> LoadAsync(string slot, string? packagePin, string? configurationPin, CancellationToken ct)
    {
        var package = EnginePackage.ReadSlot(programRoot, slot);
        if (packagePin is not null && package.Info.PackageSha256 != packagePin ||
            configurationPin is not null && package.Info.ConfigurationSha256 != configurationPin)
            throw new InvalidDataException("The selected engine no longer matches its recorded code/configuration pins.");
        var context = new EngineLoadContext(); IEnginePlugin? plugin = null;
        try
        {
            using var image = new MemoryStream(package.AssemblyBytes, writable: false);
            var assembly = context.LoadFromStream(image);
            var type = assembly.GetType(package.Info.EntryType, throwOnError: true)!;
            if (!typeof(IEnginePlugin).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidDataException("Engine entry must implement the shared engine contract with a public parameterless constructor.");
            plugin = (IEnginePlugin)Activator.CreateInstance(type)!;
            var descriptor = plugin.Descriptor;
            if (descriptor.Id != package.Info.Id || descriptor.Version != package.Info.Version || descriptor.ProtocolVersion != EngineProtocol.Version ||
                descriptor.StateFormat != EngineProtocol.StatelessFormat || descriptor.Work is null || descriptor.Work.Count is < 1 or > 16 ||
                descriptor.Work.Any(x => x is null || x.InputContract.Length is < 1 or > 128 || x.OutputContract.Length is < 1 or > 128 || x.InputVersion < 1 || x.OutputVersion < 1) ||
                descriptor.Work.Select(x => (x.InputContract, x.InputVersion)).Distinct().Count() != descriptor.Work.Count)
                throw new InvalidDataException("Engine identity, stateless checkpoint format or supported contracts are incompatible.");
            descriptor = descriptor with { Work = Array.AsReadOnly(descriptor.Work.ToArray()) };
            // Current host compatibility adapter is explicit. Other representations can be added without
            // requiring every engine to use WorkflowRun or exposing host-private types in the SPI.
            if (!descriptor.Work.Any(x => x == new EngineWorkSupport("cerebral.workflow.transition", 1, "cerebral.workflow.step", 1)))
                throw new InvalidDataException("Engine cannot consume this host's workflow transition adapter.");
            await plugin.InitializeAsync(broker, package.Configuration, ct).ConfigureAwait(false);
            await plugin.CheckHealthAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new(package.Info, plugin, context, descriptor);
        }
        catch
        {
            if (plugin is not null) try { await plugin.DisposeAsync().ConfigureAwait(false); } catch { }
            context.Unload(); lock (gate) { retired.Add(new(context)); TrimRetired(); } throw;
        }
    }
    private async Task RetireAsync(Generation? generation)
    {
        if (generation is null) return;
        try { await generation.Plugin.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (Ordinary(ex))
        {
            lock (gate)
            {
                paused = true; error = "Retired engine cleanup failed: " + Message(ex);
                var next = selection with { Paused = true };
                try { WriteSelection(next); selection = next; }
                catch (Exception persistence) when (Ordinary(persistence)) { error += "; pause persistence failed: " + Message(persistence); }
            }
        }
        generation.Context.Unload(); lock (gate) { retired.Add(new(generation.Context)); TrimRetired(); }
    }
    private void TrimRetired() => retired.RemoveAll(x => !x.IsAlive);
    private static void Bound(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined || Encoding.UTF8.GetByteCount(value.GetRawText()) > EngineProtocol.MaximumPayloadBytes)
            throw new EngineEvaluationException("Engine payload is missing or exceeds its 4 MiB boundary.");
    }
    private Selection ReadSelection()
    {
        EnginePackage.CheckPath(selectionPath);
        var values = EngineIni.Read(EnginePackage.Read(selectionPath, 32 * 1024), "selection");
        var keys = new[] { "schema", "epoch", "slot", "package", "configuration", "known-slot", "known-package", "known-configuration", "pending", "paused" };
        if (!values.Keys.Order().SequenceEqual(keys.Order()) || values["schema"] != "1" ||
            !long.TryParse(values["epoch"], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) || epoch < 0 ||
            !bool.TryParse(values["pending"], out var pending) || !bool.TryParse(values["paused"], out var isPaused))
            throw new InvalidDataException("Invalid engine selection record.");
        string? Text(string key) => values[key] == "" ? null : values[key];
        if (Text("slot") is not (null or "A" or "B") || Text("known-slot") is not (null or "A" or "B"))
            throw new InvalidDataException("Invalid recorded engine slot.");
        foreach (var key in new[] { "package", "configuration", "known-package", "known-configuration" })
            if (Text(key) is { } pin && (pin.Length != 64 || !pin.All(Uri.IsHexDigit))) throw new InvalidDataException("Invalid recorded engine digest.");
        if ((Text("slot") is null) != (Text("package") is null) || (Text("slot") is null) != (Text("configuration") is null) ||
            (Text("known-slot") is null) != (Text("known-package") is null) || (Text("known-slot") is null) != (Text("known-configuration") is null) ||
            (Text("slot") is not null && epoch < 1) || (pending && (Text("slot") is null || Text("known-slot") is null || Text("slot") == Text("known-slot"))))
            throw new InvalidDataException("Engine selection is missing its exact code/configuration pairing.");
        return new(epoch, Text("slot"), Text("package"), Text("configuration"), Text("known-slot"), Text("known-package"), Text("known-configuration"), pending, isPaused);
    }
    private void WriteSelection(Selection value) => EngineIni.AtomicWrite(selectionPath, EngineIni.Write("selection", new Dictionary<string, string>
    {
        ["schema"] = "1", ["epoch"] = value.Epoch.ToString(CultureInfo.InvariantCulture), ["slot"] = value.Slot ?? "",
        ["package"] = value.Package ?? "", ["configuration"] = value.Configuration ?? "", ["known-slot"] = value.KnownSlot ?? "",
        ["known-package"] = value.KnownPackage ?? "", ["known-configuration"] = value.KnownConfiguration ?? "",
        ["pending"] = value.Pending.ToString(), ["paused"] = value.Paused.ToString()
    }));
    private static bool Ordinary(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    private static string Message(Exception ex) => ex.Message.Length <= 2048 ? ex.Message : ex.Message[..2048];

    private sealed class FeatureBroker : IEngineFeatureHost
    {
        private readonly object gate = new();
        private sealed class Issued(EngineRequest request, Func<CancellationToken, Task<JsonElement>> feature)
        { public EngineRequest Request = request; public Func<CancellationToken, Task<JsonElement>> Feature = feature; public int Calls; }
        private readonly Dictionary<string, Issued> issued = new(StringComparer.Ordinal);
        public void Add(EngineRequest request, Func<CancellationToken, Task<JsonElement>> feature) { lock (gate) issued.Add(request.OperationId, new(request, feature)); }
        public void Remove(string id) { lock (gate) issued.Remove(id); }
        public async Task<JsonElement> EvaluateFeatureAsync(EngineRequest request, CancellationToken ct)
        {
            Func<CancellationToken, Task<JsonElement>> feature;
            lock (gate)
            {
                if (!issued.TryGetValue(request.OperationId, out var value) || value.Request.WorkId != request.WorkId ||
                    value.Request.ExpectedRevision != request.ExpectedRevision || value.Request.ActivationEpoch != request.ActivationEpoch ||
                    value.Request.ConfigurationFingerprint != request.ConfigurationFingerprint || value.Request.Contract != request.Contract ||
                    value.Request.Version != request.Version || value.Request.Payload.GetRawText() != request.Payload.GetRawText() || ++value.Calls > 4)
                    throw new UnauthorizedAccessException("Feature evaluation requires the exact active issued engine request.");
                feature = value.Feature;
            }
            try { var result = await feature(ct).ConfigureAwait(false); Bound(result); return result.Clone(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (Ordinary(ex)) { throw new EngineFeatureFailureException(Message(ex)); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await changes.WaitAsync().ConfigureAwait(false);
        try
        {
            Generation? previous;
            lock (gate)
            {
                if (disposed) return;
                if (evaluations != 0) throw new InvalidOperationException("The engine still owns active evaluations.");
                disposed = true; paused = true; previous = active; active = null;
            }
            await RetireAsync(previous).ConfigureAwait(false);
        }
        finally { changes.Release(); }
    }
}
