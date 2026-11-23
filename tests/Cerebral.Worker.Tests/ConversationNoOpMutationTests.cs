using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Context;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    public static Task RunNoOpMutationOnlyAsync()
    {
        var checks = 0;
        VerifyNoOpMutationEvidence((condition, message) =>
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        });
        Console.WriteLine($"Conversation accepted-no-op mutation fixture checks passed: {checks}");
        return Task.CompletedTask;
    }

    internal static void VerifyNoOpMutationEvidence(Action<bool, string> check)
    {
        const string marker = "\nAccepted unchanged write:\n";
        const string body = "// Same admitted source: café\n";
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralKnownNoOp", "Plugin"));
        var directory = Path.Combine(root, "Desktop");
        var target = Path.Combine(directory, "GeneratedDesktopPlugin.cs");
        var project = Path.Combine(directory, "Cerebral.GeneratedPlugin.Desktop.csproj");
        var manifest = Path.Combine(directory, "bin", "Release", "net10.0-windows", "desktop-plugin.json");
        var pathSchema = CerebralJson.Element(new { type = "object", properties = new { path = new { type = "string" } },
            required = new[] { "path" }, additionalProperties = false });
        var inventorySchema = CerebralJson.Element(new { type = "object", properties = new { root = new { type = "string" } },
            required = new[] { "root" }, additionalProperties = false });
        var writeSchema = CerebralJson.Element(new
        {
            type = "object", properties = new
            {
                path = new { type = "string" }, content = new { type = "string" },
                collision = new { type = "string", @enum = new[] { "fail", "skip", "replace" } },
                expectedSha256 = new { type = "string", minLength = 64, maxLength = 64 }
            }, required = new[] { "path", "content" }, additionalProperties = false
        });
        var editSchema = CerebralJson.Element(new
        {
            type = "object", properties = new
            {
                path = new { type = "string" }, oldText = new { type = "string" }, newText = new { type = "string" },
                expectedSha256 = new { type = "string", minLength = 64, maxLength = 64 }
            }, required = new[] { "path", "oldText", "newText", "expectedSha256" }, additionalProperties = false
        });
        var processSchema = CerebralJson.Element(new
        {
            type = "object", properties = new
            {
                executableId = new { type = "string" }, arguments = new { type = "array", items = new { type = "string" } },
                workingDirectory = new { type = "string" }
            }, required = new[] { "executableId", "arguments", "workingDirectory" }, additionalProperties = false
        });
        var inventory = JsonSerializer.Serialize(new
        {
            projects = new[] { new { root, id = "noop.plugin", name = "No-op fixture", version = "1.0.0",
                components = new[] { new { component = "desktop", manifest = Path.Combine(directory, "desktop-plugin.json"),
                    source = target, id = "noop.plugin", name = "No-op fixture", version = "1.0.0", build = new
                    {
                        executableId = "dotnet", arguments = new[] { "build", project, "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" },
                        workingDirectory = directory, builtManifest = manifest
                    } } } } }, count = 1, limited = false,
            desktopAuthoringContract = ToolModelProjection.DesktopPluginAuthoringContractId
        }, CerebralJson.Options);

        byte[] Encode(Encoding encoding) => [.. encoding.GetPreamble(), .. encoding.GetBytes(body)];
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string Detail(string path, string hash, int bytes) => JsonSerializer.Serialize(new { path, sha256 = hash, bytes }, CerebralJson.Options);
        JsonElement Work(Fixture fixture) => fixture.Step.State.GetProperty("pluginWork");

        Fixture Bound(byte[] bytes)
        {
            var fixture = new Fixture(warm: false);
            fixture.History([Past("older-noop-history", 10, "Earlier work is reducible.")]);
            fixture.Catalog(forcedNames: ["plugin.projects", "file.read", "file.write", "file.edit", "process.run", "program.source.generate"]);
            fixture.Select("plugin.projects", inventorySchema);
            fixture.Model(new { action = "tool", name = "plugin.projects", arguments = new { root } });
            fixture.Tool(true, inventory, provider: "cerebral.plugin-sdk");
            check(Work(fixture).GetProperty("build").GetProperty("projectFile").GetString() == project,
                "the no-op fixture first selects an actual provider inventory coordinate");
            // Establish a prior changed-source receipt through the public workflow harness fixture.
            fixture.Select("file.write", writeSchema);
            fixture.Model(new { action = "tool", name = "file.write", arguments = new { path = target, content = body, collision = "fail" } });
            fixture.Tool(true, Detail(target, Hash(bytes), bytes.Length), omitEvidence: true);
            check(Work(fixture).GetProperty("sourceGeneration").GetInt32() == 1,
                "the baseline actual source mutation receives one generation credit");
            fixture.Select("file.read", pathSchema);
            fixture.Model(new { action = "tool", name = "file.read", arguments = new { path = target } });
            fixture.Tool(true, JsonSerializer.Serialize(new { path = target, sha256 = Hash(bytes), startLine = 1, totalLines = 1, content = body }));
            return fixture;
        }
        Fixture Pending(byte[] bytes, string submitted = body, string collision = "replace")
        {
            var fixture = Bound(bytes);
            fixture.Model(new { action = "tool", name = "file.write", arguments = new
                { path = target, content = submitted, collision, expectedSha256 = Hash(bytes) } });
            check(fixture.Is("tool"), "the exact admitted replacement is dispatched through the public harness");
            return fixture;
        }

        var utf8 = Encode(new UTF8Encoding(false, true));
        foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true),
                     new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) })
        {
            var bytes = Encode(encoding);
            var fixture = Pending(bytes);
            fixture.Tool(true, Detail(target, Hash(bytes).ToUpperInvariant(), bytes.Length), omitEvidence: true);
            check(fixture.Is("generate") && Work(fixture).GetProperty("sourceGeneration").GetInt32() == 1 &&
                  fixture.LastContributionText.Contains(marker, StringComparison.Ordinal) &&
                  !fixture.LastContributionText.Contains("Applied mutation:", StringComparison.Ordinal),
                "a compatible identical " + encoding.WebName + " replacement receives no source progress or applied-mutation claim");
            var receipt = fixture.Step.State.GetProperty("toolReceipts").EnumerateArray().Last();
            check(receipt.GetProperty("state").GetString() == "Succeeded" &&
                  fixture.LastContributionText.Contains(receipt.GetProperty("effectId").GetString()!, StringComparison.Ordinal) &&
                  fixture.LastContributionText.Contains("no source-generation or repair credit", StringComparison.Ordinal),
                "an accepted no-op keeps its actual successful effect and compact no-repair diagnostic");
            check(!fixture.LastContributionText.Contains("Same admitted source", StringComparison.Ordinal) &&
                  Work(fixture).GetProperty("lastMutationPath").GetString() == target,
                "the no-op does not replay its submitted source or invent a later changed-source coordinate");
        }

        var changed = Pending(utf8, "// Material new source\n");
        var changedBytes = Encoding.UTF8.GetBytes("// Material new source\n");
        changed.Tool(true, Detail(target, Hash(changedBytes), changedBytes.Length), omitEvidence: true);
        check(Work(changed).GetProperty("sourceGeneration").GetInt32() == 2 &&
              changed.LastContributionText.Contains("Applied mutation:", StringComparison.Ordinal) &&
              !changed.LastContributionText.Contains(marker, StringComparison.Ordinal),
            "a proved changed replacement still earns a new source generation");

        var pending = Pending(utf8);
        var retainedCompletion = pending.Fork();
        var restoredState = JsonNode.Parse(retainedCompletion.Step.State.GetRawText())!.AsObject();
        var restoredWork = restoredState["pluginWork"]!.AsObject();
        foreach (var pair in new[] { ("successfulBuildGeneration", "buildEffectId"), ("verifiedGeneration", "verifyEffectId"),
                     ("presentedGeneration", "presentEffectId") })
        {
            restoredWork[pair.Item1] = 1;
            restoredWork[pair.Item2] = "retained-unit-fixture-" + pair.Item2;
        }
        retainedCompletion.RestoreState(CerebralJson.Element(restoredState));
        retainedCompletion.Tool(true, Detail(target, Hash(utf8), utf8.Length), omitEvidence: true);
        check(Work(retainedCompletion).GetProperty("sourceGeneration").GetInt32() == 1 &&
              new[] { "successfulBuildGeneration", "verifiedGeneration", "presentedGeneration" }.All(name =>
                  Work(retainedCompletion).GetProperty(name).ValueKind == JsonValueKind.Null),
            "an accepted no-op invalidates retained completion proof without inventing another source generation");

        var generated = Bound(utf8);
        generated.Select("program.source.generate", pathSchema);
        generated.Model(new { action = "tool", name = "program.source.generate", arguments = new { path = target } });
        generated.Tool(true, "Retained source-generation plan.", followUp: true,
            generation: new ToolGenerationRequest("Produce the requested complete source.", "file.write",
                CerebralJson.Element(new { path = target, collision = "replace", expectedSha256 = Hash(utf8) }),
                MaxTokens: 4096, CompleteAssignment: false, MaxAttempts: 2));
        generated.RawGeneration(body);
        check(generated.Is("tool") && generated.Command.Arguments.GetProperty("arguments").GetProperty("content").GetString() == body,
            "the nested generation delivers its exact text to the ordinary guarded sink");
        generated.Tool(true, Detail(target, Hash(utf8), utf8.Length), omitEvidence: true);
        check(Work(generated).GetProperty("sourceGeneration").GetInt32() == 1 &&
              generated.LastContributionText.Contains(marker, StringComparison.Ordinal) &&
              generated.Step.State.GetProperty("pendingGeneration").ValueKind == JsonValueKind.Null,
            "a completed generated sink earns no false source progress and returns to ordinary model diagnosis without replay");

        var negatives = new (string Label, Action<Fixture> Complete)[]
        {
            ("failed effect", f => f.Tool(false, Detail(target, Hash(utf8), utf8.Length))),
            ("unknown provider outcome", f => f.Tool(true, Detail(target, Hash(utf8), utf8.Length), outcome: ToolExecutionOutcome.Unknown)),
            ("partial evidence", f => f.Tool(true, Detail(target, Hash(utf8), utf8.Length), partial: true)),
            ("truncated body", f => f.Tool(true, Detail(target, Hash(utf8), utf8.Length), truncated: true)),
            ("foreign request", f => f.Tool(true, Detail(target, Hash(utf8), utf8.Length), requestId: "foreign-effect")),
            ("foreign request with changed digest", f => f.Tool(true, Detail(target, Hash(changedBytes), changedBytes.Length), requestId: "foreign-effect")),
            ("foreign destination", f => f.Tool(true, Detail(target + ".other", Hash(utf8), utf8.Length))),
            ("malformed result", f => f.Tool(true, "{not json")),
            ("missing bytes", f => f.Tool(true, JsonSerializer.Serialize(new { path = target, sha256 = Hash(utf8) }))),
            ("negative bytes", f => f.Tool(true, Detail(target, Hash(utf8), -1))),
            ("incompatible bytes", f => f.Tool(true, Detail(target, Hash(utf8), utf8.Length + 1))),
            ("duplicate digest", f => f.Tool(true, Detail(target, Hash(utf8), utf8.Length).Replace("{", "{\"sha256\":\"" + Hash(utf8) + "\",")))
        };
        foreach (var (label, complete) in negatives)
        {
            var fixture = pending.Fork(); complete(fixture);
            check(Work(fixture).GetProperty("sourceGeneration").GetInt32() == 1 &&
                  !fixture.LastContributionText.Contains(marker, StringComparison.Ordinal) &&
                  !fixture.LastContributionText.Contains("Applied mutation:", StringComparison.Ordinal),
                label + " cannot manufacture no-op or new source credit");
        }
        var incompatible = Pending(utf8, "// Provider did not apply this content\n");
        incompatible.Tool(true, Detail(target, Hash(utf8), utf8.Length), omitEvidence: true);
        check(Work(incompatible).GetProperty("sourceGeneration").GetInt32() == 1 &&
              !incompatible.LastContributionText.Contains(marker, StringComparison.Ordinal),
            "equal preimage/result identity with incompatible admitted content earns no progress and no known-no-op claim");
        var unknownEffect = pending.Fork();
        unknownEffect.Uncertain();
        check(Work(unknownEffect).GetProperty("sourceGeneration").GetInt32() == 1 && !unknownEffect.Is("tool"),
            "an unknown workflow effect earns no source credit and is not automatically replayed");
        var skipped = Pending(utf8, collision: "skip");
        skipped.Tool(true, JsonSerializer.Serialize(new { path = target, skipped = true }), omitEvidence: true);
        check(Work(skipped).GetProperty("sourceGeneration").GetInt32() == 1 &&
              !skipped.LastContributionText.Contains(marker, StringComparison.Ordinal),
            "collision skip stays distinct from an accepted byte-identical replacement");

        var compacted = pending.Fork();
        compacted.Tool(true, Detail(target, Hash(utf8), utf8.Length), omitEvidence: true);
        compacted.RejectContext(ContextProjectionStatus.NeedsCompaction);
        check(compacted.Is("compact"), "earlier reducible work permits ordinary compaction after the no-op");
        compacted.CompactSuccess();
        using var saved = JsonDocument.Parse(compacted.Step.State.GetRawText());
        compacted.RestoreState(saved.RootElement.Clone());
        check(Work(compacted).GetProperty("sourceGeneration").GetInt32() == 1 &&
              compacted.LastContributionText.Contains(marker, StringComparison.Ordinal),
            "compaction and serialized recovery retain current no-change evidence without creating progress");

        Fixture PendingEdit(bool afterFailedBuild = false)
        {
            var fixture = Bound(utf8);
            if (afterFailedBuild)
            {
                fixture.Select("process.run", processSchema);
                fixture.Model(new { action = "tool", name = "process.run", arguments = new
                {
                    executableId = "dotnet", arguments = new[] { "build", project, "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" },
                    workingDirectory = directory
                } });
                fixture.Tool(false, JsonSerializer.Serialize(new { exitCode = 1, stdout = target + "(2,1): error CS8602: Possibly null reference." }),
                    outcome: ToolExecutionOutcome.Failed, exitCode: 1);
                check(Work(fixture).GetProperty("diagnosticBuildAttemptGeneration").GetInt32() == 1 &&
                      Work(fixture).GetProperty("lastCanonicalBuildGeneration").GetInt32() == 1,
                    "a real failed canonical fixture receipt pins the diagnostic attempt before the edit");
                fixture.Model(new { action = "tool", name = "file.read", arguments = new { path = target } });
                fixture.Tool(true, JsonSerializer.Serialize(new { path = target, sha256 = Hash(utf8), startLine = 1, totalLines = 1, content = body }));
            }
            fixture.Select("file.edit", editSchema);
            fixture.Model(new { action = "tool", name = "file.edit", arguments = new
                { path = target, oldText = "admitted source", newText = "corrected source", expectedSha256 = Hash(utf8) } });
            check(fixture.Is("tool"), "distinct edit strings dispatch against the admitted current preimage");
            return fixture;
        }

        const string editMarker = "\nAccepted unchanged edit:\n";
        var editPending = PendingEdit(afterFailedBuild: true);
        var unchangedEdit = editPending.Fork();
        var failedBuildId = Work(unchangedEdit).GetProperty("lastCanonicalBuildEffectId").GetString();
        unchangedEdit.Tool(true, Detail(target, Hash(utf8).ToUpperInvariant(), utf8.Length));
        check(unchangedEdit.Is("generate") && Work(unchangedEdit).GetProperty("sourceGeneration").GetInt32() == 1 &&
              Work(unchangedEdit).GetProperty("diagnosticBuildAttemptGeneration").GetInt32() == 1 &&
              Work(unchangedEdit).GetProperty("lastCanonicalBuildEffectId").GetString() == failedBuildId &&
              unchangedEdit.LastContributionText.Contains(editMarker, StringComparison.Ordinal) &&
              !unchangedEdit.LastContributionText.Contains("Applied mutation:", StringComparison.Ordinal),
            "an unchanged edit preserves failed-build coordinates without manufacturing progress or an applied repair");
        var editReceipt = unchangedEdit.Step.State.GetProperty("toolReceipts").EnumerateArray().Last();
        check(editReceipt.GetProperty("state").GetString() == "Succeeded" &&
              unchangedEdit.LastContributionText.Contains(editReceipt.GetProperty("effectId").GetString()!, StringComparison.Ordinal) &&
              unchangedEdit.LastContributionText.Contains("did not prove changed bytes", StringComparison.Ordinal) &&
              !unchangedEdit.LastContributionText.Contains("corrected source", StringComparison.Ordinal),
            "the no-change edit keeps its successful immutable effect without claiming the requested correction or repeating source text");
        var recoveredEdit = unchangedEdit.Fork();
        recoveredEdit.RejectContext(ContextProjectionStatus.NeedsCompaction);
        check(recoveredEdit.Is("compact"), "ordinary reducible history permits compaction after an unchanged edit");
        recoveredEdit.CompactSuccess();
        recoveredEdit.RestoreState(JsonDocument.Parse(recoveredEdit.Step.State.GetRawText()).RootElement.Clone());
        check(Work(recoveredEdit).GetProperty("sourceGeneration").GetInt32() == 1 &&
              Work(recoveredEdit).GetProperty("diagnosticBuildAttemptGeneration").GetInt32() == 1 &&
              recoveredEdit.LastContributionText.Contains(editMarker, StringComparison.Ordinal),
            "compaction and recovery retain no-change and one-attempt evidence without replaying the edit");
        recoveredEdit.Model(new { action = "tool", name = "file.read", arguments = new { path = target } });
        recoveredEdit.Tool(true, JsonSerializer.Serialize(new { path = target, sha256 = Hash(utf8), startLine = 1, totalLines = 1, content = body }));
        recoveredEdit.Model(new { action = "tool", name = "file.read", arguments = new { path = target } });
        check(recoveredEdit.Is("generate") && Work(recoveredEdit).GetProperty("diagnosticBuildAttemptGeneration").GetInt32() == 1,
            "unchanged source after a failed build cannot earn another automatic diagnostic build through repeated reads");

        var changedEdit = editPending.Fork();
        changedEdit.Tool(true, Detail(target, Hash(changedBytes), changedBytes.Length));
        check(Work(changedEdit).GetProperty("sourceGeneration").GetInt32() == 2 &&
              changedEdit.LastContributionText.Contains("Applied mutation:", StringComparison.Ordinal) &&
              !changedEdit.LastContributionText.Contains(editMarker, StringComparison.Ordinal),
            "a matched changed edit still earns source progress independently of the preceding failed build");
        var editCompletion = editPending.Fork();
        var editState = JsonNode.Parse(editCompletion.Step.State.GetRawText())!.AsObject();
        foreach (var pair in new[] { ("successfulBuildGeneration", "buildEffectId"), ("verifiedGeneration", "verifyEffectId"),
                      ("presentedGeneration", "presentEffectId") })
        {
            editState["pluginWork"]![pair.Item1] = 1;
            editState["pluginWork"]![pair.Item2] = "retained-edit-fixture-" + pair.Item2;
        }
        editCompletion.RestoreState(CerebralJson.Element(editState));
        editCompletion.Tool(true, Detail(target, Hash(utf8), utf8.Length));
        check(Work(editCompletion).GetProperty("sourceGeneration").GetInt32() == 1 &&
              new[] { "successfulBuildGeneration", "verifiedGeneration", "presentedGeneration" }.All(name =>
                  Work(editCompletion).GetProperty(name).ValueKind == JsonValueKind.Null),
            "unchanged edits conservatively invalidate stale completion proof without inventing progress");
        foreach (var (label, complete) in negatives.Where(item => item.Label != "incompatible bytes"))
        {
            var fixture = editPending.Fork(); complete(fixture);
            check(Work(fixture).GetProperty("sourceGeneration").GetInt32() == 1 &&
                  !fixture.LastContributionText.Contains(editMarker, StringComparison.Ordinal) &&
                  !fixture.LastContributionText.Contains("Applied mutation:", StringComparison.Ordinal),
                "edit " + label + " cannot manufacture confirmed no-change or new source credit");
        }
        var uncertainEdit = editPending.Fork();
        uncertainEdit.Uncertain();
        check(Work(uncertainEdit).GetProperty("sourceGeneration").GetInt32() == 1 && !uncertainEdit.Is("tool"),
            "an unknown edit remains non-replayable and earns no source progress");
    }
}
