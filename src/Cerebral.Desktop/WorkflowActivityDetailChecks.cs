using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Deterministic formatter fixtures; no commands execute and no native renderer is claimed.</summary>
internal static class WorkflowActivityDetailChecks
{
    internal static int Check()
    {
        const int maximumCharacters = 16_384;
        const string filePath = @"C:\CerebralProjects\Plugins\Detail Fixture\GeneratedDesktopPlugin.cs";
        const string projectPath = @"C:\CerebralProjects\Plugins\Detail Fixture\Desktop\DetailFixture.csproj";
        const string workingDirectory = @"C:\CerebralProjects\Plugins\Detail Fixture";
        const string privateSource = "PRIVATE FILE SOURCE FIXTURE MUST NOT APPEAR";
        const string privateContext = "PRIVATE GENERATION CONTEXT FIXTURE MUST NOT APPEAR";
        const string secretValue = "fixture-api-secret-do-not-display";
        const string inlineSecretValue = "fixture-token-secret-do-not-display";
        var checks = 0;
        void Verify(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        void Bounded(string text, string label) =>
            Verify(text.Length <= maximumCharacters, label + " must remain within the 16384-character display bound.");
        void Readable(string text, string label) =>
            Verify(!text.Contains(@"\u0022", StringComparison.OrdinalIgnoreCase) &&
                   !text.Contains(@"\u005c", StringComparison.OrdinalIgnoreCase) &&
                   !text.Contains(@"C:\\CerebralProjects", StringComparison.Ordinal),
                label + " must decode presentation escaping into ordinary human-readable paths and quotes.");
        CerebralEvent Call(string id, string name, object arguments, bool wrapped = true) =>
            new(id, "activity-detail-fixture", "activity-detail-run", 1, CerebralEventTypes.ToolCall,
                new(EventActorKind.Agent, "builder", "Builder"), [],
                new(ModelInclusion.Required, HumanVisibility.Activity), new(EventSourceKind.Agent, AgentId: "builder"),
                DateTimeOffset.UnixEpoch, CerebralJson.Element(new ToolCallData(id + "-call",
                    wrapped ? "cerebral.host:tool" : name,
                    wrapped ? CerebralJson.Element(new { name, arguments }) : CerebralJson.Element(arguments))));
        CerebralEvent Result(string id, object payload, bool success = true, string tool = "cerebral.host:tool") =>
            new(id + "-result", "activity-detail-fixture", "activity-detail-run", 2, CerebralEventTypes.ToolResult,
                new(EventActorKind.Tool), [new StructuredContentPart("workflow.command.result", CerebralJson.Element(payload))],
                new(ModelInclusion.Never, HumanVisibility.Activity), new(EventSourceKind.Tool), DateTimeOffset.UnixEpoch,
                CerebralJson.Element(new ToolResultData(id + "-call", tool, success, DurationMilliseconds: 450)));

        var read = Call("read", "file.read", new { path = filePath, startLine = 31, lineCount = 12 });
        var readText = WorkflowWorkspace.ActivityCommandText(read);
        Verify(readText.Contains("file.read", StringComparison.Ordinal) &&
               readText.Contains(filePath, StringComparison.Ordinal) &&
               readText.Contains("31", StringComparison.Ordinal) && readText.Contains("12", StringComparison.Ordinal),
            "File-read activity must display the actual command, full path, and requested line range.");
        Readable(readText, "File-read command");
        Bounded(readText, "File-read command");

        var build = Call("build", "process.run", new
        {
            executableId = "dotnet",
            arguments = new[] { "build", projectPath, "--configuration", "Release", "--no-restore" },
            workingDirectory, timeoutSeconds = 120
        });
        var buildText = WorkflowWorkspace.ActivityCommandText(build);
        Verify(buildText.Contains("dotnet", StringComparison.Ordinal) &&
               buildText.Contains("build", StringComparison.Ordinal) && buildText.Contains(projectPath, StringComparison.Ordinal) &&
               buildText.Contains("--configuration", StringComparison.Ordinal) && buildText.Contains("Release", StringComparison.Ordinal) &&
               buildText.Contains(workingDirectory, StringComparison.Ordinal),
            "Registered-process activity must display the executable ID, separate argv, and working directory.");
        Readable(buildText, "Registered build command");
        Bounded(buildText, "Registered build command");

        var compilerPayload = JsonSerializer.Serialize(new
        {
            exitCode = 1,
            stdout = "Build started for café fixture.",
            stderr = filePath + "(31,12): error CS0246: FixtureMissingType was not found.",
            receiptId = "receipt-fixture-01",
            path = filePath
        });
        var compiler = Result("build", new { state = "Failed", data = new { content = compilerPayload } }, success: false);
        var compilerText = WorkflowWorkspace.ActivityResultText(compiler, build);
        Verify(compilerText.Contains("Failed", StringComparison.OrdinalIgnoreCase) &&
               compilerText.Contains("CS0246", StringComparison.Ordinal) && compilerText.Contains("FixtureMissingType", StringComparison.Ordinal) &&
               compilerText.Contains(filePath, StringComparison.Ordinal) && compilerText.Contains("café", StringComparison.Ordinal),
            "Nested result content must expose the actual failure state and decoded compiler stdout/stderr.");
        Verify(compilerText.Contains("receipt-fixture-01", StringComparison.Ordinal) &&
               compilerText.Contains("1", StringComparison.Ordinal),
            "Compiler activity must retain its completion receipt and exit code.");
        Readable(compilerText, "Nested compiler result");
        Bounded(compilerText, "Nested compiler result");

        var successfulProcess = Result("success", new { state = "Succeeded", data = new
        {
            content = JsonSerializer.Serialize(new { exitCode = 0, stdout = "PASS", stderr = "", outcome = "succeeded" })
        } });
        var successfulText = WorkflowWorkspace.ActivityResultText(successfulProcess, build);
        Verify(successfulText.Contains("PASS", StringComparison.Ordinal) &&
               !successfulText.Contains("Error output:", StringComparison.Ordinal),
            "A successful process with empty stderr must not display an error-output label.");

        var plainText = WorkflowWorkspace.ActivityResultText(Result("plain", new
        {
            state = "Succeeded", data = new { content = "Copied " + filePath + "\nComplete." }
        }));
        Verify(plainText.Contains(filePath, StringComparison.Ordinal) && plainText.Contains("Complete.", StringComparison.Ordinal),
            "Plain-text nested result content must remain available in activity details.");
        Readable(plainText, "Plain-text result");
        Bounded(plainText, "Plain-text result");

        foreach (var tool in new[] { "file.write", "file.edit", "file.append" })
        {
            object arguments = tool switch
            {
                "file.edit" => new { path = filePath, oldText = privateSource, newText = privateSource, expectedSha256 = new string('a', 64) },
                "file.append" => new { path = filePath, content = privateSource, expectedSha256 = new string('a', 64), expectedBytes = 0 },
                _ => new { path = filePath, content = privateSource, collision = "fail" }
            };
            var text = WorkflowWorkspace.ActivityCommandText(Call("source-" + tool, tool, arguments));
            Verify(text.Contains(filePath, StringComparison.Ordinal) && !text.Contains(privateSource, StringComparison.Ordinal),
                "File mutation activity must show its path while omitting full source bodies: " + tool + ".");
            Bounded(text, "File mutation command");
        }

        var secretCall = Call("secret", "process.run", new
        {
            executableId = "dotnet",
            arguments = new[] { "run", "--api-key", secretValue, "--token=" + inlineSecretValue, "--name", "visible-name" },
            workingDirectory, timeoutSeconds = 30
        });
        var secretCommandText = WorkflowWorkspace.ActivityCommandText(secretCall);
        Verify(!secretCommandText.Contains(secretValue, StringComparison.Ordinal) &&
               !secretCommandText.Contains(inlineSecretValue, StringComparison.Ordinal) &&
               secretCommandText.Contains("visible-name", StringComparison.Ordinal),
            "Separate and inline secret CLI values must be redacted without dropping unrelated command arguments.");
        Bounded(secretCommandText, "Redacted process command");
        var echoText = WorkflowWorkspace.ActivityResultText(Result("secret", new
        {
            state = "Succeeded", data = new
            {
                content = JsonSerializer.Serialize(new
                {
                    stdout = "Command completed: " + secretValue,
                    stderr = "Echoed inline token: " + inlineSecretValue
                })
            }
        }), secretCall);
        Verify(!echoText.Contains(secretValue, StringComparison.Ordinal) && !echoText.Contains(inlineSecretValue, StringComparison.Ordinal),
            "Results echoed by the known command must redact its secret argument values.");
        Verify(echoText.Contains("Command completed", StringComparison.Ordinal),
            "Known-secret result redaction must preserve useful surrounding execution details.");
        Bounded(echoText, "Redacted echoed result");

        foreach (var tool in new[] { "cerebral.host:generate", "cerebral.host:compact", "cerebral.host:history" })
        {
            var call = Call("private-" + tool, tool, new
            {
                prompt = privateContext, context = privateContext, messages = new[] { privateContext },
                reasoning = privateContext, prepared = new { inputTokens = 42, context = privateContext }
            }, wrapped: false);
            var commandText = WorkflowWorkspace.ActivityCommandText(call);
            Verify(!commandText.Contains(privateContext, StringComparison.Ordinal),
                "Generation, compaction, and history commands must never render their private context: " + tool + ".");
            Bounded(commandText, "Private-context command");
            var resultText = WorkflowWorkspace.ActivityResultText(Result("private-" + tool, new
            {
                state = "Succeeded",
                data = new
                {
                    prepared = new { inputTokens = 42, context = privateContext, messages = new[] { privateContext } },
                    generation = new { generatedTokens = 11, text = privateContext, reasoning = privateContext },
                    reasoning = privateContext
                }
            }, tool: tool));
            Verify(!resultText.Contains(privateContext, StringComparison.Ordinal),
                "Generation, compaction, and history results must omit private prepared context and reasoning: " + tool + ".");
            Bounded(resultText, "Private-context result");
        }

        var mixedText = WorkflowWorkspace.ActivityResultText(Result("mixed", new
        {
            state = "Succeeded",
            data = new
            {
                content = JsonSerializer.Serialize(new
                {
                    stdout = "Public compiler diagnostic",
                    context = privateContext, reasoning = privateContext,
                    prepared = new { messages = new[] { privateContext } }
                })
            }
        }));
        Verify(mixedText.Contains("Public compiler diagnostic", StringComparison.Ordinal) &&
               !mixedText.Contains(privateContext, StringComparison.Ordinal),
            "Public execution details must not expose adjacent private context or reasoning fields.");

        var malformedFields = new[]
        {
            CerebralJson.Element(new { callId = "invalid", toolName = 17, arguments = new[] { 1, 2 } }),
            CerebralJson.Element(new { callId = "invalid", toolName = "cerebral.host:tool", arguments = new { name = 17, arguments = true } }),
            CerebralJson.Element(new[] { "not", "an", "object" }),
            CerebralJson.Element("not an object"),
            default(JsonElement)
        };
        foreach (var fields in malformedFields)
        {
            Bounded(WorkflowWorkspace.ActivityCommandText(read with { Data = fields }), "Malformed command data");
            Bounded(WorkflowWorkspace.ActivityResultText(compiler with { Data = fields }), "Malformed result metadata");
        }
        foreach (var content in new object[] { new { content = true }, new { content = "{not-valid-json" }, new[] { 1, 2 }, 17 })
            Bounded(WorkflowWorkspace.ActivityResultText(Result("malformed", new { state = "Failed", data = content }, success: false)),
                "Malformed nested result content");
        var timeoutText = WorkflowWorkspace.ActivityResultText(Result("timeout", new
        {
            state = "Failed", error = "The registered process exceeded its timeout.", data = new { content = "{invalid" }
        }, success: false));
        Verify(timeoutText.Contains("timeout", StringComparison.OrdinalIgnoreCase),
            "A timeout error must remain visible even when nested result content is malformed.");
        Bounded(timeoutText, "Timeout failure");

        Bounded(WorkflowWorkspace.ActivityCommandText(Call("large", "file.read", new
        {
            path = @"C:\" + new string('p', 40_000), startLine = 1, lineCount = 200
        })), "Oversized command field");
        var largeText = WorkflowWorkspace.ActivityResultText(Result("large", new
        {
            state = "Failed", data = new { content = JsonSerializer.Serialize(new { stdout = new string('z', 60_000) }) }
        }, success: false));
        Verify(largeText.Contains(new string('z', 32), StringComparison.Ordinal),
            "Oversized output must retain a useful bounded excerpt rather than lose the real result.");
        Bounded(largeText, "Oversized compiler output");
        object nested = new { stdout = "Deep nested fixture output" };
        for (var depth = 0; depth < 24; depth++) nested = new { content = nested };
        Bounded(WorkflowWorkspace.ActivityResultText(Result("deep", new { state = "Succeeded", data = nested })),
            "Deep nested content");
        var operationRows = WorkflowWorkspace.ProjectActivityRows([build, compiler]);
        Verify(operationRows.Count == 1 && operationRows[0].Call?.Id == build.Id &&
               operationRows[0].Event.Id == compiler.Id && WorkflowWorkspace.ActivityRowFailed(operationRows[0]),
            "A dispatch and its first matching terminal receipt must produce one distinct failed operation.");
        Verify(operationRows[0].Key == build.Id && operationRows[0].FirstSequence == 1 && operationRows[0].LastSequence == 2,
            "Grouped rows retain the dispatch identity and both source sequence boundaries.");
        Verify(WorkflowWorkspace.ProjectActivityRows([build, compiler, compiler with { Id = "second-result", Sequence = 3 }]).Count == 2,
            "An additional receipt must remain separate rather than hiding conflicting or repeated result evidence.");
        foreach (var unmatched in new[]
        {
            compiler with { ConversationId = "another-conversation" },
            compiler with { TurnId = "another-turn" },
            compiler with { Data = CerebralJson.Element(new ToolResultData("build-call", "another-tool", false)) },
            compiler with { Data = CerebralJson.Element(new ToolResultData("another-call", "cerebral.host:tool", false)) }
        })
            Verify(WorkflowWorkspace.ProjectActivityRows([build, unmatched]).Count == 2,
                "Matching receipt IDs must not cross conversation, turn, tool, or call identity.");
        Verify(WorkflowWorkspace.ProjectActivityRows([compiler]).Single().Call is null,
            "A receipt whose dispatch was evicted from the bounded page must remain independently visible.");
        Verify(WorkflowWorkspace.ProjectActivityRows([compiler with { Sequence = 0 }, build]).Count == 2,
            "A receipt preceding its recorded dispatch must not be attached to a later command.");
        var secondBuild = build with { Id = "second-build", Sequence = 3,
            Data = CerebralJson.Element(new ToolCallData("second-build-call", "cerebral.host:tool",
                CerebralJson.Element(new { name = "process.run", arguments = new { executableId = "dotnet" } }))) };
        Verify(WorkflowWorkspace.ProjectActivityRows([build, compiler, secondBuild]).Count == 2,
            "A later separate command stays a separate row beside the failed operation.");
        var run = new WorkflowConversationRun("activity-detail-run", "activity-detail-fixture", "builder", "Fixture",
            "pegasus.conversation:chat", "1.0.0", 1, WorkflowStatus.Running, null, null, null, null,
            false, "review-effect", WorkflowEffectState.Dispatched, 1);
        var generation = Call("review", "cerebral.host:generate", new { }, wrapped: false) with { Sequence = 3 };
        var reviewItems = new[] { build, compiler, generation };
        var reviewRows = WorkflowWorkspace.ProjectActivityRows(reviewItems);
        var profiles = new Dictionary<string, string> { ["builder"] = "Cerebral" };
        var defaultGeneration = generation with { Actor = new(EventActorKind.Agent, "builder") };
        Verify(WorkflowWorkspace.ActivityRowCaption(reviewRows[^1], run, reviewItems, profiles)
                .Contains("Reviewing failed call", StringComparison.Ordinal) &&
               WorkflowWorkspace.ActivityHeader(reviewItems, run, profiles).Contains("Reviewing failed call", StringComparison.Ordinal),
            "A running generation directly after a failed receipt exposes a bounded recovery state.");
        Verify(!WorkflowWorkspace.ActivityRowCaption(new(defaultGeneration.Id, defaultGeneration), run,
                [build, compiler, defaultGeneration], profiles).Contains("Cerebral", StringComparison.Ordinal),
            "The ordinary Cerebral identity is omitted from activity rows without removing distinct helper names.");
        Verify(WorkflowWorkspace.ActivityRowCaption(reviewRows[0], run, reviewItems, profiles)
                .StartsWith("Builder · Failed", StringComparison.Ordinal) &&
               WorkflowWorkspace.ActivityRowCaption(reviewRows[0], run, reviewItems, profiles).Contains("dotnet build", StringComparison.Ordinal),
            "Distinct named helper identity and useful actual executable/argv summaries remain visible.");

        var generationResult = Result("review", new { state = "Succeeded", data = new { outputOmitted = false } },
            tool: "cerebral.host:generate") with { Sequence = 4 };
        var edit = Call("repair-edit", "file.edit", new { path = filePath, oldText = "old", newText = "new" }) with { Sequence = 5 };
        var edited = Result("repair-edit", new { state = "Succeeded" }) with { Sequence = 6 };
        var rebuilt = secondBuild with { Sequence = 7,
            Data = CerebralJson.Element(new ToolCallData("second-build-call", "cerebral.host:tool",
                CerebralJson.Element(new { name = "process.run", arguments = new { executableId = "dotnet",
                    arguments = new[] { "build", projectPath } } }))) };
        var rebuiltResult = Result("second-build", new { state = "Succeeded" }) with { Sequence = 8 };
        var recoveredItems = new[] { build, compiler, generation, generationResult, edit, edited, rebuilt, rebuiltResult };
        var sections = WorkflowWorkspace.ProjectActivitySections(WorkflowWorkspace.ProjectActivityRows(recoveredItems));
        Verify(sections.Select(section => section.Label).SequenceEqual(new[] { "Building", "Changing files", "Building" }) &&
               sections[0].Rows.Count == 2 && sections[0].Rows[0].Key == build.Id,
            "Named chronological sections use recorded operation purposes and retain the failed command with its review.");
        Verify(WorkflowWorkspace.ActivityState(recoveredItems, run) == "Running",
            "Reviewing-failure presentation clears once its actual review invocation finishes.");
        var completed = run with { Status = WorkflowStatus.Completed, EffectState = WorkflowEffectState.Succeeded };
        Verify(WorkflowWorkspace.ActivityHeader(recoveredItems, completed, profiles).Contains("Completed", StringComparison.Ordinal) &&
               WorkflowWorkspace.ActivitySectionHeader(sections[0], false, recoveredItems, completed)
                   .Contains("Finished · 2 calls · 1 failed call", StringComparison.Ordinal) &&
               WorkflowWorkspace.ActivitySectionHeader(sections[^1], true, recoveredItems, completed)
                   .Contains("Completed", StringComparison.Ordinal) &&
               WorkflowWorkspace.ActivityRowFailed(sections[0].Rows[0]),
            "A recovered run and its latest section show the final outcome while historical failures remain visible.");
        var waiting = run with { Status = WorkflowStatus.Waiting, EffectState = WorkflowEffectState.Unknown };
        Verify(!WorkflowWorkspace.ActivityHeader(reviewItems, waiting, profiles).Contains("Reviewing failed call", StringComparison.Ordinal),
            "A paused or uncertain run must not claim to be reviewing a failure.");

        var priorIteration = new[] { build, compiler };
        var priorHeader = WorkflowWorkspace.ActivityHeader(priorIteration, run, profiles,
            measuredAt: DateTimeOffset.UnixEpoch.AddMinutes(5), currentIteration: false);
        Verify(WorkflowWorkspace.ActivityState(priorIteration, run, currentIteration: false) == "Finished" &&
               priorHeader.Contains("Finished", StringComparison.Ordinal) &&
               !priorHeader.Contains("Running", StringComparison.Ordinal) &&
               !priorHeader.Contains("Working", StringComparison.Ordinal),
            "An earlier iteration in the current run freezes as finished instead of borrowing the newest iteration's live state.");
        Verify(WorkflowWorkspace.ActivityRowCaption(new(generation.Id, generation), run, [generation], profiles,
                    measuredAt: DateTimeOffset.UnixEpoch.AddMinutes(5), currentIteration: false)
                .Contains("Awaiting receipt", StringComparison.Ordinal),
            "An unmatched operation in an earlier iteration stays static instead of borrowing the current iteration's running clock.");

        var uncertain = compiler with { Content = [new StructuredContentPart("workflow.command.result",
            CerebralJson.Element(new { state = "Unknown", error = "Execution outcome is not confirmed." }))] };
        var uncertainRow = WorkflowWorkspace.ProjectActivityRows([build, uncertain]).Single();
        Verify(!WorkflowWorkspace.ActivityRowFailed(uncertainRow) &&
               WorkflowWorkspace.ActivityRowCaption(uncertainRow, waiting, [build, uncertain], profiles)
                   .Contains("Outcome unconfirmed", StringComparison.Ordinal),
            "Unknown receipt state remains distinct from failure even when the generic success flag is false.");
        var historicalTransition = new CerebralEvent("old-transition", build.ConversationId, build.TurnId, 9,
            CerebralEventTypes.WorkflowTransition, new(EventActorKind.Service), [],
            new(ModelInclusion.Never, HumanVisibility.Activity), new(EventSourceKind.Service), DateTimeOffset.UnixEpoch,
            CerebralJson.Element(new { status = "Completed", reason = "completed" }));
        var anotherRun = run with { Id = "new-run", Status = WorkflowStatus.Failed };
        Verify(WorkflowWorkspace.ActivityState([build, compiler, historicalTransition], anotherRun) == "Completed" &&
               WorkflowWorkspace.ActivityState([build, compiler], anotherRun) == "Recorded",
            "Historical activity uses its own recorded workflow outcome and never borrows a later run's failure.");

        CerebralEvent EngineTransition(string id, long sequence, string reason, string status, long activeMilliseconds) =>
            new(id, "activity-detail-fixture", "activity-detail-run", sequence, CerebralEventTypes.WorkflowTransition,
                new(EventActorKind.Service, "cerebral.engine"), [],
                new(ModelInclusion.Never, HumanVisibility.Activity),
                new(EventSourceKind.Service, "cerebral.engine", JobId: "activity-detail-run"), DateTimeOffset.UnixEpoch,
                CerebralJson.Element(new { runId = "activity-detail-run", revision = sequence, status, reason,
                    waitingReason = (string?)null, error = (string?)null, cancelRequested = false, steps = sequence,
                    activeMilliseconds, failedEffects = new { } }));
        CerebralEvent WindowCall(int index) =>
            new("window-call-" + index, "activity-detail-fixture", "activity-detail-run", index * 2,
                CerebralEventTypes.ToolCall, new(EventActorKind.Agent, "builder", "Builder"), [],
                new(ModelInclusion.Required, HumanVisibility.Activity), new(EventSourceKind.Agent, AgentId: "builder"),
                DateTimeOffset.UnixEpoch, CerebralJson.Element(new ToolCallData("window-operation-" + index,
                    "cerebral.host:generate", CerebralJson.Element(new { }))));
        CerebralEvent WindowResult(int index) =>
            new("window-result-" + index, "activity-detail-fixture", "activity-detail-run", index * 2 + 1,
                CerebralEventTypes.ToolResult, new(EventActorKind.Tool),
                [new StructuredContentPart("workflow.command.result", CerebralJson.Element(new
                {
                    state = "Succeeded", data = new { prepared = new { inputTokens = 10 },
                        generation = new { generatedTokens = 1 }, elapsedMilliseconds = 1000L }
                }))], new(ModelInclusion.Evidence, HumanVisibility.Activity), new(EventSourceKind.Tool),
                DateTimeOffset.UnixEpoch, CerebralJson.Element(new ToolResultData("window-operation-" + index,
                    "cerebral.host:generate", true, DurationMilliseconds: 1000)));
        var completeWindow = new[] { EngineTransition("window-submitted", 1, "submitted", "Queued", 0) }
            .Concat(Enumerable.Range(1, 35).SelectMany(index => new[] { WindowCall(index), WindowResult(index) }))
            .Append(EngineTransition("window-completed", 100, "completed", "Completed", 434_000)).ToArray();
        var contractedWindow = completeWindow.Skip(1 + 16 * 2).ToArray();
        var laterSelectedRun = completed with { Id = "later-selected-run", Status = WorkflowStatus.Running };
        var completeHeader = WorkflowWorkspace.ActivityHeader(completeWindow, laterSelectedRun, profiles,
            olderActivityOmitted: true);
        var contractedHeader = WorkflowWorkspace.ActivityHeader(contractedWindow, laterSelectedRun, profiles,
            olderActivityOmitted: true);
        Verify(completeHeader.Contains("Completed 7:14", StringComparison.Ordinal) &&
               completeHeader.Contains("35 calls", StringComparison.Ordinal) &&
               completeHeader.Contains("350 in / 35 out", StringComparison.Ordinal) &&
               !completeHeader.Contains("shown", StringComparison.Ordinal) &&
               !completeHeader.Contains("Earlier activity omitted", StringComparison.Ordinal),
            "A complete historical turn uses its exact terminal active time and reports complete call and token totals.");
        Verify(contractedHeader.Contains("Completed 7:14", StringComparison.Ordinal) &&
               contractedHeader.Contains("19 calls shown", StringComparison.Ordinal) &&
               contractedHeader.Contains("190 in / 19 out shown", StringComparison.Ordinal) &&
               contractedHeader.Contains("Earlier activity omitted", StringComparison.Ordinal) &&
               !contractedHeader.Contains("35 calls", StringComparison.Ordinal),
            "Contracting the event window preserves terminal elapsed time while labeling call and token counts as visible subsets.");
        Verify(WorkflowWorkspace.ActivityHeader([completeWindow[^1]], laterSelectedRun, profiles,
                olderActivityOmitted: true) is var emptyPartial &&
               emptyPartial.Contains("Completed 7:14", StringComparison.Ordinal) &&
               emptyPartial.Contains("Earlier activity omitted", StringComparison.Ordinal) &&
               !emptyPartial.Contains("call", StringComparison.OrdinalIgnoreCase),
            "A clipped terminal-only window identifies omitted activity without inventing zero call or token totals.");
        var malformedTail = contractedWindow.Append(new CerebralEvent("foreign-terminal", "activity-detail-fixture",
            "activity-detail-run", 101, CerebralEventTypes.WorkflowTransition, new(EventActorKind.Service), [],
            new(ModelInclusion.Never, HumanVisibility.Activity), new(EventSourceKind.Service), DateTimeOffset.UnixEpoch,
            CerebralJson.Element(new { runId = "activity-detail-run", status = "Completed", activeMilliseconds = 999_000L }))).ToArray();
        Verify(WorkflowWorkspace.ActivityHeader(malformedTail, laterSelectedRun, profiles)
                .Contains("Completed 0:19", StringComparison.Ordinal),
            "A foreign or malformed final transition cannot lend an earlier terminal record a durable elapsed-time floor.");
        var oversizedTerminal = contractedWindow[..^1].Append(
            EngineTransition("oversized-terminal", 100, "completed", "Completed", long.MaxValue)).ToArray();
        Verify(WorkflowWorkspace.ActivityHeader(oversizedTerminal, laterSelectedRun, profiles,
                olderActivityOmitted: true).Contains("Completed 0:19", StringComparison.Ordinal),
            "An out-of-range terminal duration is ignored rather than overflowing the activity formatter.");

        var omitted = WorkflowWorkspace.ActivityResultText(Result("omitted",
            new { state = "Succeeded", data = new { outputOmitted = true } }, tool: "cerebral.host:generate"));
        var retained = WorkflowWorkspace.ActivityResultText(generationResult);
        var lengthLimited = WorkflowWorkspace.ActivityResultText(Result("limited", new { state = "Succeeded",
            data = new { outputOmitted = false, generation = new { finish = "Length", generatedTokens = 7 } } },
            tool: "cerebral.host:generate"));
        Verify(omitted.Contains("omitted", StringComparison.Ordinal) && !omitted.Contains("truncated", StringComparison.Ordinal) &&
               !retained.Contains("omitted", StringComparison.OrdinalIgnoreCase) &&
               lengthLimited.Contains("response limit", StringComparison.Ordinal) &&
               lengthLimited.Contains("7 output", StringComparison.Ordinal) && !lengthLimited.Contains("input", StringComparison.Ordinal),
            "Output omission, response-limit termination and actual measured tokens remain distinct without invented counts.");
        var trueTruncation = WorkflowWorkspace.ActivityResultText(Result("truncated", new { state = "Succeeded",
            data = new { content = JsonSerializer.Serialize(new { truncated = true, out_truncated = false, partial = true }) } }));
        Verify(trueTruncation.Contains("was truncated", StringComparison.Ordinal) &&
               !trueTruncation.Contains("False", StringComparison.OrdinalIgnoreCase) && trueTruncation.Contains("Partial: True", StringComparison.Ordinal),
            "Only actual truncation is labeled; distinct partial state remains visible.");

        var oversizedSummary = Call("summary", "process.run", new { executableId = "dotnet",
            arguments = new[] { "build", new string('x', 20_000), "--token", secretValue } });
        var summary = WorkflowWorkspace.ActivityRowCaption(new(oversizedSummary.Id, oversizedSummary), run, [oversizedSummary], profiles);
        Verify(summary.Length < 260 && !summary.Contains(secretValue, StringComparison.Ordinal),
            "Visible command summaries stay bounded and redacted while full admitted details remain expandable.");
        var remoteReceipt = compiler with { Actor = new(EventActorKind.Tool, DisplayName: "GB10 helper") };
        var orphanCaption = WorkflowWorkspace.ActivityRowCaption(new(remoteReceipt.Id, remoteReceipt), completed,
            [remoteReceipt], profiles);
        Verify(orphanCaption == "Agent not recorded · Failed · Unpaired tool receipt" &&
               !orphanCaption.Contains("GB10 helper", StringComparison.Ordinal) &&
               !orphanCaption.Contains(" ·  · ", StringComparison.Ordinal),
            "An orphan Tool receipt cannot establish a recorded agent from its display name or repeat separators.");
        var recordedHelperCall = Call("recorded-helper", "process.run", new
        {
            executableId = "dotnet", arguments = new[] { "--version" }
        }) with
        {
            Actor = new(EventActorKind.Agent, "gb10-helper", "GB10 helper"),
            Provenance = new(EventSourceKind.Agent, AgentId: "gb10-helper")
        };
        var recordedHelperReceipt = Result("recorded-helper", new { state = "Succeeded" }) with
        {
            Provenance = new(EventSourceKind.Tool, AgentId: "gb10-helper")
        };
        var recordedHelperRows = WorkflowWorkspace.ProjectActivityRows([recordedHelperCall, recordedHelperReceipt]);
        Verify(recordedHelperRows.Count == 1 && recordedHelperRows[0].Call?.Id == recordedHelperCall.Id &&
               recordedHelperRows[0].Event.Id == recordedHelperReceipt.Id,
            "A matching recorded Agent call and Tool receipt retain one paired activity operation.");
        var recordedHelperCaption = WorkflowWorkspace.ActivityRowCaption(recordedHelperRows[0], completed,
            [recordedHelperCall, recordedHelperReceipt], profiles);
        Verify(recordedHelperCaption.StartsWith("GB10 helper · Completed · ", StringComparison.Ordinal) &&
               !recordedHelperCaption.Contains("Agent not recorded", StringComparison.Ordinal) &&
               !recordedHelperCaption.Contains(" ·  · ", StringComparison.Ordinal),
            "A paired call retains its recorded agent identity when Actor.Id and both provenance AgentIds agree.");
        var otherConversationCall = build with { Id = "other-conversation-build", ConversationId = "another-conversation", Sequence = 2 };
        Verify(WorkflowWorkspace.ProjectActivitySections(WorkflowWorkspace.ProjectActivityRows([build, otherConversationCall])).Count == 2,
            "Operation sections never merge calls from different conversations even when their turn and purpose match.");
        string Inline(CerebralEvent item, CerebralEvent? call = null) =>
            WorkflowWorkspace.ActivityInlineDetail(new(call?.Id ?? item.Id, item, call));
        void InlineBound(string text, string label) =>
            Verify(text.Length <= 180 && !text.Contains('\n') && !text.Contains('\r'),
                label + " must be a bounded plain-text line.");
        var inlineRead = Inline(read);
        Verify(inlineRead.StartsWith("Operation: ", StringComparison.Ordinal) && inlineRead.Contains(filePath, StringComparison.Ordinal) &&
               inlineRead.Contains("Start line: 31", StringComparison.Ordinal) && inlineRead.Contains("Line count: 12", StringComparison.Ordinal),
            "Pending operation detail must show the recorded file target and range without expanding the row.");
        Readable(inlineRead, "Inline file target");
        InlineBound(inlineRead, "Inline file target");
        var inlineBuild = Inline(build);
        Verify(inlineBuild.Contains("Executable: dotnet", StringComparison.Ordinal) &&
               inlineBuild.Contains("Arguments: build", StringComparison.Ordinal) && inlineBuild.Contains(projectPath, StringComparison.Ordinal),
            "Inline build detail uses recorded executable and separate argv rather than inferred intent.");
        InlineBound(inlineBuild, "Inline build target");
        var inlineFailure = Inline(compiler, build);
        Verify(inlineFailure.StartsWith("Result: Error output: ", StringComparison.Ordinal) &&
               inlineFailure.Contains("CS0246", StringComparison.Ordinal) && inlineFailure.Contains("FixtureMissingType", StringComparison.Ordinal) &&
               !inlineFailure.Contains("Build started", StringComparison.Ordinal) && !inlineFailure.Contains("receipt-fixture-01", StringComparison.Ordinal),
            "A failed operation exposes its real compiler diagnostic before generic stdout and receipt bookkeeping.");
        InlineBound(inlineFailure, "Inline compiler diagnostic");
        var inlineFinding = Inline(Result("finding", new { state = "Succeeded",
            data = new { summary = "Saved café note.\nReload verified.", receiptId = "fixture-receipt" } }));
        Verify(inlineFinding == "Result: Summary: Saved café note.",
            "Inline findings preserve Unicode and select a real recorded result excerpt.");
        Verify(!Inline(secretCall).Contains(secretValue, StringComparison.Ordinal) &&
               !Inline(Result("secret", new { state = "Succeeded", data = new { stdout = "Completed " + secretValue } }), secretCall)
                   .Contains(secretValue, StringComparison.Ordinal),
            "Inline operations and echoed results reuse known-secret redaction.");
        var inlineSource = Inline(Call("inline-source", "file.write", new { path = filePath, content = privateSource }));
        Verify(inlineSource.Contains(filePath, StringComparison.Ordinal) && !inlineSource.Contains(privateSource, StringComparison.Ordinal),
            "Inline file targets never include source-body payloads.");
        foreach (var tool in new[] { "cerebral.host:generate", "cerebral.host:compact", "cerebral.host:history" })
        {
            var privateCall = Call("inline-private-" + tool, tool, new { context = privateContext, reasoning = privateContext }, wrapped: false);
            var privateResult = Result("inline-private-" + tool, new { state = "Succeeded",
                data = new { generation = new { text = privateContext, reasoning = privateContext }, context = privateContext } }, tool: tool);
            Verify(!Inline(privateCall).Contains(privateContext, StringComparison.Ordinal) &&
                   !Inline(privateResult, privateCall).Contains(privateContext, StringComparison.Ordinal),
                "Inline detail excludes prepared context and private model output: " + tool + ".");
        }
        var inlineMalformed = Inline(Result("inline-json", new { state = "Failed", data = new { content = "{not-valid-json" } }, success: false));
        Verify(inlineMalformed == "Result: Structured output is available in the expanded detail.",
            "Malformed output envelopes are not copied as raw JSON into the visible activity line.");
        InlineBound(Inline(Result("inline-large", new { state = "Succeeded", data = new { stdout = new string('x', 60_000) } })),
            "Oversized inline output");
        foreach (var count in new[] { 160, 161, 162 })
        {
            var inlineUnicode = Inline(Result("inline-unicode-" + count, new { state = "Succeeded",
                data = new { message = new string('x', count) + "\U0001F600" + new string('y', 30) } }));
            InlineBound(inlineUnicode, "Unicode inline output");
            var validUtf16 = true;
            for (var index = 0; index < inlineUnicode.Length; index++)
            {
                if (char.IsHighSurrogate(inlineUnicode[index]))
                {
                    if (++index >= inlineUnicode.Length || !char.IsLowSurrogate(inlineUnicode[index])) { validUtf16 = false; break; }
                }
                else if (char.IsLowSurrogate(inlineUnicode[index])) { validUtf16 = false; break; }
            }
            Verify(validUtf16, "Inline excerpt boundaries cannot split an astral character.");
        }

        CerebralEvent Audit(string id, object? data) =>
            new(id, "activity-detail-fixture", "activity-detail-run", 3, "workflow.audit",
                new(EventActorKind.Service, "audit-fixture"), [],
                new(ModelInclusion.OnDemand, HumanVisibility.Activity), new(EventSourceKind.Service, "audit-fixture"),
                DateTimeOffset.UnixEpoch, CerebralJson.Element(data));
        bool ValidUtf16(string text)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (char.IsHighSurrogate(text[index]))
                {
                    if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false;
                }
                else if (char.IsLowSurrogate(text[index])) return false;
            }
            return true;
        }
        string AuditDetail(CerebralEvent item)
        {
            var text = WorkflowWorkspace.ActivityAuditText(item) ?? "";
            Bounded(text, "Recorded audit detail");
            Verify(ValidUtf16(text), "Audit detail keeps valid UTF-16 at excerpt boundaries.");
            return text;
        }
        const string privateAuditHash = "ad1cbb7b56ea4a7fb1e703d93f1ae152e9d729da5cc3499bbcb4d9eeefcfd238";
        const string privateAuditProposal = "PRIVATE AUDIT PROPOSAL MUST NOT APPEAR";
        const string privateAuditReasoning = "PRIVATE AUDIT REASONING MUST NOT APPEAR";
        var auditResult = new WorkflowAuditResult("audit-request", "audit-fixture", 1, "private-fingerprint",
            privateAuditHash, privateAuditHash, "reviewed", "The recorded review found the build check complete.");
        // Exact WorkflowStore.Commit shape: result is nested and typed, not a top-level summary or invented findings list.
        var audit = Audit("actual-audit-shape", new
        {
            purpose = "before.response", requestId = auditResult.RequestId, engineEpoch = 3,
            inputSha256 = auditResult.InputSha256, proposalSha256 = auditResult.ProposalSha256,
            result = auditResult, held = false
        });
        var auditText = AuditDetail(audit);
        var auditInline = Inline(audit);
        Verify(auditText.Contains("Summary: " + auditResult.Summary, StringComparison.Ordinal) &&
               auditText.Contains("Disposition: reviewed", StringComparison.Ordinal) &&
               auditText.Contains("Checkpoint: before.response", StringComparison.Ordinal) &&
               auditText.Contains("Review effect: Did not block the workflow", StringComparison.Ordinal),
            "Expanded audit projection uses actual nested Summary/Disposition and recorded purpose/held state.");
        Verify(auditInline.Contains(auditResult.Summary, StringComparison.Ordinal) &&
               auditInline.Contains("Disposition: reviewed", StringComparison.Ordinal),
            "Inline audit detail reuses the nested recorded review rather than an empty generic activity excerpt.");
        InlineBound(auditInline, "Inline recorded audit");
        Verify(!auditText.Contains(privateAuditHash, StringComparison.Ordinal) &&
               !auditText.Contains(auditResult.Fingerprint, StringComparison.Ordinal) &&
               !auditInline.Contains(privateAuditHash, StringComparison.Ordinal),
            "Audit projection omits hashes, fingerprints and adapter bookkeeping.");

        foreach (var disposition in new[] { "failed", "held", "gap", "unknown", "future_audit_state", "unconfigured" })
        {
            var summaryText = disposition == "unconfigured" ? "" : "Recorded review status: " + disposition;
            var entry = Audit("audit-" + disposition, new { purpose = "checkpoint", result = auditResult with
                { Disposition = disposition, Summary = summaryText }, held = disposition == "held" });
            var text = AuditDetail(entry);
            Verify(text.Contains("Disposition: " + disposition, StringComparison.Ordinal) &&
                   WorkflowWorkspace.ProjectActivityRows([entry]).Count == 1,
                "Failed, held, gap, unknown and empty/unconfigured audit entries remain visible: " + disposition + ".");
            Verify(Inline(entry).Contains(disposition, StringComparison.Ordinal), "Inline audit status stays visible: " + disposition + ".");
        }
        var unavailableAudit = Audit("unavailable-audit", new { purpose = "before.effect",
            result = auditResult with { Disposition = "unavailable", Summary = "No admitted auditor is selected; deterministic checks remain authoritative." },
            held = false });
        Verify(AuditDetail(unavailableAudit).Contains("Review effect: Did not block the workflow", StringComparison.Ordinal) &&
               Inline(unavailableAudit).Contains("unavailable", StringComparison.Ordinal),
            "An unavailable reviewer is distinguished from a workflow held for required review.");
        var visibleGenerationInput = WorkflowWorkspace.ActivityCommandText(
            Call("private-caption", "cerebral.host:generate", new { context = privateContext }, wrapped: false));
        Verify(visibleGenerationInput.Contains("hidden in this activity view", StringComparison.Ordinal) &&
               !visibleGenerationInput.Contains(privateContext, StringComparison.Ordinal),
            "The input caption identifies display redaction without disclosing prepared context.");

        var heldAudit = Audit("held-audit", new { purpose = "before.effect",
            result = auditResult with { Disposition = "failed", Summary = "Required review was unavailable." }, held = true });
        Verify(AuditDetail(heldAudit).Contains("Review effect: Workflow held", StringComparison.Ordinal) &&
               Inline(heldAudit).Contains("Review effect: Workflow held", StringComparison.Ordinal),
            "A required held audit cannot lose its held state behind the summary.");
        var privateAudit = Audit("audit-private", new
        {
            purpose = "before.response", held = false, inputSha256 = privateAuditHash, proposalSha256 = privateAuditHash,
            context = new { raw = privateContext, apiKey = secretValue }, proposal = privateAuditProposal,
            reasoning = privateAuditReasoning,
            result = new { disposition = "reviewed", summary = "Reviewed; echoed value " + secretValue,
                findings = new { raw = privateSource }, context = privateContext, reasoning = privateAuditReasoning }
        }) with { Content = [new TextContentPart(privateAuditReasoning)] };
        var privateAuditText = AuditDetail(privateAudit);
        foreach (var hidden in new[] { privateContext, privateSource, privateAuditProposal, privateAuditReasoning, privateAuditHash, secretValue })
            Verify(!privateAuditText.Contains(hidden, StringComparison.Ordinal) && !Inline(privateAudit).Contains(hidden, StringComparison.Ordinal),
                "Audit projection excludes private fields, arbitrary objects and known secret echoes.");
        Verify(privateAuditText.Contains("Reviewed", StringComparison.Ordinal) && privateAuditText.Contains("[Redacted]", StringComparison.Ordinal),
            "Audit sanitization keeps useful review text while redacting known credential values.");

        foreach (var data in new object?[] { null, "PRIVATE RAW AUDIT", new[] { privateSource },
                     new { held = true, purpose = "before.effect", error = "Audit adapter failed." },
                     new { held = "invalid", purpose = new { raw = privateSource },
                         result = new { disposition = 7, summary = new { raw = privateSource } } },
                     new { held = false, result = new[] { privateSource } } })
        {
            var entry = Audit("audit-malformed", data);
            var text = AuditDetail(entry);
            Verify(text.Length > 0 && WorkflowWorkspace.ProjectActivityRows([entry]).Count == 1 &&
                   !text.Contains(privateSource, StringComparison.Ordinal) && !text.Contains("PRIVATE RAW AUDIT", StringComparison.Ordinal),
                "Malformed or missing audit data stays visibly unavailable without serializing opaque values.");
        }
        Verify(AuditDetail(Audit("audit-error", new { held = true, error = "Audit adapter failed." }))
                .Contains("Audit adapter failed.", StringComparison.Ordinal),
            "Existing public failure diagnostics survive a missing nested audit result.");
        using (var duplicateAudit = JsonDocument.Parse("""{"held":true,"held":false,"result":{"summary":"conflicting"}}"""))
            Verify(AuditDetail(audit with { Data = duplicateAudit.RootElement.Clone() }).Contains("Malformed", StringComparison.Ordinal),
                "Duplicate audit fields are shown as malformed instead of silently selecting a conflicting held state.");
        using (var invalidAuditText = JsonDocument.Parse("""{"held":true,"result":{"disposition":"failed","summary":"\uD800"}}"""))
            Verify(AuditDetail(audit with { Data = invalidAuditText.RootElement.Clone() }).Contains("Malformed", StringComparison.Ordinal),
                "Malformed encoded audit text cannot escape the bounded projection as an exception.");
        var overwideAudit = Enumerable.Range(0, 65).ToDictionary(index => "field" + index, _ => (object?)privateSource);
        Verify(AuditDetail(Audit("audit-wide", overwideAudit)).Contains("Malformed", StringComparison.Ordinal),
            "Overwide audit objects are bounded without serializing arbitrary members.");
        foreach (var count in new[] { 130, 131, 132, 16_318, 16_319, 16_320, 300_000 })
        {
            var entry = Audit("audit-size-" + count, new { held = true, purpose = "before.response",
                result = auditResult with { Disposition = "failed", Summary = new string('x', count) + "\U0001F600" + new string('y', 90) } });
            var text = AuditDetail(entry);
            var inline = Inline(entry);
            Bounded(text, "Oversized audit");
            InlineBound(inline, "Oversized inline audit");
            Verify(ValidUtf16(inline) && text.Contains("Review effect: Workflow held", StringComparison.Ordinal) &&
                   text.Contains("Disposition: failed", StringComparison.Ordinal),
                "Audit size/Unicode bounds keep failure and held state visible.");
        }
        Verify(WorkflowWorkspace.ActivityAuditText(read) is null,
            "The audit projection does not replace ordinary command/result formatting.");

        return checks;
    }
}
