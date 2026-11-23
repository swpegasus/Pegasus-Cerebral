using System.IO.Pipes;
using System.Net;
using System.Security.Principal;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker;

internal enum OwnerSnapshotHttpRoute { None, Canonical, Rejected }
internal sealed record OwnerSnapshotInput(string WorkerId, string ControllerId, string? ModelId);
internal sealed record OwnerSnapshotBindingEvidence(string BindingMode, bool RawBindingAvailable,
    int? CanonicalJsonUtf8Bytes, int? FingerprintPreimageBytes, string? Sha256,
    RemotePreparedModelBinding? RawBinding, string BindingCodec, string? Reason);
internal sealed record OwnerSnapshotRegistration(string Id, string State, string? RegistrationFence,
    OwnerSnapshotBindingEvidence Binding);
internal sealed record OwnerSnapshotRuntime(string ProcessGenerationId, long RuntimeEpoch, string InstanceState,
    string? InstanceId, OwnerSnapshotBindingEvidence? LaunchBinding, string LaunchBindingState,
    OwnerRuntimeProviderObservation? Provider, OwnerRuntimeContextObservation? EffectiveContext,
    int? ConfiguredParallelRequests, object Slots, object Capabilities, object ComponentPins);
internal sealed record OwnerSnapshotCatalog(bool Complete, string[] Ids, string? Fence, string[] Diagnostics);
internal sealed record OwnerSnapshotReply(int SchemaVersion, string Operation, string HttpRoute,
    string WorkerInstallationId, string ControllerInstallationId, string Role, int ConfigurationRevision,
    OwnerSnapshotCatalog Catalog, OwnerSnapshotRegistration? Registration, OwnerSnapshotRuntime Runtime,
    bool Admission, string AdmissionReason);

/// <summary>Private transport admission and pure observation. Expected IDs never grant ownership.</summary>
internal sealed class OwnerModelSnapshotService(StateStore store, Configuration configuration,
    ModelRuntime runtime, LocalRunControl? localRun)
{
    internal const string Operation = "models.binding.snapshot";
    internal const string AccountingOperation = "models.accounting.snapshot";
    internal const string ReleaseOperation = "models.admission.release";
    internal static bool IsOwnerOperation(string operation) => operation is Operation or AccountingOperation or ReleaseOperation;
    internal static string PathFor(string operation) => "/api/command/" + operation;
    internal const string HttpPath = "/api/command/models.binding.snapshot";
    internal const int MaximumRequestBytes = 4096;
    internal const int MaximumResponseBytes = 256 * 1024;
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    // Recognizable aliases must never fall through to legacy middleware that normalizes state.
    // Only the exact declared path can acquire owner proof; case/slash aliases fail purely.
    internal static OwnerSnapshotHttpRoute ClassifyHttpPath(string? path)
    {
        if (path is null) return OwnerSnapshotHttpRoute.None;
        var paths = new[] { HttpPath, PathFor(AccountingOperation), PathFor(ReleaseOperation) };
        var known = paths.FirstOrDefault(value => string.Equals(path.TrimEnd('/'), value, StringComparison.OrdinalIgnoreCase));
        if (known is null) return OwnerSnapshotHttpRoute.None;
        return path == known ? OwnerSnapshotHttpRoute.Canonical : OwnerSnapshotHttpRoute.Rejected;
    }

    private long verifiedOwnerAdmissions;
    internal long VerifiedOwnerAdmissions => Interlocked.Read(ref verifiedOwnerAdmissions);

    // Neither a serialized request, actor=null nor a caller-created DTO can supply this proof.
    private sealed class VerifiedOwnerContext(OwnerModelSnapshotService issuer, OwnerSnapshotConfiguration state)
    {
        internal bool WasIssuedBy(OwnerModelSnapshotService value) => ReferenceEquals(issuer, value);
        internal OwnerSnapshotConfiguration State { get; } = state;
    }

    internal async Task<CommandResult> ExecuteHttpAsync(HttpContext context, CommandRequest request, CancellationToken token)
    {
        if (!IsOwnerOperation(request.Operation) || context.Request.Path.Value != PathFor(request.Operation) ||
            ClassifyHttpPath(context.Request.Path.Value) != OwnerSnapshotHttpRoute.Canonical || context.Request.Method != "POST")
            throw new UnauthorizedAccessException("owner_transport_required");
        if (context.Request.Headers.ContainsKey("Origin"))
            throw new UnauthorizedAccessException("browser_origin_denied");
        var state = configuration.ReadOwnerSnapshotConfiguration(token);
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || (!IPAddress.IsLoopback(remote) && !state.AllowLan))
            throw new UnauthorizedAccessException("owner_network_denied");
        var values = context.Request.Headers["X-Cerebral-Key"];
        if (values.Count != 1 || !state.Authenticates(values[0] ?? ""))
            throw new UnauthorizedAccessException("owner_authentication_required");
        return await ExecuteVerifiedAsync(new(this, state), request, token);
    }

    internal async Task<CommandResult> ExecutePipeAsync(NamedPipeServerStream pipe,
        InstalledWorkerBinding? binding, CommandRequest request, int frameBytes, CancellationToken token)
    {
        if (frameBytes is < 1 or > MaximumRequestBytes)
            throw new OwnerSnapshotUnavailableException("request_byte_bound");
        if (!OperatingSystem.IsWindows()) throw new UnauthorizedAccessException("owner_transport_required");
        if (binding is not null)
        {
            var registered = InstalledWorkerBindingStore.ReadRegistered()
                ?? throw new UnauthorizedAccessException("installed_owner_binding_unavailable");
            if (registered != binding) throw new UnauthorizedAccessException("installed_owner_binding_changed");
            if (!InstalledWorkerBinding.PathEquals(store.DirectoryPath, binding.DataDirectory, directory: true))
                throw new UnauthorizedAccessException("installed_owner_root_mismatch");
            _ = InstalledWorkerWindows.RequireOwnerCaller(pipe, binding);
        }
        else
        {
            var caller = InstalledWorkerWindows.ReadAuthenticatedCaller(pipe);
            using var identity = WindowsIdentity.GetCurrent();
            if (!caller.Authenticated || caller.NetworkLogon || caller.Anonymous || caller.SessionId <= 0 ||
                identity.IsSystem || identity.User?.Value != caller.UserSid)
                throw new UnauthorizedAccessException("owner_transport_required");
        }
        var state = configuration.ReadOwnerSnapshotConfiguration(token);
        return await ExecuteVerifiedAsync(new(this, state), request, token);
    }

    internal static CommandRequest ParseEnvelope(byte[] bytes)
    {
        using var document = OwnerSnapshotJson.Parse(bytes, MaximumRequestBytes);
        var fields = OwnerSnapshotJson.Fields(document.RootElement,
            new HashSet<string>(["operation", "arguments", "profileId"], StringComparer.Ordinal));
        var operation = OwnerSnapshotJson.Text(fields, "operation", 128, false, out _);
        if (!IsOwnerOperation(operation)) throw new OwnerSnapshotUnavailableException("operation_mismatch");
        if (fields.TryGetValue("profileId", out var profile) && profile.ValueKind != JsonValueKind.Null)
            throw new UnauthorizedAccessException("owner_profile_denied");
        var args = OwnerSnapshotJson.Required(fields, "arguments");
        if (operation == Operation) _ = ParseInput(args);
        else _ = ParseAccountingInput(operation, args);
        return new(operation, args.Clone(), null);
    }

    private static OwnerSnapshotInput ParseInput(JsonElement args)
    {
        // JsonElement preserves escaped source text in the pipe envelope; validate scalar
        // encoding before GetString can repair or reject an escaped surrogate implicitly.
        using var validated = OwnerSnapshotJson.Parse(System.Text.Encoding.UTF8.GetBytes(args.GetRawText()), MaximumRequestBytes);
        var fields = OwnerSnapshotJson.Fields(validated.RootElement, new HashSet<string>(
            ["schemaVersion", "expectedWorkerInstallationId", "expectedControllerInstallationId", "modelId"], StringComparer.Ordinal));
        if (OwnerSnapshotJson.Integer(fields, "schemaVersion") != 1)
            throw new OwnerSnapshotUnavailableException("schema_version_unsupported");
        var worker = OwnerSnapshotJson.Text(fields, "expectedWorkerInstallationId", 128, false, out _);
        var controller = OwnerSnapshotJson.Text(fields, "expectedControllerInstallationId", 128, false, out _);
        OwnerSnapshotConfiguration.RequireInstallationId(worker); OwnerSnapshotConfiguration.RequireInstallationId(controller);
        string? model = null;
        if (fields.ContainsKey("modelId"))
        {
            model = OwnerSnapshotJson.Text(fields, "modelId", 128, false, out _);
            try { OwnerModelSnapshotCodec.ValidateIdentifier(model); }
            catch (InvalidDataException) { throw new OwnerSnapshotUnavailableException("model_identity_invalid"); }
        }
        return new(worker, controller, model);
    }

    private string RequireOwner(OwnerSnapshotConfiguration captured, OwnerSnapshotConfiguration current, OwnerSnapshotInput input)
    {
        if (!captured.Matches(current)) throw new OwnerSnapshotUnavailableException("owner_configuration_changed");
        string role, controller;
        if (localRun?.Enabled == true && current.Coordinator && !current.Standalone && current.ControllerId is null)
        { role = "source_local"; controller = current.InstallationId; }
        else if (localRun?.Enabled != true && current.Standalone && !current.Coordinator && current.ControllerId is { } assigned)
        { role = "assigned_standalone"; controller = assigned; }
        else throw new OwnerSnapshotUnavailableException("owner_role_unavailable");
        if (input.WorkerId != current.InstallationId || input.ControllerId != controller)
            throw new UnauthorizedAccessException("owner_identity_mismatch");
        return role;
    }

    private async Task<CommandResult> ExecuteVerifiedAsync(VerifiedOwnerContext proof, CommandRequest request, CancellationToken token)
    {
        if (!proof.WasIssuedBy(this) || !IsOwnerOperation(request.Operation) || request.ProfileId is not null)
            throw new UnauthorizedAccessException("verified_owner_context_required");
        if (request.Operation != Operation) return ExecuteAccountingVerified(proof, request, token);
        var input = ParseInput(request.Arguments);
        _ = RequireOwner(proof.State, proof.State, input); // Refusal before entering runtime or model enumeration.
        Interlocked.Increment(ref verifiedOwnerAdmissions);
        OwnerRuntimeModelObservation? observed = null;
        var result = await runtime.WithOwnerRuntimeSnapshotAsync(input.ModelId, observation =>
        {
            observed = observation;
            string? role = null;
            return store.WithOwnerSnapshotState(true,
                state => BuildResult(state, observation, input, proof.State, role!, token), token,
                raw => role = RequireOwner(proof.State, OwnerSnapshotConfiguration.Parse(raw), input));
        }, token);
        token.ThrowIfCancellationRequested();
        if (observed is null || !runtime.IsOwnerRuntimeSnapshotCurrent(observed))
            throw new OwnerSnapshotUnavailableException("runtime_snapshot_changed");
        var finalOwner = configuration.ReadOwnerSnapshotConfiguration(token);
        _ = RequireOwner(proof.State, finalOwner, input);
        token.ThrowIfCancellationRequested();
        if (!runtime.IsOwnerRuntimeSnapshotCurrent(observed))
            throw new OwnerSnapshotUnavailableException("runtime_snapshot_changed");
        return result;
    }

    private sealed record AccountingInput(OwnerSnapshotInput Owner, string? Generation, long? Revision);

    private static AccountingInput ParseAccountingInput(string operation, JsonElement args)
    {
        using var validated = OwnerSnapshotJson.Parse(System.Text.Encoding.UTF8.GetBytes(args.GetRawText()), MaximumRequestBytes);
        var allowed = new HashSet<string>(["schemaVersion", "expectedWorkerInstallationId", "expectedControllerInstallationId"], StringComparer.Ordinal);
        if (operation == ReleaseOperation) { allowed.Add("expectedProcessGenerationId"); allowed.Add("expectedAccountingRevision"); }
        var fields = OwnerSnapshotJson.Fields(validated.RootElement, allowed);
        if (fields.Count != allowed.Count || OwnerSnapshotJson.Integer(fields, "schemaVersion") != 1)
            throw new OwnerSnapshotUnavailableException("accounting_input_invalid");
        var worker = OwnerSnapshotJson.Text(fields, "expectedWorkerInstallationId", 128, false, out _);
        var controller = OwnerSnapshotJson.Text(fields, "expectedControllerInstallationId", 128, false, out _);
        OwnerSnapshotConfiguration.RequireInstallationId(worker); OwnerSnapshotConfiguration.RequireInstallationId(controller);
        string? generation = null; long? revision = null;
        if (operation == ReleaseOperation)
        {
            generation = OwnerSnapshotJson.Text(fields, "expectedProcessGenerationId", 128, false, out _);
            OwnerSnapshotConfiguration.RequireInstallationId(generation);
            if (!fields["expectedAccountingRevision"].TryGetInt64(out var value) || value < 1)
                throw new OwnerSnapshotUnavailableException("accounting_revision_invalid");
            revision = value;
        }
        return new(new(worker, controller, null), generation, revision);
    }

    private CommandResult ExecuteAccountingVerified(VerifiedOwnerContext proof, CommandRequest request, CancellationToken token)
    {
        var input = ParseAccountingInput(request.Operation, request.Arguments);
        _ = RequireOwner(proof.State, proof.State, input.Owner);
        return store.WithModelAccountingState(() =>
        {
            token.ThrowIfCancellationRequested();
            var current = configuration.ReadOwnerSnapshotConfiguration(token);
            var role = RequireOwner(proof.State, current, input.Owner);
            var retained = CerebralJson.Element(new RemoteWorkerManagement(configuration).ReadPreparedOwnership(token));
            if (request.Operation == ReleaseOperation)
            {
                if (retained.GetProperty("state").GetString() != "observed" || !retained.GetProperty("complete").GetBoolean() ||
                    retained.GetProperty("unknown").ValueKind != JsonValueKind.Number || retained.GetProperty("unknown").GetInt32() != 0 ||
                    retained.GetProperty("reservations").GetArrayLength() != 0)
                    throw new OwnerSnapshotUnavailableException("retained_model_ownership_unconfirmed");
                token.ThrowIfCancellationRequested();
                try { runtime.Accounting.ReleaseStartup(input.Generation!, input.Revision!.Value); }
                catch (InvalidOperationException error) { throw new OwnerSnapshotUnavailableException(error.Message); }
            }
            var roleObservation = request.Operation == AccountingOperation ? runtime.ObserveAccountingOwnershipRoles() : null;
            var snapshot = roleObservation?.Accounting ?? runtime.Accounting.Snapshot();
            var detailComplete = snapshot.Entries.Length <= 256;
            var result = CommandResult.Ok(new { schemaVersion = 1, request.Operation, workerInstallationId = input.Owner.WorkerId,
                controllerInstallationId = input.Owner.ControllerId, role, configurationRevision = current.Revision,
                accounting = snapshot with { Entries = snapshot.Entries.Take(256).ToArray() }, detailComplete,
                retainedRemoteOwnership = retained, remoteProviderTotals = "unobserved", machineTotals = "unobserved", globalZero = (bool?)null });
            if (roleObservation is not null)
                result = ModelOwnershipRoleProjection.Attach(result, roleObservation.OwnershipRoles, MaximumResponseBytes);
            if (JsonSerializer.SerializeToUtf8Bytes(result, CerebralJson.Options).Length > MaximumResponseBytes)
                throw new OwnerSnapshotUnavailableException("response_byte_bound");
            Interlocked.Increment(ref verifiedOwnerAdmissions);
            return result;
        }, token);
    }

    private static CommandResult BuildResult(OwnerSnapshotRawState state, OwnerRuntimeModelObservation runtime,
        OwnerSnapshotInput input, OwnerSnapshotConfiguration owner, string role, CancellationToken token)
    {
        var issues = state.Diagnostics.ToList();
        var valid = new List<(OwnerSnapshotRawModelRow Row, RemotePreparedModelBinding Binding, string State)>();
        var complete = state.CatalogComplete;
        foreach (var row in state.Models)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var item = OwnerSnapshotJson.Binding(row);
                valid.Add((row, item.Binding, item.State));
            }
            catch (Exception error) when (error is OwnerSnapshotUnavailableException or InvalidDataException or ArgumentException)
            { complete = false; AddIssue("registration_schema_invalid"); }
        }
        var ids = valid.Select(x => x.Row.Id).Order(StringComparer.Ordinal).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) { complete = false; AddIssue("duplicate_model_coordinate"); }
        string? fence = null;
        if (complete)
        {
            try { fence = OwnerModelSnapshotCodec.CatalogFence(valid.Select(x => x.Row).ToArray()); }
            catch (InvalidDataException) { complete = false; AddIssue("catalog_not_encodable"); }
        }
        OwnerSnapshotRegistration? selected = null;
        if (input.ModelId is { } id)
        {
            var item = valid.FirstOrDefault(x => x.Row.Id == id);
            var raw = state.Models.FirstOrDefault(x => x.Id == id);
            selected = item.Row is null
                ? new(id, raw is null ? complete ? "not_found" : "unobserved" : "invalid", null, Unavailable("registration_unavailable"))
                : new(id, item.State, complete ? OwnerModelSnapshotCodec.RegistrationFence(item.Row) : null, Encode(item.Binding));
        }
        var launch = runtime.LaunchBinding is null ? null : Encode(runtime.LaunchBinding);
        var runtimeReply = new OwnerSnapshotRuntime(runtime.ProcessGenerationId, runtime.RuntimeEpoch,
            runtime.InstanceState, runtime.InstanceId, launch, runtime.LaunchBindingState,
            runtime.Provider, runtime.EffectiveContext, runtime.ConfiguredParallelRequests,
            new { state = "unobserved", busyCount = (int?)null, availableCount = (int?)null, quarantinedCount = (int?)null },
            new { state = "unobserved", entries = Array.Empty<object>() }, new { state = "unobserved" });
        var reason = !complete ? "catalog_incomplete" : selected is null ? "selection_required"
            : selected.State == "not_found" ? "registration_not_found"
            : selected.Binding.Sha256 is null ? "persisted_binding_unavailable"
            : runtime.InstanceState != "present" ? "runtime_instance_unobserved"
            : launch?.Sha256 is null ? "launch_binding_unobserved"
            : selected.Binding.Sha256 != launch.Sha256 ? "persisted_launch_binding_mismatch"
            : "runtime_readiness_or_pins_unobserved";
        var reply = new OwnerSnapshotReply(1, Operation, HttpPath, input.WorkerId, input.ControllerId, role, owner.Revision,
            new(complete, ids, fence, issues.ToArray()), selected, runtimeReply, false, reason);
        var result = CommandResult.Ok(reply);
        if (JsonSerializer.SerializeToUtf8Bytes(result, CerebralJson.Options).Length > MaximumResponseBytes)
        {
            if (selected is not null) selected = selected with { Binding = FingerprintOnly(selected.Binding) };
            if (launch is not null) launch = FingerprintOnly(launch);
            reply = reply with { Registration = selected, Runtime = runtimeReply with { LaunchBinding = launch },
                AdmissionReason = complete ? "raw_binding_unavailable" : reason };
            result = CommandResult.Ok(reply);
            if (JsonSerializer.SerializeToUtf8Bytes(result, CerebralJson.Options).Length > MaximumResponseBytes)
                throw new OwnerSnapshotUnavailableException("response_byte_bound");
        }
        token.ThrowIfCancellationRequested();
        return result;

        void AddIssue(string code) { if (issues.Count < 16 && !issues.Contains(code, StringComparer.Ordinal)) issues.Add(code); }
    }

    private static OwnerSnapshotBindingEvidence Encode(RemotePreparedModelBinding value)
    {
        try
        {
            var encoded = OwnerModelSnapshotCodec.EncodeBinding(value);
            return new("full", true, encoded.CanonicalJson.Length, encoded.FingerprintPreimageBytes,
                encoded.Sha256, value, OwnerModelSnapshotCodec.BindingCodec, null);
        }
        catch (InvalidDataException) { return Unavailable("binding_not_encodable"); }
    }
    private static OwnerSnapshotBindingEvidence Unavailable(string reason) =>
        new("unavailable", false, null, null, null, null, OwnerModelSnapshotCodec.BindingCodec, reason);
    private static OwnerSnapshotBindingEvidence FingerprintOnly(OwnerSnapshotBindingEvidence value) =>
        value.Sha256 is null ? value : value with { BindingMode = "fingerprint_only", RawBindingAvailable = false,
            RawBinding = null, Reason = "response_byte_bound" };
}
