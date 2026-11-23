using System.IO;
using System.Text;
using System.Text.Json;

namespace Cerebral.PersonalityPlugin.Tests;
internal static partial class Program
{
    private sealed partial class WorkerPipeRun
    {
        private const int MaximumEnvironmentObservations = 32;
        private readonly List<object> environmentObservations = [];
        private int contextEnvironmentIndex;

        private static JsonElement Field(JsonElement value, params string[] path)
        {
            foreach (var name in path)
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var next)) return default;
                value = next;
            }
            return value;
        }
        private static string Kind(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? "missing" : value.ValueKind.ToString().ToLowerInvariant();
        private static bool? Flag(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
        private static long? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;
        private static int? Items(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : null;
        private static bool TextIs(JsonElement value, string expected) => value.ValueKind == JsonValueKind.String && value.GetString() == expected;
        private static object BooleanObservation(JsonElement value) => new { kind = Kind(value), value = Flag(value) };
        private static object NumberObservation(JsonElement value) => new { kind = Kind(value), value = Number(value), validInteger = value.ValueKind == JsonValueKind.Number && Number(value).HasValue };
        private static object ArrayObservation(JsonElement value) => new { kind = Kind(value), count = Items(value) };
        private static object IdentityObservation(JsonElement value, string expected) => new { kind = Kind(value), matchesExpected = TextIs(value, expected) };
        private static object CodeObservation(JsonElement value, params string[] allowed) =>
            new { kind = Kind(value), value = value.ValueKind == JsonValueKind.String
                ? allowed.Contains(value.GetString(), StringComparer.Ordinal) ? value.GetString() : "other" : null };
        private static object GenerationObservation(JsonElement value) =>
            new { kind = Kind(value), validGuid = value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out _) };

        private static void RequireObservationRoom(int count)
        {
            if (count >= MaximumEnvironmentObservations) throw new InvalidOperationException("Bounded environment observations are full.");
        }

        private static object SelectEnvironmentObservation(string operation, JsonElement value, string expectedIdentity)
        {
            object selected = operation switch
            {
                "models.accounting.snapshot" => AccountingObservation(value, expectedIdentity),
                "metrics.slots.status" => SlotObservation(value, expectedIdentity),
                "metrics.execution.status" => ExecutionObservation(value, expectedIdentity),
                "metrics.generation" => new
                {
                    rootKind = Kind(value), schema = CodeObservation(Field(value, "schema"), "cerebral.generation-metrics/1"),
                    truncated = BooleanObservation(Field(value, "truncated")),
                    active = ArrayObservation(Field(value, "active")), recent = ArrayObservation(Field(value, "recent"))
                },
                _ => throw new InvalidOperationException("Environment observation operation is not admitted.")
            };
            return selected;
        }

        private void ObserveEnvironment(string operation, JsonElement value, string phase, int phaseIndex)
        {
            RequireObservationRoom(environmentObservations.Count);
            // Retain only typed scalar/shape facts before guards; never clone response bodies or model identifiers.
            environmentObservations.Add(new
            {
                observationIndex = environmentObservations.Count + 1, phase, phaseIndex,
                captureIndex = ContextRunIds.Count, operation,
                selected = SelectEnvironmentObservation(operation, value, InstallationId)
            });
        }

        private static object AccountingObservation(JsonElement value, string expected)
        {
            var a = Field(value, "accounting");
            return new
            {
                rootKind = Kind(value), schemaVersion = NumberObservation(Field(value, "schemaVersion")),
                workerIdentity = IdentityObservation(Field(value, "workerInstallationId"), expected),
                controllerIdentity = IdentityObservation(Field(value, "controllerInstallationId"), expected),
                detailComplete = BooleanObservation(Field(value, "detailComplete")), accountingKind = Kind(a),
                accountingSchemaVersion = NumberObservation(Field(a, "schemaVersion")),
                processGeneration = GenerationObservation(Field(a, "processGenerationId")),
                revision = NumberObservation(Field(a, "revision")), admissionOpen = BooleanObservation(Field(a, "admissionOpen")),
                admissionReason = CodeObservation(Field(a, "admissionReason"), "ordinary-startup", "explicit-owner-release-required", "verified-owner-release"),
                coverageComplete = BooleanObservation(Field(a, "coverageComplete")),
                localContexts = NumberObservation(Field(a, "localContexts")),
                logicalPreparations = NumberObservation(Field(a, "logicalPreparations")),
                inboundReservations = NumberObservation(Field(a, "inboundReservations")),
                queuedRequests = NumberObservation(Field(a, "queuedRequests")),
                activeRequests = NumberObservation(Field(a, "activeRequests")),
                unknownOwnership = NumberObservation(Field(a, "unknownOwnership")), entries = ArrayObservation(Field(a, "entries")),
                globalZeroKind = Kind(Field(value, "globalZero")),
                remoteProviderTotals = CodeObservation(Field(value, "remoteProviderTotals"), "unobserved"),
                machineTotals = CodeObservation(Field(value, "machineTotals"), "unobserved")
            };
        }

        private static object SlotObservation(JsonElement value, string expected)
        {
            var runtime = Field(value, "runtime"); var models = Field(runtime, "models");
            var hasModels = models.ValueKind == JsonValueKind.Array;
            var states = hasModels
                ? models.EnumerateArray().Take(64).Select(x => Field(x, "instanceState")).ToArray() : [];
            return new
            {
                rootKind = Kind(value), schema = CodeObservation(Field(value, "schema"), "cerebral.slot-metrics/1"),
                identity = IdentityObservation(Field(value, "issuingInstallationId"), expected),
                executionAdmissionOpen = BooleanObservation(Field(value, "executionAdmissionOpen")),
                runtimeKind = Kind(runtime), runtimeSchema = CodeObservation(Field(runtime, "schema"), "cerebral.managed-model-slots/1"),
                processGeneration = GenerationObservation(Field(runtime, "processGenerationId")),
                runtimeEpoch = NumberObservation(Field(runtime, "runtimeEpoch")),
                state = CodeObservation(Field(runtime, "state"), "observed", "partial", "unavailable"),
                truncated = BooleanObservation(Field(runtime, "truncated")),
                resourcePausePermission = BooleanObservation(Field(runtime, "modelAdmissionOpen")),
                models = ArrayObservation(models), stateTallySampledRows = hasModels ? states.Length : (int?)null,
                present = hasModels ? states.Count(x => TextIs(x, "present")) : (int?)null,
                absent = hasModels ? states.Count(x => TextIs(x, "absent")) : (int?)null,
                unobserved = hasModels ? states.Count(x => TextIs(x, "unobserved")) : (int?)null,
                missingOrOtherState = hasModels ? states.Count(x => !TextIs(x, "present") && !TextIs(x, "absent") && !TextIs(x, "unobserved")) : (int?)null,
                stateTallyTruncated = hasModels ? Items(models) is > 64 : (bool?)null, remoteKind = Kind(Field(value, "remote"))
            };
        }

        private static object ExecutionObservation(JsonElement value, string expected) => new
        {
            rootKind = Kind(value), schema = CodeObservation(Field(value, "schema"), "cerebral.execution-metrics/1"),
            identity = IdentityObservation(Field(value, "issuingInstallationId"), expected),
            processGeneration = GenerationObservation(Field(value, "processGenerationId")),
            state = CodeObservation(Field(value, "state"), "observed", "partial"),
            partial = BooleanObservation(Field(value, "partial")), truncated = BooleanObservation(Field(value, "truncated")),
            capturedOwnershipCount = NumberObservation(Field(value, "capturedOwnershipCount")),
            unprojectableCount = NumberObservation(Field(value, "unprojectableCount")),
            ownershipChangedCount = NumberObservation(Field(value, "ownershipChangedCount")),
            localWorkingAgentCount = NumberObservation(Field(value, "localWorkingAgentCount")),
            confirmedRemoteWorkingAgentCount = NumberObservation(Field(value, "confirmedRemoteWorkingAgentCount")),
            observations = ArrayObservation(Field(value, "observations"))
        };

        private static bool ClosedAccountingGuard(JsonElement value, string expected)
        {
            var a = Field(value, "accounting");
            return Number(Field(value, "schemaVersion")) == 1 &&
                TextIs(Field(value, "workerInstallationId"), expected) && TextIs(Field(value, "controllerInstallationId"), expected) &&
                Flag(Field(value, "detailComplete")) == true && Number(Field(a, "schemaVersion")) == 1 &&
                Flag(Field(a, "admissionOpen")) == false && TextIs(Field(a, "admissionReason"), "explicit-owner-release-required") &&
                Flag(Field(a, "coverageComplete")) == true && Items(Field(a, "entries")) == 0 &&
                Number(Field(a, "localContexts")) == 0 && Number(Field(a, "logicalPreparations")) == 0 &&
                Number(Field(a, "inboundReservations")) == 0 && Number(Field(a, "queuedRequests")) == 0 &&
                Number(Field(a, "activeRequests")) == 0 && Number(Field(a, "unknownOwnership")) == 0 &&
                Field(value, "globalZero").ValueKind == JsonValueKind.Null;
        }

        private static bool ManagedSlotsGuard(JsonElement value, string expected) =>
            TextIs(Field(value, "schema"), "cerebral.slot-metrics/1") &&
            TextIs(Field(value, "issuingInstallationId"), expected) && Flag(Field(value, "executionAdmissionOpen")) == false &&
            TextIs(Field(value, "runtime", "schema"), "cerebral.managed-model-slots/1") &&
            TextIs(Field(value, "runtime", "state"), "observed") && Flag(Field(value, "runtime", "truncated")) == false &&
            // This fixture requests no resource pause. Inference remains closed through the separate ownership ledger.
            Flag(Field(value, "runtime", "modelAdmissionOpen")) == true && Items(Field(value, "runtime", "models")) == 0;

        private static bool ExecutionGuard(JsonElement value, string expected) =>
            TextIs(Field(value, "schema"), "cerebral.execution-metrics/1") &&
            TextIs(Field(value, "issuingInstallationId"), expected) && TextIs(Field(value, "state"), "observed") &&
            Flag(Field(value, "partial")) == false && Flag(Field(value, "truncated")) == false &&
            Number(Field(value, "capturedOwnershipCount")) == 0 && Items(Field(value, "observations")) == 0;

        private static bool GenerationGuard(JsonElement value) =>
            TextIs(Field(value, "schema"), "cerebral.generation-metrics/1") &&
            Flag(Field(value, "truncated")) == false && Items(Field(value, "active")) == 0 && Items(Field(value, "recent")) == 0;

        internal static int EnvironmentObservationChecksMain()
        {
            const string identity = "11111111111111111111111111111111";
            var labels = new List<string>();
            void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); labels.Add(label); Console.WriteLine("PASS: " + label); }
            JsonElement Accounting(bool coverage = true, bool detail = true, bool admission = false, int queued = 0, int unknown = 0,
                string? worker = null, bool global = false) => Element(new
                {
                    schemaVersion = 1, workerInstallationId = worker ?? identity, controllerInstallationId = identity, detailComplete = detail,
                    accounting = new { schemaVersion = 1, processGenerationId = identity, revision = 1, admissionOpen = admission,
                        admissionReason = "explicit-owner-release-required", coverageComplete = coverage, entries = Array.Empty<object>(),
                        localContexts = 0, logicalPreparations = 0, inboundReservations = 0, queuedRequests = queued, activeRequests = 0, unknownOwnership = unknown },
                    globalZero = global ? (bool?)true : null, remoteProviderTotals = "unobserved", machineTotals = "unobserved",
                    unrelatedBody = "synthetic-body-marker"
                });
            JsonElement Slots(string state = "observed", object? permission = null, bool loaded = false) => Element(new
                {
                    schema = "cerebral.slot-metrics/1", issuingInstallationId = identity, executionAdmissionOpen = false,
                    runtime = new { schema = "cerebral.managed-model-slots/1", processGenerationId = identity, runtimeEpoch = 0,
                        state, truncated = false, modelAdmissionOpen = permission ?? true,
                        models = loaded ? new[] { new { instanceState = "present", modelId = "synthetic-body-marker", path = "synthetic-body-marker" } } : [] },
                    remote = (object?)null
                });
            JsonElement Execution(bool partial = false) => Element(new { schema = "cerebral.execution-metrics/1",
                issuingInstallationId = identity, state = partial ? "partial" : "observed", partial, truncated = false,
                capturedOwnershipCount = 0, observations = Array.Empty<object>(), localWorkingAgentCount = 0,
                confirmedRemoteWorkingAgentCount = (int?)null });
            JsonElement Generation(bool active = false) => Element(new { schema = "cerebral.generation-metrics/1", truncated = false,
                active = active ? new[] { new { modelPath = "synthetic-body-marker" } } : [], recent = Array.Empty<object>() });
            try
            {
                Check(ClosedAccountingGuard(Accounting(), identity), "closed complete exact-identity accounting fixture passes");
                Check(!ClosedAccountingGuard(Accounting(coverage: false), identity), "incomplete ownership coverage refuses");
                Check(!ClosedAccountingGuard(Accounting(detail: false), identity), "truncated accounting details refuse");
                Check(!ClosedAccountingGuard(Accounting(admission: true), identity), "open inference accounting refuses");
                Check(!ClosedAccountingGuard(Accounting(queued: 1), identity), "queued ownership refuses");
                Check(!ClosedAccountingGuard(Accounting(unknown: 1), identity), "unknown ownership refuses");
                Check(!ClosedAccountingGuard(Accounting(worker: "22222222222222222222222222222222"), identity), "wrong worker identity refuses");
                Check(!ClosedAccountingGuard(Accounting(global: true), identity), "unsupported global-zero claim refuses");
                Check(!ClosedAccountingGuard(Element(new { schemaVersion = 1 }), identity), "missing accounting state refuses");
                Check(ManagedSlotsGuard(Slots(), identity), "unpaused resource permission stays distinct from closed accounting");
                Check(!ManagedSlotsGuard(Slots(permission: false), identity), "unexpected resource-pause permission refuses");
                Check(!ManagedSlotsGuard(Slots(state: "unavailable"), identity), "unavailable empty slot page is not zero");
                Check(!ManagedSlotsGuard(Slots(state: "partial"), identity), "partial empty slot page is not zero");
                Check(!ManagedSlotsGuard(Slots(loaded: true), identity), "observed loaded instance refuses");
                Check(!ManagedSlotsGuard(Slots(permission: "false"), identity), "malformed boolean permission refuses");
                Check(ExecutionGuard(Execution(), identity), "observed zero scheduler ownership fixture passes");
                Check(!ExecutionGuard(Execution(partial: true), identity), "partial scheduler state refuses");
                Check(GenerationGuard(Generation()), "empty nontruncated generation fixture passes");
                Check(!GenerationGuard(Generation(active: true)), "active generation fixture refuses");
                var selected = JsonSerializer.Serialize(new
                {
                    accounting = SelectEnvironmentObservation("models.accounting.snapshot", Accounting(), identity),
                    slots = SelectEnvironmentObservation("metrics.slots.status", Slots(loaded: true), identity),
                    generation = SelectEnvironmentObservation("metrics.generation", Generation(active: true), identity)
                });
                Check(!selected.Contains("synthetic-body-marker", StringComparison.Ordinal), "selected observations exclude raw model and unrelated bodies");
                var unavailable = JsonSerializer.SerializeToElement(SlotObservation(Element(new { runtime = new { state = "unavailable" } }), identity));
                Check(unavailable.GetProperty("models").GetProperty("count").ValueKind == JsonValueKind.Null &&
                    unavailable.GetProperty("present").ValueKind == JsonValueKind.Null && unavailable.GetProperty("unobserved").ValueKind == JsonValueKind.Null,
                    "missing model collections retain unknown counts rather than zero tallies");
                var missing = JsonSerializer.Serialize(BooleanObservation(default));
                var nil = JsonSerializer.Serialize(BooleanObservation(Element((object?)null!)));
                var malformed = JsonSerializer.Serialize(BooleanObservation(Element("false")));
                Check(missing.Contains("missing") && nil.Contains("null") && malformed.Contains("string") && missing != nil && nil != malformed,
                    "missing null and malformed scalar states remain distinct");
                RequireObservationRoom(31);
                var full = false; try { RequireObservationRoom(32); } catch (InvalidOperationException) { full = true; }
                Check(full, "environment observation collection refuses beyond its32-entry bound");
                var path = Path.Combine(Workspace, "build", "Plugins", "SettingsPresets", "personality-environment-observation-final-checks.json");
                NoLinks(path);
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var report = new { schemaVersion = 1, taskId = "10000000-0000-4000-8000-000000000002",
                    status = "passed-unit-fixtures", tier = "constructed JSON unit fixtures; no Worker/metric/provider/database/native-runtime execution",
                    assertions = labels.Count, labels, selectedSample = JsonSerializer.Deserialize<JsonElement>(selected),
                    workerCalls = 0, providerCalls = 0, databaseCalls = 0, sourcePolicyChanges = 0 };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
                output.Write(bytes); output.Flush(true);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine("FAIL: environment unit fixtures " + error.GetType().Name + ": " + error.Message); return 1; }
        }
    }
}
