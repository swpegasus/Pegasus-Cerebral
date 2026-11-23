using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class AgentPromptTests
{
    public static void Run()
    {
        var assertions = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); assertions++; Console.WriteLine("PASS: " + message); }
        using var source = JsonDocument.Parse("""
            {"path":"C:\\Projects\\quoted \"name\"\\app.cs","text":"Line one\r\nLine two\t\u03bb\ud83d\ude00","number":9007199254740993,"decimal":0.125,"flag":false,"nothing":null,"items":["a",{"nested":true}]}
            """);
        var compact = AgentPrompt.Json(source.RootElement);
        using var parsed = JsonDocument.Parse(compact);
        var indented = JsonSerializer.Serialize(source.RootElement, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = true });
        Check(JsonElement.DeepEquals(source.RootElement, parsed.RootElement),
            "compact prompt JSON preserves exact strings, escapes, Unicode, numbers, nulls and nested values");
        Check(Encoding.UTF8.GetByteCount(compact) < Encoding.UTF8.GetByteCount(indented) && !compact.Contains('\n'),
            "compact prompt JSON removes layout bytes without dropping data");
        Check(parsed.RootElement.GetProperty("number").GetInt64() == 9007199254740993 &&
            parsed.RootElement.GetProperty("text").GetString() == "Line one\r\nLine two\t\u03bb\U0001F600",
            "compact serialization preserves exact large integer and control-character values");

        using var schemaDocument = JsonDocument.Parse("""
            {"type":"object","properties":{"path":{"type":"string","pattern":"^[A-Z]:\\\\","minLength":3},"mode":{"type":"string","enum":["read","write"]},"options":{"type":"array","items":{"type":"object","properties":{"enabled":{"type":"boolean"}},"required":["enabled"],"additionalProperties":false}}},"required":["path","mode"],"additionalProperties":false}
            """);
        var descriptors = new[] {
            new ToolDescriptor("file.fixture", "Exact \"quoted\" tool description\nSecond line.", schemaDocument.RootElement.Clone(), "filesystem.fixture"),
            new ToolDescriptor("mcp.fixture.read", "Read metadata.", CerebralJson.Element(new { type = "object", required = new[] { "recordId" }, properties = new { recordId = new { type = "string" } } }), "mcp.fixture")
        };
        var catalog = AgentPrompt.ToolCatalog(descriptors);
        using var catalogDocument = JsonDocument.Parse(catalog);
        var records = catalogDocument.RootElement;
        Check(records.ValueKind == JsonValueKind.Array && records.GetArrayLength() == descriptors.Length,
            "tool catalog retains each advertised tool in compact JSON");
        for (var index = 0; index < descriptors.Length; index++)
        {
            var record = records[index]; var descriptor = descriptors[index];
            Check(record.GetProperty("name").GetString() == descriptor.Name && record.GetProperty("description").GetString() == descriptor.Description &&
                JsonElement.DeepEquals(record.GetProperty("inputSchema"), descriptor.InputSchema),
                "tool catalog preserves exact descriptor values and schema constraints for " + descriptor.Name);
            Check(record.EnumerateObject().Count() == 3 && !record.TryGetProperty("capability", out _) && !record.TryGetProperty("Capability", out _),
                "tool catalog omits service-only capability metadata for " + descriptor.Name);
        }
        Check(!catalog.Contains('\n') && Encoding.UTF8.GetByteCount(catalog) <
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(records, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = true })),
            "tool catalog removes indentation while retaining complete schemas");
        Check(CerebralJson.Options.WriteIndented, "prompt compaction leaves shared storage and wire serialization options unchanged");
        using var empty = JsonDocument.Parse(AgentPrompt.ToolCatalog([]));
        Check(empty.RootElement.ValueKind == JsonValueKind.Array && empty.RootElement.GetArrayLength() == 0, "empty tool availability produces an empty catalog");

        var job = new AgentJob { Id = "fixture-job", Kind = "agent", ProfileId = "lead", StepCount = 3, Profile = new AgentProfile { MaxSteps = 12 }, Prompt = "Delegate an independent review to the available helpers." };
        var lead = new AgentProfile { Id = "lead", Name = "Lead fixture", Role = "lead", MaxSteps = 12, Capabilities = ["agents.delegate"] };
        var helper = new AgentProfile
        {
            Id = "helper-local", Name = "Local helper", Purpose = "Review bounded source changes.", Role = "helper",
            ContextTokens = 16384, AllowedTools = ["fixture.private-tool"]
        };
        var remote = new AgentProfile { Id = "helper-remote", Name = "Remote helper", Purpose = "Run remote checks.", Role = "helper", WorkerId = "gx10" };
        var candidates = new[] { lead, helper, new AgentProfile { Id = "another-lead", Role = "lead" }, new AgentProfile { Id = "compactor", Role = "compactor" }, remote };
        var selected = AgentPrompt.SelectHelpers(job, lead, candidates);
        Check(selected.Select(x => x.Id).SequenceEqual(new[] { helper.Id, remote.Id }),
            "lead helper inventory excludes itself and nonhelper profiles while retaining local and remote helpers");
        var many = Enumerable.Range(0, 40).Select(index => new AgentProfile { Id = "helper-" + index, Role = "helper" }).ToList();
        Check(AgentPrompt.SelectHelpers(job, lead, many).Count == 32 &&
            AgentPrompt.SelectHelpers(job, lead, many).Select(x => x.Id).SequenceEqual(many.Take(32).Select(x => x.Id)),
            "helper inventory uses a stable bounded selection of at most 32 profiles");
        Check(AgentPrompt.SelectHelpers(job, lead, candidates, "Calculate this without delegation.").Count == 0 &&
            AgentPrompt.SelectHelpers(job, lead, candidates, "Do not delegate this task.").Count == 0,
            "explicit no-delegation wording suppresses the helper catalog and delegate action");
        Check(AgentPrompt.SelectHelpers(job, lead, candidates, "What is 2 + 2?").Count == 0 &&
            AgentPrompt.SelectHelpers(job, lead, candidates, "Read the selected file.").Count == 0 &&
            AgentPrompt.SelectHelpers(job, lead, candidates, "Fix this one typo and run its focused test.").Count == 0,
            "straightforward questions and short one-step development work stay with the lead");
        Check(AgentPrompt.RequiresDirectCapabilityAnswer("what are you capabilites and fetures give full detail") &&
            AgentPrompt.RequiresDirectCapabilityAnswer("How can you help me?") &&
            AgentPrompt.RequiresDirectCapabilityAnswer("Describe the Cerebral application features.") &&
            AgentPrompt.RequiresDirectCapabilityAnswer("How good are you a short stories") &&
            AgentPrompt.RequiresDirectCapabilityAnswer("What tools and capabilities does Pegasus Cerebral provide?"),
            "capability questions, including the observed misspellings, are recognized as direct-answer requests");
        Check(!AgentPrompt.RequiresDirectCapabilityAnswer("Add a search feature to the application.") &&
            !AgentPrompt.RequiresDirectCapabilityAnswer("Which folder should I use?") &&
            !AgentPrompt.RequiresDirectCapabilityAnswer("What features should my application have?") &&
            !AgentPrompt.RequiresDirectCapabilityAnswer("What features can you add to my application?"),
            "ordinary feature work and user-owned configuration choices remain eligible for normal clarification");
        Check(!AgentPrompt.NeedsUserAnswer("How good are you a short stories", "clarification", "which aspect of short stories") &&
            !AgentPrompt.NeedsUserAnswer("write that short story, 30 pages, with a considered plot arc", "Do you want an outline or a detailed plot summary?", "to ensure the plot is considered") &&
            !AgentPrompt.NeedsUserAnswer("write a short story", "What kind of writing assistance do you need?", "choose writing or analysis"),
            "observed generic and preliminary writing questions use reversible defaults instead of interrupting the user");
        const string directCapabilityRequest = "What capabilities and tools do you have in Pegasus Cerebral? Answer directly in five concise bullets. Do not ask me a question or delegate.";
        Check(AgentPrompt.RequiresDirectCapabilityAnswer(directCapabilityRequest) &&
            !AgentPrompt.NeedsUserAnswer(directCapabilityRequest, "Do you want a list of capabilities and tools, or should I demonstrate them?", "The request is ambiguous"),
            "the observed direct capability request cannot turn its negated ask-me phrase into guided clarification");
        Check(!AgentPrompt.NeedsUserAnswer("Do not interview me; draft the plan with reasonable defaults.", "Which planning style do you prefer?", "guided planning") &&
            !AgentPrompt.NeedsUserAnswer("Never guide me through choices; write the first draft.", "Which tone should I use?", "guided writing") &&
            !AgentPrompt.NeedsUserAnswer("Complete this without asking me any questions.", "Which output style do you prefer?", "routine preference"),
            "negated ask, interview, and guide instructions suppress only routine clarification");
        Check(AgentPrompt.NeedsUserAnswer("Fix the existing application", "Which project should I modify?", "No project is selected") &&
            !AgentPrompt.NeedsUserAnswer("Fix the selected application", "Which project should I modify?", "locate the target", hasSelectedProject: true) &&
            AgentPrompt.NeedsUserAnswer("Help me plan a book by asking me one choice at a time", "Which genre do you prefer?", "guided author choice") &&
            AgentPrompt.NeedsUserAnswer("Do not ask me questions. Publish the finished site", "Do you authorize publishing it publicly?", "external effect") &&
            AgentPrompt.NeedsUserAnswer("Do not ask me questions. Use my private account", "What is the required access key?", "protected credential") &&
            AgentPrompt.NeedsUserAnswer("Do not ask unnecessary questions. Fix the existing application", "Which project should I modify?", "No project is selected"),
            "missing existing targets, requested guided choices, protected access, and external authority still reach the user");
        Check(AgentPrompt.QuestionMustWait("Do you authorize permanent deletion?", "irreversible effect") &&
            !AgentPrompt.QuestionMustWait("Which visual theme do you prefer?", "reversible styling"),
            "approval and protected-access questions cannot be converted into timeout assumptions");
        var storyQuestion = AgentPrompt.EvaluateQuestion(
            "Write the novel in outline sections and ask useful questions while you continue.",
            "Should Mara already know the harbor keeper in chapter four?",
            "This affects the established character relationship in the next chapter.",
            modelRequired: false, canAssume: true,
            assumption: "Treat them as acquainted but not yet trusted until the author answers.");
        var codeQuestion = AgentPrompt.EvaluateQuestion(
            "Implement the application in bounded stages.",
            "Should the public API return an empty collection or a missing value when no records exist?",
            "The public API contract affects the next endpoint and client tests.",
            modelRequired: true, canAssume: true,
            assumption: "Return an empty collection for the reversible first implementation.");
        Check(storyQuestion is { Surface: true, Required: false, CanAssume: true } &&
            codeQuestion is { Surface: true, Required: false, CanAssume: true },
            "material writing and programming questions can enrich staged work without stopping independent progress");
        var blockedStoryQuestion = AgentPrompt.EvaluateQuestion(
            "Continue the manuscript from the established outline.",
            "Is Mara the keeper's daughter or a stranger?",
            "This conflicts with established canon and blocks the next file write for their reunion scene.",
            modelRequired: true, canAssume: false, assumption: null);
        Check(blockedStoryQuestion is { Surface: true, Required: true, CanAssume: false } &&
            AgentPrompt.QuestionMustWait("Which existing project should I edit?", "The target is missing.",
                "Fix the existing application", hasSelectedProject: false),
            "a material decision pauses only when it blocks the next dependent effect or exact target");
        Check(AgentPrompt.EvaluateQuestion(
                "Implement the application.", "Which indentation style do you prefer?", "Routine formatting style.",
                modelRequired: true, canAssume: true, assumption: "Use four spaces throughout the new source files.") is { Surface: false } &&
            AgentPrompt.EvaluateQuestion(
                "Write the book.", "Which plot detail should I use?", "The next chapter could use any reasonable choice.",
                modelRequired: false, canAssume: true, assumption: "Use a reasonable default.") is { Surface: false },
            "routine preferences and vague assumptions do not become enrichment questions");
        var pendingQuestions = new List<UserQuestion>
        {
            new() { Prompt = "Should Mara already know the harbor keeper in chapter four?", Required = false },
            new() { Prompt = "Should the lighthouse be visible from the village?", Required = false }
        };
        Check(AgentPrompt.EvaluateQuestion(
                "Write the novel in sections.", "Should Mara already know the harbor keeper in chapter four!",
                "Established character relationship.", modelRequired: false, canAssume: true,
                assumption: "Treat them as acquaintances for now.", existingQuestions: pendingQuestions) is { Surface: false } &&
            AgentPrompt.EvaluateQuestion(
                "Write the novel in sections.", "Does the storm begin before the ferry arrives?",
                "The scene timeline affects a later chapter.", modelRequired: false, canAssume: true,
                assumption: "Begin the storm after the ferry docks.", existingQuestions: pendingQuestions) is { Surface: true },
            "duplicate questions are suppressed while several distinct questions retain separate answers");
        pendingQuestions.Add(new() { Prompt = "Has the town seen a storm like this before?", Required = false });
        Check(AgentPrompt.EvaluateQuestion(
                "Write the novel in sections.", "Does Mara recognize the warning bell?", "Established character knowledge.",
                modelRequired: false, canAssume: true, assumption: "Let Mara recognize the bell but not its source.",
                existingQuestions: pendingQuestions) is { Surface: false } &&
            AgentPrompt.MaxPendingOptionalQuestions == 3,
            "the optional question backlog is bounded without imposing a one-question workflow");
        var currentQuestionJob = new AgentJob { Id = "current-question-job", Status = "running" };
        var activeQuestionJob = new AgentJob { Id = "active-question-job", Status = "waiting-human" };
        var staleQuestionJob = new AgentJob { Id = "stale-question-job", Status = "completed" };
        var staleRequired = new UserQuestion
        {
            Id = "stale-unanswered", JobId = staleQuestionJob.Id, Required = true,
            Prompt = "Which existing repository should I modify?"
        };
        var answeredRequired = new UserQuestion
        {
            Id = "stale-answered", JobId = staleQuestionJob.Id, Required = true, Answer = "Repository A",
            Prompt = "Which repository was selected for the earlier finished assignment?"
        };
        var activeRequired = new UserQuestion
        {
            Id = "active-unanswered", JobId = activeQuestionJob.Id, Required = true,
            Prompt = "Do you authorize the active deployment?"
        };
        var relevantQuestionHistory = AgentPrompt.RelevantQuestionHistory(currentQuestionJob.Id,
            [staleRequired, answeredRequired, activeRequired], [currentQuestionJob.Id, activeQuestionJob.Id]);
        var freshRequired = AgentPrompt.EvaluateQuestion("Fix the existing application",
            staleRequired.Prompt, "No project is selected and this blocks the next edit.",
            modelRequired: true, canAssume: false, assumption: null, existingQuestions: relevantQuestionHistory);
        var freshPreviouslyAnswered = AgentPrompt.EvaluateQuestion("Fix the existing application",
            answeredRequired.Prompt, "No project is selected and this blocks the next edit.",
            modelRequired: true, canAssume: false, assumption: null, existingQuestions: relevantQuestionHistory);
        var activeDuplicate = AgentPrompt.EvaluateQuestion("Deploy the active build", activeRequired.Prompt,
            "The next deployment requires authorization.", modelRequired: true, canAssume: false,
            assumption: null, existingQuestions: relevantQuestionHistory);
        var answeredContext = WorkingContext.Assemble(new TaskSession
        {
            Messages = [new() { Sequence = 1, Role = "user", Origin = "question-answer", SourceQuestionId = answeredRequired.Id,
                Content = "Answer to: " + answeredRequired.Prompt + "\n" + answeredRequired.Answer }]
        }, "system");
        Check(!relevantQuestionHistory.Any(x => x.Id == staleRequired.Id) &&
            !relevantQuestionHistory.Any(x => x.Id == answeredRequired.Id) &&
            relevantQuestionHistory.Any(x => x.Id == activeRequired.Id) &&
            freshRequired is { Surface: true, Required: true } &&
            freshPreviouslyAnswered is { Surface: true, Required: true } && activeDuplicate is { Surface: false } &&
            answeredContext.Any(x => x.Content.Contains("Repository A", StringComparison.Ordinal)),
            "terminal required cards, answered or not, cannot suppress a fresh decision while saved answers and active cards remain available");

        var earlierOptionalTarget = new UserQuestion
        {
            Id = "optional-old-target", JobId = staleQuestionJob.Id, Required = false, CanAssume = true,
            Assumption = "Use the currently selected project until the user chooses another one.",
            Prompt = "Which existing repository should I modify?"
        };
        var escalatedTarget = AgentPrompt.EvaluateQuestion("Fix the existing application", earlierOptionalTarget.Prompt,
            "No project is selected and this blocks the next edit.", modelRequired: true, canAssume: false,
            assumption: null, existingQuestions: AgentPrompt.RelevantQuestionHistory(currentQuestionJob.Id,
                [earlierOptionalTarget], [currentQuestionJob.Id]));
        Check(escalatedTarget is { Surface: true, Required: true },
            "an earlier optional card cannot suppress the same decision when it becomes required for the next effect");

        const string explicitBatchRequest = "Ask these three independent decisions together in one question batch. I explicitly require all three answers before you produce the plan. Wait for every answer.";
        var explicitBatch = new[]
        {
            AgentPrompt.EvaluateQuestion(explicitBatchRequest, "Which target workflow should the plan use?", "Select book, application, or both.", false, false, null),
            AgentPrompt.EvaluateQuestion(explicitBatchRequest, "Which guidance style should the plan use?", "Select fixed or adaptive steps.", false, false, null),
            AgentPrompt.EvaluateQuestion(explicitBatchRequest, "Which knowledge scope should the plan use?", "Select conversation or project scope.", false, false, null)
        };
        Check(explicitBatch.All(x => x is { Surface: true, Required: true, CanAssume: false }) &&
            AgentPrompt.Protocol.Contains("preserve every requested decision in one ask.batch", StringComparison.Ordinal) &&
            AgentPrompt.RequestedQuestionBatchSize(explicitBatchRequest) == 3 &&
            AgentPrompt.RequestedQuestionBatchSize("Ask both questions at once.") == 2 &&
            AgentPrompt.RequestedQuestionBatchSize("Ask me three required questions and wait for every answer before continuing.") == 3 &&
            AgentPrompt.RequestedQuestionBatchSize("Ask exactly two questions before you continue.") == 2 &&
            AgentPrompt.RequestedQuestionBatchSize("Ask me three questions one at a time.") == 0 &&
            AgentPrompt.RequestedQuestionBatchSize("Ask me two questions sequentially.") == 0 &&
            AgentPrompt.RequestedQuestionBatchSize("Do not ask me three questions.") == 0,
            "an explicit two-or-three-question batch preserves every separately answered decision and waits when requested");

        const string exactAssumption = "Treat the harbor keeper as an ally until the author supplies a different relationship.";
        var mixedQuestions = new List<UserQuestion>
        {
            new() { Id = "optional-context", Prompt = "What relationship should the harbor keeper have to Mara?", Required = false, CanAssume = true, Assumption = exactAssumption },
            new() { Id = "required-context", Prompt = "Do you authorize publishing the manuscript?", Required = true }
        };
        var assumptionContext = AgentPrompt.OptionalAssumptionContext(mixedQuestions)!;
        var assumptionSession = new TaskSession
        {
            Messages = [new() { Sequence = 1, Role = "user", Origin = "service", Content = assumptionContext }]
        };
        var projectedAssumptions = WorkingContext.Assemble(assumptionSession, "system");
        Check(assumptionContext.Contains(AgentPrompt.Json(new[] { new { question = mixedQuestions[0].Prompt, assumption = exactAssumption } }), StringComparison.Ordinal) &&
            !assumptionContext.Contains("questionId", StringComparison.Ordinal) &&
            !assumptionContext.Contains(mixedQuestions[1].Prompt, StringComparison.Ordinal) &&
            projectedAssumptions.Count(x => x.Content.Contains(exactAssumption, StringComparison.Ordinal)) == 1,
            "mixed batches project each optional assumption exactly once with its bounded question and no internal identifier");
        Check(AgentPrompt.EvaluateQuestion("Write the novel in sections.", "Should Mara recognize the keeper?",
                "The established character relationship affects a later chapter.", modelRequired: false, canAssume: true,
                assumption: new string('x', AgentPrompt.MaxQuestionAssumptionCharacters + 1)) is { Surface: false },
            "oversized optional assumptions are rejected before entering model-facing context");
        var isolatedTurn = new AgentJob { Prompt = "what are you capabilites and fetures give full detail", StartSequence = 9 };
        var isolatedTurnSession = new TaskSession
        {
            Messages =
            [
                new() { Sequence = 1, Role = "user", Origin = "user", Content = "Use math.calculate for choose(9,2)." },
                new() { Sequence = 2, Role = "user", Origin = "tool", ToolName = "math.calculate", Content = "Tool math.calculate; success=True" },
                new() { Sequence = 9, Role = "user", Origin = "user", Content = isolatedTurn.Prompt }
            ]
        };
        Check(!AgentPrompt.RoutingContext(isolatedTurn, isolatedTurnSession).Contains("math.calculate", StringComparison.Ordinal) &&
            !AgentPrompt.DelegationRequestContext(isolatedTurn, isolatedTurnSession).Contains("choose(9,2)", StringComparison.Ordinal),
            "a new turn does not route tools or intent from an older completed assignment");
        var oldBatchSession = new TaskSession
        {
            Messages =
            [
                new() { Sequence = 1, Role = "user", Origin = "user", Content = "Ask me three questions together." },
                new() { Sequence = 9, Role = "user", Origin = "user", Content = "Continue with the selected defaults." }
            ]
        };
        Check(AgentPrompt.RequestedQuestionBatchSize(AgentPrompt.CurrentTaskRequest(
                  new AgentJob { Prompt = "Continue with the selected defaults.", StartSequence = 9 }, oldBatchSession)) == 0,
            "an explicit batch request from an older turn does not constrain a new current request");
        Check(AgentPrompt.SelectHelpers(job, lead, candidates, "Use the coding helper to review this change.").Count == 2 &&
            AgentPrompt.SelectHelpers(job, lead, candidates, "Inspect the API and UI in parallel.").Count == 2,
            "explicit helper and parallel-work requests expose the configured helper catalog");
        Check(AgentPrompt.SelectHelpers(job, lead, candidates, "Please complete these independent parts:\n1. Review the API contract.\n2. Test the desktop workflow.\n3. Document the deployment behavior.").Count == 2 &&
            AgentPrompt.SelectHelpers(job, lead, candidates, "Review and implement changes across both the backend and frontend, then verify the different platforms.").Count == 2,
            "listed and broad multi-part work exposes helpers when a useful split is evident");
        var simpleFollowUp = new AgentJob { Prompt = "Answer this one question directly.", StartSequence = 5 };
        var simpleFollowUpSession = new TaskSession
        {
            Messages =
            [
                new() { Sequence = 1, Role = "user", Origin = "user", Content = "Delegate a large multi-part audit in parallel." },
                new() { Sequence = 5, Role = "user", Origin = "user", Content = simpleFollowUp.Prompt }
            ]
        };
        var simpleFollowUpContext = AgentPrompt.DelegationRequestContext(simpleFollowUp, simpleFollowUpSession);
        Check(!simpleFollowUpContext.Contains("multi-part audit", StringComparison.Ordinal) &&
            AgentPrompt.SelectHelpers(simpleFollowUp, lead, candidates, simpleFollowUpContext).Count == 0,
            "an earlier complex turn does not make a new simple request advertise helpers");
        lead.Capabilities.Clear();
        Check(AgentPrompt.SelectHelpers(job, lead, candidates).Count == 0, "lead without delegation capability advertises no helpers");
        lead.Capabilities.Add("agents.delegate"); job.ParentJobId = "parent-job";
        Check(AgentPrompt.SelectHelpers(job, lead, candidates).Count == 0, "a nested job cannot advertise further delegation");
        job.ParentJobId = null; remote.Capabilities.Add("agents.delegate");
        Check(AgentPrompt.SelectHelpers(job, remote, candidates).Count == 0,
            "remote helper cannot delegate even when imported execution has no parent job ID");
        JsonElement FirstJsonObject(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text[text.IndexOf('{')..]);
            var reader = new Utf8JsonReader(bytes);
            using var json = JsonDocument.ParseValue(ref reader);
            return json.RootElement.Clone();
        }
        JsonElement JsonAfter(string text, string marker)
        {
            var start = text.IndexOf(marker, StringComparison.Ordinal);
            var bytes = Encoding.UTF8.GetBytes(text[(start + marker.Length)..]);
            var reader = new Utf8JsonReader(bytes);
            using var json = JsonDocument.ParseValue(ref reader);
            return json.RootElement.Clone();
        }
        var assignment = AgentPrompt.Assignment(job, lead, selected, ["file", "application"]);
        var frame = FirstJsonObject(assignment);
        Check(frame.GetProperty("role").GetString() == "lead" && frame.GetProperty("profileId").GetString() == lead.Id &&
            frame.GetProperty("jobId").GetString() == job.Id && frame.GetProperty("kind").GetString() == job.Kind,
            "assignment identifies the current role, profile, job and work kind");
        Check(frame.GetProperty("actionsUsed").GetInt32() == 2 &&
            frame.GetProperty("actionsRemaining").GetInt32() == 10 &&
            frame.GetProperty("actionLimit").GetInt32() == 12 &&
            frame.GetProperty("contextTokenLimit").GetInt32() == lead.ContextTokens &&
            frame.GetProperty("configuredResponseTokenLimit").GetInt32() == lead.ResponseTokens &&
            !frame.TryGetProperty("responseTokenLimit", out _),
            "assignment reports used, current-inclusive remaining, and maximum action counts while labeling the configured response ceiling");
        Check(assignment.Contains("Before final", StringComparison.Ordinal) &&
            assignment.Contains("If anything remains, take another action", StringComparison.Ordinal),
            "each model step requires explicit completion accounting before a final action");
        Check(assignment.Contains("\"action\":\"delegate\"", StringComparison.Ordinal) &&
            assignment.Contains(helper.Id, StringComparison.Ordinal) && assignment.Contains(remote.Id, StringComparison.Ordinal) &&
            assignment.Contains("\"action\":\"present\"", StringComparison.Ordinal),
            "assignment advertises available helper delegation and presentation actions");
        var helperCatalog = JsonAfter(assignment, "Helper profiles: ");
        var projectedHelper = helperCatalog[0];
        Check(projectedHelper.EnumerateObject().Select(x => x.Name).ToHashSet().SetEquals(["id", "name", "purpose", "workerId"]) &&
            projectedHelper.GetProperty("id").GetString() == helper.Id &&
            projectedHelper.GetProperty("purpose").GetString() == helper.Purpose &&
            !assignment.Contains("fixture.private-tool", StringComparison.Ordinal) &&
            !assignment.Contains("contextTokens", StringComparison.OrdinalIgnoreCase),
            "helper catalog retains bounded identity and purpose while omitting execution-enforced context and tool grants");
        var unavailable = AgentPrompt.Assignment(job, lead, [], []);
        Check(!unavailable.Contains("\"action\":\"delegate\"", StringComparison.Ordinal) &&
            !unavailable.Contains("\"action\":\"present\"", StringComparison.Ordinal) &&
            !unavailable.Contains(helper.Id, StringComparison.Ordinal),
            "unavailable actions and helper catalog entries are absent from the assignment");
        var helperAssignment = AgentPrompt.Assignment(job, remote, AgentPrompt.SelectHelpers(job, remote, candidates), []);
        Check(FirstJsonObject(helperAssignment).GetProperty("role").GetString() == "helper" &&
            !helperAssignment.Contains("\"action\":\"delegate\"", StringComparison.Ordinal),
            "remote helper receives helper-role context without a nested delegation action");
        job.ParentJobId = "parent-job";
        var nested = AgentPrompt.Assignment(job, lead, AgentPrompt.SelectHelpers(job, lead, candidates), []);
        Check(FirstJsonObject(nested).GetProperty("role").GetString() == "helper" && !nested.Contains("\"assignments\"", StringComparison.Ordinal),
            "parented work receives helper-role instructions even when its saved profile role is lead");
        job.ParentJobId = null; job.Kind = "background"; job.StepCount = 99;
        var exhausted = FirstJsonObject(AgentPrompt.Assignment(job, lead, [], []));
        Check(exhausted.GetProperty("kind").GetString() == "background" &&
            exhausted.GetProperty("actionsUsed").GetInt32() == 12 && exhausted.GetProperty("actionsRemaining").GetInt32() == 0,
            "background assignment kind is retained and exhausted used/remaining action counts are clamped to their limits");

        var ordinaryLeadJob = new AgentJob { Prompt = "Calculate 36 * 7 without delegation.", StartSequence = 1 };
        var ordinaryLeadSession = new TaskSession
        {
            Messages = [new() { Sequence = 1, Role = "user", Origin = "user", Content = ordinaryLeadJob.Prompt }]
        };
        Check(!AgentPrompt.ShouldAdvertisePresentation(ordinaryLeadJob, ordinaryLeadSession),
            "ordinary lead work omits presentation guidance and actions");
        Check(AgentPrompt.ShouldAdvertisePresentation(new AgentJob { Prompt = "Open the built application window.", StartSequence = 1 }, new TaskSession()) &&
            AgentPrompt.ShouldAdvertisePresentation(new AgentJob { Prompt = "Continue the task.", StartSequence = 2 }, new TaskSession
            {
                Messages = [new() { Sequence = 1, Role = "user", Origin = "user", Content = "Please show the generated report file." }]
            }),
            "current requests and user-request history explicitly asking to present a file or application retain presentation actions");
        Check(!AgentPrompt.ShouldAdvertisePresentation(new AgentJob { Prompt = "Do not open the application; report only the result.", StartSequence = 1 }, new TaskSession()),
            "a negated presentation phrase does not advertise presentation actions");
        Check(AgentPrompt.ShouldAdvertisePresentation(new AgentJob { Prompt = "Continue.", StartSequence = 1 }, new TaskSession
            {
                Messages = [new() { Sequence = 2, Role = "user", Origin = "service", Content = "Interface presentation was not queued: path missing." }]
            }),
            "a current presentation action that failed before queueing retains the action for recovery");
        Check(!AgentPrompt.ShouldAdvertisePresentation(new AgentJob { Prompt = "Open the application.", StartSequence = 1 }, new TaskSession
            {
                Messages = [new() { Sequence = 2, Role = "user", Origin = "service", Content = "Interface request request-1; kind=application; path=C:\\fixture.exe; status=pending." }]
            }),
            "a queued presentation request suppresses replay even when the original user request asked to open it");
        Check(AgentPrompt.ShouldAdvertisePresentation(new AgentJob
        {
            Prompt = "Repair the failure.", StartSequence = 1,
            Evidence = [new() { Kind = "application-exit", Name = "C:\\fixture.exe", Success = false }]
        }, new TaskSession()), "an unresolved application failure retains presentation for verified recovery");

        ToolDescriptor Routed(string name) => new(name, "Fixture " + name, CerebralJson.Element(new { type = "object", additionalProperties = false }), "fixture");
        var routingTools = new[]
        {
            Routed("math.calculate"),
            Routed("file.read"), Routed("file.list"), Routed("file.search"), Routed("file.copy"), Routed("file.write"), Routed("file.edit"), Routed("file.mkdir"),
            Routed("process.run"),
            Routed("git.status"), Routed("git.diff"), Routed("git.run"),
            Routed("http.fetch"), Routed("shared.read"),
            Routed("mcp.cortex.cortex_search"), Routed("mcp.cortex.cortex_read"), Routed("mcp.cortex.pao")
        };
        string[] RoutedNames(string task, bool projectBound = false) =>
            AgentPrompt.SelectTools(task, routingTools, projectBound: projectBound).Select(x => x.Name).ToArray();

        Check(RoutedNames("Calculate choose(9,2) * 7 exactly.").SequenceEqual(["math.calculate"]),
            "arithmetic routes only the exact calculator family from unrelated allowed tools");
        Check(RoutedNames("Use PAO AgentOps to inspect the UI process diagnostics.").SequenceEqual(["process.run", "file.read", "file.search", "file.list", "file.copy", "file.edit", "file.write", "file.mkdir"]),
            "PAO and AgentOps wording routes stable Cerebral development tools instead of the raw PAO gateway");
        Check(RoutedNames("Search Cortex knowledge and read the continuity namespace.").ToHashSet().SetEquals(["mcp.cortex.cortex_read", "mcp.cortex.cortex_search"]) &&
            !RoutedNames("Search Cortex knowledge and read the continuity namespace.").Contains("mcp.cortex.pao"),
            "Cortex knowledge work routes retrieval tools without treating PAO as a knowledge operation");
        var developmentTools = RoutedNames("Fix the application source file, build it, and run the focused tests.");
        Check(developmentTools.SequenceEqual(["process.run", "file.read", "file.search", "file.list", "file.copy", "file.edit", "file.write", "file.mkdir"]),
            "development work receives a bounded core file and process tool set in stable priority order");
        Check(RoutedNames("The plugin-owned SQLite Book Bible and book.* tools are canonical. Continue the book project without filesystem or process operations.", projectBound: true).Length == 0,
            "a plugin-owned canonical workflow does not inherit generic project file or process tools from book and project wording");
        Check(RoutedNames("Write a science-fiction story to a text file.").ToHashSet().SetEquals(developmentTools),
            "ordinary file-backed writing still receives the generic file and process tools");
        Check(RoutedNames("Continue with the next step.", projectBound: true).ToHashSet().SetEquals(developmentTools),
            "a project-bound continuation preserves the core development tools despite terse steering text");
        Check(AgentPrompt.SelectTools("code math Cortex PAO git HTTP shared file build", routingTools).Count == AgentPrompt.MaxRoutedTools,
            "mixed lexical routing remains bounded to the normal tool-catalog limit");

        ToolDescriptor Extension(string name, params string[] prerequisites) =>
            Routed(name) with { PrerequisiteTools = prerequisites };
        var workflowTools = new[]
        {
            Extension("book.inspect"),
            Extension("book.chapter.plan", "book.inspect"),
            Extension("book.chapter.status", "book.chapter.plan") with { ActivationTools = ["book.inspect", "book.chapter.plan"] },
            Extension("book.chapter.context", "book.chapter.status") with { ActivationTools = ["book.chapter.plan", "book.chapter.status"] },
            Extension("book.chapter.draft", "book.chapter.context") with { ActivationTools = ["book.chapter.plan", "book.chapter.status"] },
            Extension("book.export") with { ActivationTools = ["book.chapter.draft"] },
            Extension("book.chapter.merge"),
            Extension("book.chapter.revise")
        };
        const string workflowRequest = "Draft a chapter through book.chapter.draft and export the completed manuscript.";
        var entryWorkflow = AgentPrompt.SelectTools(workflowRequest, workflowTools, maxTools: 8).Select(x => x.Name).ToArray();
        Check(entryWorkflow.Contains("book.inspect") && entryWorkflow.Contains("book.chapter.plan") &&
              !entryWorkflow.Contains("book.chapter.status") && !entryWorkflow.Contains("book.chapter.draft") && !entryWorkflow.Contains("book.export"),
            "an extension workflow initially exposes its entry operations while receipt-gated later operations stay out of the action schema");
        var inspectedWorkflow = AgentPrompt.SelectTools(workflowRequest, workflowTools, maxTools: 8,
                successfulToolNames: ["book.inspect"])
            .Select(x => x.Name).ToArray();
        Check(inspectedWorkflow.Contains("book.chapter.status") && !inspectedWorkflow.Contains("book.chapter.draft"),
            "a successful inspection activates chapter status without prematurely exposing drafting");
        var routedWorkflow = AgentPrompt.SelectTools(workflowRequest, workflowTools, maxTools: 6,
                successfulToolNames: ["book.inspect", "book.chapter.plan", "book.chapter.draft"])
            .Select(x => x.Name).ToArray();
        Check(routedWorkflow.SequenceEqual([
                "book.inspect", "book.chapter.plan", "book.chapter.status", "book.chapter.context", "book.chapter.draft", "book.export"
            ]),
            "extension operation terms and declared prerequisite chains keep a bounded plugin workflow in executable order: " + string.Join(", ", routedWorkflow));
        var repairWorkflowTools = workflowTools.Concat([
            Extension("book.chapter.audits", "book.chapter.status") with { SingleUsePerAssignment = true },
            Extension("book.chapter.read", "book.chapter.status"),
            Extension("book.chapter.rewrite", "book.chapter.audits", "book.chapter.read") with { ActivationTools = ["book.chapter.audits"] }
        ]).ToArray();
        const string repairRequest = "Call book.chapter.audits, read the passage with book.chapter.read, then call book.chapter.rewrite. Do not call book.chapter.plan.";
        var repairWorkflow = AgentPrompt.SelectTools(repairRequest, repairWorkflowTools,
                requiredToolNames: ["book.chapter.audits", "book.chapter.read", "book.chapter.rewrite"], maxTools: 8,
                successfulToolNames: ["book.chapter.status", "book.chapter.audits", "book.chapter.read"])
            .Select(x => x.Name).ToArray();
        Check(repairWorkflow.Contains("book.chapter.rewrite") && repairWorkflow.Contains("book.chapter.read") &&
              repairWorkflow.Contains("book.chapter.audits") && !repairWorkflow.Contains("book.chapter.plan"),
            "an explicit negated extension operation stays out of the repair catalog while required repair tools remain actionable");
        var referenceSearch = new ToolDescriptor("book.reference.search",
            "Search reusable story reference templates and architecture modules by purpose, keywords, or body text.",
            CerebralJson.Element(new { type = "object", additionalProperties = false }), "book.reference.read");
        var guideConstrainedRepair = AgentPrompt.SelectTools(
                repairRequest + " Reference templates are guides, not a framework.",
                repairWorkflowTools.Append(referenceSearch).ToArray(),
                requiredToolNames: ["book.chapter.audits", "book.chapter.read", "book.chapter.rewrite"], maxTools: 8,
                successfulToolNames: ["book.chapter.status", "book.chapter.audits", "book.chapter.read"])
            .Select(x => x.Name).ToArray();
        Check(!guideConstrainedRepair.Contains("book.reference.search") && guideConstrainedRepair.Contains("book.chapter.rewrite"),
             "a guide-only template constraint does not activate reference search during an exact repair workflow");
        var categoryConstrainedRepair = AgentPrompt.SelectTools(
                "Repair the selected passage. Do not call any book.reference tool, planning tool, production drafting tool, or any other workflow tool.",
                repairWorkflowTools.Concat([
                    referenceSearch,
                    Extension("book.production.plan"),
                    Extension("book.story.plan")
                ]).ToArray(),
                requiredToolNames: ["book.chapter.audits", "book.chapter.read", "book.chapter.rewrite"], maxTools: 8,
                successfulToolNames: ["book.chapter.status", "book.chapter.audits", "book.chapter.read"])
            .Select(x => x.Name).ToArray();
        Check(categoryConstrainedRepair.Contains("book.chapter.rewrite") &&
              !categoryConstrainedRepair.Contains("book.chapter.plan") &&
              !categoryConstrainedRepair.Contains("book.production.plan") &&
              !categoryConstrainedRepair.Contains("book.story.plan"),
            "a negated planning-tool category suppresses every planning operation while retaining the exact repair path");
        var auditWorkflowTools = new[]
        {
            Extension("book.chapter.status"),
            Extension("book.chapter.read", "book.chapter.status"),
            Extension("book.chapter.audits", "book.chapter.status") with { SingleUsePerAssignment = true },
            Extension("book.chapter.revise"),
            Extension("book.chapter.merge"),
            Extension("book.chapter.similarity", "book.chapter.read") with { ActivationTools = ["book.chapter.read"] },
            Extension("book.chapter.audit.resolve", "book.chapter.audits", "book.chapter.read")
                with { ActivationTools = ["book.chapter.similarity"] }
        };
        const string auditWorkflowRequest = "Review the chapter and resolve any repaired audit.";
        var unresolvedAuditWorkflow = AgentPrompt.SelectTools(auditWorkflowRequest, auditWorkflowTools, maxTools: 8,
                successfulToolNames: ["book.chapter.status", "book.chapter.audits", "book.chapter.read"])
            .Select(x => x.Name).ToArray();
        Check(!unresolvedAuditWorkflow.Contains("book.chapter.audit.resolve") && unresolvedAuditWorkflow.Contains("book.chapter.similarity"),
            "independent audit resolution remains hidden until the fresh similarity receipt completes post-edit verification");
        var repairedAuditWorkflow = AgentPrompt.SelectTools(auditWorkflowRequest, auditWorkflowTools, maxTools: 8,
                successfulToolNames: ["book.chapter.status", "book.chapter.audits", "book.chapter.read", "book.chapter.similarity"])
            .Select(x => x.Name).ToArray();
        Check(repairedAuditWorkflow.Contains("book.chapter.audit.resolve"),
            "a fresh similarity receipt activates exact audit resolution for an independent verification assignment");
        var verificationOnlyWorkflow = AgentPrompt.SelectTools(
                "Call book.chapter.read to verify the applied repair. Do not resolve audit 9 in this assignment.",
                auditWorkflowTools, maxTools: 8,
                successfulToolNames: ["book.chapter.status", "book.chapter.audits", "book.chapter.read", "book.chapter.similarity"])
            .Select(x => x.Name).ToArray();
        Check(!verificationOnlyWorkflow.Contains("book.chapter.audit.resolve") && verificationOnlyWorkflow.Contains("book.chapter.read"),
            "a direct do-not-resolve instruction suppresses the activated resolution tool while retaining verification reads");
        var exactAuditWorkflow = AgentPrompt.SelectTools(
                "Call book.chapter.audits, book.chapter.read, book.chapter.similarity, and book.chapter.audit.resolve.",
                auditWorkflowTools.Concat([Extension("book.production.outline"), Extension("book.context")]).ToArray(),
                requiredToolNames: ["book.chapter.audits", "book.chapter.read", "book.chapter.similarity", "book.chapter.audit.resolve"],
                maxTools: 8, successfulToolNames: ["book.chapter.audits"], requiredOnly: true)
            .Select(x => x.Name).ToArray();
        Check(exactAuditWorkflow.SequenceEqual(["book.chapter.read"]),
            "an exact multi-tool workflow retires a successful single-use gate and projects its activated successor: " +
            string.Join(", ", exactAuditWorkflow));
        var exactAuditAfterRead = AgentPrompt.SelectTools(
                "Call book.chapter.audits, book.chapter.read, book.chapter.similarity, and book.chapter.audit.resolve.",
                auditWorkflowTools.Concat([Extension("book.production.outline"), Extension("book.context")]).ToArray(),
                requiredToolNames: ["book.chapter.audits", "book.chapter.read", "book.chapter.similarity", "book.chapter.audit.resolve"],
                maxTools: 8, successfulToolNames: ["book.chapter.audits", "book.chapter.read"], requiredOnly: true)
            .Select(x => x.Name).ToArray();
        Check(exactAuditAfterRead.SequenceEqual(["book.chapter.read", "book.chapter.similarity"]),
            "an exact multi-tool workflow activates only its next required verification operation: " +
            string.Join(", ", exactAuditAfterRead));
        var exactAuditAfterFailedResolution = AgentPrompt.SelectTools(
                "Call book.chapter.audits, book.chapter.read, book.chapter.similarity, and book.chapter.audit.resolve.",
                auditWorkflowTools, requiredToolNames: ["book.chapter.audits", "book.chapter.read", "book.chapter.similarity", "book.chapter.audit.resolve"],
                maxTools: 8, successfulToolNames: ["book.chapter.audits", "book.chapter.read", "book.chapter.similarity"], requiredOnly: true,
                latestSuccessfulToolSequences: new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["book.chapter.audits"] = 10, ["book.chapter.read"] = 11, ["book.chapter.similarity"] = 12
                },
                latestFailedToolSequences: new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["book.chapter.audit.resolve"] = 13
                })
            .Select(x => x.Name).ToArray();
        Check(!exactAuditAfterFailedResolution.Contains("book.chapter.audit.resolve") && exactAuditAfterFailedResolution.Contains("book.chapter.read") &&
               !exactAuditAfterFailedResolution.Contains("book.chapter.similarity"),
            "a failed gated operation invalidates its activation receipt and walks recovery back to the preceding required read");
        var exactAuditAfterFreshSimilarity = AgentPrompt.SelectTools(
                "Call book.chapter.audits, book.chapter.read, book.chapter.similarity, and book.chapter.audit.resolve.",
                auditWorkflowTools, requiredToolNames: ["book.chapter.audits", "book.chapter.read", "book.chapter.similarity", "book.chapter.audit.resolve"],
                maxTools: 8, successfulToolNames: ["book.chapter.audits", "book.chapter.read", "book.chapter.similarity"], requiredOnly: true,
                latestSuccessfulToolSequences: new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["book.chapter.audits"] = 10, ["book.chapter.read"] = 14, ["book.chapter.similarity"] = 15
                },
                latestFailedToolSequences: new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["book.chapter.audit.resolve"] = 13
                })
            .Select(x => x.Name).ToArray();
        Check(exactAuditAfterFreshSimilarity.Contains("book.chapter.audit.resolve"),
            "a fresh activation receipt re-enables the previously failed gated operation");
        var productionReviewTools = new[]
        {
            Extension("book.production.status"),
            Extension("book.chapter.status", "book.production.status") with { ActivationTools = ["book.production.status"] },
            Extension("book.chapter.read", "book.chapter.status") with { ActivationTools = ["book.chapter.status"] },
            Extension("book.chapter.similarity", "book.chapter.status", "book.chapter.read") with { ActivationTools = ["book.chapter.read"] },
            Extension("book.chapter.review.complete", "book.chapter.status", "book.chapter.read", "book.chapter.similarity") with { ActivationTools = ["book.chapter.similarity"] },
            Extension("book.production.review", "book.production.status") with { ActivationTools = ["book.production.status"] },
            Extension("book.chapter.review.assess", "book.production.review") with { ActivationTools = ["book.production.review"], ModelVisible = false },
            Extension("book.chapter.append", "book.chapter.draft") with { ActivationTools = ["book.chapter.draft"] }
        };
        const string productionReviewRequest = "Call book.production.status, then call book.chapter.status, then call book.chapter.read, finally call book.chapter.similarity and complete the chapter review.";
        var afterProductionStatus = AgentPrompt.SelectTools(productionReviewRequest, productionReviewTools, maxTools: 8,
                successfulToolNames: ["book.production.status"])
            .Select(x => x.Name).ToArray();
        Check(afterProductionStatus.Contains("book.chapter.status") && !afterProductionStatus.Contains("book.chapter.append") &&
              !afterProductionStatus.Contains("book.chapter.similarity") && afterProductionStatus.Contains("book.production.review") &&
              !afterProductionStatus.Contains("book.chapter.review.assess"),
            "production status opens the exact chapter review path without exposing the raw append sink");
        var afterChapterRead = AgentPrompt.SelectTools(productionReviewRequest, productionReviewTools, maxTools: 8,
                successfulToolNames: ["book.production.status", "book.chapter.status", "book.chapter.read"])
            .Select(x => x.Name).ToArray();
        Check(afterChapterRead.Contains("book.chapter.similarity") && !afterChapterRead.Contains("book.chapter.review.complete") &&
              !afterChapterRead.Contains("book.chapter.append"),
            "bounded chapter reads activate similarity while completion waits for its receipt and raw append stays hidden");
        var productionChecklistJob = new AgentJob
        {
            Prompt = "Call book.production.status for the selected Book Bible. The production-owned choices are book.production.plan for plan-next-chapter, book.production.draft for draft-current-chapter, and book.production.review for review-current-chapter. Perform zero or one returned action."
        };
        var productionChecklistReceipts = AgentPrompt.ExplicitReceiptToolNames(productionChecklistJob, new TaskSession(), productionReviewTools
            .Concat([Extension("book.production.plan", "book.production.status"), Extension("book.production.draft", "book.production.status")]).ToArray());
        Check(productionChecklistReceipts.SequenceEqual(["book.production.status"]),
            "conditional production alternatives remain routed choices without becoming two impossible mandatory receipts");
        var excessiveRecoveryTools = Enumerable.Range(0, 33).Select(index => Routed("fixture.tool" + index)).ToArray();
        var excessiveRecoveryRejected = false;
        try { AgentPrompt.SelectTools("continue", excessiveRecoveryTools, excessiveRecoveryTools.Select(x => x.Name)); }
        catch (InvalidOperationException ex) { excessiveRecoveryRejected = ex.Message.Contains("33 distinct unresolved tools", StringComparison.Ordinal); }
        Check(excessiveRecoveryRejected, "an excessive required recovery catalog fails clearly instead of becoming an unbounded prompt");

        var recoveryJob = new AgentJob
        {
            StartSequence = 1,
            Evidence =
            [
                new() { Name = "http.fetch", Success = false, Summary = "HTTP failed." },
                new() { Name = "git.status", Success = false, Summary = "Already resolved.", Resolution = "later evidence" }
            ]
        };
        var recoverySession = new TaskSession
        {
            Messages =
            [
                new() { Sequence = 1, Role = "assistant", Content = "{\"action\":\"tool\",\"name\":\"mcp.cortex.pao\",\"arguments\":{}}" },
                new() { Sequence = 2, Role = "user", Origin = "service", Content = "The failed process.run action must be repaired." },
                new() { Sequence = 3, Role = "user", ToolName = "shared.read", Content = "Tool shared.read; success=false" }
            ]
        };
        var requiredTools = AgentPrompt.RequiredToolNames(recoveryJob, recoverySession, routingTools);
        Check(requiredTools.ToHashSet().SetEquals(["http.fetch", "mcp.cortex.pao", "process.run", "shared.read"]),
            "unresolved evidence and current action, service and tool messages retain their exact recovery tools");
        var correctedRecovery = AgentPrompt.SelectTools(AgentPrompt.RoutingContext(recoveryJob, recoverySession), routingTools,
                requiredTools, latestUserRequest: "Don't use PAO. Continue with the Cerebral file and process tools.")
            .Select(x => x.Name).ToArray();
        Check(!correctedRecovery.Contains("mcp.cortex.pao") && correctedRecovery.Contains("process.run"),
            "a current user correction retires an unresolved raw PAO call instead of forcing the failed gateway into the next action schema");
        var explicitMathJob = new AgentJob
        {
            Prompt = "Do not delegate. You must call the available math.calculate tool and use its receipt.", StartSequence = 1
        };
        var explicitMath = AgentPrompt.ExplicitReceiptToolNames(explicitMathJob, new TaskSession(), routingTools);
        Check(explicitMath.SequenceEqual(["math.calculate"]) &&
            AgentPrompt.RequiredToolNames(explicitMathJob, new TaskSession(), routingTools).Contains("math.calculate"),
            "an exact tool explicitly required by the current user assignment enters both receipt and routing requirement sets");
        var sequencedReceiptJob = new AgentJob
        {
            Prompt = "Inspect the saved result, then call math.calculate as supporting evidence. Finally, run mcp.cortex.pao for the requested diagnostic.",
            StartSequence = 1
        };
        Check(AgentPrompt.ExplicitReceiptToolNames(sequencedReceiptJob, new TaskSession(), routingTools)
                .SequenceEqual(["math.calculate", "mcp.cortex.pao"]),
            "sequenced then and finally tool directives require successful receipts before completion");
        var colonChecklistTools = new[]
        {
            Extension("book.inspect"), Extension("book.chapter.audits"), Extension("book.chapter.read"),
            Extension("book.chapter.rewrite"), Routed("file.inspect"), Routed("file.write")
        };
        var colonChecklistJob = new AgentJob
        {
            Prompt = "For the selected book: call book.inspect, call book.chapter.audits, call book.chapter.read, then call book.chapter.rewrite exactly once.",
            StartSequence = 1
        };
        var colonChecklistReceipts = AgentPrompt.ExplicitReceiptToolNames(colonChecklistJob, new TaskSession(), colonChecklistTools);
        var colonChecklistSelection = AgentPrompt.SelectTools(colonChecklistJob.Prompt, colonChecklistTools,
            colonChecklistReceipts, requiredOnly: colonChecklistReceipts.Count >= 2).Select(x => x.Name).ToArray();
        Check(colonChecklistReceipts.ToHashSet().SetEquals(["book.inspect", "book.chapter.audits", "book.chapter.read", "book.chapter.rewrite"]) &&
              colonChecklistSelection.ToHashSet().SetEquals(colonChecklistReceipts),
            "a colon-led exact plugin checklist requires every named call and excludes unrelated file tools");
        var quotedResolutionTools = new[]
        {
            Extension("book.chapter.audits"), Extension("book.chapter.read"), Extension("book.chapter.similarity"),
            Extension("book.chapter.audit.resolve") with { ActivationTools = ["book.chapter.similarity"] }
        };
        var quotedResolutionJob = new AgentJob
        {
            Prompt = "Call book.chapter.audits. Call book.chapter.read. Call book.chapter.similarity. Then call book.chapter.audit.resolve with resolution \"The repaired passage leaves the fork closed.\" Do not call any write or rewrite tool.",
            StartSequence = 1
        };
        var quotedResolutionReceipts = AgentPrompt.ExplicitReceiptToolNames(quotedResolutionJob, new TaskSession(), quotedResolutionTools);
        var quotedResolutionSelection = AgentPrompt.SelectTools(quotedResolutionJob.Prompt, quotedResolutionTools,
            quotedResolutionReceipts, requiredOnly: true, successfulToolNames: ["book.chapter.similarity"],
            latestSuccessfulToolSequences: new Dictionary<string, int> { ["book.chapter.similarity"] = 7 },
            latestFailedToolSequences: new Dictionary<string, int>()).Select(x => x.Name).ToArray();
        Check(quotedResolutionReceipts.ToHashSet().SetEquals(quotedResolutionTools.Select(x => x.Name)) &&
              quotedResolutionSelection.Contains("book.chapter.audit.resolve"),
            "a quoted resolution followed by a separate negative constraint retains the explicitly required gated resolve tool");
        var conditionalReceiptJob = new AgentJob
        {
            Prompt = "Inspect the inputs. If the evidence passes, call math.calculate and use its receipt.", StartSequence = 1
        };
        Check(AgentPrompt.ExplicitReceiptToolNames(conditionalReceiptJob, new TaskSession(), routingTools)
                .SequenceEqual(["math.calculate"]),
            "a conditional call directive still requires the successful receipt before claiming its branch completed");
        Check(AgentPrompt.ExplicitReceiptToolNames(new AgentJob { Prompt = "Calculate choose(9,2) * 7 exactly.", StartSequence = 1 }, new TaskSession(), routingTools).Count == 0 &&
            AgentPrompt.ExplicitReceiptToolNames(new AgentJob { Prompt = "Is math.calculate available?", StartSequence = 1 }, new TaskSession(), routingTools).Count == 0 &&
            AgentPrompt.ExplicitReceiptToolNames(new AgentJob { Prompt = "Do not use math.calculate; explain the arithmetic.", StartSequence = 1 }, new TaskSession(), routingTools).Count == 0,
            "ordinary math, availability questions and negated tool mentions do not force execution evidence");
        Check(AgentPrompt.ExplicitReceiptToolNames(recoveryJob, recoverySession, routingTools).Count == 0,
            "unresolved failed tools stay routing requirements without becoming mandatory successful-receipt requirements");
        var recoverySelection = AgentPrompt.SelectTools("Fix the project code and continue.", routingTools, requiredTools, projectBound: true);
        Check(requiredTools.All(name => recoverySelection.Any(x => x.Name == name)) &&
            developmentTools.All(name => recoverySelection.Any(x => x.Name == name)) &&
            recoverySelection.Count <= AgentPrompt.MaxRoutedTools + requiredTools.Count,
            "recovery tools augment rather than displace the bounded core project-development catalog");

        JsonElement ActionAlternative(JsonElement schema, string action) => schema.GetProperty("oneOf").EnumerateArray().Single(item =>
            item.GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == action);
        var cleanSchema = AgentActionSchema.Create([Routed("math.calculate")], [], [], []);
        var cleanFinal = ActionAlternative(cleanSchema, "final");
        var cleanResolutions = cleanFinal.GetProperty("properties").GetProperty("resolutions");
        Check(cleanResolutions.GetProperty("maxItems").GetInt32() == 0 &&
            cleanFinal.GetProperty("required").EnumerateArray().Any(x => x.GetString() == "resolutions"),
            "a final action with no unresolved failures is schema-constrained to an explicit empty resolutions array");

        var failureSchema = AgentActionSchema.Create([Routed("math.calculate"), Routed("mcp.cortex.pao")], [], [],
            ["failure-b", "failure-a", "failure-b"]);
        var failureResolutions = ActionAlternative(failureSchema, "final").GetProperty("properties").GetProperty("resolutions");
        var resolutionProperties = failureResolutions.GetProperty("items").GetProperty("properties");
        Check(failureResolutions.GetProperty("maxItems").GetInt32() == 2 &&
            resolutionProperties.GetProperty("failureId").GetProperty("enum").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["failure-a", "failure-b"]) &&
            resolutionProperties.GetProperty("evidenceId").GetProperty("type").GetString() == "string" &&
            !resolutionProperties.GetProperty("evidenceId").TryGetProperty("enum", out _),
            "failure resolution schema permits only actual unresolved failure IDs while leaving later proof evidence selectable");
        var schemaToolNames = failureSchema.GetProperty("oneOf").EnumerateArray()
            .Where(item => item.GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "tool")
            .Select(item => item.GetProperty("properties").GetProperty("name").GetProperty("const").GetString()).ToArray();
        Check(schemaToolNames.SequenceEqual(["math.calculate", "mcp.cortex.pao"]),
            "action schema advertises exactly the routed tool descriptors supplied for that generation step");
        var receiptGatedSchema = AgentActionSchema.Create([Routed("mcp.cortex.pao"), Routed("math.calculate")], ["helper"], ["file"], [], ["math.calculate"]);
        var receiptGatedAlternatives = receiptGatedSchema.GetProperty("oneOf").EnumerateArray().ToArray();
        var receiptGatedActions = receiptGatedAlternatives.Select(item => item.GetProperty("properties").GetProperty("action").GetProperty("const").GetString()).ToArray();
        var receiptGatedTools = receiptGatedAlternatives.Where(item => item.GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "tool")
            .Select(item => item.GetProperty("properties").GetProperty("name").GetProperty("const").GetString()).ToArray();
        Check(!receiptGatedActions.Contains("final") && !receiptGatedActions.Contains("present") && !receiptGatedActions.Contains("delegate") &&
            receiptGatedTools.SequenceEqual(["math.calculate"]),
            "missing explicitly required evidence suppresses final and unrelated actions while keeping only the required tool selectable");
        var stagedRequiredSchema = AgentActionSchema.Create([Routed("book.chapter.plan")], [], [], [], [], allowFinal: false);
        var stagedRequiredActions = stagedRequiredSchema.GetProperty("oneOf").EnumerateArray()
            .Select(item => item.GetProperty("properties").GetProperty("action").GetProperty("const").GetString()).ToArray();
        Check(stagedRequiredActions.Contains("tool") && stagedRequiredActions.Contains("ask") && !stagedRequiredActions.Contains("final"),
            "an inactive later receipt requirement can block final completion while prerequisite tools remain selectable");
        var gatedAsk = receiptGatedAlternatives.Single(item => item.GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "ask");
        Check(gatedAsk.GetProperty("properties").GetProperty("required").GetProperty("const").GetBoolean() &&
            !gatedAsk.GetProperty("properties").GetProperty("canAssume").GetProperty("const").GetBoolean(),
            "receipt-gated schema permits only a required non-assumable human question besides tool execution");
        var restoredActions = AgentActionSchema.Create([Routed("math.calculate")], ["helper"], ["file"], [], [])
            .GetProperty("oneOf").EnumerateArray().Select(item => item.GetProperty("properties").GetProperty("action").GetProperty("const").GetString()).ToArray();
        var batchQuestions = ActionAlternative(AgentActionSchema.Create([Routed("math.calculate")], [], [], [], []), "ask.batch")
            .GetProperty("properties").GetProperty("questions");
        Check(restoredActions.Contains("final") && restoredActions.Contains("present") && restoredActions.Contains("delegate") &&
            restoredActions.Contains("ask") && restoredActions.Contains("ask.batch") &&
            batchQuestions.GetProperty("minItems").GetInt32() == 2 &&
            batchQuestions.GetProperty("maxItems").GetInt32() == AgentPrompt.MaxPendingOptionalQuestions,
            "normal action schema supports one question or a bounded batch whose questions remain separate");
        var exactBatchSchema = AgentActionSchema.Create([Routed("math.calculate")], [], requiredQuestionCount: 3);
        var exactBatchActions = exactBatchSchema.GetProperty("oneOf").EnumerateArray()
            .Select(item => item.GetProperty("properties").GetProperty("action").GetProperty("const").GetString()).ToArray();
        var exactBatchQuestions = ActionAlternative(exactBatchSchema, "ask.batch").GetProperty("properties").GetProperty("questions");
        Check(!exactBatchActions.Contains("ask") && exactBatchActions.Count(x => x == "ask.batch") == 1 &&
              exactBatchQuestions.GetProperty("minItems").GetInt32() == 3 && exactBatchQuestions.GetProperty("maxItems").GetInt32() == 3,
            "an explicit three-question request removes the single ask shape and schema-constrains one exact batch");
        var directCapabilityActions = AgentActionSchema.Create([Routed("math.calculate")], ["helper"], ["file"], [], [], allowAsk: false)
            .GetProperty("oneOf").EnumerateArray().Select(item => item.GetProperty("properties").GetProperty("action").GetProperty("const").GetString()).ToArray();
        Check(!directCapabilityActions.Contains("ask") && !directCapabilityActions.Contains("ask.batch") && directCapabilityActions.Contains("final") && directCapabilityActions.Contains("tool") &&
            directCapabilityActions.Contains("delegate") && directCapabilityActions.Contains("present"),
            "direct capability requests remove only the ask action so a rejected self-question cannot spin");
        var unavailableRequiredRejected = false;
        try { AgentActionSchema.Create([Routed("math.calculate")], [], [], [], ["process.run"]); }
        catch (InvalidOperationException ex) { unavailableRequiredRejected = ex.Message.Contains("process.run", StringComparison.Ordinal); }
        Check(unavailableRequiredRejected, "receipt-gated schema fails clearly if a required tool action is not advertised");

        Check(AgentPrompt.OutputTokenLimit(4096, 1800, 1024, "off", 1024) == 1024,
            "output allowance keeps the configured response limit when full capacity remains");
        Check(AgentPrompt.OutputTokenLimit(4096, 3300, 1024, "off", 1024) == 668,
            "output allowance contracts to the exact remaining context capacity");
        Check(AgentPrompt.OutputTokenLimit(4096, 3000, 1024, "on", 512) == 456,
            "output allowance reserves enabled private-reasoning capacity and the safety margin");
        Check(AgentPrompt.OutputTokenLimit(4096, 3850, 1024, "off", 1024) == 0,
            "output allowance rejects capacity below the reliable structured-action floor");
        Check(AgentPrompt.OutputTokenLimit(4096, 1000, 64, "off", 1024) == 0,
            "a configured response ceiling below the reliable structured-action floor is rejected");
        Check(AgentPrompt.OutputTokenLimit(8192, 1000, 3000, "off", 1024) <= 3000,
            "output allowance never raises the configured response limit");

        var dependentStoryRequest = "no write that short story say 30 pages graphic details think hard about plot arc";
        var dependentStoryAnswer = "Answer to: Do you want an outline or detailed plot summary?\nProvide a detailed plot summary and use it to write that story to a txt file";
        var storedStoryCheckpoint = "The requested work is a 30-page fantasy story titled 'Siren's Embrace', set in a lush fairy realm, with a succubus and a deliberate plot arc. " +
            new string('x', 1500) + " Preserve continuity while writing the story iteratively to the selected text file.";
        var smallStorySession = new TaskSession
        {
            Checkpoint = storedStoryCheckpoint,
            CheckpointThroughSequence = 13,
            ResponseSharePercent = 25,
            Messages =
            [
                new() { Sequence = 11, Role = "user", Origin = "user", Content = dependentStoryRequest },
                new() { Sequence = 12, Role = "assistant", Origin = "agent", Content = "Question: clarification" },
                new() { Sequence = 13, Role = "user", Origin = "user", Content = "Answer to: clarification\n" + dependentStoryRequest },
                new() { Sequence = 14, Role = "assistant", Origin = "agent", Content = "Question: Do you want an outline or a detailed plot summary?" },
                new() { Sequence = 15, Role = "user", Origin = "user", Content = dependentStoryAnswer }
            ]
        };
        static int FixtureTokens(IReadOnlyList<ModelMessage> messages) => messages.Sum(x => x.Content.Length);
        var requiredWithoutCheckpoint = WorkingContext.AssembleWithCheckpoint(smallStorySession, "", 11, "");
        var paddedSystem = new string('g', Math.Max(0, 2920 - FixtureTokens(requiredWithoutCheckpoint)));
        var unboundedStoryPrompt = WorkingContext.Assemble(smallStorySession, paddedSystem, 11);
        Check(AgentPrompt.OutputTokenLimit(4096, FixtureTokens(unboundedStoryPrompt), 1024, "off", 1024) == 0,
            "the exact small-context regression starts with a generic checkpoint that prevents even a structured action");
        var fittedStoryPrompt = WorkingContext.FitCheckpointAsync(smallStorySession, paddedSystem, 11, null,
            4096, 1024, "off", 1024, (messages, _) => Task.FromResult(FixtureTokens(messages)), CancellationToken.None).GetAwaiter().GetResult();
        var fittedStoryText = string.Join('\n', fittedStoryPrompt.Messages.Select(x => x.Content));
        Check(fittedStoryPrompt.CheckpointProjected && fittedStoryPrompt.ProjectedCheckpointCharacters > 0 &&
              fittedStoryPrompt.OutputTokens >= 512 && fittedStoryPrompt.OutputTokens <= 1024,
            "budget-aware projection retains a dependent checkpoint excerpt and a useful bounded file-action response reserve");
        Check(fittedStoryText.Contains("Siren's Embrace", StringComparison.Ordinal) &&
              fittedStoryText.Contains(dependentStoryRequest, StringComparison.Ordinal) &&
              fittedStoryText.Contains(dependentStoryAnswer, StringComparison.Ordinal),
            "checkpoint fitting preserves dependent story facts, the original assignment anchor, and exact latest input");
        Check(smallStorySession.Checkpoint == storedStoryCheckpoint && smallStorySession.ResponseSharePercent == 25,
            "checkpoint fitting leaves the complete stored checkpoint and user response-share setting unchanged");

        var uncompacted = new TaskSession
        {
            Messages =
            [
                new() { Sequence = 1, Role = "user", Origin = "user", Content = new string('a', 500) },
                new() { Sequence = 2, Role = "assistant", Origin = "agent", Content = new string('b', 500) },
                new() { Sequence = 3, Role = "assistant", Origin = "agent", Content = new string('c', 500) },
                new() { Sequence = 4, Role = "assistant", Origin = "agent", Content = new string('d', 100) },
                new() { Sequence = 5, Role = "user", Origin = "user", Content = new string('e', 100) }
            ]
        };
        var compactionSystem = new string('s', 2600);
        var crowded = WorkingContext.FitCheckpointAsync(uncompacted, compactionSystem, 1, null,
            4096, 1024, "off", 1024, (messages, _) => Task.FromResult(FixtureTokens(messages)), CancellationToken.None).GetAwaiter().GetResult();
        var usefulFloor = WorkingContext.FitAfterCompactionFloorAsync(uncompacted, compactionSystem, 1, null,
            4096, 1024, "off", 1024, (messages, _) => Task.FromResult(FixtureTokens(messages)), CancellationToken.None).GetAwaiter().GetResult();
        Check(crowded.OutputTokens == 0 && usefulFloor is { OutputTokens: > 0 } && AgentEngine.CompactionCanImprove(crowded, usefulFloor),
            "automatic compaction is selected only when removing older history can make the original assignment fit");
        var impossibleFloor = WorkingContext.FitAfterCompactionFloorAsync(uncompacted, new string('s', 3900), 1, null,
            4096, 1024, "off", 1024, (messages, _) => Task.FromResult(FixtureTokens(messages)), CancellationToken.None).GetAwaiter().GetResult();
        Check(impossibleFloor is { OutputTokens: 0 } && !AgentEngine.CompactionCanImprove(crowded, impossibleFloor) &&
              uncompacted.CheckpointThroughSequence == 0 && uncompacted.Checkpoint is null,
            "fixed guidance and current-input overflow is detected before a futile checkpoint is generated or adopted");
        var longFormGuidance = LongFormWorkflow.Guidance(new LongFormObjective(4000, "4,000 words", null), null, 10);
        Check(WorkingContext.CheckpointWordTarget(4096) == 170 &&
              !AgentPrompt.Protocol.Contains("long-form objective", StringComparison.OrdinalIgnoreCase) &&
              longFormGuidance.Contains("Service-owned long-form objective", StringComparison.Ordinal) &&
              longFormGuidance.Contains("outline is preparation", StringComparison.Ordinal),
            "a 4K owner keeps its compact checkpoint target while long-form guidance loads only for a detected long-form task");

        var compactable = new TaskSession { Messages = [new() { Sequence = 1 }, new() { Sequence = 2 }, new() { Sequence = 3 }] };
        Check(AgentPrompt.HasCompactableHistory(compactable) && !AgentPrompt.HasCompactableHistory(new TaskSession { Messages = [new() { Sequence = 1 }] }),
            "compaction is attempted only when the session has older material beyond its preserved latest exchange");

        var scope = new CortexAccessScope("internal-project-id", "cortex", ["project.fixture", "knowledge.shared"], "project.fixture", true,
            KnowledgeWriteApproved: true, ProviderFingerprint: "private-provider-fingerprint");
        var cortexText = AgentPrompt.Cortex(scope, "cerebral-native-session", true);
        var cortex = FirstJsonObject(cortexText);
        Check(cortex.GetProperty("taskId").GetString() == "cerebral-native-session" &&
            cortex.GetProperty("taskLifecycleOwner").GetString() == "service" &&
            cortex.GetProperty("requireKnowledgeApproval").GetBoolean(),
            "Cortex prompt uses the native Cerebral session identity and explicit service lifecycle ownership");
        Check(!cortexText.Contains("private-provider-fingerprint", StringComparison.Ordinal) &&
            !cortex.TryGetProperty("providerFingerprint", out _) && !cortex.TryGetProperty("knowledgeWriteApproved", out _) &&
            !cortexText.Contains("internal-project-id", StringComparison.Ordinal),
            "Cortex prompt excludes provider fingerprints and internal approval-state metadata");
        Check(FirstJsonObject(AgentPrompt.Cortex(scope, "cerebral-native-session", false)).GetProperty("taskLifecycleOwner").GetString() == "agent",
            "Cortex prompt identifies agent-owned lifecycle when automatic continuity is disabled");
        Check(AgentPrompt.Cortex(null, "unbound-session", false).Length == 0,
            "an unbound Cortex layer adds no placeholder prompt text or invented task binding");

        Console.WriteLine($"Agent prompt: {assertions} deterministic checks passed; no model, tool or provider invoked.");
    }
}
