using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private const int MaximumCapacityEffectsBytes = 524288;
    private const int MaximumCapacityResponseBytes = 1048576;
    private const string CapacityWeightBusy = "Unload every model registration using this weight file before changing it.";
    private const string CapacityAssignmentsBusy = "Finish or cancel this model's assignments before changing its settings.";
    private sealed record CapacityInput(string ModelId, ModelCapacityChoice? Choice,
        string? ExpectedCurrent, int? RequiredContext);

    private static readonly IReadOnlyList<WorkerWorkflows.OperationDescriptor> ModelCapacityOperations =
    [
        new(ModelCapacityProtocol.PreviewOperation, 1,
            "Preview a fixed or automatic context/slot choice, affected profile budgets and estimate-only alternatives. No load or saved configuration change.",
            CapacityRequestSchema(false)) { RequiredCapabilities = ["administration"] },
        new(ModelCapacityProtocol.ApplyOperation, 1,
            "Apply the selected capacity choice only while its complete expected-current snapshot still matches and the weight is unloaded and unassigned. No drain, reload or scheduler change.",
            CapacityRequestSchema(true)) { RequiredCapabilities = ["administration"] },
        new("workers.models.capacity.preview", 1,
            "Preview a capacity choice on an assigned remote Worker through its normal configuration authority.",
            CapacityRequestSchema(false, remote: true), true),
        new("workers.models.capacity.apply", 1,
            "Forward an exact expected-current capacity Apply to an assigned remote Worker without a retry.",
            CapacityRequestSchema(true, remote: true), true)
    ];

    private static JsonElement CapacityRequestSchema(bool apply, bool remote = false)
    {
        var properties = new Dictionary<string, object>
        {
            ["version"] = new { type = "integer", @const = 1 },
            ["modelId"] = new { type = "string", minLength = 1, maxLength = 100 },
            ["choice"] = new
            {
                type = apply ? new[] { "object" } : new[] { "object", "null" },
                properties = new
                {
                    contextMode = new { type = "string", @enum = new[] { "fixed", "automatic" } },
                    parallelRequests = new { type = "integer", minimum = 1, maximum = 16 },
                    fixedContextTokens = new { type = new[] { "integer", "null" }, minimum = 512, maximum = 1048576 },
                    automaticContextCeilingTokens = new { type = new[] { "integer", "null" }, minimum = 0, maximum = 1048576 }
                },
                required = new[] { "contextMode", "parallelRequests" }, additionalProperties = false
            },
            ["requiredPerSlotContextTokens"] = new { type = new[] { "integer", "null" }, minimum = 512, maximum = 1048576 }
        };
        if (apply) properties.Add("expectedCurrent", new { type = "string", minLength = 68, maxLength = 68 });
        if (remote) properties.Add("workerId", new { type = "string", minLength = 1, maxLength = 100 });
        var required = apply ? new List<string> { "version", "modelId", "choice", "expectedCurrent" }
            : new List<string> { "version", "modelId" };
        if (remote) required.Add("workerId");
        return CerebralJson.Element(new
        {
            type = "object", properties,
            required,
            additionalProperties = false
        });
    }

    internal static void ValidateCapacityTransportRequest(JsonElement args, bool apply) =>
        _ = ReadCapacityInput(args, apply);

    private static CapacityInput ReadCapacityInput(JsonElement args, bool apply)
    {
        RequireCapacityFields(args, apply
            ? ["version", "modelId", "choice", "expectedCurrent", "requiredPerSlotContextTokens"]
            : ["version", "modelId", "choice", "requiredPerSlotContextTokens"]);
        if (!args.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1)
            throw new ArgumentException("capacity_version");
        var id = Text(args, "modelId"); Configuration.ValidateId(id);
        ModelCapacityChoice? choice = null;
        if (args.TryGetProperty("choice", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            RequireCapacityFields(value, ["contextMode", "parallelRequests", "fixedContextTokens", "automaticContextCeilingTokens"]);
            var mode = Text(value, "contextMode");
            if (!value.TryGetProperty("parallelRequests", out var slots) || !slots.TryGetInt32(out var count))
                throw new ArgumentException("capacity_slots");
            choice = new(mode, count, NullableCapacityInt(value, "fixedContextTokens"),
                NullableCapacityInt(value, "automaticContextCeilingTokens"));
        }
        if (apply && choice is null) throw new ArgumentException("capacity_choice_required");
        // Generic protocol bounds only: this placeholder supplies no model/runtime evidence.
        // PrepareCapacitySave validates again against the actual stored native limit.
        if (choice is not null)
            ModelCapacityChoices.ValidateChoice(new ModelRecord { NativeContextTokens = 1_048_576 }, choice);
        var required = NullableCapacityInt(args, "requiredPerSlotContextTokens");
        if (required is { } requiredTokens && requiredTokens is < 512 or > 1048576)
            throw new ArgumentException("capacity_required_context");
        var expected = apply ? Text(args, "expectedCurrent") : null;
        if (apply && (expected!.Length != 68 || !expected.StartsWith("mc1.", StringComparison.Ordinal) ||
            expected.AsSpan(4).ContainsAnyExcept("0123456789abcdef")))
            throw new ArgumentException("capacity_expected_current");
        return new(id, choice, expected, required);
    }

    private static int? NullableCapacityInt(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var number))
            throw new ArgumentException("capacity_integer");
        return number;
    }

    private static void RequireCapacityFields(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("capacity_object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new ArgumentException("capacity_fields");
    }

    private async Task<ModelCapacityPreview> ModelCapacityPreviewAsync(JsonElement args, CancellationToken ct)
    {
        var input = ReadCapacityInput(args, false);
        ResourceInfo? resources = null;
        DateTimeOffset? sampledAt = null;
        // Exactly one ordinary read-only resource sample, outside modelFiles/lifecycle/store.
        // It is a planning observation, never part of the comparison token or Apply eligibility.
        try { resources = await HardwareProbe.ReadAsync(ct); sampledAt = DateTimeOffset.UtcNow; }
        catch (Exception error) when (error is IOException or InvalidOperationException or Win32Exception)
        { ct.ThrowIfCancellationRequested(); }
        return await runtime.WithOwnerRuntimeSnapshotAsync(input.ModelId,
            observation => store.WithModelCapacityState(input.ModelId, state =>
                BuildCapacityPreview(state, observation, input.Choice, input.RequiredContext, resources, sampledAt), ct), ct);
    }

    // The exact producer callback is independently usable with a supplied nullable observation in
    // SQLite/unit checks. It never samples hardware or writes metadata/configuration.
    internal ModelCapacityPreview BuildCapacityPreview(ModelCapacityStoredState state,
        OwnerRuntimeModelObservation observation, ModelCapacityChoice? selected, int? required,
        ResourceInfo? resources, DateTimeOffset? sampledAt)
    {
        var model = state.Model.Value;
        selected ??= model.AutomaticContextSizing
            ? new("automatic", model.ParallelRequests, null, model.MaximumContextTokens)
            : new("fixed", model.ParallelRequests, model.ContextTokens);
        var prepared = PrepareCapacitySave(state, selected);
        var effects = CapacityProfileEffects(state, prepared);
        var capabilities = ModelCapabilityPolicy.Assess(prepared.Model);
        var eligibility = CapacityEligibility(state, observation);
        try { PreparedModelSave.ObserveCapabilities(prepared); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException)
        { eligibility = new("unavailable", "capacity_capability_unavailable"); }
        var token = CapacityCurrentToken(state, observation.ProcessGenerationId, selected, prepared, capabilities, required);
        ModelCapacityEffective? effective = observation.InstanceState == "present" && observation.InstanceId is { } instanceId
            ? new("present", instanceId, observation.LaunchBindingState,
                observation.EffectiveContext?.ConfiguredContextTokens, observation.EffectiveContext?.MeasuredContextTokens,
                observation.ConfiguredParallelRequests, observation.EffectiveContext?.State ?? "unobserved",
                observation.EffectiveContext?.Source ?? "launch_arguments") : null;
        var candidates = ModelCapacityChoices.Frontier(model, selected, resources, state.Settings.ReservedMemoryMb, sampledAt, required);
        var response = new ModelCapacityPreview(1, model.Id, ModelCapacityChoices.Configuration(model), selected,
            ModelCapacityChoices.Configuration(prepared.Model), ModelCapacityChoices.Changes(model, prepared.Model),
            ModelCapacityChoices.PreviewPlan(prepared.Model, resources, state.Settings.ReservedMemoryMb, sampledAt, required),
            effective, new("estimate", "generic_capacity_planner", "current_configuration"),
            ModelCapacityChoices.Bounds(model), effects, true, new(state.Settings.MaxConcurrentJobs), token, eligibility,
            candidates, candidates.Count == 16);
        RequireCapacityResponseBound(response);
        return response;
    }

    private ModelCapacityApplyEligibility CapacityEligibility(ModelCapacityStoredState state,
        OwnerRuntimeModelObservation observation)
    {
        if (observation.InstanceState == "unobserved") return new("unavailable", "capacity_runtime_unobserved");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var path = Path.GetFullPath(state.Model.Value.Path);
        var aliases = state.Models.Where(x => Path.GetFullPath(x.Value.Path).Equals(path, comparison)).ToArray();
        var ownership = runtime.Accounting.Snapshot().Entries;
        if (observation.InstanceState == "present" || aliases.Any(x => runtime.IsLoaded(x.Id) ||
            x.Value.State is "loaded" or "loading" || ownership.Any(entry => entry.ModelId == x.Id &&
                entry.Kind is ModelOwnershipKind.LocalContext or ModelOwnershipKind.FittingProcess)))
            return new("busy", "capacity_weight_loaded_or_owned");
        if (CapacityHasModelInterest(state.Model.Id)) return new("busy", "capacity_model_has_accepted_work");
        return new("ready", "capacity_current_unloaded_and_unassigned");
    }

    private bool CapacityHasModelInterest(string modelId) => HasModelAssignments(modelId) ||
        workflows?.HasActiveLocalModelWork(modelId) == true;

    private static PreparedModelSave PrepareCapacitySave(ModelCapacityStoredState state, ModelCapacityChoice choice)
    {
        var requested = ModelCapacityChoices.ApplyChoice(state.Model.Value, choice);
        // Capacity changes deliberately do not run metadata refresh. The shared pure validator and
        // profile calculation use existing metadata and preserve all unrelated saved fields.
        return PreparedModelSave.Prepare(requested, state.Profiles.Select(x => x.Value));
    }

    private static IReadOnlyList<ModelCapacityProfileEffect> CapacityProfileEffects(ModelCapacityStoredState state,
        PreparedModelSave prepared)
    {
        var before = state.Profiles.ToDictionary(x => x.Id, x => x.Value, StringComparer.Ordinal);
        static ModelCapacityProfileBudget Budget(AgentProfile p) =>
            new(p.Revision, p.ContextTokens, p.ResponseTokens, p.ReasoningMode, p.ReasoningTokens);
        var effects = prepared.Profiles.Select(p => new ModelCapacityProfileEffect(p.Id, p.Name, p.ModelWorkerId,
            p.ModelId, p.FollowsModelBudgets, Budget(before[p.Id]), Budget(p))).ToArray();
        if (JsonSerializer.SerializeToUtf8Bytes(effects, CerebralJson.Options).Length > MaximumCapacityEffectsBytes)
            throw new ModelCapacityUnavailableException("capacity_profile_effect_byte_bound");
        return effects;
    }

    private static string CapacityCurrentToken(ModelCapacityStoredState state, string generation,
        ModelCapacityChoice choice, PreparedModelSave prepared, IReadOnlyList<ModelCapabilityStatus> capabilities, int? required)
    {
        static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        var value = new
        {
            domain = "Cerebral.ModelCapacity.ExpectedCurrent/1", workerGeneration = generation,
            settings = Hash(state.SettingsBody), model = new { state.Model.Id, sha256 = Hash(state.Model.Body) },
            profiles = state.Profiles.Select(p => new { p.Id, sha256 = Hash(p.Body) }).ToArray(),
            choice, requiredPerSlotContextTokens = required,
            preparedConfiguration = ModelCapacityChoices.Configuration(prepared.Model),
            effects = CapacityProfileEffects(state, prepared),
            featureDecision = capabilities,
            policy = "ordinary_model_save_capacity_and_profile_budgets/1"
        };
        return "mc1." + Hash(JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options));
    }

    private async Task<ModelCapacityApplyResult> ModelCapacityApplyAsync(JsonElement args, CancellationToken ct)
    {
        CapacityInput input;
        try { input = ReadCapacityInput(args, true); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException)
        { return CapacityRefusal("", "invalid", "capacity_request_invalid"); }
        ModelCapacityApplyResult result;
        ModelRecord? committedModel = null;
        try
        {
            var generation = await runtime.WithOwnerRuntimeSnapshotAsync(input.ModelId,
                observation => observation.ProcessGenerationId, ct);
            await modelFiles.WaitAsync(ct);
            try
            {
                result = await runtime.WithUnloadedModelAsync(input.ModelId, _ => store.Locked(() =>
                    store.WithModelCapacityState(input.ModelId, state =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (CapacityHasModelInterest(input.ModelId))
                            return CapacityRefusal(input.ModelId, "busy", "capacity_model_has_accepted_work");
                        var prepared = PrepareCapacitySave(state, input.Choice!);
                        var capabilities = ModelCapabilityPolicy.Assess(prepared.Model);
                        var expected = CapacityCurrentToken(state, generation, input.Choice!, prepared, capabilities, input.RequiredContext);
                        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected),
                            Encoding.ASCII.GetBytes(input.ExpectedCurrent!)))
                            return CapacityRefusal(input.ModelId, "stale", "capacity_expected_current_changed");
                        try { PreparedModelSave.ObserveCapabilities(prepared); }
                        catch (Exception error) when (error is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException)
                        { return CapacityRefusal(input.ModelId, "unavailable", "capacity_capability_unavailable"); }
                        var effects = CapacityProfileEffects(state, prepared);
                        // This is the first write. Every comparison/validation above shared this
                        // same transaction, after modelFiles and the authoritative lifecycle gate.
                        var committed = new ModelCapacityApplyResult(1, input.ModelId, "applied", true,
                            "capacity_configuration_committed", ModelCapacityChoices.Configuration(prepared.Model), effects, []);
                        RequireCapacityResponseBound(committed);
                        PersistCapacitySave(state, prepared);
                        committedModel = prepared.Model;
                        return committed;
                    }, ct)), ct);
            }
            finally { modelFiles.Release(); }
        }
        catch (InvalidOperationException error) when (error.Message == CapacityWeightBusy || error.Message == CapacityAssignmentsBusy)
        { return CapacityRefusal(input.ModelId, "busy", "capacity_weight_loaded_or_owned"); }
        catch (Exception error) when (error is ModelCapacityUnavailableException or OwnerRuntimeSnapshotUnavailableException or SqliteException)
        { return CapacityRefusal(input.ModelId, "unavailable", "capacity_store_or_runtime_unavailable"); }
        catch (Exception error) when (error is ArgumentException or JsonException or OverflowException)
        { return CapacityRefusal(input.ModelId, "invalid", "capacity_configuration_invalid"); }
        if (!result.MutationCommitted) return result;
        return PublishCapacityNotifications(committedModel!, result,
            (operation, data) => events.Publish(operation, null, data));
    }

    private static void RequireCapacityResponseBound<T>(T response)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(response, CerebralJson.Options).Length > MaximumCapacityResponseBytes)
            throw new ModelCapacityUnavailableException("capacity_response_byte_bound");
    }

    private static ModelCapacityApplyResult CapacityRefusal(string modelId, string state, string reason) =>
        new(1, modelId, state, false, reason, null, [], []);

    private void PersistCapacitySave(ModelCapacityStoredState state, PreparedModelSave prepared)
    {
        static JsonElement Json<T>(T value) => CerebralJson.Element(value);
        var modelPatch = new Dictionary<string, JsonElement>
        {
            ["automaticContextSizing"] = Json(prepared.Model.AutomaticContextSizing),
            ["contextTokens"] = Json(prepared.Model.ContextTokens),
            ["maximumContextTokens"] = Json(prepared.Model.MaximumContextTokens),
            ["parallelRequests"] = Json(prepared.Model.ParallelRequests)
        };
        var profilePatches = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.Ordinal);
        foreach (var profile in prepared.Profiles)
        {
            var patch = new Dictionary<string, JsonElement>
            {
                ["contextTokens"] = Json(profile.ContextTokens), ["responseTokens"] = Json(profile.ResponseTokens),
                ["reasoningMode"] = Json(profile.ReasoningMode), ["reasoningTokens"] = Json(profile.ReasoningTokens),
                ["revision"] = Json(profile.Revision)
            };
            profilePatches.Add(profile.Id, patch);
        }
        var writes = StateStore.PrepareCapacityWrites(state, modelPatch, profilePatches);
        // All final serialized rows and the complete replacement-catalog total are admitted
        // before this first Put. The immutable values use the exact validated storage serializer.
        foreach (var row in writes.Rows) store.Put(row.Kind, row.Id, row.Value);
    }

    internal static ModelCapacityApplyResult PublishCapacityNotifications(ModelRecord saved,
        ModelCapacityApplyResult committed, Action<string, object?> publish)
    {
        if (!committed.MutationCommitted) throw new ArgumentException("Capacity notifications require a committed result.");
        var warnings = new List<string>();
        void Publish(string operation, object data)
        {
            try { publish(operation, data); }
            catch (Exception error) { warnings.Add(operation + ":" + error.GetType().Name); }
        }
        Publish("model.updated", saved);
        Publish("agents.updated", new { modelId = saved.Id, budgetsResolved = true });
        return committed with { Warnings = warnings.AsReadOnly() };
    }
}
