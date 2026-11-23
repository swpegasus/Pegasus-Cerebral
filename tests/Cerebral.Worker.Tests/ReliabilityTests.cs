using System.Text.Json;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class ReliabilityTests
{
    public static async Task RunAsync()
    {
        WorkingContextProjectionTests.Run();
        var assertions = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); assertions++; Console.WriteLine("PASS: " + name); }
        JsonElement Json(object value) => CerebralJson.Element(value);
        var project = Path.Combine(Path.GetTempPath(), "cerebral-reliability-fixture");
        var initial = "Create a WinForms Hello World app, build it, and open the actual application.";
        var steering = "Preserve the existing project and fix the observed crash.";
        var warning = "Form1.cs(18,19): warning CS0649: Field 'Form1.label' is never assigned to, and will always have its default value null.";
        var processOutput = Json(new { exitCode = 0, stdout = warning + "\nBuild succeeded.", stderr = "" }).GetRawText();
        var session = new TaskSession
        {
            Checkpoint = "The app ran successfully; field declaration ordering caused the crash.",
            CheckpointThroughSequence = 6,
            Messages =
            [
                new() { Sequence = 1, Role = "user", Origin = "user", Content = initial },
                new() { Sequence = 2, Role = "assistant", Origin = "agent", Content = "Build action." },
                new() { Sequence = 3, Role = "user", Origin = "tool", ToolName = "process.run", Content = "Tool process.run; success=True; truncated=False\n" + processOutput },
                new() { Sequence = 4, Role = "user", Origin = "user", Content = steering },
                new() { Sequence = 5, Role = "user", Origin = "tool", ToolName = "file.read", Content = "Tool file.read; success=False; truncated=False\nFile not found." },
                new() { Sequence = 6, Role = "user", Origin = "helper", Content = "Helper says everything worked." },
                new() { Sequence = 7, Role = "assistant", Origin = "agent", Content = "Recent action." },
                new() { Sequence = 8, Role = "user", Origin = "service", Content = "Recent receipt." }
            ]
        };
        var context = WorkingContext.Assemble(session, "app protocol", 1);
        var contextText = string.Join("\n", context.Select(x => x.Content));
        Check(contextText.Contains(initial, StringComparison.Ordinal) && contextText.Contains(steering, StringComparison.Ordinal),
            "compaction preserves the original job request and latest actual user steering verbatim");
        Check(contextText.Contains(warning, StringComparison.Ordinal) && contextText.Contains("Source message 3; role=user; origin=tool; tool=process.run", StringComparison.Ordinal),
            "actual compiler warning and source provenance survive a factually wrong checkpoint");
        Check(contextText.Contains("may contain mistaken interpretations", StringComparison.Ordinal) && contextText.Contains("File not found.", StringComparison.Ordinal),
            "generated checkpoint remains fallible reference and cannot erase decisive failures");
        Check(contextText.Split(steering, StringSplitOptions.None).Length == 2 && !WorkingContext.IsUserRequest(session.Messages[5]),
            "helper receipts are never mistaken for the latest user request");
        Check(session.Messages[2].Content == "Tool process.run; success=True; truncated=False\n" + processOutput && session.Messages.Count == 8,
            "assembling bounded context does not mutate retained source messages");
        session.Messages[4].ExcludedFromContext = true;
        Check(!string.Join("\n", WorkingContext.Assemble(session, "system", 1).Select(x => x.Content)).Contains("File not found.", StringComparison.Ordinal),
            "removed messages are not restored by the source-evidence preservation layer");
        var later = string.Join("\n", WorkingContext.Assemble(session, "system", 4).Select(x => x.Content));
        Check(!later.Contains(warning, StringComparison.Ordinal), "a new job does not pin unrelated earlier process diagnostics");
        var verbose = Json(new { exitCode = 1, stdout = new string('x', 12000) + "\n" + warning, stderr = "Unhandled exception: NullReferenceException" }).GetRawText();
        var diagnostic = ExecutionReliability.Diagnostic(verbose);
        Check(diagnostic.Length <= 2000 && diagnostic.Contains("CS0649", StringComparison.Ordinal) && diagnostic.Contains("NullReferenceException", StringComparison.Ordinal),
            "bounded process evidence extracts diagnostic lines instead of retaining an unbounded log");
        Check(ExecutionReliability.Clip(new string('x', 3000), 400).Length == 400 && ExecutionReliability.Clip("long", 1).Length == 1,
            "source excerpt bounds include the truncation marker");
        const string dispatchedDraft = "A bounded chapter passage that belongs in the artifact, not in repeated model context.";
        var savedAction = ExecutionReliability.ActionForContext(Json(new
        {
            action = "tool", name = "file.append",
            arguments = new { path = Path.Combine(project, "draft.txt"), content = dispatchedDraft, expectedSha256 = new string('a', 64), expectedBytes = 12 }
        }));
        Check(savedAction.Contains("file.append", StringComparison.Ordinal) && savedAction.Contains("draft.txt", StringComparison.Ordinal) &&
            savedAction.Contains($"{dispatchedDraft.Length:N0} characters", StringComparison.Ordinal) && !savedAction.Contains(dispatchedDraft, StringComparison.Ordinal),
            "saved file actions retain routing and receipt fields without feeding dispatched prose back into active context");
        var activityArguments = ExecutionReliability.ArgumentsForActivity("file.append", Json(new
        {
            path = Path.Combine(project, "draft.txt"), content = dispatchedDraft,
            expectedSha256 = new string('a', 64), expectedBytes = 12
        }));
        Check(activityArguments.GetProperty("path").GetString()!.EndsWith("draft.txt", StringComparison.Ordinal) &&
            activityArguments.GetProperty("content").GetString()!.Contains($"{dispatchedDraft.Length:N0} characters", StringComparison.Ordinal) &&
            !activityArguments.GetRawText().Contains(dispatchedDraft, StringComparison.Ordinal),
            "live activity keeps file routing and preconditions while omitting a generated passage payload");

        var arguments = Json(new { executableId = "dotnet", workingDirectory = project, arguments = new[] { "build", "Wrong.csproj" } });
        var reordered = Json(new { arguments = new[] { "build", "Wrong.csproj" }, workingDirectory = project, executableId = "dotnet" });
        var failure = ExecutionReliability.FromTool("process.run", arguments, new(false, Json(new { exitCode = 1, stdout = "", stderr = "error: project missing" }).GetRawText()), 11);
        failure.At = DateTimeOffset.UtcNow.AddMinutes(-3);
        Check(ExecutionReliability.Fingerprint("process.run", arguments) == ExecutionReliability.Fingerprint("process.run", reordered),
            "retry identity ignores JSON object field order");
        var second = ExecutionReliability.FromTool("process.run", reordered, new(false, "same missing project"), 13); second.At = failure.At.AddSeconds(1);
        Check(ExecutionReliability.ExecutableAttemptCount([failure, second], "process.run", arguments) == 0,
            "ordinary dotnet build and test commands do not consume the user-application launch limit");
        var appRunArguments = Json(new { executableId = "dotnet", workingDirectory = project, arguments = new[] { "run", "--project", "App.csproj" } });
        var appRun = ExecutionReliability.FromTool("process.run", appRunArguments, new(false, "Application exited."), 13);
        Check(ExecutionReliability.ExecutableAttemptCount([appRun, appRun], "process.run", appRunArguments) == 2,
            "launching a built application through dotnet run is still bounded per assignment");
        var presentationRoot = Path.Combine(Path.GetTempPath(), "cerebral-workspace-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(presentationRoot);
        try
        {
            var application = Path.Combine(presentationRoot, "HelloPegasus.exe");
            File.WriteAllBytes(application, [0x4d, 0x5a]);
            var mistakenApplicationRun = Json(new { executableId = "HelloPegasus.exe", workingDirectory = presentationRoot, arguments = Array.Empty<string>() });
            Check(ExecutionReliability.WorkspaceApplicationForPresentation("process.run", mistakenApplicationRun,
                    [new ExecutableRegistration { Id = "dotnet", Path = "dotnet.exe" }]) == application,
                "an existing argument-free workspace executable sent as an unregistered process ID is redirected to visible application presentation");
            Check(ExecutionReliability.WorkspaceApplicationForPresentation("process.run",
                    Json(new { executableId = "HelloPegasus.exe", workingDirectory = presentationRoot, arguments = new[] { "--unsafe-to-drop" } }), []) is null &&
                  ExecutionReliability.WorkspaceApplicationForPresentation("process.run",
                    Json(new { executableId = "dotnet", workingDirectory = presentationRoot, arguments = Array.Empty<string>() }),
                    [new ExecutableRegistration { Id = "dotnet", Path = "dotnet.exe" }]) is null,
                "workspace application adaptation never discards arguments or overrides a registered command ID");
        }
        finally { Directory.Delete(presentationRoot, true); }
        Check(ExecutionReliability.IdenticalFailureCount([failure, second], "process.run", arguments) == 2,
            "two identical unresolved failures are recognized before a third execution");
        var repairedSource = ExecutionReliability.FromTool("file.edit", Json(new { path = Path.Combine(project, "Form1.cs") }), new(true, "File edited."), 14);
        repairedSource.At = second.At.AddSeconds(1);
        Check(ExecutionReliability.IdenticalFailureCount([failure, second, repairedSource], "process.run", arguments) == 0,
            "the same build command can run again after a source repair in its project");
        var unrelatedEdit = ExecutionReliability.FromTool("file.edit", Json(new { path = Path.Combine(project + "-other", "Form1.cs") }), new(true, "File edited."), 14);
        unrelatedEdit.At = repairedSource.At;
        Check(ExecutionReliability.IdenticalFailureCount([failure, second, unrelatedEdit], "process.run", arguments) == 2,
            "an unrelated project edit cannot disguise a blind repeated failure");
        var correctedArgs = Json(new { executableId = "dotnet", workingDirectory = project, arguments = new[] { "build", "Actual.csproj" } });
        var proof = ExecutionReliability.FromTool("process.run", correctedArgs, new(true, Json(new { exitCode = 0, stdout = "Build succeeded.", stderr = "" }).GetRawText()), 15);
        proof.At = failure.At.AddMinutes(1);
        Check(ExecutionReliability.IdenticalFailureCount([failure, second], "process.run", correctedArgs) == 0,
            "corrected arguments can execute after repeated failures");
        var repeatedReadArguments = Json(new { bookId = "book-1", chapterId = 4, passageIds = new[] { 19, 20 } });
        var successfulRead = ExecutionReliability.FromTool("book.chapter.read", repeatedReadArguments,
            new(true, "bounded prose"), 14);
        Check(ExecutionReliability.LastToolCallWasIdenticalSuccess([successfulRead], "book.chapter.read", repeatedReadArguments),
            "an immediately repeated successful tool call is detected before executing it again");
        Check(!ExecutionReliability.LastToolCallWasIdenticalSuccess([successfulRead], "book.chapter.read",
                Json(new { bookId = "book-1", chapterId = 4, passageIds = new[] { 20 } })),
            "a changed bounded read is not mistaken for a successful replay");
        Check(!ExecutionReliability.LastToolCallWasIdenticalSuccess([successfulRead, repairedSource],
                "book.chapter.read", repeatedReadArguments),
            "an intervening tool action allows the workflow to return to a previously successful read");
        Check(ExecutionReliability.ReconcileOutcome("completed", [failure, proof]) == "incomplete",
            "a later success cannot silently clear an unresolved failure");
        Check(ExecutionReliability.ApplyExactSuccessfulRetry([failure, proof], proof) == 0 && failure.Resolution is null,
            "a corrected command with different arguments still requires an explicit failure resolution");
        var writeArguments = Json(new { path = Path.Combine(project, "Program.cs"), content = "Console.WriteLine(\"proof\");" });
        var failedWrite = ExecutionReliability.FromTool("file.write", writeArguments, new(false, "Parent directory does not exist."), 17);
        failedWrite.At = failure.At;
        var successfulRetry = ExecutionReliability.FromTool("file.write", writeArguments, new(true, "File written."), 19);
        successfulRetry.At = failedWrite.At.AddSeconds(1);
        var exactRetryEvidence = new List<ExecutionEvidence> { failedWrite, successfulRetry };
        Check(ExecutionReliability.ApplyExactSuccessfulRetry(exactRetryEvidence, successfulRetry) == 1 &&
            failedWrite.Resolution!.Contains(successfulRetry.Id, StringComparison.Ordinal) &&
            ExecutionReliability.ReconcileOutcome("completed", exactRetryEvidence) == "completed",
            "an identical successful retry resolves the earlier failure while retaining service evidence provenance");
        var rejectedPassage = ExecutionReliability.FromTool("book.chapter.append",
            Json(new { bookId = "book-one", chapterId = 3, sceneId = 8, operationId = "bounded-step", content = "repeated draft" }),
            new(false, "duplicate passage"), 19);
        rejectedPassage.At = failure.At;
        var correctedPassage = ExecutionReliability.FromTool("book.chapter.append",
            Json(new { bookId = "book-one", chapterId = 3, sceneId = 8, operationId = "bounded-step", content = "materially different draft" }),
            new(true, "passage saved"), 20);
        correctedPassage.At = proof.At;
        var differentScene = ExecutionReliability.FromTool("book.chapter.append",
            Json(new { bookId = "book-one", chapterId = 3, sceneId = 9, operationId = "other-step", content = "other scene" }),
            new(true, "passage saved"), 21);
        differentScene.At = proof.At;
        var correctedEvidence = new List<ExecutionEvidence> { rejectedPassage, differentScene };
        Check(ExecutionReliability.ApplyExactSuccessfulRetry(correctedEvidence, differentScene) == 0 && rejectedPassage.Resolution is null,
            "a successful generated append to a different scene cannot resolve an earlier rejection");
        correctedEvidence.Add(correctedPassage);
        Check(ExecutionReliability.ApplyExactSuccessfulRetry(correctedEvidence, correctedPassage) == 1 &&
              rejectedPassage.Resolution!.Contains(correctedPassage.Id, StringComparison.Ordinal),
            "a corrected generated append resolves an earlier rejection for the same stable book target and operation");
        var rejectedAssessment = ExecutionReliability.FromTool("book.chapter.review.assess",
            Json(new { bookId = "book-one", chapterId = 3, chapterRevision = 8, batchNumber = 2, assessment = "missing findings" }),
            new(false, "The chapter assessment requires a findings array."), 22);
        rejectedAssessment.At = failure.At;
        var correctedAssessment = ExecutionReliability.FromTool("book.chapter.review.assess",
            Json(new { bookId = "book-one", chapterId = 3, chapterRevision = 8, batchNumber = 2, assessment = "valid findings" }),
            new(true, "Assessment stored."), 23);
        correctedAssessment.At = proof.At;
        var wrongBatchAssessment = ExecutionReliability.FromTool("book.chapter.review.assess",
            Json(new { bookId = "book-one", chapterId = 3, chapterRevision = 8, batchNumber = 3, assessment = "valid findings" }),
            new(true, "Assessment stored."), 24);
        wrongBatchAssessment.At = proof.At;
        var assessmentEvidence = new List<ExecutionEvidence> { rejectedAssessment, wrongBatchAssessment };
        Check(ExecutionReliability.ApplyExactSuccessfulRetry(assessmentEvidence, wrongBatchAssessment) == 0 && rejectedAssessment.Resolution is null,
            "a successful generated assessment for another review batch cannot clear a prior rejection");
        assessmentEvidence.Add(correctedAssessment);
        Check(ExecutionReliability.ApplyExactSuccessfulRetry(assessmentEvidence, correctedAssessment) == 1 &&
              ExecutionReliability.ReconcileOutcome("completed", assessmentEvidence) == "completed",
            "a corrected generated assessment resolves its rejected payload for the same immutable review batch");
        var otherProject = ExecutionReliability.FromTool("process.run", Json(new { executableId = "dotnet", workingDirectory = project + "-other", arguments = new[] { "build" } }), new(true, "Build succeeded."), 16);
        otherProject.At = proof.At.AddSeconds(1);
        var rejected = ExecutionReliability.ApplyResolutions([failure, otherProject], Json(new[] { new { failureId = failure.Id, evidenceId = otherProject.Id, reason = "A different project built." } }));
        Check(rejected.Count == 1 && failure.Resolution is null, "unrelated project success cannot resolve a failed build");
        var accepted = ExecutionReliability.ApplyResolutions([failure, proof], Json(new[] { new { failureId = failure.Id, evidenceId = proof.Id, reason = "Corrected the project path and built the intended project." } }));
        Check(accepted.Count == 0 && !failure.Success && failure.Resolution!.Contains(proof.Id, StringComparison.Ordinal) &&
            ExecutionReliability.ReconcileOutcome("completed", [failure, proof]) == "completed",
            "explicit repair can complete while preserving the original failure and resolution provenance");
        var incompleteReview = ExecutionReliability.FromTool("book.chapter.review.complete",
            Json(new { bookId = "book-one", chapterId = 2, note = "Coverage was incomplete." }), new(false, "Missing passage ID 7."), 17);
        incompleteReview.At = failure.At;
        var completedReview = ExecutionReliability.FromTool("book.chapter.review.complete",
            Json(new { bookId = "book-one", chapterId = 2, note = "All passages were read." }), new(true, "Reviewed."), 19);
        completedReview.At = proof.At;
        var wrongChapterReview = ExecutionReliability.FromTool("book.chapter.review.complete",
            Json(new { bookId = "book-one", chapterId = 3, note = "Different chapter." }), new(true, "Reviewed."), 20);
        wrongChapterReview.At = proof.At;
        Check(ExecutionReliability.Related(incompleteReview, completedReview) && !ExecutionReliability.Related(incompleteReview, wrongChapterReview) &&
              ExecutionReliability.ApplyResolutions([incompleteReview, completedReview], Json(new[]
              {
                  new { failureId = incompleteReview.Id, evidenceId = completedReview.Id, reason = "Read the missing passage and retried the same chapter review." }
              })).Count == 0,
            "a corrected plugin call can resolve an earlier failure for the same typed book and chapter without matching mutable note text");
        var missing = ExecutionReliability.FromTool("file.read", Json(new { path = Path.Combine(project, "Wrong.cs") }), new(false, "missing file"), 21); missing.At = failure.At;
        var found = ExecutionReliability.FromTool("file.read", Json(new { path = Path.Combine(project, "Actual.cs") }), new(true, "source"), 23); found.At = proof.At;
        Check(ExecutionReliability.ApplyResolutions([missing, found], Json(new[] { new { failureId = missing.Id, evidenceId = found.Id, reason = "Located the actual source filename." } })).Count == 0,
            "a repaired exploratory file path need not remain a permanent task failure");
        var contradictory = ExecutionReliability.FromTool("process.run", correctedArgs, new(true, Json(new { exitCode = 1, stdout = "", stderr = "error" }).GetRawText()), 25);
        Check(!contradictory.Success && contradictory.ExitCode == 1, "explicit nonzero process exit cannot be promoted by a success flag");
        Check(proof.Kind == "tool" && proof.Name == "process.run" && proof.RequestId is null,
            "a successful wrapper process is recorded only as process evidence, never application observation");
        var executable = Path.Combine(project, "bin", "Hello.exe");
        var crash = new ExecutionEvidence { Kind = "application-exit", Name = executable, Success = false, ExitCode = 1, At = failure.At, RequestId = "old-launch" };
        var ready = new ExecutionEvidence { Kind = "application-ready", Name = executable, Success = true, At = proof.At, RequestId = "repaired-launch" };
        var startedOnly = new ExecutionEvidence { Kind = "application-start", Name = executable, Success = true, At = proof.At, RequestId = "repaired-launch" };
        Check(!ExecutionReliability.Related(crash, startedOnly) && !ExecutionReliability.Related(crash, proof),
            "a new process-start receipt or successful wrapper cannot resolve an observed application crash");
        Check(ExecutionReliability.ApplyResolutions([crash, ready], Json(new[] { new { failureId = crash.Id, evidenceId = ready.Id, reason = "The repaired executable's window was observed responsive." } })).Count == 0,
            "later direct readiness of the same executable can resolve its earlier startup crash");
        ready.Name = Path.Combine(project, "bin", "Other.exe");
        Check(!ExecutionReliability.Related(crash, ready), "readiness of a different executable cannot resolve the original application failure");
        var brief = ExecutionReliability.EvidenceBrief([contradictory, proof]);
        Check(brief.Contains(contradictory.Id, StringComparison.Ordinal) && brief.Contains("messageSequence", StringComparison.Ordinal) && brief.Contains("exitCode", StringComparison.Ordinal),
            "bounded evidence exposes stable resolution IDs and actual source references");
        var requiredMath = new[] { "math.calculate" };
        Check(ExecutionReliability.MissingSuccessfulToolEvidence([], []).Count == 0 &&
            ExecutionReliability.MissingSuccessfulToolEvidence(requiredMath, []).SequenceEqual(requiredMath),
            "ordinary turns need no tool receipt while a required tool does");
        Check(ExecutionReliability.MissingSuccessfulToolEvidence(requiredMath, [failure]).SequenceEqual(requiredMath) &&
            ExecutionReliability.MissingSuccessfulToolEvidence(requiredMath, [proof]).SequenceEqual(requiredMath),
            "failed or unrelated successful evidence cannot satisfy a required tool");
        var mathProof = ExecutionReliability.FromTool("math.calculate", Json(new { expression = "choose(9,2)*7" }), new(true, "252"), 30);
        Check(ExecutionReliability.MissingSuccessfulToolEvidence(requiredMath, [mathProof]).Count == 0 &&
            ExecutionReliability.MissingSuccessfulToolEvidence(["math.calculate", "math.calculate"], [mathProof]).Count == 0,
            "matching successful service evidence satisfies a required tool exactly once");

        var schema = Json(new { type = "object", properties = new { action = new { type = "string" } } });
        var request = Json(ReasoningCompletion.CreateRequest([new("user", "hello")], 2000, 0.2, true, schema, 512, []));
        Check(request.GetProperty("max_tokens").GetInt32() == 2512 && request.GetProperty("reasoning_budget_tokens").GetInt32() == 512 &&
            request.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean() && request.GetProperty("reasoning_format").GetString() == "deepseek",
            "reasoning requests reserve a separate bounded budget and enable template-aware separation");
        Check(request.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString() == "object",
            "reasoning requests use llama.cpp's nested structured chat-response schema");
        string Frame(object delta, string? finish = null) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta, finish_reason = finish } } }) + "\n\n";
        const string hidden = "PRIVATE REASONING MUST NEVER BE EXPOSED";
        var visible = "{\"action\":\"final\",\"outcome\":\"completed\",\"text\":\"Hello\",\"resolutions\":[]}";
        var stream = Frame(new { reasoning_content = hidden }) + Frame(new { content = visible[..20] }) + Frame(new { content = visible[20..] }, "stop") + "data: [DONE]\n";
        var delivered = new List<string>();
        var parsed = await ReasoningCompletion.ReadAsync(new StringReader(stream), true, delivered.Add, default);
        Check(parsed == visible && delivered.SequenceEqual([visible]) && !string.Join("", delivered).Contains(hidden, StringComparison.Ordinal),
            "private reasoning is discarded and only adopted visible JSON reaches the UI callback");
        async Task<bool> RejectsStream(string source, bool json = true)
        {
            delivered.Clear();
            try { await ReasoningCompletion.ReadAsync(new StringReader(source), json, delivered.Add, default); return false; }
            catch (IOException ex) { return delivered.Count == 0 && !ex.Message.Contains(hidden, StringComparison.Ordinal); }
        }
        Check(await RejectsStream(Frame(new { content = visible }, "length")), "truncated reasoning/action output is rejected before callbacks");
        Check(await RejectsStream(Frame(new { content = visible }) + "data: [DONE]\n"), "a disconnected chat stream cannot be adopted from a bare DONE marker");
        Check(await RejectsStream(Frame(new { content = hidden + visible }, "stop")), "mixed reasoning and malformed JSON is rejected without exposing partial content");
        Check(await RejectsStream(Frame(new { reasoning_content = hidden }, "stop")), "reasoning-only responses fail without leaking their content");
        Check(await RejectsStream("data: " + JsonSerializer.Serialize(new { error = hidden }) + "\n"), "provider error frames never copy raw reasoning into saved errors");
        Check(await RejectsStream(Frame(new { content = "<think>" + hidden + "</think>visible" }, "stop"), false), "unseparated tagged reasoning is rejected for ordinary text responses");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var observedCancellation = false;
        try { await ReasoningCompletion.ReadAsync(new StringReader(stream), true, _ => throw new Exception("unexpected callback"), cancelled.Token); }
        catch (OperationCanceledException) { observedCancellation = true; }
        Check(observedCancellation, "reasoning-stream cancellation stops before adopting visible output");
        Console.WriteLine($"Reliability: {assertions} deterministic context/evidence/protocol checks passed; no real model inference or application launch.");
    }
}
