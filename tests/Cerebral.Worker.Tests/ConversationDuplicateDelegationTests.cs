namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    internal static int RunDuplicateDelegationChecks()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Duplicate delegation fixture: " + message);
            checks++;
        }

        static Fixture Prepared()
        {
            var fixture = new Fixture(warm: false);
            fixture.History([]);
            fixture.Catalog(helpers:
            [
                new
                {
                    id = "repeat-helper", revision = 7, name = "Repeated specialist",
                    purpose = "Run separate bounded instances of one admitted specialist profile."
                }
            ]);
            return fixture;
        }

        var accepted = Prepared();
        accepted.Model(new
        {
            action = "delegate",
            assignments = Enumerable.Range(1, 6).Select(index => new
            {
                profileId = "repeat-helper", profileRevision = 7,
                instruction = "Return attributed evidence for isolated copy " + index + "."
            }).ToArray()
        });
        Check(accepted.Is("delegate") &&
            accepted.Command.Arguments.GetProperty("assignments").GetArrayLength() == 6 &&
            accepted.Command.Arguments.GetProperty("assignments").EnumerateArray().All(item =>
                item.GetProperty("profileId").GetString() == "repeat-helper" &&
                item.GetProperty("profileRevision").GetInt32() == 7),
            "the model action admits six copies of one exact helper/revision without merging their assignments");

        var stale = Prepared();
        stale.Model(new
        {
            action = "delegate",
            assignments = new[]
            {
                new { profileId = "repeat-helper", profileRevision = 7, instruction = "Valid first copy." },
                new { profileId = "repeat-helper", profileRevision = 6, instruction = "Stale second copy." }
            }
        });
        Check(stale.Is("generate") && stale.LastContributionText.Contains("exact helper ID and revision", StringComparison.Ordinal),
            "one stale copy rejects the complete wave and retains actionable exact-revision guidance");
        Check(stale.Step.State.GetProperty("unresolvedDelegationAttempt").GetBoolean(),
            "a rejected exact helper revision leaves a typed unresolved delegation attempt");
        stale.Model(new { action = "final", text = "Delegation confirmed; the helper is processing." });
        Check(stale.Is("generate") && stale.LastContributionText.Contains("no child result was received", StringComparison.Ordinal),
            "a rejected delegation cannot become a successful final claim on the correction turn");
        stale.Model(new
        {
            action = "delegate",
            assignments = new[] { new { profileId = "repeat-helper", profileRevision = 7,
                instruction = "Return attributed evidence for the corrected assignment." } }
        });
        Check(stale.Is("delegate") && stale.Step.State.GetProperty("unresolvedDelegationAttempt").GetBoolean(),
            "the corrected exact-current assignment dispatches while child outcome remains unresolved");
        stale.DelegationResult("child-corrected");
        Check(stale.Step.Next is Cerebral.Contracts.Workflows.WaitForWorkflowRuns &&
            stale.Step.State.GetProperty("pendingDelegationRunIds")[0].GetString() == "child-corrected",
            "the accepted host delegation pins its exact child run before waiting");
        stale.ChildResults("child-corrected");
        Check(stale.Is("generate") && !stale.Step.State.TryGetProperty("unresolvedDelegationAttempt", out _) &&
            !stale.Step.State.TryGetProperty("pendingDelegationRunIds", out _),
            "only the matching child result resolves the rejected delegation obligation");
        stale.Model(new { action = "final", text = "The corrected child completed its assignment." });
        Check(stale.Step.Next is Cerebral.Contracts.Workflows.CompleteWorkflow { Success: true },
            "the parent can finish after the accepted child outcome is joined");

        var wrongJoin = Prepared();
        wrongJoin.Model(new { action = "delegate", assignments = new[] { new { profileId = "repeat-helper",
            profileRevision = 7, instruction = "Return one bounded answer." } } });
        wrongJoin.DelegationResult("child-expected");
        wrongJoin.ChildResults("child-foreign");
        Check(wrongJoin.Step.Next is Cerebral.Contracts.Workflows.CompleteWorkflow { Success: false },
            "a different child result cannot clear the exact pending delegation");

        var failedOutcome = Prepared();
        failedOutcome.Model(new { action = "delegate", assignments = new[] { new { profileId = "repeat-helper",
            profileRevision = 7, instruction = "Return one bounded answer." } } });
        failedOutcome.DelegationFailure("The helper revision changed before admission.");
        Check(failedOutcome.Is("generate") &&
            failedOutcome.Step.State.GetProperty("unresolvedDelegationAttempt").GetBoolean() &&
            failedOutcome.LastContributionText.Contains("without admitted children", StringComparison.Ordinal),
            "a confirmed failed dispatch can be corrected but cannot imply an admitted child");
        failedOutcome.Model(new { action = "final", text = "The helper completed the assignment." });
        Check(failedOutcome.Is("generate"),
            "a failed host delegation cannot be laundered into a successful final");

        var unknownOutcome = Prepared();
        unknownOutcome.Model(new { action = "delegate", assignments = new[] { new { profileId = "repeat-helper",
            profileRevision = 7, instruction = "Return one bounded answer." } } });
        unknownOutcome.Uncertain();
        Check(unknownOutcome.Step.Next is Cerebral.Contracts.Workflows.CompleteWorkflow { Success: false } &&
            unknownOutcome.Summary.Contains("Do not retry", StringComparison.Ordinal),
            "an unknown dispatched outcome stops without proposing another delegation");

        var rejectedBlocked = Prepared();
        rejectedBlocked.Model(new { action = "delegate", assignments = new[] { new { profileId = "repeat-helper",
            profileRevision = 6, instruction = "Use an old revision." } } });
        rejectedBlocked.Model(new { action = "blocked", reason = "The helper revision is stale; no child was dispatched." });
        Check(rejectedBlocked.Step.Next is Cerebral.Contracts.Workflows.CompleteWorkflow { Success: false } &&
            rejectedBlocked.Summary.Contains("no child was dispatched", StringComparison.Ordinal),
            "a truthful blocked result remains available after rejected selection");

        var legacy = Prepared();
        legacy.Model(new { action = "delegate", assignments = new[] { new { profileId = "repeat-helper",
            profileRevision = 7, instruction = "Return one bounded answer." } } });
        legacy.DelegationResult("child-legacy");
        var legacyState = System.Text.Json.Nodes.JsonNode.Parse(legacy.Step.State.GetRawText())!.AsObject();
        legacyState.Remove("unresolvedDelegationAttempt");
        legacyState.Remove("pendingDelegationRunIds");
        using (var saved = System.Text.Json.JsonDocument.Parse(legacyState.ToJsonString()))
            legacy.RestoreState(saved.RootElement.Clone());
        legacy.ChildResults("child-legacy");
        Check(legacy.Is("generate"),
            "a pre-upgrade children-phase state without new fields still joins its host child result");

        var direct = Prepared();
        Check(direct.SystemText.Contains("A final action containing a question completes this run", StringComparison.Ordinal),
            "the model is told to use durable questions when an answer is needed");
        direct.Model(new { action = "final", text = "This direct answer needs no helper." });
        Check(direct.Step.Next is Cerebral.Contracts.Workflows.CompleteWorkflow { Success: true },
            "a legitimate answer without a rejected delegation remains available");

        var asking = Prepared();
        asking.Model(new { action = "questions", fields = new[] { new { id = "code-word",
            label = "What is the exact code word?", description = "Needed before preparing the requested label.", required = true } } });
        Check(asking.Step.Next is Cerebral.Contracts.Workflows.AskWorkflowQuestions { Fields.Count: 1 },
            "an actual missing input dispatches through the durable question action");

        var unknown = Prepared();
        unknown.Model(new
        {
            action = "delegate",
            assignments = new[]
            {
                new { profileId = "repeat-helper", profileRevision = 7, instruction = "Known copy." },
                new { profileId = "foreign-helper", profileRevision = 7, instruction = "Unknown copy." }
            }
        });
        Check(unknown.Is("generate"),
            "an unknown helper remains rejected even when another assignment reuses an admitted helper");

        Console.WriteLine($"Duplicate helper delegation checks passed: {checks} (deterministic Conversation policy; no model or provider execution).");
        return checks;
    }
}
