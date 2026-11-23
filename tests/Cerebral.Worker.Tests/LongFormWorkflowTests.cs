using System.Text.Json;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class LongFormWorkflowTests
{
    public static void Run()
    {
        var assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            assertions++;
            Console.WriteLine("PASS: " + message);
        }

        var pageObjective = LongFormWorkflow.Detect(
            new AgentJob { Prompt = "Write a 30 page novel with a complete plot arc." }, new TaskSession(), null);
        Check(pageObjective is { TargetWords: 7500, WordsPerPage: 250, MinimumSections: 3 } &&
            pageObjective.TargetLabel.Contains("30 pages (7,500 words", StringComparison.Ordinal),
            "a 30-page request becomes a measurable 7,500-word objective");

        var wordObjective = LongFormWorkflow.Detect(
            new AgentJob { Prompt = "Draft a 7500 word report and save the complete document." }, new TaskSession(), null);
        Check(wordObjective is { TargetWords: 7500 } && wordObjective.TargetLabel == "approximately 7,500 words",
            "an uncommaed 7500-word request is detected and normalized");

        var continuationSession = new TaskSession
        {
            Messages =
            [
                new() { Sequence = 1, Role = "user", Content = "Write a 30 page story with a beginning, middle, and ending." },
                new() { Sequence = 2, Role = "assistant", Content = "I have started the manuscript." }
            ]
        };
        var inherited = LongFormWorkflow.Detect(
            new AgentJob { Prompt = "Continue with the next section." }, continuationSession, null);
        Check(inherited is { TargetWords: 7500 } && inherited.TargetLabel.Contains("30 pages", StringComparison.Ordinal),
            "a continuation inherits the latest explicit long-form target from the session");

        var iterativePath = @"C:\drafts\iterative.txt";
        var iterativeContinuation = LongFormWorkflow.Detect(new AgentJob { Prompt = "Do it in 60 to 120 iteration chunks based on the plot outline.", StartSequence = 4 },
            new TaskSession
            {
                Messages =
                [
                    new() { Sequence = 1, Role = "user", Origin = "user", Content = "Write a 30 page story." },
                    new() { Sequence = 2, Role = "assistant", Content = JsonSerializer.Serialize(new { action = "tool", name = "file.write", arguments = new { path = iterativePath } }) },
                    new() { Sequence = 3, Role = "user", ToolName = "file.write", Content = "receipt\n" + JsonSerializer.Serialize(new { path = iterativePath }) },
                    new() { Sequence = 4, Role = "user", Origin = "user", Content = "Do it in 60 to 120 iteration chunks based on the plot outline." }
                ]
            }, null);
        Check(iterativeContinuation is { TargetWords: 7500, CandidatePath: not null } && iterativeContinuation.CandidatePath == iterativePath,
            "a chunked continuation that inherits an older target also recovers that artifact path");

        var retained = new LongFormState
        {
            TargetWords = 9000, TargetLabel = "approximately 9,000 words", Path = @"C:\drafts\retained.txt", WordsPerPage = 300
        };
        var inheritedState = LongFormWorkflow.Detect(
            new AgentJob { Prompt = "Finish the file." }, new TaskSession(), retained);
        Check(inheritedState is { TargetWords: 9000, WordsPerPage: 300, CandidatePath: @"C:\drafts\retained.txt" },
            "a continuation inherits the persisted objective before older conversation targets");

        var steeredState = new LongFormState
        {
            SourceJobId = "steered-job", SourceRequestSequence = 5, TargetWords = 7500,
            TargetLabel = "approximately 30 pages", Path = @"C:\drafts\steered.txt"
        };
        var steeredObjective = LongFormWorkflow.Detect(new AgentJob
        {
            Id = "steered-job", Prompt = "Write a 30 page story.", StartSequence = 5
        }, new TaskSession
        {
            Messages =
            [
                new() { Sequence = 5, Role = "user", Origin = "user", Content = "Write a 30 page story." },
                new() { Sequence = 6, Role = "user", Origin = "user", Content = "Actually, make the same story a 10 page manuscript." }
            ]
        }, steeredState);
        Check(steeredObjective is { TargetWords: 2500, CandidatePath: @"C:\drafts\steered.txt", SourceRequestSequence: 6, RequiresNewArtifact: false },
            "the latest active steering request overrides the original measurable target while retaining the same artifact");

        var completedOldPlan = new LongFormState
        {
            SourceJobId = "old-job", SourceRequestSequence = 1, TargetWords = 7500,
            TargetLabel = "approximately 30 pages", Path = @"C:\drafts\old-story.txt",
            VerifiedWords = 7500, CurrentSectionIndex = 3,
            Sections = [new(), new(), new()]
        };
        var freshSession = new TaskSession
        {
            Messages =
            [
                new() { Sequence = 1, Role = "user", Origin = "user", Content = "Write a 30 page story." },
                new() { Sequence = 2, Role = "user", Origin = "user", Content = "Write another 30 page story as a separate manuscript." },
                new() { Sequence = 3, Role = "assistant", ToolName = "file.write", Content = "receipt\n{\"path\":\"C:\\\\drafts\\\\old-story.txt\"}" }
            ]
        };
        var freshObjective = LongFormWorkflow.Detect(
            new AgentJob { Id = "new-job", Prompt = "Write a 30 page story.", StartSequence = 1 }, freshSession, completedOldPlan);
        Check(freshObjective is { TargetWords: 7500, CandidatePath: null, RequiresNewArtifact: true, SourceRequestSequence: 2 } &&
            !LongFormWorkflow.Matches(completedOldPlan, freshObjective),
            "an explicit new, another, or separate artifact request cannot reuse a completed plan or recovered old path");

        var incompleteOldPlan = new LongFormState
        {
            SourceJobId = "partial-job", SourceRequestSequence = 1, TargetWords = 5000,
            TargetLabel = "approximately 5,000 words", Path = @"C:\drafts\partial.txt",
            VerifiedWords = 1000, Sections = [new() { TargetWords = 5000 }]
        };
        var sizedFreshObjective = LongFormWorkflow.Detect(
            new AgentJob { Id = "sized-fresh-job", Prompt = "Start a new 30-page story.", StartSequence = 4 },
            new TaskSession { Messages = [new() { Sequence = 4, Role = "user", Origin = "user", Content = "Start a new 30-page story." }] },
            incompleteOldPlan);
        Check(sizedFreshObjective is { TargetWords: 7500, CandidatePath: null, RequiresNewArtifact: true },
            "fresh-artifact detection handles a size between new or another and the artifact kind");

        var unrelatedReportObjective = LongFormWorkflow.Detect(
            new AgentJob { Id = "requirements-job", Prompt = "Write a 3,000-word requirements report.", StartSequence = 5 },
            new TaskSession
            {
                Messages = [new() { Sequence = 5, Role = "user", Origin = "user", Content = "Write a 3,000-word requirements report." }]
            }, incompleteOldPlan);
        Check(unrelatedReportObjective is { TargetWords: 3000, CandidatePath: null, RequiresNewArtifact: true } &&
            !LongFormWorkflow.Matches(incompleteOldPlan, unrelatedReportObjective),
            "an unrelated explicit target in a new job starts a new artifact even when the prior long-form job is unfinished");

        foreach (var unrelatedPrompt in new[]
                 {
                     "Write a 3,000-word report about software update practices.",
                     "Write a 3,000-word report about how editors revise and expand manuscripts."
                 })
        {
            var unrelatedEditingObjective = LongFormWorkflow.Detect(
                new AgentJob { Id = "editing-vocabulary-job", Prompt = unrelatedPrompt, StartSequence = 5 },
                new TaskSession { Messages = [new() { Sequence = 5, Role = "user", Origin = "user", Content = unrelatedPrompt }] },
                incompleteOldPlan);
            Check(unrelatedEditingObjective is { TargetWords: 3000, CandidatePath: null, RequiresNewArtifact: true } &&
                  !LongFormWorkflow.Matches(incompleteOldPlan, unrelatedEditingObjective),
                "generic edit, update, revise, or expand vocabulary cannot identify an unrelated prior artifact");
        }

        var resizedPartialObjective = LongFormWorkflow.Detect(
            new AgentJob { Id = "partial-revision-job", Prompt = "Revise the existing story to 3,000 words.", StartSequence = 6 },
            new TaskSession
            {
                Messages = [new() { Sequence = 6, Role = "user", Origin = "user", Content = "Revise the existing story to 3,000 words." }]
            }, incompleteOldPlan);
        Check(resizedPartialObjective is { TargetWords: 3000, CandidatePath: @"C:\drafts\partial.txt", RequiresNewArtifact: false },
            "an explicit cross-job continuation can resize and retain an unfinished artifact");

        var sameJobResizeObjective = LongFormWorkflow.Detect(
            new AgentJob { Id = "partial-job", Prompt = "Write a 5,000-word story.", StartSequence = 1 },
            new TaskSession
            {
                Messages =
                [
                    new() { Sequence = 1, Role = "user", Origin = "user", Content = "Write a 5,000-word story." },
                    new() { Sequence = 7, Role = "user", Origin = "user", Content = "Make the same story 3,000 words." }
                ]
            }, incompleteOldPlan);
        Check(sameJobResizeObjective is { TargetWords: 3000, CandidatePath: @"C:\drafts\partial.txt", RequiresNewArtifact: false, SourceRequestSequence: 7 },
            "same-source-job steering still resizes the active artifact in place");

        var plainNewTurn = new TaskSession
        {
            Messages =
            [
                new() { Sequence = 2, Role = "assistant", ToolName = "file.write", Content = "receipt\n{\"path\":\"C:\\\\drafts\\\\old-story.txt\"}" },
                new() { Sequence = 5, Role = "user", Origin = "user", Content = "Write a 30 page story with a complete arc." }
            ]
        };
        var plainNewObjective = LongFormWorkflow.Detect(
            new AgentJob { Id = "plain-new-job", Prompt = "Write a 30 page story with a complete arc.", StartSequence = 5 },
            plainNewTurn, completedOldPlan);
        Check(plainNewObjective is { TargetWords: 7500, CandidatePath: null, RequiresNewArtifact: true, SourceRequestSequence: 5 } &&
            !LongFormWorkflow.Matches(completedOldPlan, plainNewObjective),
            "a later explicit target defaults to a new plan when the same-size prior artifact is already complete");

        const string unrelatedPlanningPrompt = "Run a guided planning check. Ask which target workflow to use: book, application, or both.";
        var unrelatedAfterCompletion = LongFormWorkflow.Detect(
            new AgentJob { Id = "unrelated-job", Prompt = unrelatedPlanningPrompt, StartSequence = 6 },
            new TaskSession
            {
                Messages =
                [
                    new() { Sequence = 1, Role = "user", Origin = "user", Content = "Write a 30 page story." },
                    new() { Sequence = 6, Role = "user", Origin = "user", Content = unrelatedPlanningPrompt },
                    new() { Sequence = 7, Role = "user", Origin = "question-answer", Content = "Answer to: What knowledge scope?\nProject shared." }
                ]
            }, completedOldPlan);
        Check(unrelatedAfterCompletion is null,
            "an unrelated request and its answers cannot inherit an older completed artifact merely because it mentions a long-form kind");

        var untrackedNewObjective = LongFormWorkflow.Detect(
            new AgentJob { Id = "untracked-new-job", Prompt = "Write a 20 page report.", StartSequence = 8 },
            new TaskSession
            {
                Messages =
                [
                    new() { Sequence = 4, Role = "assistant", ToolName = "file.write", Content = "receipt\n{\"path\":\"C:\\\\drafts\\\\unrelated.txt\"}" },
                    new() { Sequence = 8, Role = "user", Origin = "user", Content = "Write a 20 page report." }
                ]
            }, null);
        Check(untrackedNewObjective is { TargetWords: 5000, CandidatePath: null },
            "candidate-path recovery does not cross into an earlier turn without explicit continuation or revision wording");

        var continuedObjective = LongFormWorkflow.Detect(
            new AgentJob { Id = "revision-job", Prompt = "Expand the existing story to 40 pages.", StartSequence = 6 },
            new TaskSession
            {
                Messages =
                [
                    new() { Sequence = 6, Role = "user", Origin = "user", Content = "Expand the existing story to 40 pages." },
                    new() { Sequence = 3, Role = "assistant", ToolName = "file.write", Content = "receipt\n{\"path\":\"C:\\\\drafts\\\\old-story.txt\"}" }
                ]
            }, completedOldPlan);
        Check(continuedObjective is { TargetWords: 10000, CandidatePath: @"C:\drafts\old-story.txt", RequiresNewArtifact: false },
            "an explicit continuation or revision can resize and retain the completed artifact");

        var newestPath = @"C:\drafts\current.txt";
        var pathJob = new AgentJob
        {
            Prompt = "Expand this into a 7500 word manuscript.",
            Evidence =
            [
                new() { Name = "file.write", At = DateTimeOffset.UtcNow.AddMinutes(-1), Command = "{\"path\":\"C:\\\\drafts\\\\old.txt\"}" },
                new() { Name = "file.write", At = DateTimeOffset.UtcNow, Command = JsonSerializer.Serialize(new { path = newestPath }) },
                new() { Name = "file.read", At = DateTimeOffset.UtcNow.AddSeconds(1), Command = JsonSerializer.Serialize(new { path = @"C:\drafts\reference.txt" }) }
            ]
        };
        var recovered = LongFormWorkflow.Detect(pathJob, new TaskSession
        {
            Messages = [new() { Sequence = 1, Role = "assistant", Content = "{\"action\":\"tool\",\"name\":\"file.write\",\"arguments\":{\"path\":\"C:\\\\drafts\\\\message.txt\"}}" }]
        }, null);
        Check(recovered?.CandidatePath == newestPath,
            "candidate path recovery uses the newest execution evidence before conversation history");

        using var planDocument = JsonDocument.Parse("""
            {
              "action": "longform.plan",
              "path": "C:\\drafts\\planned.txt",
              "sections": [
                { "title": "Opening", "purpose": "Establish the conflict." },
                { "title": "Development", "purpose": "Escalate the conflict." },
                { "title": "Resolution", "purpose": "Resolve the conflict." }
              ]
            }
            """);
        var plannedObjective = new LongFormObjective(7501, "approximately 7,501 words", @"C:\drafts\planned.txt");
        var plan = LongFormWorkflow.CreatePlan("session-1", "job-1", plannedObjective, planDocument.RootElement);
        var freshPlan = LongFormWorkflow.CreatePlan("session-new", "job-new", plannedObjective with { CandidatePath = null }, planDocument.RootElement);
        Check(plan.SessionId == "session-1" && plan.SourceJobId == "job-1" && plan.Revision == 1 &&
            plan.Path == @"C:\drafts\planned.txt" && !plan.StartsNewArtifact && freshPlan.StartsNewArtifact &&
            plan.Sections.Select(x => x.TargetWords).SequenceEqual([2501, 2500, 2500]) &&
            plan.Sections.Sum(x => x.TargetWords) == plannedObjective.TargetWords,
            "a valid plan preserves ownership, new-versus-existing intent, and allocates every target word across its sections");

        using var mismatchedPath = JsonDocument.Parse("""
            {"path":"C:\\drafts\\different.txt","sections":[
              {"title":"One","purpose":"First."},{"title":"Two","purpose":"Second."},{"title":"Three","purpose":"Third."}]}
            """);
        Check(ThrowsInvalidData(() => LongFormWorkflow.CreatePlan("session-1", "job-1", plannedObjective, mismatchedPath.RootElement)),
            "plan validation rejects a path that abandons the recovered artifact");

        using var duplicateSection = JsonDocument.Parse("""
            {"path":"C:\\drafts\\planned.txt","sections":[
              {"title":"Same","purpose":"First."},{"title":"same","purpose":"Second."},{"title":"Three","purpose":"Third."}]}
            """);
        Check(ThrowsInvalidData(() => LongFormWorkflow.CreatePlan("session-1", "job-1", plannedObjective, duplicateSection.RootElement)),
            "plan validation rejects duplicate section titles without case sensitivity");

        using var tooFewSections = JsonDocument.Parse("""
            {"path":"C:\\drafts\\planned.txt","sections":[
              {"title":"One","purpose":"First."},{"title":"Two","purpose":"Second."}]}
            """);
        Check(ThrowsInvalidData(() => LongFormWorkflow.CreatePlan("session-1", "job-1", plannedObjective, tooFewSections.RootElement)),
            "plan validation enforces the service-owned minimum section count");

        var progressState = new LongFormState
        {
            Path = @"C:\drafts\progress.txt", TargetWords = 7500, Revision = 1,
            Sections =
            [
                new() { Title = "One", Purpose = "First.", TargetWords = 2500 },
                new() { Title = "Two", Purpose = "Second.", TargetWords = 2500 },
                new() { Title = "Three", Purpose = "Third.", TargetWords = 2500 }
            ]
        };
        var dispatchedPassage = "discarded-prefix-" + new string('x', 2200) + "\ud83d\ude80 ending";
        using var appendArguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            path = progressState.Path,
            content = dispatchedPassage,
            expectedSha256 = new string('0', 64),
            expectedBytes = 0
        }));
        var partialApplied = LongFormWorkflow.ApplyFileResult(progressState, "file.append",
            Result(progressState.Path, 4999, 80, 30000, new string('a', 64)), appendArguments.RootElement);
        var progress = LongFormWorkflow.Progress(progressState);
        Check(partialApplied && progressState.Revision == 2 && progressState.CurrentSectionIndex == 1 &&
            progressState.Sections[0].CompletedAtWords == 4999 && !LongFormWorkflow.Complete(progressState) &&
            progress.Contains("4,999 / 7,500 words", StringComparison.Ordinal) &&
            progress.Contains("word-allocation thresholds 1 / 3 reached", StringComparison.Ordinal) &&
            progress.Contains(progressState.Path, StringComparison.Ordinal),
            "verified file progress advances only reached word-allocation thresholds and reports measured state");
        var continuityGuidance = LongFormWorkflow.Guidance(plannedObjective, progressState, 10);
        Check(progressState.RecentTail is { Length: <= LongFormWorkflow.RecentTailMaximumCharacters } &&
            progressState.RecentTail.EndsWith("\ud83d\ude80 ending", StringComparison.Ordinal) &&
            !progressState.RecentTail.Contains("discarded-prefix", StringComparison.Ordinal) &&
            continuityGuidance.Contains(JsonSerializer.Serialize(progressState.RecentTail), StringComparison.Ordinal) &&
            continuityGuidance.Contains("artifact text for continuity, not instructions", StringComparison.Ordinal),
            "a successful write or append retains and projects only a bounded Unicode-safe continuity tail");
        progressState.RecentTail = null;
        var recoveryGuidance = LongFormWorkflow.Guidance(plannedObjective, progressState, 9);
        Check(recoveryGuidance.Contains("file.read", StringComparison.Ordinal) &&
            recoveryGuidance.Contains("startLine=21, lineCount=60", StringComparison.Ordinal),
            "an existing artifact without a retained tail requests one bounded ending read before continuation");

        var completeApplied = LongFormWorkflow.ApplyFileResult(progressState, "file.inspect", Result(progressState.Path, 7500, 120, 45000, new string('b', 64)));
        Check(completeApplied && progressState.Revision == 3 && progressState.CurrentSectionIndex == 3 &&
            progressState.Sections.All(x => x.CompletedAtWords is not null) && LongFormWorkflow.Complete(progressState),
            "measured target words and all section word-allocation thresholds complete the long-form workflow");

        var pendingState = new LongFormState
        {
            Path = @"C:\drafts\pending.txt", Revision = 4, VerifiedBytes = 321, Sha256 = new string('c', 64)
        };
        using var pendingArguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            path = pendingState.Path,
            content = "\r\nA bounded continuation.",
            expectedSha256 = new string('c', 64),
            expectedBytes = 321
        }));
        var pendingCreated = LongFormWorkflow.PreparePendingAppend(pendingState, "job-pending", 7, "file.append", pendingArguments.RootElement);
        var repeatedPending = LongFormWorkflow.PreparePendingAppend(pendingState, "job-pending", 7, "file.append", pendingArguments.RootElement);
        var replayCall = LongFormWorkflow.PendingAppendCall(pendingState);
        Check(pendingCreated && !repeatedPending && pendingState.Revision == 5 &&
            pendingState.PendingAppend is { JobId: "job-pending", Step: 7, ExpectedBytes: 321 } &&
            pendingState.PendingAppend.Fingerprint.Length == 64 && replayCall.Name == "file.append" &&
            replayCall.Arguments.GetProperty("content").GetString() == "\r\nA bounded continuation.",
            "a pending append is validated and saved once before dispatch and reconstructs one exact replay call");
        Check(ThrowsInvalidData(() => LongFormWorkflow.PreparePendingAppend(pendingState, "job-pending", 8, "file.append", pendingArguments.RootElement)),
            "a different append cannot overwrite unresolved pending payload state");
        LongFormWorkflow.ClearPendingAppend(pendingState);
        Check(pendingState.PendingAppend is null && pendingState.Revision == 6,
            "clearing a reconciled pending append advances state without retaining duplicate payloads");

        var tool = new ToolDescriptor("file.append", "Append prose.", CerebralJson.Element(new { type = "object" }), "filesystem.write");
        var planSchema = AgentActionSchema.Create([tool], ["helper"], longFormPlan: new(@"C:\drafts\planned.txt", 3, 8));
        var planAlternatives = planSchema.GetProperty("oneOf").EnumerateArray().ToArray();
        var planProperties = planAlternatives.Single().GetProperty("properties");
        Check(planAlternatives.Length == 1 && Action(planAlternatives[0]) == "longform.plan" &&
            planProperties.GetProperty("path").GetProperty("const").GetString() == @"C:\drafts\planned.txt" &&
            planProperties.GetProperty("sections").GetProperty("minItems").GetInt32() == 3 &&
            planProperties.GetProperty("sections").GetProperty("maxItems").GetInt32() == 8,
            "a pending long-form plan produces a plan-only schema with fixed path and section bounds");

        var noFinalActions = AgentActionSchema.Create([tool], ["helper"], allowFinal: false)
            .GetProperty("oneOf").EnumerateArray().Select(Action).ToArray();
        Check(!noFinalActions.Contains("final") && noFinalActions.Contains("ask") &&
            noFinalActions.Contains("tool") && noFinalActions.Contains("delegate"),
            "the no-final schema suppresses final while retaining the other eligible actions");

        var incompleteFinal = Alternative(AgentActionSchema.Create([tool], [], allowCompleted: false), "final");
        var outcomes = incompleteFinal.GetProperty("properties").GetProperty("outcome").GetProperty("enum")
            .EnumerateArray().Select(x => x.GetString()).ToArray();
        Check(outcomes.SequenceEqual(["incomplete", "failed"]),
            "the no-completed-final schema permits only truthful incomplete or failed outcomes");

        Console.WriteLine($"Long-form workflow tests passed: {assertions}");
    }

    private static bool ThrowsInvalidData(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }

    private static ToolResult Result(string path, int words, int lines, int bytes, string hash) =>
        new(true, JsonSerializer.Serialize(new { path, words, lines, bytes, sha256 = hash }));

    private static string? Action(JsonElement alternative) =>
        alternative.GetProperty("properties").GetProperty("action").GetProperty("const").GetString();

    private static JsonElement Alternative(JsonElement schema, string action) =>
        schema.GetProperty("oneOf").EnumerateArray().Single(x => Action(x) == action);
}
