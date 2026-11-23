using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>Pure bounded JSON projection fixtures; no window, connection, provider or runtime observation.</summary>
    public static int CheckManagedSlotPresentation()
    {
        const string owner = "11111111111111111111111111111111";
        var checks = 0;
        void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); checks++; }
        static JsonObject Runtime(JsonObject value) => (JsonObject)value["runtime"]!;
        static JsonArray Rows(JsonObject value) => (JsonArray)Runtime(value)["models"]!;
        static JsonObject First(JsonObject value) => (JsonObject)Rows(value)[0]!;
        static JsonElement Element(JsonObject value) => JsonSerializer.SerializeToElement(value);
        ManagedSlotPresentation Show(JsonObject value) => ProjectManagedSlotStatus(Element(value), owner);
        JsonObject Changed(Action<JsonObject> edit)
        {
            var value = ManagedSlotFixture(); edit(value); return value;
        }
        void Reject(JsonElement value, string reason, string expected = owner) =>
            Check(ProjectManagedSlotStatus(value, expected) == UnavailableManagedSlots(), reason);
        void RejectChange(Action<JsonObject> edit, string reason) => Reject(Element(Changed(edit)), reason);

        var fixture = ManagedSlotFixture();
        var original = fixture.ToJsonString();
        var observed = Show(fixture);
        Check(observed.OpenSlots == "3 free / 6 total / 2 quarantined" &&
            observed.Details.Contains("available 3; allocated 6; quarantined 2", StringComparison.Ordinal),
            "Observed allocator availability, allocation and quarantine remain separate measured counts.");
        Check(fixture.ToJsonString() == original,
            "Projecting a sample does not mutate its source or carry state between observations.");
        Check(observed.Details.Contains("Local workflow admission: open. Local model admission: open.", StringComparison.Ordinal) &&
            observed.Details.Contains("not an atomic admission fence", StringComparison.Ordinal) &&
            observed.Details.Contains("Remote slot availability is unobserved.", StringComparison.Ordinal),
            "Free allocator permits are qualified independently of admission policy, readiness and remote capacity.");

        var held = Show(Changed(value => value["executionAdmissionOpen"] = false));
        Check(held.OpenSlots == "3 free / 6 total / 2 quarantined · workflow closed" &&
            held.Details.Contains("Local workflow admission: closed. Local model admission: open.", StringComparison.Ordinal),
            "A workflow admission hold preserves measured free slots while visibly closing work admission.");
        var disabled = Show(Changed(value => Runtime(value)["modelAdmissionOpen"] = false));
        Check(disabled.OpenSlots == "3 free / 6 total / 2 quarantined · model disabled" &&
            disabled.Details.Contains("Local workflow admission: open. Local model admission: disabled.", StringComparison.Ordinal),
            "Disabled local model admission is distinct from workflow policy and from zero allocator availability.");
        var both = Show(Changed(value => { value["executionAdmissionOpen"] = false; Runtime(value)["modelAdmissionOpen"] = false; }));
        Check(both.OpenSlots.EndsWith("workflow closed · model disabled", StringComparison.Ordinal),
            "Both closed policies remain visible when managed slots are still free.");
        var unknownPolicy = Show(Changed(value => Runtime(value)["modelAdmissionOpen"] = null));
        Check(unknownPolicy.OpenSlots == "3 free / 6 total / 2 quarantined · model policy —" &&
            unknownPolicy.Details.Contains("Local model admission: unobserved.", StringComparison.Ordinal),
            "Null model policy does not become an open or disabled policy or erase valid allocator measurements.");

        var zero = Show(Changed(value => Runtime(value)["models"] = new JsonArray(
            ManagedSlotFixtureRow("zero", "cccccccccccccccccccccccccccccccc", "present", 2, 0, 2))));
        Check(zero.OpenSlots == "0 free / 2 total / 2 quarantined" &&
            zero.Details.Contains("available 0; allocated 2; quarantined 2", StringComparison.Ordinal),
            "An observed exhausted allocator is a real zero and retains its quarantine count.");
        var empty = Show(Changed(value => Runtime(value)["models"] = new JsonArray()));
        Check(empty.LoadedModels == 0, "A complete empty native-instance observation proves no local model is loaded.");
        Check(empty.OpenSlots == "0 free / 0 total / 0 quarantined" &&
            empty.Details.Contains("available 0; allocated 0; quarantined 0", StringComparison.Ordinal),
            "A complete empty managed-instance map can report zero managed allocation without configured capacity.");
        var absent = Show(Changed(value => Runtime(value)["models"] = new JsonArray(
            ManagedSlotFixtureRow("absent", "cccccccccccccccccccccccccccccccc", "absent", null, null, null))));
        Check(absent.OpenSlots == "0 free / 0 total / 0 quarantined" &&
            absent.Details.Contains("0 present, 1 absent, 0 unobserved", StringComparison.Ordinal),
            "A confirmed absent managed instance contributes no allocation and remains explicitly absent.");

        var partial = Show(Changed(value => { Runtime(value)["state"] = "partial"; Runtime(value)["truncated"] = true; }));
        Check(partial.LoadedModels is null, "A partial allocator observation cannot claim zero loaded models.");
        Check(partial.OpenSlots == SlotStatusPresentation.UnknownAllocatorCaption + " · partial" &&
            partial.Details.Contains("Complete managed local totals remain unknown.", StringComparison.Ordinal) &&
            partial.Details.Contains("Valid observed subset: available 3; allocated 6; quarantined 2", StringComparison.Ordinal),
            "Truncated observations expose only labeled subset counters and leave complete availability unknown.");
        var unobserved = Show(Changed(value =>
        {
            Runtime(value)["state"] = "partial";
            Rows(value).Add(ManagedSlotFixtureRow("unknown", "cccccccccccccccccccccccccccccccc", "unobserved", null, null, null));
        }));
        Check(unobserved.OpenSlots == SlotStatusPresentation.UnknownAllocatorCaption + " · partial" &&
            unobserved.Details.Contains("1 unobserved", StringComparison.Ordinal) &&
            unobserved.Details.Contains("available 3; allocated 6; quarantined 2", StringComparison.Ordinal),
            "An unobserved instance prevents a total even when other allocator rows have complete measurements.");
        var partialZero = Show(Changed(value =>
        {
            Runtime(value)["state"] = "partial"; Runtime(value)["truncated"] = true;
            Runtime(value)["models"] = new JsonArray(ManagedSlotFixtureRow("zero", "cccccccccccccccccccccccccccccccc", "present", 2, 0, 2));
        }));
        Check(partialZero.OpenSlots == SlotStatusPresentation.UnknownAllocatorCaption + " · partial" &&
            partialZero.Details.Contains("Valid observed subset: available 0", StringComparison.Ordinal),
            "Zero in a valid observed subset does not fabricate a complete zero for a partial page.");

        var unavailable = Show(Changed(value =>
        {
            Runtime(value)["state"] = "unavailable"; Runtime(value)["modelAdmissionOpen"] = null;
            Runtime(value)["models"] = new JsonArray(); value["executionAdmissionOpen"] = false;
        }));
        Check(unavailable.OpenSlots == SlotStatusPresentation.UnknownAllocatorCaption + " · unavailable · workflow closed · model policy —" &&
            unavailable.Details.Contains("allocator observations are unavailable", StringComparison.Ordinal) &&
            !unavailable.Details.Contains("available 0", StringComparison.Ordinal),
            "An unavailable runtime can retain a verified workflow policy without inventing zero counters.");
        foreach (var field in new[] { "allocatedSlots", "managedAvailableSlots", "managedQuarantinedSlots" })
        {
            var missing = Show(Changed(value => First(value)[field] = null));
            Check(missing.OpenSlots == SlotStatusPresentation.UnknownAllocatorCaption + " · partial" &&
                missing.Details.Contains("1 present instances have incomplete counters", StringComparison.Ordinal) &&
                missing.Details.Contains("across 1 present instances", StringComparison.Ordinal),
                "A null " + field + " keeps complete totals unknown instead of being filled or clamped.");
        }
        var noCounters = Show(Changed(value =>
        {
            foreach (var row in Rows(value).OfType<JsonObject>())
                foreach (var field in new[] { "allocatedSlots", "managedAvailableSlots", "managedQuarantinedSlots" }) row[field] = null;
        }));
        Check(noCounters.OpenSlots == SlotStatusPresentation.UnknownAllocatorCaption + " · partial" &&
            noCounters.Details.Contains("no complete present-instance counter observation", StringComparison.Ordinal) &&
            !noCounters.Details.Contains("available 0", StringComparison.Ordinal),
            "Entirely missing counters remain unavailable rather than appearing as an observed empty subset.");

        RejectChange(value => { First(value)["managedAvailableSlots"] = 3; First(value)["managedQuarantinedSlots"] = 2; },
            "Individually sampled available plus quarantined counts cannot exceed allocation and must not be clamped.");
        RejectChange(value => First(value)["managedQuarantinedSlots"] = 5,
            "Quarantine cannot exceed the same instance's observed allocation.");
        foreach (var field in new[] { "allocatedSlots", "managedAvailableSlots", "managedQuarantinedSlots" })
        {
            RejectChange(value => First(value)[field] = -1, "Negative " + field + " cannot enter numeric presentation.");
            RejectChange(value => First(value)[field] = 17, "Out-of-contract " + field + " cannot imply configured or hardware capacity.");
        }
        RejectChange(value => First(value)["allocatedSlots"] = 0, "A present pool cannot claim zero allocated capacity.");
        RejectChange(value => First(value)["managedAvailableSlots"] = 1.5, "Fractional slot counters are invalid measurements.");
        RejectChange(value => First(value)["managedAvailableSlots"] = "2", "String counters are not numeric observations.");
        RejectChange(value => { First(value)["instanceState"] = "absent"; },
            "Absent instances cannot retain present-instance allocator counters.");
        RejectChange(value => First(value)["instanceState"] = "unobserved",
            "Unobserved instances cannot publish counters as if they were confirmed.");
        RejectChange(value => First(value)["instanceState"] = "ready",
            "A provider-ready label cannot substitute for the managed instance-state contract.");
        RejectChange(value => ((JsonObject)Rows(value)[1]!)["modelId"] = "alpha",
            "Duplicate model IDs cannot double-count allocator observations.");
        RejectChange(value => ((JsonObject)Rows(value)[1]!)["instanceId"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            "One instance cannot be counted twice, including differently cased UUID text.");
        RejectChange(value => First(value)["instanceId"] = Guid.Empty.ToString("N"),
            "Empty instance identity is not a valid observed generation.");
        RejectChange(value => First(value)["instanceId"] = "not-an-instance", "Malformed instance identity is rejected.");
        RejectChange(value => First(value)["modelId"] = "private model body",
            "Model identity is bounded identifier text, not an arbitrary body.");
        RejectChange(value => First(value)["modelId"] = new string('a', 101), "Overlong model IDs are rejected.");

        RejectChange(value => value["issuingInstallationId"] = "33333333333333333333333333333333",
            "Another Worker's counters cannot enter the verified owner's status bar.");
        Reject(Element(fixture), "A missing verified owner cannot be replaced by the incoming sample.", "");
        Reject(Element(fixture), "An empty owner UUID cannot bind a sample.", Guid.Empty.ToString("N"));
        RejectChange(value => value["schema"] = "cerebral.slot-metrics/2", "Unknown envelope versions remain unavailable.");
        RejectChange(value => Runtime(value)["schema"] = "cerebral.managed-model-slots/2", "Unknown runtime schema versions remain unavailable.");
        RejectChange(value => value["scope"] = "hardware maximum", "Hardware or all-machine capacity cannot impersonate managed-local scope.");
        RejectChange(value => value["remote"] = new JsonObject { ["available"] = 8 },
            "Unreviewed remote counters are not accepted as a known remote total.");
        RejectChange(value => value["executionAdmissionOpen"] = null, "Missing workflow admission policy is not assumed open.");
        RejectChange(value => Runtime(value)["modelAdmissionOpen"] = "true", "Model policy must be a Boolean or explicit null.");
        RejectChange(value => Runtime(value)["processGenerationId"] = Guid.Empty.ToString("N"), "Empty runtime generation is rejected.");
        RejectChange(value => Runtime(value)["processGenerationId"] = "current", "A runtime-generation label is not UUID provenance.");
        RejectChange(value => Runtime(value)["runtimeEpoch"] = -1, "Negative runtime epochs cannot bind observations.");
        RejectChange(value => Runtime(value)["runtimeEpoch"] = 1.5, "Fractional runtime epochs are invalid.");
        foreach (var stamp in new[] { "invalid-date", "2026-10-12", "2026-10-12T21:00:00", "0001-01-01T00:00:00Z", "2026-13-12T21:00:00Z" })
            RejectChange(value => Runtime(value)["sampledAt"] = stamp, "Malformed or missing UTC timestamp provenance is rejected: " + stamp);
        RejectChange(value => Runtime(value)["state"] = "ready", "Unknown sample state cannot imply availability.");
        RejectChange(value => Runtime(value)["truncated"] = true, "An observed-complete page cannot also declare truncation.");
        RejectChange(value =>
        {
            Runtime(value)["models"] = new JsonArray(ManagedSlotFixtureRow("unknown", "cccccccccccccccccccccccccccccccc", "unobserved", null, null, null));
        }, "An observed-complete page cannot include an unobserved instance.");
        RejectChange(value => Runtime(value)["state"] = "unavailable", "Unavailable runtime pages cannot carry observed rows.");
        RejectChange(value =>
        {
            Runtime(value)["state"] = "unavailable"; Runtime(value)["models"] = new JsonArray();
        }, "Unavailable runtime pages cannot claim observed model admission.");
        RejectChange(value => Runtime(value)["models"] = new JsonObject(), "Model rows must be an explicitly bounded array.");
        RejectChange(value => First(value).Remove("managedQuarantinedSlots"), "Required counters cannot silently disappear from the contract.");

        const string privateMarker = "fixture-private-body-and-api-key";
        foreach (var area in new[] { "envelope", "runtime", "row" })
        {
            var value = Changed(root =>
            {
                var target = area == "envelope" ? root : area == "runtime" ? Runtime(root) : First(root);
                target["privateBody"] = new JsonObject { ["apiKey"] = privateMarker };
            });
            var projected = Show(value);
            Check(projected == UnavailableManagedSlots() && !projected.Details.Contains(privateMarker, StringComparison.Ordinal),
                "Unknown " + area + " fields reject the sample without echoing private bodies or credentials.");
        }
        RejectChange(value => First(value)["busy"] = 99, "Busy counters cannot substitute for actual managed availability.");
        RejectChange(value => First(value)["parallelRequests"] = 16, "Configured concurrency cannot enter the measured allocator contract.");
        static JsonElement Raw(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
        foreach (var token in new[] { "\"schema\":\"cerebral.slot-metrics/1\"", "\"runtimeEpoch\":7", "\"modelId\":\"alpha\"" })
            Reject(Raw(original.Replace(token, token + "," + token, StringComparison.Ordinal)),
                "Duplicate JSON fields at each contract layer are rejected: " + token);
        Reject(Raw(original.Insert(1, new string(' ', MaximumManagedSlotPayloadCharacters))),
            "An oversized raw sample is rejected even when its decoded fields appear valid.");
        Reject(default, "An undefined JsonElement remains unavailable without throwing.");
        Reject(Raw("null"), "A null wrapper cannot become an empty observed allocator.");
        Reject(Raw("[]"), "A non-object wrapper is rejected.");

        JsonArray Many(int count) => new(Enumerable.Range(0, count).Select(index => (JsonNode)
            ManagedSlotFixtureRow("model-" + index, (index + 1).ToString("x32", CultureInfo.InvariantCulture), "present", 4, 2, 1)).ToArray());
        var bounded = Show(Changed(value => Runtime(value)["models"] = Many(MaximumManagedSlotRows)));
        Check(bounded.OpenSlots == "512 free / 1024 total / 256 quarantined" &&
            bounded.Details.Contains("allocated 1024; quarantined 256", StringComparison.Ordinal) && bounded.Details.Length < 2048,
            "The maximum admitted row page retains bounded aggregate details and actual counters without model bodies.");
        RejectChange(value => Runtime(value)["models"] = Many(MaximumManagedSlotRows + 1),
            "A page beyond the row bound remains unavailable instead of summing or truncating it.");
        var maximumPool = Show(Changed(value => Runtime(value)["models"] = new JsonArray(
            ManagedSlotFixtureRow("maximum", "cccccccccccccccccccccccccccccccc", "present", 16, 0, 16))));
        Check(maximumPool.OpenSlots == "0 free / 16 total / 16 quarantined" && maximumPool.Details.Contains("quarantined 16", StringComparison.Ordinal),
            "The allocator's admitted capacity boundary can be fully quarantined without claiming busy agents or free slots.");
        return checks;
    }

    private static JsonObject ManagedSlotFixture() => new()
    {
        ["schema"] = "cerebral.slot-metrics/1", ["issuingInstallationId"] = "11111111111111111111111111111111",
        ["executionAdmissionOpen"] = true,
        ["runtime"] = new JsonObject
        {
            ["schema"] = "cerebral.managed-model-slots/1", ["sampledAt"] = "2026-10-12T21:00:00.0000000+00:00",
            ["processGenerationId"] = "22222222222222222222222222222222", ["runtimeEpoch"] = 7,
            ["state"] = "observed", ["truncated"] = false, ["modelAdmissionOpen"] = true,
            ["models"] = new JsonArray(
                ManagedSlotFixtureRow("alpha", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "present", 4, 2, 1),
                ManagedSlotFixtureRow("beta", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "present", 2, 1, 1))
        },
        ["remote"] = null, ["scope"] = ManagedSlotScope
    };

    private static JsonObject ManagedSlotFixtureRow(string model, string instance, string state, int? allocated, int? available, int? quarantined) => new()
    {
        ["modelId"] = model, ["instanceId"] = instance, ["instanceState"] = state,
        ["allocatedSlots"] = allocated, ["managedAvailableSlots"] = available, ["managedQuarantinedSlots"] = quarantined
    };
}
