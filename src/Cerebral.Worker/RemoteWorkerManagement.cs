using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Contracts.Models;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Transport seam for deterministic owner-management tests. No peer route is used by production.</summary>
public interface IRemoteWorkerOwnerClient : IDisposable
{
    Task<T> GetAsync<T>(string operation, object? arguments = null, CancellationToken cancellationToken = default);
}

internal sealed record RemoteStoppedModelRecovery(string WorkerId, string WorkerInstallationId,
    string ModelId, string State);

public sealed class RemoteWorkerManagement(Configuration configuration,
    Func<WorkerRegistration, IRemoteWorkerOwnerClient>? clientFactory = null,
    Func<string, string, bool>? hasAssignments = null)
{
    internal ModelExecutionAccounting Accounting { get; } = ModelExecutionAccounting.For(configuration.State);
    private static readonly ConditionalWeakTable<StateStore, Dictionary<string, ModelExecutionAccounting.ModelOwnership>> RouteOwnership = new();
    private readonly Dictionary<string, ModelExecutionAccounting.ModelOwnership> routeOwnership = RouteOwnership.GetValue(configuration.State, static _ => new(StringComparer.Ordinal));

    internal object ReadPreparedOwnership(CancellationToken token = default) => configuration.State.WithModelAccountingState(() =>
    {
        try
        {
            var ledger = ReadPreparedLedgerValue(token);
            if (ledger is null) return (object)new { state = "unobserved", complete = false, unknown = (int?)null, reservations = Array.Empty<object>() };
            return new { state = "observed", complete = true,
                unknown = (int?)ledger.Entries.Count(entry => entry.Unknown || !preparedOwned.Contains(entry.Id)),
                reservations = ledger.Entries.Select(entry => (object)new { entry.Id, entry.Route.InstallationId,
                    entry.Route.ModelId, entry.Route.EndpointHash, entry.Route.BindingHash,
                    unknown = entry.Unknown || !preparedOwned.Contains(entry.Id) }).ToArray() };
        }
        catch (Exception error) when (error is JsonException or RemoteModelProtocolException or InvalidOperationException or
            OwnerSnapshotUnavailableException or Microsoft.Data.Sqlite.SqliteException or IOException or InvalidDataException or UnauthorizedAccessException)
        { return new { state = "unavailable", complete = false, unknown = (int?)null, reservations = Array.Empty<object>() }; }
    }, token);

    private const int MaximumAdmittedScanModels = 512;
    private const int MaximumReturnedScanModels = 256;
    private const int MaximumAdmittedScanIssues = 256;
    private const int MaximumReturnedScanIssues = 64;
    private const int MaximumRemotePathCharacters = 32_768;
    private const int MaximumRemoteNameCharacters = 1_024;
    private const int MaximumRemoteDetailCharacters = 4_096;
    private readonly Func<WorkerRegistration, IRemoteWorkerOwnerClient> factory = clientFactory ??
        (worker => new NetworkOwnerClient(worker));

    private const string PreparedRouteKind = "remote-prepared-routes";
    private const string PreparedRouteId = "quarantine";
    private const int MaximumPreparedReservations = 128;
    // All management/adapters sharing the live store share ownership; a new store/process inherits only durable uncertainty.
    private static readonly ConditionalWeakTable<StateStore, HashSet<string>> PreparedOwnership = new();
    private readonly HashSet<string> preparedOwned = PreparedOwnership.GetValue(configuration.State, static _ => new(StringComparer.Ordinal));
    private static readonly ConditionalWeakTable<StateStore, HashSet<string>> PreparedRecoveryOwnership = new();
    private readonly HashSet<string> preparedRecovering = PreparedRecoveryOwnership.GetValue(configuration.State, static _ => new(StringComparer.Ordinal));
    private sealed record PreparedRoute(string InstallationId, string EndpointHash, string ModelId, string BindingHash);
    private sealed record PreparedReservation(string Id, PreparedRoute Route, bool Unknown, DateTimeOffset StartedAt);
    private sealed record PreparedLedger(int Version, List<PreparedReservation> Entries);

    private PreparedLedger ReadPreparedLedger()
    {
        try { return ReadPreparedLedgerValue(CancellationToken.None) ?? new(1, []); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        { throw new RemoteModelProtocolException("Saved remote prepared ownership is invalid. No model work was dispatched; inspect retained ownership before recovery."); }
    }

    private PreparedLedger? ReadPreparedLedgerValue(CancellationToken token)
    {
        var raw = configuration.State.ReadRemotePreparedOwnershipBody(token);
        if (raw is null) return null;
        using var document = OwnerSnapshotJson.Parse(raw, 262144);
        var fields = OwnerSnapshotJson.Fields(document.RootElement, new HashSet<string>(["version", "entries"], StringComparer.Ordinal));
        if (fields.Count != 2 || !fields["version"].TryGetInt32(out var version) || version != 1 ||
            fields["entries"].ValueKind != JsonValueKind.Array || fields["entries"].GetArrayLength() > MaximumPreparedReservations)
            throw new InvalidDataException("remote_prepared_ledger_invalid");
        foreach (var entry in fields["entries"].EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var values = OwnerSnapshotJson.Fields(entry, new HashSet<string>(["id", "route", "unknown", "startedAt"], StringComparer.Ordinal));
            if (values.Count != 4 || values["unknown"].ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                values["startedAt"].ValueKind != JsonValueKind.String || !values["startedAt"].TryGetDateTimeOffset(out _))
                throw new InvalidDataException("remote_prepared_entry_invalid");
            if (OwnerSnapshotJson.Fields(values["route"], new HashSet<string>(["installationId", "endpointHash", "modelId", "bindingHash"], StringComparer.Ordinal)).Count != 4)
                throw new InvalidDataException("remote_prepared_route_invalid");
        }
        var ledger = document.RootElement.Deserialize<PreparedLedger>(CerebralJson.Options) ?? throw new InvalidDataException("remote_prepared_ledger_invalid");
        ValidatePreparedLedger(ledger);
        return ledger;
    }

    private static void ValidatePreparedLedger(PreparedLedger value)
    {
        if (value.Version != 1 || value.Entries is null || value.Entries.Count > MaximumPreparedReservations ||
            value.Entries.Select(entry => entry?.Id).Distinct(StringComparer.Ordinal).Count() != value.Entries.Count ||
            value.Entries.Any(entry => entry is null || entry.Route is null || entry.Id?.Length != 32 ||
                !entry.Id.All(char.IsAsciiHexDigit) || string.IsNullOrWhiteSpace(entry.Route.InstallationId) ||
                entry.Route.InstallationId.Length > 256 || entry.Route.InstallationId.Contains('\0') ||
                string.IsNullOrWhiteSpace(entry.Route.ModelId) || entry.Route.ModelId.Length > 128 ||
                entry.Route.ModelId.Contains('\0') || entry.Route.EndpointHash?.Length != 64 ||
                !entry.Route.EndpointHash.All(char.IsAsciiHexDigit) ||
                entry.Route.BindingHash != "*" && (entry.Route.BindingHash?.Length != 64 || !entry.Route.BindingHash.All(char.IsAsciiHexDigit))))
            throw new RemoteModelProtocolException("Remote prepared route ownership is invalid. Inspect its saved diagnostic and verify the remote model stopped before recovery.");
    }

    private static string PreparedHash<T>(T value) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options)));

    private static PreparedRoute Route(string endpoint, string installationId, RemotePreparedModelBinding model) =>
        new(installationId, PreparedHash(new Uri(endpoint, UriKind.Absolute).AbsoluteUri), model.Id, PreparedHash(model));

    private void RequirePreparedRoute(PreparedLedger ledger, PreparedRoute route)
    {
        if (preparedRecovering.Contains(RecoveryKey(route.InstallationId, route.ModelId)))
            throw new RemoteModelProtocolException("This remote model is being unloaded and independently verified. No new preparation was sent; wait for recovery to finish.");
        if (ledger.Entries.Any(entry => entry.Route.InstallationId == route.InstallationId && entry.Route.ModelId == route.ModelId &&
            (entry.Unknown || !preparedOwned.Contains(entry.Id))))
            throw new RemoteModelProtocolException("This remote model route has unconfirmed prepared work. No new preparation was sent. Explicitly unload it, verify the same worker reports it unloaded, then retry; restarting the coordinator does not prove remote work stopped.");
        if (ledger.Entries.Count >= MaximumPreparedReservations)
            throw new RemoteModelProtocolException("Remote prepared ownership tracking is full. No new preparation was sent. Finish active work or explicitly unload and verify affected remote models before retrying.");
    }

    internal void RequirePreparedRoute(string workerId, string installationId, RemotePreparedModelBinding model) =>
        configuration.State.Locked(() =>
        {
            var worker = configuration.Get().Workers.SingleOrDefault(item => item.Id == workerId) ??
                throw new ArgumentException("Worker not found.");
            RequirePreparedRoute(ReadPreparedLedger(), Route(worker.Url, installationId, model));
            return true;
        });

    internal string BeginPreparedRoute(RemoteManagedWorkerSession session, RemotePreparedModelBinding model,
        ModelExecutionAccounting.ModelOwnership? ownership = null)
    {
        ownership ??= Accounting.Begin(ModelOwnershipKind.OutboundPreparation, model.Id);
        try { return BeginPreparedRoute(session, Route(session.Registration.Url, session.Management.WorkerInstallationId, model), ownership); }
        catch { ownership.Complete(); throw; }
    }

    private string BeginPreparedRoute(RemoteManagedWorkerSession session, PreparedRoute route,
        ModelExecutionAccounting.ModelOwnership ownership) =>
        configuration.State.Locked(() =>
        {
            session.EnsureCurrent();
            var ledger = ReadPreparedLedger(); RequirePreparedRoute(ledger, route);
            var id = Guid.NewGuid().ToString("N");
            ledger.Entries.Add(new(id, route, false, DateTimeOffset.UtcNow));
            // A durable in-flight marker prevents a new process from mistaking its restart for provider completion.
            configuration.State.Put(PreparedRouteKind, PreparedRouteId, ledger);
            preparedOwned.Add(id);
            ownership.Bind(reservationId: id, workerInstallationId: route.InstallationId);
            routeOwnership.Add(id, ownership);
            return id;
        });

    internal void CompletePreparedRoute(string id) => configuration.State.Locked(() =>
    {
        preparedOwned.Remove(id);
        var ledger = ReadPreparedLedger();
        ledger.Entries.RemoveAll(entry => entry.Id == id);
        configuration.State.Put(PreparedRouteKind, PreparedRouteId, ledger);
        if (routeOwnership.Remove(id, out var ownership)) ownership.Complete();
        return true;
    });

    internal void QuarantinePreparedRoute(string id) => configuration.State.Locked(() =>
    {
        preparedOwned.Remove(id);
        var ledger = ReadPreparedLedger();
        if (routeOwnership.TryGetValue(id, out var ownership)) ownership.Unknown("remote-provider-release-unconfirmed");
        for (var index = 0; index < ledger.Entries.Count; index++)
            if (ledger.Entries[index].Id == id) ledger.Entries[index] = ledger.Entries[index] with { Unknown = true };
        configuration.State.Put(PreparedRouteKind, PreparedRouteId, ledger);
        return true;
    });

    private static string RecoveryKey(string installationId, string modelId) => PreparedHash(new { installationId, modelId });
    private sealed record PreparedRecovery(string InstallationId, string EndpointHash, string ModelId, string[] Reservations, string OwnershipKey);

    private PreparedRecovery BeginStoppedPreparedRecovery(RemoteManagedWorkerSession session, string modelId) =>
        configuration.State.Locked(() =>
        {
            session.EnsureCurrent();
            var installation = session.Management.WorkerInstallationId;
            var ownershipKey = RecoveryKey(installation, modelId);
            if (preparedRecovering.Contains(ownershipKey))
                throw new RemoteModelProtocolException("Recovery for this verified worker and model is already in progress. No additional unload was sent.");
            var endpoint = PreparedHash(new Uri(session.Registration.Url, UriKind.Absolute).AbsoluteUri);
            var ledger = ReadPreparedLedger();
            var matching = ledger.Entries.Where(entry => entry.Route.InstallationId == installation &&
                entry.Route.ModelId == modelId).ToArray();
            if (matching.Any(entry => preparedOwned.Contains(entry.Id)))
                throw new RemoteModelProtocolException("Finish or cancel live prepared model work before unloading this remote model; its provider ownership has not been released.");
            if (matching.Length == 0 && ledger.Entries.Count >= MaximumPreparedReservations)
                throw new RemoteModelProtocolException("Remote prepared ownership tracking is full; recover an affected model before unloading another route.");
            // Persist a model-scoped fence before unload. Reuse an uncertain entry at the bounded limit.
            var id = matching.FirstOrDefault()?.Id ?? Guid.NewGuid().ToString("N");
            ledger.Entries.RemoveAll(entry => entry.Id == id);
            ledger.Entries.Add(new(id, new(installation, endpoint, modelId, "*"), true, DateTimeOffset.UtcNow));
            configuration.State.Put(PreparedRouteKind, PreparedRouteId, ledger);
            preparedRecovering.Add(ownershipKey);
            return new PreparedRecovery(installation, endpoint, modelId,
                matching.Select(entry => entry.Id).Append(id).Distinct(StringComparer.Ordinal).ToArray(), ownershipKey);
        });

    private void FinishPreparedRecovery(PreparedRecovery recovery) => configuration.State.Locked(() =>
    {
        // Only live ownership is released here; failed recovery leaves its durable uncertainty fenced.
        preparedRecovering.Remove(recovery.OwnershipKey);
        return true;
    });

    private void ClearStoppedPreparedRoutes(RemoteManagedWorkerSession session, PreparedRecovery recovery) =>
        configuration.State.Locked(() =>
        {
            session.EnsureCurrent();
            if (session.Management.WorkerInstallationId != recovery.InstallationId ||
                PreparedHash(new Uri(session.Registration.Url, UriKind.Absolute).AbsoluteUri) != recovery.EndpointHash)
                throw new RemoteModelProtocolException("The verified remote recovery route changed; its prepared ownership remains unavailable.");
            var ledger = ReadPreparedLedger();
            // Clear exactly the fenced uncertainty captured before unload, never a later reservation.
            ledger.Entries.RemoveAll(entry => recovery.Reservations.Contains(entry.Id, StringComparer.Ordinal) &&
                entry.Route.InstallationId == recovery.InstallationId && entry.Route.ModelId == recovery.ModelId);
            configuration.State.Put(PreparedRouteKind, PreparedRouteId, ledger);
            foreach (var id in recovery.Reservations)
                if (routeOwnership.Remove(id, out var ownership)) ownership.Complete();
            return true;
        });

    public async Task<RemoteManagedWorkerSession> ConnectAsync(string workerId, CancellationToken ct)
    {
        Configuration.ValidateId(workerId);
        if (workerId == "local") throw new ArgumentException("Choose a registered remote worker.");
        var settings = configuration.Get();
        if (!settings.Coordinator) throw new InvalidOperationException("Only a coordinator manages assigned remote workers.");
        var worker = settings.Workers.SingleOrDefault(x => x.Id == workerId) ?? throw new ArgumentException("Worker not found.");
        PortableRegistrationPolicy.RequireReady(worker);
        if (worker.Url.Length > 4096 || !Uri.TryCreate(worker.Url, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Owner model management requires a bounded HTTP(S) worker endpoint without credentials, query or fragment.");
        if (string.IsNullOrWhiteSpace(worker.AccessKey) || worker.AccessKey.Length is < 24 or > 512 || worker.AccessKey.Any(char.IsWhiteSpace))
            throw new UnauthorizedAccessException("This worker requires its owner access key. A peer token does not grant model management.");
        var retained = CerebralJson.Read<WorkerRegistration>(CerebralJson.Element(worker));
        var session = new RemoteManagedWorkerSession(configuration, settings.InstallationId, retained, factory(retained));
        try { await session.RevalidateAsync(ct); return session; }
        catch { session.Dispose(); throw; }
    }

    public async Task<ModelRecord> GetModelAsync(string workerId, string id, CancellationToken ct)
    {
        Configuration.ValidateId(id);
        using var session = await ConnectAsync(workerId, ct);
        return await session.InspectAsync(id, ct);
    }

    internal async Task<RemoteStoppedModelRecovery> RecoverStoppedModelAsync(string workerId, string modelId,
        Func<string, RemoteModelRecoveryBarrier> captureBarrier,
        Action<RemoteModelRecoveryBarrier> validateBarrier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(captureBarrier);
        ArgumentNullException.ThrowIfNull(validateBarrier);
        Configuration.ValidateId(workerId); Configuration.ValidateId(modelId);
        cancellationToken.ThrowIfCancellationRequested();
        using var session = await ConnectAsync(workerId, cancellationToken).ConfigureAwait(false);
        var barrier = captureBarrier(session.Management.WorkerInstallationId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        var recovery = BeginStoppedPreparedRecovery(session, modelId);
        try
        {
            // One owner-authorized unload is the recovery effect. Every uncertain or failed exit keeps
            // the durable route fence and must be retried explicitly; this method never replays it.
            await session.UnloadAsync(modelId, token).ConfigureAwait(false);
            await session.RevalidateAsync(token).ConfigureAwait(false);
            var stopped = await session.InspectAsync(modelId, token).ConfigureAwait(false);
            if (stopped.State != "unloaded")
                throw new RemoteModelProtocolException("Remote recovery did not independently prove the same model stopped; its prepared route remains unavailable.");
            token.ThrowIfCancellationRequested();
            validateBarrier(barrier);
            // Revalidation can inspect a bounded set of retained workflows. Cancellation during that
            // read must keep the durable provider fence instead of authorizing a completed recovery.
            token.ThrowIfCancellationRequested();
            ClearStoppedPreparedRoutes(session, recovery);
            return new(workerId, recovery.InstallationId, modelId, stopped.State);
        }
        finally { FinishPreparedRecovery(recovery); }
    }

    /// <summary>One fixed owner inference operation; worker identity is verified before dispatch and before adopting its result.</summary>
    internal async Task<JsonElement> GenerateAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireFields(args, ["workerId", "modelId", "messages", "maxTokens", "temperature", "json", "responseSchema", "reasoningMode", "reasoningTokens"]);
        if (args.GetRawText().Length > 1_048_576) throw new ArgumentException("Remote inference input exceeds its bounded envelope.");
        var workerId = CommandDispatcher.Text(args, "workerId");
        var modelId = CommandDispatcher.Text(args, "modelId");
        Configuration.ValidateId(workerId); Configuration.ValidateId(modelId);
        var input = args.GetProperty("messages");
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() is < 1 or > 10_000)
            throw new ArgumentException("Remote inference requires 1–10000 text messages.");
        foreach (var message in input.EnumerateArray())
        {
            RequireFields(message, ["role", "content"]);
            if (CommandDispatcher.Text(message, "role") is not ("system" or "user" or "assistant") ||
                message.GetProperty("content").ValueKind != JsonValueKind.String || message.GetProperty("content").GetString()!.Contains('\0'))
                throw new ArgumentException("Remote inference accepts only system, user and assistant text messages.");
        }
        var messages = CerebralJson.Read<List<ModelMessage>>(input);
        var maxTokens = CommandDispatcher.Number(args, "maxTokens", 1024, 1, AgentTokenLimits.MaximumResponseTokens);
        var temperatureValue = args.GetProperty("temperature");
        if (temperatureValue.ValueKind != JsonValueKind.Number || !temperatureValue.TryGetDouble(out var temperature) ||
            !double.IsFinite(temperature) || temperature is < 0 or > 2)
            throw new ArgumentException("Remote inference temperature must be a finite number from 0 to 2.");
        var jsonValue = args.GetProperty("json");
        if (jsonValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("Remote inference json must be a boolean.");
        var json = jsonValue.GetBoolean();
        var schema = args.GetProperty("responseSchema");
        if (schema.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object) || schema.GetRawText().Length > 65_536)
            throw new ArgumentException("Remote response schema must be a bounded object or null.");
        JsonElement? responseSchema = schema.ValueKind == JsonValueKind.Null ? null : schema.Clone();
        var reasoningMode = CommandDispatcher.Text(args, "reasoningMode");
        if (reasoningMode is not ("off" or "on")) throw new ArgumentException("Reasoning mode must be off or on.");
        var reasoningTokens = CommandDispatcher.Number(args, "reasoningTokens", 1024,
            AgentTokenLimits.MinimumReasoningTokens, AgentTokenLimits.MaximumReasoningTokens);

        // Resolve only the admitted registration; neither arbitrary operations nor caller-supplied endpoints are forwarded.
        using var ownership = Accounting.Begin(ModelOwnershipKind.OutboundGeneration, modelId);
        RemoteManagedWorkerSession session;
        try { session = await ConnectAsync(workerId, ct); }
        catch { ownership.Complete(); throw; }
        using var sessionLifetime = session;
        try { SkillDispatchFence.RequireCurrent(); }
        catch { ownership.Complete(); throw; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        // This legacy response has no cancellation evidence. Reserve ownership across dispatch and
        // clear only its verified successful text response; every interrupted call remains uncertain.
        var reservation = BeginPreparedRoute(session, new PreparedRoute(session.Management.WorkerInstallationId,
            PreparedHash(new Uri(session.Registration.Url, UriKind.Absolute).AbsoluteUri), modelId,
            PreparedHash("legacy-owner-inference-v1")), ownership);
        ownership.Phase(ModelOwnershipPhase.Generating, effectStarted: true);
        try
        {
            var result = await session.Client.GetAsync<JsonElement>("inference.generate", new
                { modelId, messages, maxTokens, temperature, json, responseSchema, reasoningMode, reasoningTokens }, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            await session.RevalidateAsync(timeout.Token);
            RequireFields(result, ["text"]);
            if (result.GetProperty("text").ValueKind != JsonValueKind.String || result.GetProperty("text").GetString() is not { } text ||
                text.Length > 1_048_576 || text.Contains('\0'))
                throw new InvalidOperationException("The verified worker returned invalid or oversized generated text.");
            CompletePreparedRoute(reservation);
            return result;
        }
        catch
        {
            QuarantinePreparedRoute(reservation);
            throw;
        }
    }

    internal async Task<int> CountAsync(string workerId, string modelId, IReadOnlyList<ModelMessage> messages,
        string reasoningMode, CancellationToken token)
    {
        using var ownership = Accounting.Begin(ModelOwnershipKind.OutboundMeasurement, modelId);
        RemoteManagedWorkerSession session;
        try { session = await ConnectAsync(workerId, token).ConfigureAwait(false); }
        catch { ownership.Complete(); throw; }
        using var lifetime = session;
        return await RunModelOperationAsync<int>(session, modelId, "inference.count", new { modelId, messages, reasoningMode },
            ModelOwnershipKind.OutboundMeasurement, value => value >= 0, token, ownership).ConfigureAwait(false);
    }

    internal Task<int> CountAsync(RemoteManagedWorkerSession session, string modelId,
        IReadOnlyList<ModelMessage> messages, string reasoningMode, CancellationToken token) =>
        RunModelOperationAsync<int>(session, modelId, "inference.count", new { modelId, messages, reasoningMode },
            ModelOwnershipKind.OutboundMeasurement, value => value >= 0, token);

    internal Task<ModelImageVerification> VerifyImageAsync(RemoteManagedWorkerSession session, string modelId, CancellationToken token) =>
        RunModelOperationAsync<ModelImageVerification>(session, modelId, "models.verify-image", new { id = modelId },
            ModelOwnershipKind.OutboundGeneration, value => value is not null && value.ModelId == modelId &&
                !string.IsNullOrWhiteSpace(value.InstanceId) && value.InstanceId.Length <= 256 &&
                value.InputTokens >= 0 && value.OutputTokens is null or >= 0, token);

    private async Task<T> RunModelOperationAsync<T>(RemoteManagedWorkerSession session, string modelId, string operation,
        object arguments, ModelOwnershipKind kind, Func<T, bool> valid, CancellationToken token,
        ModelExecutionAccounting.ModelOwnership? existing = null)
    {
        using var ownership = existing ?? Accounting.Begin(kind, modelId);
        session.EnsureCurrent();
        var reservation = BeginPreparedRoute(session, new PreparedRoute(session.Management.WorkerInstallationId,
            PreparedHash(new Uri(session.Registration.Url, UriKind.Absolute).AbsoluteUri), modelId,
            PreparedHash("legacy-owner-" + operation + "-v1")), ownership);
        var sent = false;
        try
        {
            token.ThrowIfCancellationRequested();
            sent = true; ownership.Phase(ModelOwnershipPhase.Generating, effectStarted: true);
            var result = await session.Client.GetAsync<T>(operation, arguments, token).ConfigureAwait(false);
            session.EnsureCurrent();
            if (!valid(result)) throw new RemoteModelProtocolException("The remote model operation returned invalid or mismatched terminal evidence.");
            CompletePreparedRoute(reservation); return result;
        }
        catch
        {
            if (sent) QuarantinePreparedRoute(reservation);
            else CompletePreparedRoute(reservation);
            throw;
        }
    }

    private async Task<JsonElement> ReadRuntimeSupportAsync(JsonElement args, CancellationToken ct)
    {
        var input = ModelRuntimeSupportQueryProtocol.ReadRemoteRequest(args);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ModelRuntimeSupportQueryProtocol.Deadline);
        var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        using var session = await ConnectAsync(input.WorkerId, token).ConfigureAwait(false);
        if (session.Management.WorkerInstallationId != input.Request.ExpectedWorkerInstallationId)
        {
            var unavailable = ModelRuntimeSupportQueryProtocol.WriteResponse(
                ModelRuntimeSupportQueryProtocol.Unavailable(input.Request, "identity_changed"), input.Request);
            token.ThrowIfCancellationRequested();
            return unavailable;
        }
        session.EnsureCurrent();
        // Independent target discovery is essential: advertising the coordinator proxy does
        // not establish target support. This uses only the existing verified owner transport.
        var query = new ModelRuntimeSupportClient((operation, body, cancellation) =>
            session.Client.GetAsync<JsonElement>(operation, body, cancellation));
        var response = await query.ReadAsync(input.Request, token).ConfigureAwait(false);
        // Rejoin health, assignment and saved owner route after all target reads. A changed
        // session discards the response; never retarget, retry, or substitute coordinator IDs.
        await session.RevalidateAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var encoded = ModelRuntimeSupportQueryProtocol.WriteResponse(response, input.Request);
        token.ThrowIfCancellationRequested();
        return encoded;
    }

    internal async Task<object> ExecuteAsync(string operation, JsonElement args, CancellationToken ct)
    {
        if (operation == ModelRuntimeSupportQueryProtocol.RemoteReadOperation)
            return await ReadRuntimeSupportAsync(args, ct).ConfigureAwait(false);
        string[] permitted = operation switch
        {
            "workers.models.list" or "workers.models.diagnostics" or "workers.models.scan" => new[] { "workerId" },
            "workers.models.import" or "workers.models.download" => ["workerId", "request"],
            "workers.models.load" or "workers.models.unload" or "workers.models.inspect" => ["workerId", "id"],
            "workers.models.save" => ["workerId", "model"],
            "workers.models.capacity.preview" or "workers.models.capacity.apply" => CapacityRemoteFields(args, operation.EndsWith(".apply", StringComparison.Ordinal)),
            "workers.models.configure" => ["workerId", "id", "visionProjectorPath"],
            "workers.models.idle.set" => ["workerId", "id", "idleMinutes"],
            _ => throw new ArgumentException("Unknown remote model management operation.")
        };
        RequireFields(args, permitted);
        var workerId = CommandDispatcher.Text(args, "workerId");
        var changingModelId = operation == "workers.models.capacity.apply" ? CommandDispatcher.Text(args, "modelId") :
            operation == "workers.models.save" ? CommandDispatcher.Text(args.GetProperty("model"), "id") :
            operation is "workers.models.unload" or "workers.models.configure" ? CommandDispatcher.Text(args, "id") : null;
        if (changingModelId is not null && hasAssignments?.Invoke(workerId, changingModelId) == true)
        {
            if (operation == "workers.models.capacity.apply") return CapacityCoordinatorBusy(changingModelId);
            throw new InvalidOperationException("Finish or cancel this coordinator's accepted model assignments before changing or unloading the remote model.");
        }
        using var session = await ConnectAsync(workerId, ct);
        if (changingModelId is not null && hasAssignments?.Invoke(workerId, changingModelId) == true)
        {
            if (operation == "workers.models.capacity.apply") return CapacityCoordinatorBusy(changingModelId);
            throw new InvalidOperationException("Work was accepted while connecting. The remote model change was not dispatched.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(operation is "workers.models.import" or "workers.models.download" ? TimeSpan.FromHours(8) :
            TimeSpan.FromMinutes(operation == "workers.models.load" ? 20 : 3));
        var token = timeout.Token;
        switch (operation)
        {
            case "workers.models.list":
                var models = await session.Client.GetAsync<List<ModelRecord>>("models.list", cancellationToken: token);
                if (models.Count > 256) throw new InvalidOperationException("Remote model catalog exceeds the 256-model management limit.");
                return models;
            case "workers.models.scan":
                var scan = await session.Client.GetAsync<ModelRepositoryScan>("models.scan", cancellationToken: token);
                return BoundRemoteScan(scan);
            case "workers.models.import":
            case "workers.models.download":
                var request = args.GetProperty("request");
                var fields = new[] { "path", "copy", "url", "fileName", "id", "name", "sourceUrl", "license", "publisher", "upstreamModelId", "upstreamModelUrl", "licenseUrl", "upstreamRevision", "description", "tags", "packageId", "packageVersion", "packagePurpose", "starterPackage", "automaticContextSizing", "maximumContextTokens", "contextTokens", "parallelRequests", "gpuLayers", "idleMinutes", "chatTemplate", "stopSequences" };
                if (request.ValueKind != JsonValueKind.Object || request.GetRawText().Length > 65536 ||
                    request.EnumerateObject().Any(x => !fields.Contains(x.Name, StringComparer.Ordinal)) ||
                    request.EnumerateObject().GroupBy(x => x.Name, StringComparer.Ordinal).Any(x => x.Count() != 1))
                    throw new ArgumentException("Use a bounded model acquisition request with existing import/download fields.");
                return await session.Client.GetAsync<ModelRecord>(operation == "workers.models.import" ? "models.import" : "models.download", request, token);
            case "workers.models.inspect": return await session.InspectAsync(CommandDispatcher.Text(args, "id"), token);
            case "workers.models.load": return await session.LoadAsync(CommandDispatcher.Text(args, "id"), token);
            case "workers.models.unload":
                var unloadedId = CommandDispatcher.Text(args, "id");
                var recovery = BeginStoppedPreparedRecovery(session, unloadedId);
                try
                {
                    await session.UnloadAsync(unloadedId, token);
                    await session.RevalidateAsync(token);
                    var stopped = await session.InspectAsync(unloadedId, token);
                    if (stopped.State != "unloaded")
                        throw new RemoteModelProtocolException("Remote unload did not independently prove the same model stopped; its prepared route remains unavailable.");
                    ClearStoppedPreparedRoutes(session, recovery);
                    return new { unloaded = unloadedId };
                }
                finally { FinishPreparedRecovery(recovery); }
            case "workers.models.capacity.preview":
            {
                var capacityRequest = CapacityRemoteRequest(args, false);
                var capacityPreview = await session.Client.GetAsync<ModelCapacityPreview>(
                    ModelCapacityProtocol.PreviewOperation, capacityRequest, token);
                return ValidateRemoteCapacityPreview(capacityPreview, CommandDispatcher.Text(capacityRequest, "modelId"));
            }
            case "workers.models.capacity.apply":
            {
                var capacityRequest = CapacityRemoteRequest(args, true);
                // One dispatch. An exception/invalid acknowledgement stays unconfirmed; no retry,
                // reload, recovery or conversion to a no-mutation result occurs here.
                var capacityResult = await session.Client.GetAsync<JsonElement>(
                    ModelCapacityProtocol.ApplyOperation, capacityRequest, token);
                return ReadRemoteCapacityApply(capacityResult, CommandDispatcher.Text(capacityRequest, "modelId"));
            }
            case "workers.models.save":
                var model = CerebralJson.Read<ModelRecord>(args.GetProperty("model"));
                Configuration.ValidateId(model.Id);
                return await session.Client.GetAsync<ModelRecord>("models.save", model, token);
            case "workers.models.configure":
                var id = CommandDispatcher.Text(args, "id"); Configuration.ValidateId(id);
                var path = args.GetProperty("visionProjectorPath");
                if (path.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new ArgumentException("visionProjectorPath must be a remote path string or null.");
                // Paths belong to the remote OS. Do not normalize or open them on this coordinator.
                return await session.Client.GetAsync<ModelRecord>("models.configure", new { id, visionProjectorPath = path }, token);
            case "workers.models.idle.set":
                var idleId = CommandDispatcher.Text(args, "id"); Configuration.ValidateId(idleId);
                var minutes = CommandDispatcher.Number(args, "idleMinutes", -1, -1, 10080);
                return await session.Client.GetAsync<ModelRecord>("models.idle.set", new { id = idleId, idleMinutes = minutes }, token);
            case "workers.models.diagnostics":
                var diagnostics = await session.Client.GetAsync<WorkerModelDiagnostics>("models.runtime.status", cancellationToken: token);
                // A registration can be removed or repointed while the remote diagnostic read is in flight.
                session.EnsureCurrent();
                if (diagnostics.WorkerInstallationId != session.Management.WorkerInstallationId || diagnostics.Models.Count > 256)
                    throw new InvalidOperationException("Remote diagnostics do not match the verified worker identity or bounded catalog.");
                return diagnostics;
            default: throw new ArgumentException("Unknown remote model operation.");
        }
    }

    private static string[] CapacityRemoteFields(JsonElement args, bool apply)
    {
        _ = CapacityRemoteRequest(args, apply); // Validate the complete flat envelope before connecting.
        return args.EnumerateObject().Select(property => property.Name).ToArray();
    }

    internal static JsonElement CapacityRemoteRequest(JsonElement args, bool apply)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            System.Text.Encoding.UTF8.GetByteCount(args.GetRawText()) > 16384)
            throw new ArgumentException("Remote capacity requests require a bounded object.");
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var foundWorker = false;
        foreach (var property in args.EnumerateObject())
        {
            if (property.Name == "workerId")
            {
                if (foundWorker || property.Value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Remote capacity requests require one workerId.");
                Configuration.ValidateId(property.Value.GetString()!);
                foundWorker = true;
            }
            else if (!values.TryAdd(property.Name, property.Value.Clone()))
                throw new ArgumentException("Remote capacity request contains duplicate fields.");
        }
        if (!foundWorker) throw new ArgumentException("Remote capacity request requires workerId.");
        var request = CerebralJson.Element(values);
        CommandDispatcher.ValidateCapacityTransportRequest(request, apply);
        return request;
    }

    private static ModelCapacityApplyResult CapacityCoordinatorBusy(string modelId) =>
        new(ModelCapacityProtocol.Version, modelId, ModelCapacityProtocol.Busy, false,
            "capacity_coordinator_has_accepted_work", null, [], []);

    internal static ModelCapacityPreview ValidateRemoteCapacityPreview(ModelCapacityPreview? value, string modelId)
    {
        if (value is null || value.Version != ModelCapacityProtocol.Version || value.ModelId != modelId ||
            value.Saved is null || value.Choice is null || value.Requested is null || value.Planned is null ||
            value.Evidence is null || value.SupportedBounds is null || value.Scheduler is null ||
            value.ApplyEligibility is null || value.FieldChanges is null || value.AffectedProfiles is null ||
            value.Candidates is null || value.Candidates.Count > 16 ||
            value.AffectedProfiles.Count > ModelCapacityProtocol.MaximumAffectedProfiles ||
            value.CandidatesComplete && value.Candidates.Count != 16 ||
            value.ApplyEligibility.State is not ("ready" or "busy" or "unavailable") ||
            value.ApplyEligibility.State == ModelCapacityProtocol.Ready &&
                (!value.AffectedProfilesComplete || !ValidCapacityToken(value.ExpectedCurrent)) ||
            value.ExpectedCurrent is not null && !ValidCapacityToken(value.ExpectedCurrent) ||
            value.Evidence.Kind != ModelCapacityProtocol.Estimate ||
            value.Evidence.Scope != ModelCapacityProtocol.EstimateScope ||
            value.Evidence.Applicability != ModelCapacityProtocol.CurrentConfiguration ||
            value.Scheduler.ChangesScheduler || value.Scheduler.MaxConcurrentJobs is < 1 or > 16 ||
            value.SupportedBounds.MinimumSlots != 1 || value.SupportedBounds.MaximumSlots != 16 ||
            value.SupportedBounds.MinimumContextTokens != 512 ||
            value.SupportedBounds.MaximumContextTokens is < 512 or > 1048576 ||
            value.SupportedBounds.BoundKind != ModelCapacityProtocol.CompatibilityBounds ||
            value.SupportedBounds.MeasuredHardwareMaximumSlots is not null)
            throw new RemoteModelProtocolException("Remote capacity preview returned invalid or mismatched version1 data.");
        ModelCapacityChoices.ValidateChoice(new ModelRecord { NativeContextTokens = value.SupportedBounds.MaximumContextTokens }, value.Choice);
        RequireCapacityPlan(value.Planned);
        RequireCapacityEffects(value.AffectedProfiles, modelId);
        var lastSlots = 0; var lastContext = int.MaxValue;
        foreach (var candidate in value.Candidates)
        {
            if (candidate is null || candidate.Choice is null || candidate.Requested is null ||
                candidate.FieldChanges is null || candidate.Planned is null ||
                candidate.Choice.ContextMode != "fixed" ||
                candidate.Planned.SelectionSource != ModelCapacityProtocol.MemoryEstimate ||
                candidate.Choice.ParallelRequests <= lastSlots || candidate.Planned.ContextTokens > lastContext)
                throw new RemoteModelProtocolException("Remote capacity alternatives are invalid or unordered.");
            ModelCapacityChoices.ValidateChoice(new ModelRecord { NativeContextTokens = value.SupportedBounds.MaximumContextTokens }, candidate.Choice);
            RequireCapacityPlan(candidate.Planned);
            if (candidate.Choice.FixedContextTokens != candidate.Planned.ContextTokens ||
                candidate.Choice.ParallelRequests != candidate.Planned.ParallelRequests)
                throw new RemoteModelProtocolException("Remote capacity alternative does not match its explicit choice.");
            lastSlots = candidate.Choice.ParallelRequests; lastContext = candidate.Planned.ContextTokens;
        }
        if (value.Effective is { } effective && (effective.State != "present" ||
            string.IsNullOrWhiteSpace(effective.InstanceId) ||
            effective.BindingState is not ("captured" or "unavailable") ||
            effective.ContextState is not ("measured" or "unobserved") ||
            effective.ContextSource is not ("startup_log" or "launch_arguments")))
            throw new RemoteModelProtocolException("Remote effective capacity observation is invalid.");
        RequireCapacityResponseBytes(value);
        return value;
    }

    // Preserve raw member evidence until the complete ACK is checked. Deserializing a missing
    // bool directly would turn it into false and could invent a confirmed no-mutation outcome.
    internal static ModelCapacityApplyResult ReadRemoteCapacityApply(JsonElement raw, string modelId)
    {
        if (raw.ValueKind != JsonValueKind.Object || System.Text.Encoding.UTF8.GetByteCount(raw.GetRawText()) > 1048576)
            throw new RemoteModelProtocolException("Remote capacity acknowledgement exceeds its bounded contract.");
        var fields = CapacityAckFields(raw,
            ["version", "modelId", "state", "mutationCommitted", "reason", "saved", "affectedProfiles", "warnings"]);
        if (!CapacityAckInteger(fields["version"]) || fields["version"].GetInt32() != ModelCapacityProtocol.Version ||
            !CapacityAckString(fields["modelId"]) || fields["modelId"].GetString() != modelId ||
            !CapacityAckString(fields["state"]) || !CapacityAckString(fields["reason"]) ||
            fields["mutationCommitted"].ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            fields["affectedProfiles"].ValueKind != JsonValueKind.Array ||
            fields["affectedProfiles"].GetArrayLength() > ModelCapacityProtocol.MaximumAffectedProfiles ||
            fields["warnings"].ValueKind != JsonValueKind.Array || fields["warnings"].GetArrayLength() > 2)
            throw new RemoteModelProtocolException("Remote capacity acknowledgement members are missing or mistyped; read back before another action.");
        var state = fields["state"].GetString();
        var committed = fields["mutationCommitted"].GetBoolean();
        if (state is not ("applied" or "stale" or "busy" or "invalid" or "unavailable") ||
            committed != (state == ModelCapacityProtocol.Applied) ||
            !committed && (fields["saved"].ValueKind != JsonValueKind.Null ||
                fields["affectedProfiles"].GetArrayLength() != 0 || fields["warnings"].GetArrayLength() != 0))
            throw new RemoteModelProtocolException("Remote capacity acknowledgement contradicts its outcome; read back before another action.");
        if (committed)
        {
            var saved = CapacityAckFields(fields["saved"],
                ["contextMode", "contextTokens", "maximumContextTokens", "parallelRequests", "gpuLayers", "visionProjectorConfigured"]);
            if (!CapacityAckString(saved["contextMode"]) ||
                saved["contextMode"].GetString() is not ("fixed" or "automatic") ||
                !CapacityAckInteger(saved["contextTokens"]) || saved["contextTokens"].GetInt32() is < 512 or > 1048576 ||
                !CapacityAckInteger(saved["maximumContextTokens"]) || saved["maximumContextTokens"].GetInt32() is < 0 or > 1048576 ||
                !CapacityAckInteger(saved["parallelRequests"]) || saved["parallelRequests"].GetInt32() is < 1 or > 16 ||
                !CapacityAckInteger(saved["gpuLayers"]) || saved["gpuLayers"].GetInt32() < -1 ||
                saved["visionProjectorConfigured"].ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                saved["maximumContextTokens"].GetInt32() is > 0 and < 512 ||
                saved["contextMode"].GetString() == "fixed" &&
                    saved["maximumContextTokens"].GetInt32() != saved["contextTokens"].GetInt32() ||
                saved["contextMode"].GetString() == "automatic" && saved["maximumContextTokens"].GetInt32() > 0 &&
                    saved["contextTokens"].GetInt32() > saved["maximumContextTokens"].GetInt32())
                throw new RemoteModelProtocolException("Remote committed capacity settings are missing or mistyped.");
            foreach (var effect in fields["affectedProfiles"].EnumerateArray())
            {
                var values = CapacityAckFields(effect,
                    ["profileId", "name", "modelWorkerId", "modelId", "followsModelBudgets", "before", "after"]);
                if (!CapacityAckString(values["profileId"]) || !CapacityAckString(values["name"]) ||
                    !CapacityAckString(values["modelWorkerId"]) || !CapacityAckString(values["modelId"]) ||
                    values["followsModelBudgets"].ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new RemoteModelProtocolException("Remote committed profile effects are missing or mistyped.");
                CapacityAckBudget(values["before"]); CapacityAckBudget(values["after"]);
            }
            foreach (var warning in fields["warnings"].EnumerateArray())
                if (!CapacityAckString(warning))
                    throw new RemoteModelProtocolException("Remote committed notification warnings must be strings.");
        }
        return ValidateRemoteCapacityApply(CerebralJson.Read<ModelCapacityApplyResult>(raw), modelId);
    }

    private static Dictionary<string, JsonElement> CapacityAckFields(JsonElement value, string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new RemoteModelProtocolException("Remote capacity acknowledgement requires complete objects.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value))
                throw new RemoteModelProtocolException("Remote capacity acknowledgement has unknown or duplicate members.");
        if (fields.Count != names.Length)
            throw new RemoteModelProtocolException("Remote capacity acknowledgement is missing required members.");
        return fields;
    }

    private static bool CapacityAckInteger(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _);
    private static bool CapacityAckString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());
    private static void CapacityAckBudget(JsonElement value)
    {
        var fields = CapacityAckFields(value,
            ["revision", "contextTokens", "responseTokens", "reasoningMode", "reasoningTokens"]);
        if (!CapacityAckInteger(fields["revision"]) || !CapacityAckInteger(fields["contextTokens"]) ||
            !CapacityAckInteger(fields["responseTokens"]) || !CapacityAckString(fields["reasoningMode"]) ||
            !CapacityAckInteger(fields["reasoningTokens"]))
            throw new RemoteModelProtocolException("Remote committed profile budgets are missing or mistyped.");
    }

    internal static ModelCapacityApplyResult ValidateRemoteCapacityApply(ModelCapacityApplyResult? value, string modelId)
    {
        if (value is null || value.Version != ModelCapacityProtocol.Version || value.ModelId != modelId ||
            value.State is not ("applied" or "stale" or "busy" or "invalid" or "unavailable") ||
            value.MutationCommitted != (value.State == ModelCapacityProtocol.Applied) ||
            value.MutationCommitted && value.Saved is null || value.AffectedProfiles is null ||
            value.Warnings is null || value.Warnings.Count > 2 || string.IsNullOrWhiteSpace(value.Reason) ||
            !value.MutationCommitted && (value.Saved is not null || value.AffectedProfiles.Count != 0 || value.Warnings.Count != 0))
            throw new RemoteModelProtocolException("Remote capacity acknowledgement is invalid; read back before another action.");
        RequireCapacityEffects(value.AffectedProfiles, modelId);
        RequireCapacityResponseBytes(value);
        return value;
    }

    private static bool ValidCapacityToken(string? value) => value is { Length: 68 } &&
        value.StartsWith("mc1.", StringComparison.Ordinal) && !value.AsSpan(4).ContainsAnyExcept("0123456789abcdef");

    private static void RequireCapacityPlan(ModelCapacityPreviewPlan plan)
    {
        if (plan.ContextTokens is < 512 or > 1048576 || plan.ParallelRequests is < 1 or > 16 ||
            plan.TotalContextTokens != (long)plan.ContextTokens * plan.ParallelRequests ||
            plan.GuaranteedNextLoad || plan.FitState != ModelCapacityProtocol.UnknownFit ||
            plan.EstimatedPlacementMb is not null || plan.EstimatedRemainingMb is not null ||
            plan.SelectionSource is not ("unreported" or "fixed_request" or "retained_fallback" or "memory_estimate") ||
            plan.SelectionSource == ModelCapacityProtocol.RetainedFallback && plan.MeetsRequiredContextTokens is not null ||
            plan.Limitations is null || plan.Limitations.Count > 32)
            throw new RemoteModelProtocolException("Remote capacity estimate is invalid or claims unsupported fit.");
    }

    private static void RequireCapacityEffects(IReadOnlyList<ModelCapacityProfileEffect> effects, string modelId)
    {
        if (effects.Count > ModelCapacityProtocol.MaximumAffectedProfiles ||
            effects.Any(effect => effect is null || effect.Before is null || effect.After is null ||
                effect.ModelId != modelId || effect.ModelWorkerId != "local" ||
                (long)effect.After.Revision != (long)effect.Before.Revision + 1) ||
            effects.Select(effect => effect.ProfileId).Distinct(StringComparer.Ordinal).Count() != effects.Count)
            throw new RemoteModelProtocolException("Remote capacity profile effects are invalid or incomplete.");
    }

    private static void RequireCapacityResponseBytes<T>(T value)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options).Length > 1048576)
            throw new RemoteModelProtocolException("Remote capacity response exceeds the bounded version1 contract.");
    }

    internal static void RequireFields(JsonElement args, IReadOnlyCollection<string> fields)
    {
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Count() != fields.Count ||
            args.EnumerateObject().Any(x => !fields.Contains(x.Name, StringComparer.Ordinal)) ||
            args.EnumerateObject().GroupBy(x => x.Name, StringComparer.Ordinal).Any(x => x.Count() != 1))
            throw new ArgumentException("Remote model management accepts only: " + string.Join(", ", fields) + ".");
    }

    private static ModelRepositoryScan BoundRemoteScan(ModelRepositoryScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        RequireRemoteText(scan.Directory, MaximumRemotePathCharacters, "scan directory", required: true);
        if (scan.Models is null || scan.Models.Count > MaximumAdmittedScanModels)
            throw new InvalidOperationException($"Remote scan exceeds the {MaximumAdmittedScanModels}-model admission limit.");
        if (scan.Issues is null || scan.Issues.Count > MaximumAdmittedScanIssues)
            throw new InvalidOperationException($"Remote scan exceeds the {MaximumAdmittedScanIssues}-issue admission limit.");
        foreach (var model in scan.Models)
        {
            if (model is null) throw new InvalidOperationException("Remote scan contains an empty model entry.");
            RequireRemoteText(model.Path, MaximumRemotePathCharacters, "model path", required: true);
            RequireRemoteText(model.Name, MaximumRemoteNameCharacters, "model name", required: true);
            if (model.SizeBytes < 0) throw new InvalidOperationException("Remote scan contains a negative model size.");
            if (model.RegisteredId is not null) Configuration.ValidateId(model.RegisteredId);
            RequireRemoteText(model.Error, MaximumRemoteDetailCharacters, "model error", required: false);
            RequireRemoteText(model.Architecture, MaximumRemoteNameCharacters, "model architecture", required: false);
            RequireRemoteText(model.DeclaredType, MaximumRemoteNameCharacters, "declared model type", required: false);
            RequireRemoteText(model.Quantization, MaximumRemoteNameCharacters, "model quantization", required: false);
            RequireRemoteText(model.Purpose, MaximumRemoteNameCharacters, "model purpose", required: false);
            RequireRemoteText(model.MetadataWarning, MaximumRemoteDetailCharacters, "model metadata warning", required: false);
        }
        foreach (var issue in scan.Issues)
            RequireRemoteText(issue, MaximumRemoteDetailCharacters, "scan issue", required: true);

        var projectedModels = scan.Models.Take(MaximumReturnedScanModels).ToList();
        var projectedIssues = scan.Issues.Count <= MaximumReturnedScanIssues
            ? scan.Issues.ToList()
            : scan.Issues.Take(MaximumReturnedScanIssues - 1)
                .Append("Additional remote scan issues were omitted by the coordinator.").ToList();
        return new ModelRepositoryScan
        {
            Directory = scan.Directory,
            Models = projectedModels,
            Issues = projectedIssues,
            Truncated = scan.Truncated || projectedModels.Count != scan.Models.Count || projectedIssues.Count != scan.Issues.Count
        };
    }

    private static void RequireRemoteText(string? value, int maximumCharacters, string label, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value) ||
            value is not null && (value.Length > maximumCharacters || value.Contains('\0')))
            throw new InvalidOperationException($"Remote {label} is missing or exceeds its bounded format.");
    }

    private sealed class NetworkOwnerClient : IRemoteWorkerOwnerClient, IRemotePreparedStreamingOwnerClient
    {
        private readonly CerebralClient client;
        private readonly RemotePreparedStreamingOwnerClient streaming;
        internal NetworkOwnerClient(WorkerRegistration worker)
        {
            client = CerebralClient.ForOwner(worker);
            try { streaming = new(worker); }
            catch { client.Dispose(); throw; }
        }
        public Task<T> GetAsync<T>(string operation, object? arguments = null, CancellationToken cancellationToken = default) =>
            client.GetAsync<T>(operation, arguments, cancellationToken: cancellationToken);
        public Task<RemotePreparedStreamCapabilities?> GetPreparedStreamCapabilitiesAsync(RemotePreparedStreamOwnerIdentity owner, CancellationToken token) =>
            streaming.GetPreparedStreamCapabilitiesAsync(owner, token);
        public Task<RemoteGeneratedResponse> GenerateStreamingAsync(RemotePreparedStreamRequest request, Action<string>? onText, CancellationToken token) =>
            streaming.GenerateStreamingAsync(request, onText, token);
        public void Dispose() { streaming.Dispose(); client.Dispose(); }
    }
}

/// <summary>A bounded owner connection verified against both stable worker identity and current assignment.</summary>
public sealed class RemoteManagedWorkerSession : IDisposable
{
    private readonly Configuration configuration;
    private readonly string controllerId;
    public WorkerRegistration Registration { get; }
    public IRemoteWorkerOwnerClient Client { get; }
    public HostInfo Host { get; private set; } = null!;
    public WorkerManagementStatus Management { get; private set; } = null!;

    internal RemoteManagedWorkerSession(Configuration configuration, string controllerId, WorkerRegistration registration, IRemoteWorkerOwnerClient client)
    { this.configuration = configuration; this.controllerId = controllerId; Registration = registration; Client = client; }

    public void EnsureCurrent()
    {
        var settings = configuration.Get();
        var current = settings.Workers.SingleOrDefault(x => x.Id == Registration.Id);
        if (!settings.Coordinator || settings.InstallationId != controllerId || current is null || current.RequiresConfiguration ||
            current.Url != Registration.Url || current.AccessKey != Registration.AccessKey)
            throw new UnauthorizedAccessException("The worker owner connection changed or was removed. No further management request was sent.");
    }

    public async Task RevalidateAsync(CancellationToken ct)
    {
        EnsureCurrent();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var host = await Client.GetAsync<HostInfo>("health", cancellationToken: timeout.Token);
        if (host.Product != "Pegasus Cerebral" || host.Protocol != 1 || host.Coordinator ||
            string.IsNullOrWhiteSpace(host.InstallationId) || host.InstallationId == controllerId)
            throw new UnauthorizedAccessException("Remote model management requires a current standalone Worker with a distinct verified identity.");
        var management = await Client.GetAsync<WorkerManagementStatus>("worker.management.status", cancellationToken: timeout.Token);
        if (management.State != "assigned" || management.ControllerInstallationId != controllerId ||
            management.WorkerInstallationId != host.InstallationId || Management is not null && management.WorkerInstallationId != Management.WorkerInstallationId)
            throw new UnauthorizedAccessException("This standalone worker is not assigned to this exact Cerebral coordinator.");
        EnsureCurrent();
        Host = host; Management = management;
    }

    public async Task<ModelRecord> InspectAsync(string id, CancellationToken ct)
    {
        Configuration.ValidateId(id); EnsureCurrent();
        var model = await Client.GetAsync<ModelRecord>("models.inspect", new { id }, ct);
        if (model.Id != id) throw new InvalidOperationException("The remote model response does not match the requested model.");
        return model;
    }

    public async Task<ModelRecord> LoadAsync(string id, CancellationToken ct)
    {
        using var ownership = ModelExecutionAccounting.For(configuration.State).Begin(ModelOwnershipKind.RemoteModelChange, id);
        Configuration.ValidateId(id); EnsureCurrent();
        ownership.Bind(workerInstallationId: Management.WorkerInstallationId);
        ownership.Phase(ModelOwnershipPhase.Loading, effectStarted: true);
        var model = await Client.GetAsync<ModelRecord>("models.load", new { id }, ct);
        if (model.Id != id || model.State != "loaded") throw new InvalidOperationException("The remote worker did not confirm the selected model loaded.");
        ownership.Complete(); return model;
    }

    public async Task UnloadAsync(string id, CancellationToken ct)
    {
        using var ownership = ModelExecutionAccounting.For(configuration.State).Begin(ModelOwnershipKind.RemoteModelChange, id, requiresAdmission: false);
        Configuration.ValidateId(id); EnsureCurrent();
        ownership.Bind(workerInstallationId: Management.WorkerInstallationId);
        ownership.Phase(ModelOwnershipPhase.Stopping, effectStarted: true);
        _ = await Client.GetAsync<JsonElement>("models.unload", new { id }, ct);
        // The legacy reply does not observe the complete remote process group.
        ownership.Unknown("remote-context-cleanup-unobserved");
    }

    public async Task<WorkerModelLoadResult> LoadTrackedAsync(string id, CancellationToken ct)
    {
        using var ownership = ModelExecutionAccounting.For(configuration.State).Begin(ModelOwnershipKind.RemoteModelChange, id);
        Configuration.ValidateId(id); EnsureCurrent();
        ownership.Bind(workerInstallationId: Management.WorkerInstallationId);
        ownership.Phase(ModelOwnershipPhase.Loading, effectStarted: true);
        var result = await Client.GetAsync<WorkerModelLoadResult>("models.load-tracked", new { id }, ct);
        if (result.Model.Id != id || result.Model.State != "loaded") throw new InvalidOperationException("The remote worker did not confirm the selected model loaded.");
        ownership.Complete(); return result;
    }

    internal Task<int> CountAsync(string id, IReadOnlyList<ModelMessage> messages, string reasoningMode, CancellationToken token) =>
        new RemoteWorkerManagement(configuration).CountAsync(this, id, messages, reasoningMode, token);

    internal Task<ModelImageVerification> VerifyImageAsync(string id, CancellationToken token) =>
        new RemoteWorkerManagement(configuration).VerifyImageAsync(this, id, token);

    public static void EnsureCapabilities(AgentProfile profile, ModelRecord model, bool configurationOnly = false)
    {
        ModelCapabilityPolicy.ValidateRequirements(profile);
        foreach (var requirement in ModelCapabilityPolicy.Requirements(profile))
        {
            if (configurationOnly && requirement == ModelCapabilityIds.ImageInput) continue;
            if (model.CapabilityStatus.SingleOrDefault(x => x.Id == requirement)?.Usable != true)
                throw new InvalidOperationException($"Remote model '{model.Name}' does not currently prove {ModelCapabilityIds.DisplayName(requirement)} for '{profile.Name}'.");
        }
    }

    public void Dispose() => Client.Dispose();
}

public sealed partial class CommandDispatcher
{
    private async Task<WorkerModelLoadResult> LoadTrackedModelAsync(string id, CancellationToken ct)
    {
        Configuration.ValidateId(id);
        await modelFiles.WaitAsync(ct);
        try { var result = await runtime.LoadTrackedAsync(id, ct); return new(result.Model, result.Started); }
        finally { modelFiles.Release(); }
    }

    private async Task<WorkerModelDiagnostics> ModelRuntimeStatusAsync(CancellationToken ct)
    {
        var models = CerebralJson.Read<List<WorkerModelRuntimeStatus>>(CerebralJson.Element(runtime.Diagnostics()));
        if (models.Count > 256) throw new InvalidOperationException("Runtime diagnostics exceed the bounded 256-model limit.");
        var settings = configuration.Get();
        return new(settings.InstallationId, await HardwareProbe.ReadAsync(ct), models, DateTimeOffset.UtcNow, settings.ReservedMemoryMb);
    }
}
