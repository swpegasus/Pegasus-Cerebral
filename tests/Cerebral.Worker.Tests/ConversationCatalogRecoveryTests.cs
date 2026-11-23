using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static void RunCatalogRecoveryChecks(Action<bool, string> check)
    {
        const string invalidName = "src/Cerebral.Core/ScopedPathAccessPolicy.cs";
        static Fixture Failed(params string[] names)
        {
            var fixture = new Fixture();
            fixture.Describe(names);
            fixture.CatalogFailure("No granted tool matches the requested exact name.");
            return fixture;
        }

        var repeat = Failed(invalidName);
        check(repeat.Is("generate") && repeat.LastContributionText.Contains("not file paths", StringComparison.Ordinal) &&
              repeat.LastContributionText.Contains("history{eventId,offset:0}", StringComparison.Ordinal) &&
              repeat.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0,
            "failed exact schema discovery reports routing guidance without inventing execution evidence");
        var failedEffectId = repeat.Step.State.GetProperty("lastFailedCatalogSelection").GetProperty("effectId").GetString()!;
        repeat.Describe(invalidName);
        check(repeat.Is("generate") && repeat.Step.State.GetProperty("descriptions").GetInt32() == 1 &&
              repeat.Step.State.GetProperty("repeatedCatalogFailureCorrections").GetInt32() == 1 &&
              repeat.LastContributionText.Contains(failedEffectId, StringComparison.Ordinal) &&
              repeat.LastContributionText.Contains("No duplicate lookup", StringComparison.Ordinal),
            "first identical failed selection receives a correction without another host lookup");
        repeat.Describe(invalidName);
        check(repeat.Is("generate") && repeat.Step.State.GetProperty("repeatedCatalogFailureCorrections").GetInt32() == 2 &&
              repeat.Step.State.GetProperty("descriptions").GetInt32() == 1,
            "second correction survives schema-valid model actions without spending another schema lookup");
        repeat.Describe(invalidName);
        check(repeat.Step.Next is CompleteWorkflow { Success: false } &&
              repeat.Summary.Contains("two corrections", StringComparison.Ordinal) &&
              repeat.Step.State.GetProperty("descriptions").GetInt32() == 1,
            "third unchanged failed selection stops instead of consuming the wider lookup quota");

        var reordered = Failed("missing.alpha", "missing.beta");
        reordered.Describe("missing.beta", "missing.alpha");
        check(reordered.Is("generate") && reordered.Step.State.GetProperty("descriptions").GetInt32() == 1,
            "reordering an identical failed selection does not evade the duplicate guard");

        var changed = Failed(invalidName);
        changed.Describe("file.read");
        check(changed.Is("catalog") && changed.Command.Arguments.GetProperty("names")[0].GetString() == "file.read",
            "a different exact selection remains available for recovery");
        changed.Catalog(exact: true);
        check(changed.Is("generate") && !changed.Step.State.TryGetProperty("lastFailedCatalogSelection", out _) &&
              !changed.Step.State.TryGetProperty("repeatedCatalogFailureCorrections", out _),
            "successful schema resolution clears the failure guard without persisting default fields");
        changed.Describe(invalidName);
        check(changed.Is("catalog"),
            "real successful catalog progress permits a fresh lookup after dynamic provider recovery");

        var separateFailure = Failed(invalidName);
        separateFailure.Describe(invalidName);
        separateFailure.Describe("missing.other");
        separateFailure.CatalogFailure("The different requested schema is unavailable.");
        check(separateFailure.Step.State.GetProperty("lastFailedCatalogSelection").GetProperty("names")[0].GetString() == "missing.other" &&
              !separateFailure.Step.State.TryGetProperty("repeatedCatalogFailureCorrections", out _),
            "a different failed selection starts its own bounded correction window");

        var history = Failed(invalidName);
        history.Model(new { action = "history", eventId = "source-qualification", offset = 0 });
        check(history.Is("history"),
            "a schema lookup failure does not prevent the model from choosing a real retained history read");
        history.HistoryPage("The managed tool path policy is not an OS process sandbox.");
        check(history.Is("generate") && history.LastContributionText.Contains("not an OS process sandbox", StringComparison.Ordinal),
            "a corrective history read preserves its exact diagnostic source for the next model turn");
        history.Model(new { action = "final", text = "Recorded tool path policy is not an OS process sandbox." });
        check(history.Step.Next is CompleteWorkflow { Success: true },
            "a model that corrects its routing may complete without successful unrelated discovery");

        var legacy = new Fixture();
        var legacyState = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(legacy.Step.State.GetRawText(), CerebralJson.Options)!;
        legacyState.Remove("lastFailedCatalogSelection"); legacyState.Remove("repeatedCatalogFailureCorrections");
        legacy.RestoreState(CerebralJson.Element(legacyState));
        legacy.Describe("file.read");
        check(legacy.Is("catalog"),
            "pre-upgrade private harness state without catalog guard fields remains usable");
    }
}
