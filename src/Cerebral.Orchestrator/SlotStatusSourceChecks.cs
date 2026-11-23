using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

/// <summary>Focused pure JSON/presentation checks. No window, dispatcher, endpoint, process or provider is created.</summary>
public static class SlotStatusSourceChecks
{
    private const string Owner = "11111111111111111111111111111111";
    private const string Generation = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Accounting = "33333333333333333333333333333333";
    private const string Runtime = "44444444444444444444444444444444";
    private const string Instance = "55555555555555555555555555555555";
    private const string Scheduler = "66666666666666666666666666666666";
    private const string Stamp = "2026-10-15T20:30:00Z";

    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Slot status: " + label);
            checks++;
        }
        static JsonElement Element(JsonObject value) => JsonSerializer.SerializeToElement(value);
        static JsonObject Page(JsonObject value) => (JsonObject)value["runtime"]!;
        static JsonArray Rows(JsonObject value) => (JsonArray)value["rows"]!;
        static JsonObject Active(JsonObject value) => (JsonObject)Rows(value)[1]!;
        static InferenceOwnershipPresentation Ownership(JsonObject value) =>
            InferenceOwnershipProjection.ReadSample(Element(value), Owner).Presentation;
        static string Readable(InferenceOwnershipPresentation value) =>
            value.Details[..value.Details.IndexOf("Captured provenance and source ages:", StringComparison.Ordinal)];
        static OrchestratorWindow.ManagedSlotPresentation Allocator(JsonObject value) =>
            OrchestratorWindow.ProjectManagedSlotStatus(Element(value), Owner);
        static JsonObject Changed(Action<JsonObject> change)
        {
            var value = Fixture(); change(value); return value;
        }
        void RejectOwnership(JsonObject value, string label)
        {
            try { _ = Ownership(value); }
            catch (InvalidDataException) { Check(true, label); return; }
            Check(false, label);
        }

        var allocation = Allocator(Slots());
        Check(allocation.OpenSlots == "3 free / 6 allocated / 2 quarantined" &&
            allocation.Details.Contains("available 3; allocated 6; quarantined 2", StringComparison.Ordinal),
            "free, allocated and quarantined are all primary observations");
        Check(!allocation.OpenSlots.Contains("generating", StringComparison.OrdinalIgnoreCase) &&
            allocation.Details.Contains("Allocated minus free is not a generating count", StringComparison.Ordinal),
            "allocator subtraction is never presented as generation");
        var exhausted = Slots(); FirstSlot(exhausted)["managedAvailableSlots"] = 0;
        Check(Allocator(exhausted).OpenSlots == "0 free / 6 allocated / 2 quarantined",
            "known zero availability retains allocation and quarantine");
        var partial = Slots(); Page(partial)["state"] = "partial"; Page(partial)["truncated"] = true;
        Check(Allocator(partial).OpenSlots == SlotStatusPresentation.UnknownAllocatorCaption + " · partial" &&
            Allocator(partial).Details.Contains("Valid observed subset: available 3", StringComparison.Ordinal),
            "partial subsets do not become complete totals");
        var incomplete = Slots(); FirstSlot(incomplete)["managedAvailableSlots"] = null;
        Check(Allocator(incomplete).OpenSlots == SlotStatusPresentation.UnknownAllocatorCaption + " · partial",
            "missing counters are unknown rather than free or zero");
        var empty = Slots(); Page(empty)["models"] = new JsonArray();
        Check(Allocator(empty).OpenSlots == "0 free / 0 allocated / 0 quarantined",
            "complete empty allocation is a measured zero without inventing occupancy");
        var unobserved = Slots(); Page(unobserved)["state"] = "partial";
        Page(unobserved)["models"] = new JsonArray(new JsonObject
        {
            ["modelId"] = "unobserved", ["instanceId"] = Instance, ["instanceState"] = "unobserved",
            ["allocatedSlots"] = null, ["managedAvailableSlots"] = null, ["managedQuarantinedSlots"] = null
        });
        Check(Allocator(unobserved).OpenSlots.StartsWith(SlotStatusPresentation.UnknownAllocatorCaption, StringComparison.Ordinal),
            "unobserved instances do not fabricate free allocation");
        var invalid = Slots(); FirstSlot(invalid)["managedAvailableSlots"] = 7;
        Check(Allocator(invalid).OpenSlots == SlotStatusPresentation.UnknownAllocatorCaption,
            "contradictory allocator counts retain strict refusal");
        foreach (var mutation in new Action<JsonObject>[]
        {
            value => value["issuingInstallationId"] = new string('9', 32),
            value => value["schema"] = "cerebral.slot-metrics/2",
            value => FirstSlot(value)["inventedOccupants"] = 1
        })
        {
            var value = Slots(); mutation(value);
            Check(Allocator(value).OpenSlots == SlotStatusPresentation.UnknownAllocatorCaption,
                "wrong owner, schema and extra row fields are refused");
        }
        var held = Slots(); held["executionAdmissionOpen"] = false;
        Page(held)["modelAdmissionOpen"] = false;
        Check(Allocator(held).OpenSlots == allocation.OpenSlots + " · workflow closed · model disabled",
            "admission remains distinct from the same observed free counters");

        var fixture = Fixture(); var original = fixture.ToJsonString();
        var ownership = Ownership(fixture); var readable = Readable(ownership);
        Check(readable.StartsWith("Observed slot ownership", StringComparison.Ordinal) &&
            readable.IndexOf("Slot 0", StringComparison.Ordinal) <
            readable.IndexOf("Accepted local work:", StringComparison.Ordinal),
            "readable occupants precede accepted totals and low-level provenance");
        Check(readable.Contains("agent fixture-profile@3", StringComparison.Ordinal) &&
            readable.Contains("run run-active-b", StringComparison.Ordinal) &&
            readable.Contains("observed phase generating", StringComparison.Ordinal) &&
            readable.Contains("instance " + Instance[..24], StringComparison.Ordinal),
            "current occupant keeps instance-slot, profile, run and observed phase");
        Check(!readable.Contains("run-released-a", StringComparison.Ordinal) &&
            readable.Contains("lease " + new string('b', 24), StringComparison.Ordinal),
            "confirmed-release history cannot replace the current lease on a reused slot");
        Check(fixture.ToJsonString() == original,
            "formatting leaves its complete validated source unchanged");
        Check(ownership.Summary == "Model work: 1 profiles · 2 executions · 1 observed leases" &&
            readable.Contains("not slot capacity or proof of generation", StringComparison.Ordinal),
            "existing accepted-work facts remain secondary and separate from slot capacity");

        var twoInstances = Fixture();
        var second = Row("active-c", new string('c', 32)); second["instanceId"] = new string('7', 32);
        Rows(twoInstances).Clear(); Rows(twoInstances).Add(Row("active-b", new string('b', 32))); Rows(twoInstances).Add(second);
        twoInstances["retainedCurrentRows"] = 2;
        var secondModel = Model(); secondModel["instanceId"] = new string('7', 32);
        ((JsonArray)twoInstances["models"]!).Add(secondModel);
        var instanceDetails = Readable(Ownership(twoInstances));
        Check(instanceDetails.Contains("run run-active-b", StringComparison.Ordinal) &&
            instanceDetails.Contains("run run-active-c", StringComparison.Ordinal) &&
            !instanceDetails.Contains("conflicting current", StringComparison.Ordinal),
            "slot zero on distinct instances is not one occupant");
        var conflicting = Changed(value =>
        {
            Rows(value).Add(Row("active-c", new string('c', 32))); value["retainedCurrentRows"] = 2;
        });
        Check(Readable(Ownership(conflicting)).Contains("conflicting current lease/attempt observations (2)", StringComparison.Ordinal),
            "multiple current attempts on one instance-slot remain ambiguous");

        var terminal = Fixture(); var terminalRow = Row("terminal", new string('d', 32), released: true);
        terminalRow["phase"] = "terminal"; terminalRow["release"] = "unobserved";
        terminalRow["releaseProof"] = null; terminalRow["releasedAt"] = null;
        terminal["rows"] = new JsonArray(terminalRow);
        Check(Readable(Ownership(terminal)).Contains("observed phase terminal", StringComparison.Ordinal),
            "terminal result does not remove unreleased ownership");
        var uncertain = Ownership(Changed(value =>
        {
            Active(value)["phase"] = "quarantined"; Active(value)["uncertaintyCode"] = "ownership-unresolved";
        }));
        Check(Readable(uncertain).Contains("ownership uncertain", StringComparison.Ordinal) &&
            Readable(uncertain).Contains("observed phase quarantined", StringComparison.Ordinal),
            "unknown or quarantined ownership stays visible rather than free");
        var unmapped = Ownership(Changed(value => Active(value)["instanceId"] = null));
        Check(Readable(unmapped).Contains("lack a complete instance/slot coordinate", StringComparison.Ordinal) &&
            !Readable(unmapped).Contains("observed phase generating", StringComparison.Ordinal),
            "missing instance identity is not a mapped occupant");
        var stale = Ownership(Changed(value => Active(value)["progressSource"] = Source("stale", Accounting, null, 2001)));
        Check(Readable(stale).Contains("stale, age 2001 ms", StringComparison.Ordinal) &&
            stale.Details.Contains("Allocator source: observed", StringComparison.Ordinal) &&
            stale.Details.Contains("age 20 ms", StringComparison.Ordinal),
            "progress age never refreshes or replaces the independent allocator age");
        var zeroOwners = Ownership(Changed(value =>
        {
            value["rows"] = new JsonArray(); value["retainedCurrentRows"] = 0;
            var counts = (JsonObject)value["counts"]!;
            counts["observedKnownWorkingProfileIdentities"] = 0; counts["completeWorkingProfileIdentities"] = 0;
            counts["observedKnownExecutions"] = 0; counts["completeExecutions"] = 0;
        }));
        Check(Readable(zeroOwners).Contains("occupancy is not inferred", StringComparison.Ordinal) &&
            Readable(zeroOwners).Contains("Missing ownership rows do not mean free slots", StringComparison.Ordinal),
            "empty ownership rows do not classify an allocator slot as free");
        var partialOwnership = Ownership(Changed(value =>
        {
            value["partial"] = true; value["state"] = "partial"; value["reasons"] = new JsonArray("scheduler-unobserved");
            var counts = (JsonObject)value["counts"]!;
            counts["completeWorkingProfileIdentities"] = null; counts["completeExecutions"] = null;
            ((JsonObject)counts["source"]!)["state"] = "partial";
            ((JsonObject)counts["source"]!)["reason"] = "required-coverage-unavailable";
        }));
        Check(Readable(partialOwnership).Contains("Ownership coverage is partial", StringComparison.Ordinal),
            "known occupants never upgrade partial source coverage");
        RejectOwnership(Changed(value => Active(value)["modality"] = "image"),
            "unsupported modality fields cannot become display policy");
        RejectOwnership(Changed(value => Rows(value).Add(Active(value).DeepClone())),
            "duplicate current row and attempt identities still refuse");
        Check(Readable(ownership).Contains("No image/text modality, context count, KV residency", StringComparison.Ordinal),
            "presentation explicitly declines inferred modality, context counts and KV residency");
        Check(ownership.Details.Contains(
            "Allocator and ownership pages are separate observations. They are not combined into an atomic free/occupied map.",
            StringComparison.Ordinal),
            "presentation keeps allocator and ownership observations separate from an atomic free/occupied map");

        var stamp = DateTimeOffset.Parse(Stamp, CultureInfo.InvariantCulture);
        var large = Enumerable.Range(0, 128).Select(index => new SlotStatusOccupant(
            new string('m', 256), index.ToString("D32", CultureInfo.InvariantCulture), 0,
            new string('l', 256), new string('a', 256), new string('p', 256), 3,
            new string('r', 256), "preparing", false, "unknown", "unavailable", null, stamp)).ToArray();
        var bounded = SlotStatusPresentation.OccupantLines(large, 0, true, stamp);
        Check(bounded.Sum(line => line.Length + 1) < 14000 &&
            bounded.Any(line => line.Contains("omitted at the display bound", StringComparison.Ordinal)),
            "large readable lists remain bounded and label omitted occupants");

        var metrics = new DesktopStatusMetrics(new ResourceInfo(10, 100, []) { SystemTotalMb = 200 },
            7, 4, 7, new ModelUsageTotals(20, 10), stamp);
        var work = new OrchestratorWindow.SystemWorkPresentation("2 local / — remote", allocation.OpenSlots, allocation.Details);
        var resources = OrchestratorWindow.ProjectSystemResources(metrics, work);
        Check(resources.Values.Count == 6 && resources.Values[4] == allocation.OpenSlots && resources.Values[5] == "4",
            "primary resource groups use Slots then Queued with all allocator counters");
        Check(resources.Details.Contains("Accepted agents (scheduler observation): 2 local", StringComparison.Ordinal) &&
            resources.Details.Contains("This is not slot capacity", StringComparison.Ordinal),
            "accepted-agent counts move to readable details");
        var differentWork = OrchestratorWindow.ProjectSystemResources(metrics, work with { Agents = "3 local / — remote" });
        Check(resources.Values.SequenceEqual(differentWork.Values),
            "changing accepted agents does not change displayed slot capacity");
        Check(OrchestratorWindow.ProjectSystemResources(metrics).Values[4] == SlotStatusPresentation.UnknownAllocatorCaption,
            "missing observations never reuse scheduler assignments as free slots");
        return checks;
    }

    private static JsonObject Slots() => new()
    {
        ["schema"] = "cerebral.slot-metrics/1", ["issuingInstallationId"] = Owner,
        ["executionAdmissionOpen"] = true, ["remote"] = null,
        ["scope"] = "local-managed-slot-observation; not hardware maximum or atomic admission",
        ["runtime"] = new JsonObject
        {
            ["schema"] = "cerebral.managed-model-slots/1", ["sampledAt"] = Stamp,
            ["processGenerationId"] = Runtime, ["runtimeEpoch"] = 7, ["state"] = "observed",
            ["truncated"] = false, ["modelAdmissionOpen"] = true,
            ["models"] = new JsonArray(new JsonObject
            {
                ["modelId"] = "fixture-model", ["instanceId"] = Instance, ["instanceState"] = "present",
                ["allocatedSlots"] = 6, ["managedAvailableSlots"] = 3, ["managedQuarantinedSlots"] = 2
            })
        }
    };
    private static JsonObject FirstSlot(JsonObject value) => (JsonObject)((JsonArray)((JsonObject)value["runtime"]!)["models"]!)[0]!;

    private static JsonObject Fixture() => new()
    {
        ["schema"] = "cerebral.inference-ownership/1", ["issuingInstallationId"] = Owner,
        ["ownerConnectionRegistrationFence"] = null, ["ownerConnectionRegistrationFenceState"] = "unavailable",
        ["ownerConnectionRegistrationFenceReason"] = "owner-connection-registration-fence-not-captured",
        ["observerGenerationId"] = Generation, ["revision"] = 10, ["sampledAt"] = Stamp,
        ["accountingGenerationId"] = Accounting, ["accountingCoverageComplete"] = true,
        ["state"] = "observed", ["partial"] = false, ["truncated"] = false,
        ["maximumRows"] = 128, ["retainedCurrentRows"] = 1, ["reasons"] = new JsonArray(),
        ["counts"] = new JsonObject
        {
            ["observedKnownWorkingProfileIdentities"] = 1, ["completeWorkingProfileIdentities"] = 1,
            ["observedKnownExecutions"] = 2, ["completeExecutions"] = 2, ["completeContextCount"] = null,
            ["source"] = Source("observed", Scheduler, null, 30)
        },
        ["modelCoverage"] = Source("observed", Runtime, 7, 40),
        ["models"] = new JsonArray(Model()),
        ["rows"] = new JsonArray(Row("released-a", new string('a', 32), released: true), Row("active-b", new string('b', 32))),
        ["remoteState"] = "unsupported", ["eventDeliveryState"] = "available",
        ["executionAdmissionOpen"] = null, ["modelAdmissionOpen"] = null,
        ["admissionSource"] = Unavailable("combined-admission-policy-not-captured"),
        ["semantics"] = "Issuing local owner only. Profiles, accepted executions, exact leases and reusable native slots are distinct. Terminal is not release or task/build success."
    };

    private static JsonObject Model() => new()
    {
        ["modelId"] = "fixture-model", ["instanceId"] = Instance, ["runtimeGenerationId"] = Runtime,
        ["runtimeEpoch"] = 7, ["state"] = "startup-captured", ["runningLaunchSlotMaximum"] = 2,
        ["effectiveContextTokens"] = 8192, ["launchBindingSha256"] = new string('a', 64),
        ["launchSource"] = Source("captured", Runtime, 6, 50),
        ["managedAllocatedSlots"] = 2, ["managedAvailableSlots"] = 1, ["managedQuarantinedSlots"] = 0,
        ["allocatorCoverageComplete"] = true, ["allocatorSource"] = Source("observed", Generation, 9, 20),
        ["providerObservedCapacity"] = null, ["providerCapacitySource"] = Unavailable("native-slot-capacity-not-captured"),
        ["providerObservedBusySlots"] = null, ["providerActivitySource"] = Unavailable("provider-activity-unobserved")
    };

    private static JsonObject Row(string id, string lease, bool released = false) => new()
    {
        ["rowId"] = Accounting + ":" + id, ["accountingEntryId"] = id,
        ["accountingGenerationId"] = Accounting, ["accountingRevision"] = 8,
        ["originInstallationId"] = Owner, ["kind"] = "LocalPreparation", ["modelId"] = "fixture-model",
        ["instanceId"] = Instance, ["correlationId"] = "correlation-" + id, ["attemptId"] = "attempt-" + id,
        ["purpose"] = "task-generation", ["execution"] = new JsonObject
        {
            ["executionId"] = "execution-" + id, ["profileId"] = "fixture-profile", ["profileRevision"] = 3,
            ["conversationId"] = "fixture-conversation", ["requestId"] = "request-" + id,
            ["runId"] = "run-" + id, ["effectId"] = "effect-" + id
        },
        ["executionAttribution"] = "local-host-invocation", ["accountingLeaseId"] = null,
        ["reservationId"] = null, ["leaseId"] = lease, ["nativeSlotId"] = 0,
        ["phase"] = released ? "released" : "generating", ["cancellationRequested"] = false,
        ["uncertaintyCode"] = null,
        ["budget"] = new JsonObject
        {
            ["inputTokens"] = 900, ["effectiveContext"] = 8192, ["desiredResponse"] = 1024,
            ["response"] = 1024, ["reasoningReserve"] = 128, ["safetyReserve"] = 128,
            ["countBasis"] = "exact-native-template-token-ids"
        },
        ["terminal"] = released ? new JsonObject
        {
            ["finish"] = "stop", ["cancellationConfirmed"] = false, ["providerError"] = false,
            ["evaluatedTokens"] = 900, ["generatedTokens"] = 25,
            ["generatedTokenScope"] = "provider-unspecified", ["reasoningTokens"] = null, ["at"] = Stamp
        } : null,
        ["cache"] = null, ["progress"] = released ? "unknown" : "output",
        ["processedInputTokens"] = released ? null : JsonValue.Create(900),
        ["outputTokens"] = released ? null : JsonValue.Create(25),
        ["progressSource"] = released ? Unavailable("provider-progress-unobserved") : Source("observed", Accounting, null, 10),
        ["release"] = released ? "confirmed" : "unobserved",
        ["releaseProof"] = released ? "accounting-owner-confirmed-retirement" : null,
        ["releasedAt"] = released ? Stamp : null, ["observedAt"] = Stamp
    };

    private static JsonObject Source(string state, string generation, long? revision, long age) => new()
    {
        ["state"] = state, ["reason"] = null,
        ["observedAt"] = DateTimeOffset.Parse(Stamp, System.Globalization.CultureInfo.InvariantCulture)
            .AddMilliseconds(-age).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["generationId"] = generation, ["revision"] = revision, ["ageMilliseconds"] = age
    };

    private static JsonObject Unavailable(string reason) => new()
    {
        ["state"] = "unavailable", ["reason"] = reason, ["observedAt"] = null,
        ["generationId"] = null, ["revision"] = null, ["ageMilliseconds"] = null
    };
}
