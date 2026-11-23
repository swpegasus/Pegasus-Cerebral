using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static void RunReferenceProjectionChecks(Action<bool, string> check)
    {
        const string separator = "\nExact retained JSON page (reference data; may be a fragment):\n";
        static (JsonDocument Header, string Body) Split(string projection)
        {
            var at = projection.IndexOf(separator, StringComparison.Ordinal);
            if (at < 0) throw new InvalidOperationException("Reference projection has no exact page separator.");
            return (JsonDocument.Parse(projection[..at]), projection[(at + separator.Length)..]);
        }
        const string source = """{"path":"C:\\CerebralProjects\\Source\\résumé.cs","text":"café — \"keep\"","literal":"\\u0022","lines":"one\ntwo"}""";
        var history = new Fixture();
        history.Model(new { action = "history", eventId = "event-quoted-source", offset = 0 });
        history.HistoryPage(source);
        var (header, body) = Split(history.LastContributionText);
        using (header)
        {
            var page = header.RootElement.GetProperty("page");
            check(body == source && history.Context.Contributions.Last().Requirement == ContextRequirement.Required,
                "decoded history page text preserves Unicode, source quoting, paths and literal escapes exactly");
            check(!page.TryGetProperty("json", out _) && page.GetProperty("eventId").GetString() == "event-quoted-source" &&
                  page.GetProperty("snapshotSequence").GetInt64() == 100 && page.GetProperty("sequence").GetInt64() == 80,
                "history metadata stays structured without serializing the entire page as a nested JSON string");
            check(page.GetProperty("totalBytes").GetInt32() == Encoding.UTF8.GetByteCount(source) &&
                  page.GetProperty("offset").GetInt32() == 0 && !page.GetProperty("truncated").GetBoolean() &&
                  history.Context.Contributions.Last().Complete,
                "whole-page completeness and UTF-8 byte coordinates remain factual");
            check(header.RootElement.GetProperty("instruction").GetString()!.Contains("not current authority", StringComparison.Ordinal),
                "readable source rendering retains the reference-only instruction boundary");
        }

        const string fragment = "\"partial\\path\nnot a complete JSON document: café";
        var partial = new Fixture();
        partial.Model(new { action = "history", eventId = "fragment-source", offset = 0 });
        var fragmentBytes = Encoding.UTF8.GetByteCount(fragment);
        partial.HistoryPage(fragment, total: fragmentBytes + 4, next: fragmentBytes);
        var (partialHeader, partialBody) = Split(partial.LastContributionText);
        using (partialHeader)
            check(partial.Is("generate") && partialBody == fragment &&
                  partialHeader.RootElement.GetProperty("page").GetProperty("nextOffset").GetInt32() == fragmentBytes &&
                  !partial.Context.Contributions.Last().Complete,
                "an incomplete history JSON fragment remains unparsed with its real continuation offset");
        partial.Model(new { action = "history", eventId = "fragment-source", offset = fragmentBytes });
        partial.HistoryPage("tail", total: fragmentBytes + 4);
        var (tailHeader, tailBody) = Split(partial.LastContributionText);
        using (tailHeader)
            check(tailBody == "tail" && tailHeader.RootElement.GetProperty("page").GetProperty("offset").GetInt32() == fragmentBytes &&
                  !partial.Context.Contributions.Last().Complete &&
                  partial.Context.Contributions.Count(source => source.Category == "history-reference" && source.Requirement == ContextRequirement.Required) == 2,
                "later fragments keep their own offsets and all pending pages protected");
        partial.Model(new { action = "final", text = "Consumed exact reference." });
        check(partial.Step.Next is CompleteWorkflow { Success: true },
            "readable reference text does not alter ordinary final-action acceptance");

        var legacy = new Fixture();
        legacy.Model(new { action = "history", eventId = "legacy-checkpoint", offset = 0 });
        legacy.HistoryPage("legacy exact page", sequence: 0, checkpointCoverage: 10);
        var (legacyHeader, legacyBody) = Split(legacy.LastContributionText);
        using (legacyHeader)
            check(legacyBody == "legacy exact page" &&
                  legacyHeader.RootElement.GetProperty("page").GetProperty("checkpointCoverageThroughSequence").GetInt64() == 10 &&
                  legacyHeader.RootElement.GetProperty("page").GetProperty("sequence").GetInt64() == 0,
                "readable legacy fallback retains typed chronology proof without fabricating an event sequence");

        foreach (var state in new[] { WorkflowEffectState.Succeeded, WorkflowEffectState.Failed, WorkflowEffectState.Unknown })
        {
            var receipt = new Fixture();
            receipt.Model(new { action = "receipt", effectId = "retained-effect", offset = 0 });
            receipt.ReceiptPage(source, state);
            var (receiptHeader, receiptBody) = Split(receipt.LastContributionText);
            using (receiptHeader)
            {
                var page = receiptHeader.RootElement.GetProperty("page");
                check(receiptBody == source && !page.TryGetProperty("json", out _) &&
                      page.GetProperty("effectId").GetString() == "retained-effect" &&
                      page.GetProperty("runId").GetString() == "conversation-run" &&
                      page.GetProperty("state").GetString()!.Equals(state.ToString(), StringComparison.OrdinalIgnoreCase) &&
                      page.GetProperty("runRevision").GetInt64() == 20 &&
                      page.GetProperty("totalBytes").GetInt32() == Encoding.UTF8.GetByteCount(source),
                    "receipt page preserves identity, original outcome and exact UTF-8 payload: " + state);
                check(receipt.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0 &&
                      receiptHeader.RootElement.GetProperty("instruction").GetString()!.Contains("never reruns", StringComparison.Ordinal),
                    "successful reference lookup cannot manufacture tool success or rerun an uncertain effect: " + state);
            }
        }

        var wrongSnapshot = new Fixture();
        wrongSnapshot.Model(new { action = "history", eventId = "foreign", offset = 0 });
        wrongSnapshot.HistoryPage(source, snapshot: 101);
        check(wrongSnapshot.Step.Next is CompleteWorkflow { Success: false },
            "readable rendering still rejects a foreign accepted snapshot before exposing the page");
        var wrongReceipt = new Fixture();
        wrongReceipt.Model(new { action = "receipt", effectId = "retained-effect", offset = 0 });
        wrongReceipt.ReceiptPage(source, WorkflowEffectState.Succeeded, runId: "foreign-run");
        check(wrongReceipt.Step.Next is CompleteWorkflow { Success: false },
            "readable receipt rendering still rejects foreign-run evidence");
        var oversized = new Fixture();
        oversized.Model(new { action = "history", eventId = "oversized", offset = 0 });
        oversized.HistoryPage(new string('x', 4097));
        check(oversized.Step.Next is CompleteWorkflow { Success: false },
            "separate text rendering cannot bypass the fixed reference page byte limit");
    }
}
