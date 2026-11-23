using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Conversation;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    public static Task RunQaPresentationGuardOnlyAsync()
    {
        var checks = 0;
        VerifyQaPresentationGuard((condition, message) =>
        {
            if (!condition) throw new InvalidOperationException("QA presentation guard: " + message);
            checks++;
        });
        Console.WriteLine($"Conversation automatic QA presentation guard fixture checks passed: {checks}");
        return Task.CompletedTask;
    }

    internal static void VerifyQaPresentationGuard(Action<bool, string> check)
    {
        const string reviewed = "11111111111111111111111111111111", pending = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string mount = "note.main";
        var oldFingerprint = new string('3', 64); var newFingerprint = new string('4', 64);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralQaPresentation", "Plugin"));
        var directory = Path.Combine(root, "Desktop");
        var project = Path.Combine(directory, "Cerebral.GeneratedPlugin.Desktop.csproj");
        var source = Path.Combine(directory, "GeneratedDesktopPlugin.cs");
        var manifest = Path.Combine(directory, "bin", "Release", "net10.0-windows", "desktop-plugin.json");
        var buildArguments = new[] { "build", project, "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" };
        var schema = CerebralJson.Element(new { type = "object", properties = new
        {
            path = new { type = "string" }, root = new { type = "string" }, executableId = new { type = "string" },
            arguments = new { type = "array", items = new { type = "string" } }, workingDirectory = new { type = "string" },
            expectedPackageFingerprint = new { type = "string" }, expectedSha256 = new { type = "string" },
            oldText = new { type = "string" }, newText = new { type = "string" },
            presentationId = new { type = "string" }, mountId = new { type = "string" }, operation = new { type = "string" },
            buttonName = new { type = "string" }
        } });
        var names = new[] { "plugin.projects", "file.read", "file.edit", "process.run", "plugin.verify", "plugin.present",
            "interface.status", "plugin.controls", "fixture.original" };
        var schemas = names.ToDictionary(name => name, _ => schema, StringComparer.Ordinal);
        var inventory = JsonSerializer.Serialize(new
        {
            projects = new[] { new { root, id = "qa.note", name = "QA note", version = "1.0.0", components = new[]
            {
                new { component = "desktop", manifest = Path.Combine(directory, "desktop-plugin.json"), source,
                    id = "qa.note", name = "QA note", version = "1.0.0", build = new
                    { executableId = "dotnet", arguments = buildArguments, workingDirectory = directory, builtManifest = manifest } }
            } } }, count = 1, limited = false, desktopAuthoringContract = ToolModelProjection.DesktopPluginAuthoringContractId
        }, CerebralJson.Options);
        JsonElement Work(Fixture fixture) => fixture.Step.State.GetProperty("pluginWork");
        int Corrections(Fixture fixture) => fixture.Step.State.TryGetProperty("automaticPresentationCorrections", out var value) ? value.GetInt32() : 0;
        int Receipts(Fixture fixture) => fixture.Step.State.GetProperty("toolReceipts").GetArrayLength();
        void Select(Fixture fixture, string name)
        {
            if (!fixture.Step.State.GetProperty("selected").EnumerateArray().Any(item => item.GetProperty("name").GetString() == name))
                fixture.Select(name, schema);
        }
        void Request(Fixture fixture, string name, object arguments)
        {
            Select(fixture, name);
            fixture.Model(new { action = "tool", name, arguments });
        }
        Fixture Bound(bool automatic = true, bool legacyRequirements = false)
        {
            var required = automatic ? new[] { "fixture.original", "interface.status", "plugin.controls" } : ["fixture.original"];
            var fixture = new Fixture(warm: false, input: new { requiredSuccessfulTools = required },
                requestId: automatic ? PluginControlReferences.TestRequestPrefix + reviewed : "ordinary-presentation",
                caller: automatic ? new EventActor(EventActorKind.Service, "cerebral.plugin-tests", "QA") : null,
                hostAddedRequiredTools: automatic && !legacyRequirements ? ["interface.status", "plugin.controls"] : [],
                hostPluginTestMountIds: automatic ? [mount] : []);
            fixture.History([Past("qa-older", 10, "Earlier reducible reference.")]);
            fixture.Catalog(forcedNames: names);
            if (fixture.Is("catalog")) fixture.Catalog(exact: true, inputSchemas: schemas);
            CompleteInitialQaObservationFixture(fixture);
            Request(fixture, "plugin.projects", new { root });
            fixture.Tool(true, inventory, provider: "cerebral.plugin-sdk");
            check(Work(fixture).GetProperty("sourceGeneration").GetInt32() == 0,
                "actual exact inventory starts with zero source-change credit");
            return fixture;
        }
        void Observe(Fixture fixture, bool failed = false, bool partial = false, bool truncated = false,
            string provider = "cerebral.plugin.controls", string returned = reviewed, string? observedManifest = null,
            bool interact = false)
        {
            var operation = failed || interact ? "invoke" : "inspect";
            var arguments = new Dictionary<string, object> { ["presentationId"] = "reviewed", ["mountId"] = mount, ["operation"] = operation };
            if (operation == "invoke") arguments["buttonName"] = "Save";
            Request(fixture, "plugin.controls", arguments);
            var snapshot = new PluginControlSnapshot(mount, ["Note"], [new("Save", true, true)], false);
            var receipt = new string('7', 32);
            fixture.Tool(!failed, JsonSerializer.Serialize(new
            {
                receipt, presentationId = returned, mountId = mount, operation,
                status = failed ? "failed" : "completed", nativeStatus = "completed",
                sourceManifest = observedManifest ?? manifest, packageFingerprint = oldFingerprint,
                observation = new PluginControlObservation(snapshot, snapshot, ClickDelivered: operation == "invoke"),
                error = failed ? "The actual save failed." : null
            }, CerebralJson.Options), provider: provider, requestId: receipt, partial: partial, truncated: truncated);
        }
        void Mutate(Fixture fixture)
        {
            Request(fixture, "file.read", new { path = source });
            fixture.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = new string('b', 64), startLine = 1, totalLines = 1, content = "before" }));
            Request(fixture, "file.edit", new { path = source, oldText = "before", newText = "after", expectedSha256 = new string('b', 64) });
            fixture.Tool(true, JsonSerializer.Serialize(new { path = source, sha256 = new string('e', 64), bytes = 5, lines = 1, words = 1 }));
        }
        void BuildVerify(Fixture fixture, string? fingerprint = null)
        {
            Request(fixture, "process.run", new { executableId = "dotnet", arguments = buildArguments, workingDirectory = directory });
            fixture.Tool(true, "{\"exitCode\":0}", exitCode: 0);
            Request(fixture, "plugin.verify", new { root });
            fixture.Tool(true, JsonSerializer.Serialize(new { root, verified = true,
                desktop = new { manifest, packageFingerprint = fingerprint ?? newFingerprint } }), provider: "cerebral.plugin-sdk");
        }
        void Present(Fixture fixture, string? fingerprint = null) =>
            Request(fixture, "plugin.present", new { path = manifest, expectedPackageFingerprint = fingerprint ?? newFingerprint });
        void CompletePresentation(Fixture fixture, string provider = "cerebral.interface", bool partial = false,
            bool truncated = false, string request = pending, string? returnedRequest = null, string? fingerprint = null)
        {
            fixture.Tool(true, JsonSerializer.Serialize(new { requestId = returnedRequest ?? request, status = "pending",
                packageFingerprint = fingerprint ?? newFingerprint }), provider: provider, requestId: request,
                partial: partial, truncated: truncated);
        }
        Fixture Repair(bool failed = false)
        {
            var fixture = Bound(); Observe(fixture, failed: failed); Mutate(fixture); BuildVerify(fixture);
            return fixture;
        }

        foreach (var legacy in new[] { false, true })
        {
            var untouched = Bound(legacyRequirements: legacy); BuildVerify(untouched, oldFingerprint);
            var before = Receipts(untouched); Present(untouched, oldFingerprint);
            check(untouched.Is("generate") && Receipts(untouched) == before && Corrections(untouched) == 1 &&
                  untouched.LastContributionText.Contains("no confirmed material source change", StringComparison.Ordinal) &&
                  untouched.LastContributionText.Contains("\"presentationId\":\"reviewed\"", StringComparison.Ordinal) &&
                  untouched.LastContributionText.Contains(mount, StringComparison.Ordinal),
                "unchanged zero-generation current and legacy installed QA receives exact approved-control feedback before any present dispatch");
            BuildVerify(untouched, oldFingerprint); Present(untouched, oldFingerprint);
            check(untouched.Is("generate") && Corrections(untouched) == 2,
                "valid rebuild/reverify actions cannot reset the semantic presentation budget");
            untouched.RejectContext(ContextProjectionStatus.NeedsCompaction); untouched.CompactSuccess();
            untouched.RestoreState(untouched.Step.State.Clone()); Present(untouched, oldFingerprint);
            check(untouched.Step.Next is CompleteWorkflow { Success: false } &&
                  untouched.Summary.Contains("exhausted two presentation corrections", StringComparison.Ordinal) &&
                  untouched.Step.State.GetProperty("toolReceipts").EnumerateArray().All(item => item.GetProperty("name").GetString() != "plugin.present"),
                "the third blocked proposal terminates truthfully after compaction without fabricated success or native effect");
        }

        var noObservation = Bound(); Mutate(noObservation); BuildVerify(noObservation); Present(noObservation);
        check(noObservation.Is("generate") && noObservation.LastContributionText.Contains("No complete native package observation", StringComparison.Ordinal),
            "material source generation alone cannot bypass approved native testing");
        foreach (var invalid in new[] { "partial", "truncated", "foreign", "foreign-review", "foreign-source" })
        {
            var fixture = Bound(); Observe(fixture, partial: invalid == "partial", truncated: invalid == "truncated",
                provider: invalid == "foreign" ? "foreign" : "cerebral.plugin.controls",
                returned: invalid == "foreign-review" ? new string('2', 32) : reviewed,
                observedManifest: invalid == "foreign-source" ? manifest + ".other" : null);
            Mutate(fixture); BuildVerify(fixture); var before = Receipts(fixture); Present(fixture);
            check(fixture.Is("generate") && Receipts(fixture) == before &&
                  fixture.LastContributionText.Contains("No complete native package observation", StringComparison.Ordinal),
                invalid + " native evidence cannot admit replacement review");
        }
        var unchanged = Bound(); Observe(unchanged); Mutate(unchanged); BuildVerify(unchanged, oldFingerprint); Present(unchanged, oldFingerprint);
        check(unchanged.Is("generate") && unchanged.LastContributionText.Contains("unchanged bytes are not a repair", StringComparison.Ordinal),
            "actual edits/reverts cannot earn replacement credit when the verified package still equals observed approved bytes");

        foreach (var failed in new[] { false, true })
        {
            var repaired = Repair(failed); Present(repaired);
            check(repaired.Is("tool"), "different verified material repair with exact successful/failed native observation remains admissible");
            CompletePresentation(repaired);
            var before = Receipts(repaired);
            check(repaired.Is("generate") && repaired.Step.State.GetProperty("toolReceipts").EnumerateArray().Last()
                      .GetProperty("presentation").GetProperty("requestId").GetString() == pending,
                "only exact complete native presentation response adds bounded receipt-backed identity");
            Present(repaired);
            check(repaired.Is("generate") && Receipts(repaired) == before &&
                  repaired.LastContributionText.Contains(pending, StringComparison.Ordinal) &&
                  repaired.LastContributionText.Contains("already have successful plugin.present receipt", StringComparison.Ordinal),
                "the same pending package cannot request another review and its actual success remains retained");
            BuildVerify(repaired);
            check(Work(repaired).GetProperty("presentRequestId").ValueKind == JsonValueKind.Null,
                "ordinary current-generation build/verify clearing still invalidates current presentation binding");
            repaired.RejectContext(ContextProjectionStatus.NeedsCompaction); repaired.CompactSuccess();
            repaired.RestoreState(repaired.Step.State.Clone()); var rebuiltBefore = Receipts(repaired); Present(repaired);
            check(repaired.Is("generate") && Receipts(repaired) == rebuiltBefore &&
                  repaired.LastContributionText.Contains(pending, StringComparison.Ordinal),
                "duplicate identity survives rebuild clearing, compaction and restoration without claiming review approval");
        }

        var progress = Bound(); BuildVerify(progress, oldFingerprint); Present(progress, oldFingerprint); Present(progress, oldFingerprint);
        Observe(progress);
        check(Corrections(progress) == 0 && progress.Is("generate"),
            "a first complete approved native observation remains available and resets prior semantic corrections");
        Present(progress, oldFingerprint); Observe(progress);
        check(Corrections(progress) == 1, "repeating the same inspection is not new semantic recovery progress");
        Observe(progress, interact: true);
        check(Corrections(progress) == 0, "first actually delivered native interaction resets corrections");
        Present(progress, oldFingerprint); Mutate(progress);
        check(Corrections(progress) == 0 && Work(progress).GetProperty("sourceGeneration").GetInt32() == 1,
            "confirmed material source change resets the semantic budget");
        BuildVerify(progress); Present(progress);
        check(progress.Is("tool"), "useful native controls and a valid changed repair are never blocked by earlier corrections");

        var legacyCurrent = Repair(); Present(legacyCurrent); CompletePresentation(legacyCurrent);
        var legacyState = JsonNode.Parse(legacyCurrent.Step.State.GetRawText())!.AsObject();
        foreach (var receipt in legacyState["toolReceipts"]!.AsArray()) receipt!.AsObject().Remove("presentation");
        legacyCurrent.RestoreState(CerebralJson.Element(legacyState)); BuildVerify(legacyCurrent); Present(legacyCurrent);
        check(legacyCurrent.Is("generate") && legacyCurrent.LastContributionText.Contains(pending, StringComparison.Ordinal),
            "legacy current native coordinate migrates only through its exact retained succeeded receipt before rebuild clears it");

        foreach (var invalid in new[] { "partial", "truncated", "foreign", "foreign-request", "foreign-package" })
        {
            var fixture = Repair(); Present(fixture);
            CompletePresentation(fixture, partial: invalid == "partial", truncated: invalid == "truncated",
                provider: invalid == "foreign" ? "foreign" : "cerebral.interface",
                returnedRequest: invalid == "foreign-request" ? new string('2', 32) : null,
                fingerprint: invalid == "foreign-package" ? oldFingerprint : null);
            check(!fixture.Step.State.GetProperty("toolReceipts").EnumerateArray().Last().TryGetProperty("presentation", out _),
                invalid + " presentation response gets no exact successful identity or duplicate credit");
            fixture.RestoreState(fixture.Step.State.Clone());
            BuildVerify(fixture);
            check(fixture.Is("generate") && fixture.Step.State.GetProperty("toolReceipts").EnumerateArray()
                      .All(item => !item.TryGetProperty("presentation", out _)) &&
                  fixture.Step.State.GetProperty("toolReceipts").EnumerateArray().Single(item =>
                      item.GetProperty("name").GetString() == "plugin.present").GetProperty("state").GetString() == "Succeeded",
                invalid + " presentation cannot acquire identity through legacy migration on a later tool/rebuild; its actual succeeded effect is retained");
            fixture.RejectContext(ContextProjectionStatus.NeedsCompaction); fixture.CompactSuccess();
            fixture.RestoreState(fixture.Step.State.Clone());
            check(fixture.Is("generate") && fixture.Step.State.GetProperty("toolReceipts").EnumerateArray()
                      .All(item => !item.TryGetProperty("presentation", out _)),
                invalid + " presentation receives no fabricated migration identity after compaction and saved-state restoration");
        }
        var unknown = Repair(); Present(unknown); unknown.Uncertain();
        check(unknown.Step.Next is CompleteWorkflow { Success: false } &&
              !unknown.Step.State.GetProperty("toolReceipts").EnumerateArray().Any(item => item.TryGetProperty("presentation", out _)),
            "Unknown delivery keeps the existing terminal no-replay path and receives no duplicate identity");

        var ordinary = Bound(automatic: false); BuildVerify(ordinary, oldFingerprint); Present(ordinary, oldFingerprint);
        check(ordinary.Is("tool"), "ordinary authoring still admits a verified generation-zero initial presentation");
        CompletePresentation(ordinary, fingerprint: oldFingerprint); Present(ordinary, oldFingerprint);
        check(ordinary.Is("tool"), "the new installed-QA gate does not silently change ordinary presentation admission");

        var bounded = Repair(); Present(bounded); CompletePresentation(bounded);
        var boundedState = JsonNode.Parse(bounded.Step.State.GetRawText())!.AsObject();
        var identities = boundedState["toolReceipts"]!.AsArray();
        for (var index = 1; index < 8; index++) identities.Add(new JsonObject
        {
            ["effectId"] = "bounded-present-" + index, ["name"] = "plugin.present", ["state"] = "Succeeded",
            ["outcome"] = "Succeeded", ["truncated"] = false, ["presentation"] = new JsonObject
            { ["sourceManifest"] = manifest, ["packageFingerprint"] = new string(index.ToString("x")[0], 64), ["requestId"] = new string(index.ToString("x")[0], 32) }
        });
        bounded.RestoreState(CerebralJson.Element(boundedState)); BuildVerify(bounded, new string('f', 64)); Present(bounded, new string('f', 64));
        check(bounded.Is("generate") && bounded.LastContributionText.Contains("eight-package", StringComparison.Ordinal) &&
              Encoding.UTF8.GetByteCount(bounded.Step.State.GetRawText()) < WorkflowContract.MaximumStateBytes,
            "bounded compact identities preserve the durable state limit and refuse eviction or a ninth native review");
    }
}
