using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    // Deterministic host-effect fixtures exercise the public harness, not a live provider or model.
    private static void RunUtf8ProjectionChecks(Action<bool, string> check)
    {
        const string payloadSeparator = "\nPayload:\n";
        const string pageSeparator = "\nExact retained JSON page (reference data; may be a fragment):\n";
        const string partialMarker = "[Explicit partial excerpt; exact content remains in the indicated durable receipt.]\n";
        var strictUtf8 = new UTF8Encoding(false, true);
        static (JsonDocument Header, string Body) Split(string text, string separator)
        {
            var at = text.IndexOf(separator, StringComparison.Ordinal);
            if (at < 0) throw new InvalidOperationException("UTF-8 projection fixture has no payload separator.");
            return (JsonDocument.Parse(text[..at]), text[(at + separator.Length)..]);
        }
        static Fixture Dispatch(string content, bool producerTruncated = false)
        {
            var fixture = new Fixture(warm: false);
            fixture.History([]);
            fixture.Catalog(forcedNames: ["fixture.echo"]);
            fixture.Select("fixture.echo", CerebralJson.Element(new
            {
                type = "object", properties = new { }, additionalProperties = false
            }));
            fixture.Model(new { action = "tool", name = "fixture.echo", arguments = new { } });
            fixture.Tool(true, content, truncated: producerTruncated, provider: "fixture.echo");
            return fixture;
        }

        // This generic JSON has the observed regression's raw byte size, with enough quotes
        // that JSON-string serialization crosses the projection limit before its final field.
        var prefix = "{" + string.Join(",", Enumerable.Range(0, 120).Select(i => $"\"k{i:D3}\":\"v{i:D3}\"")) + ",\"padding\":\"";
        const string suffix = "\",\"recent\":{\"lastValue\":123.5}}";
        var quoteHeavy = prefix + new string('p', 6074 - Encoding.UTF8.GetByteCount(prefix + suffix)) + suffix;
        check(Encoding.UTF8.GetByteCount(quoteHeavy) == 6074 &&
              JsonSerializer.SerializeToUtf8Bytes(quoteHeavy, CerebralJson.Options).Length > 8192,
            "generic 6074-byte JSON reproduces unnecessary escaped-string overflow");
        var fullPayloads = new[]
        {
            quoteHeavy,
            new string('\\', 4096) + new string('"', 1978),
            new string('a', 8192),
            string.Concat(Enumerable.Repeat("\u754C", 2730)) + "aa",
            string.Concat(Enumerable.Repeat("\U0001F600", 2048)),
            "line one\r\n\"quoted\" C:\\sample\\r\u00E9sum\u00E9.cs literal \\u0022 and \U0001F600"
        };
        foreach (var payload in fullPayloads)
        {
            var fixture = Dispatch(payload);
            check(fixture.Is("generate"), "under-bound generic payload reaches generation");
            var source = fixture.Context.Contributions.Single(item => item.Category == "tool-result");
            var (header, body) = Split(fixture.LastContributionText, payloadSeparator);
            using (header)
            {
                check(body == payload && source.Complete && !header.RootElement.GetProperty("contentTruncated").GetBoolean(),
                    "readable UTF-8 payload is byte-exact and complete through the inclusive 8192-byte bound");
                check(source.Requirement == ContextRequirement.Required && source.ExactReadHandle is { Length: > 0 } &&
                      source.Id == "tool." + source.ExactReadHandle &&
                      header.RootElement.GetProperty("receipt").GetString() == source.ExactReadHandle &&
                      header.RootElement.GetProperty("tool").GetString() == "fixture.echo" &&
                      fixture.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
                    "full generic projections retain required provenance and one actual tool receipt");
                check(JsonSerializer.SerializeToUtf8Bytes(fixture.Step.State, CerebralJson.Options).Length <= WorkflowContract.MaximumStateBytes &&
                      JsonSerializer.SerializeToUtf8Bytes(fixture.Command.Arguments, CerebralJson.Options).Length <= WorkflowContract.MaximumStateBytes,
                    "readable byte accounting preserves separate serialized state and generation-envelope bounds");
            }
        }

        var oversizedPayloads = new[]
        {
            new string('a', 8193),
            string.Concat(Enumerable.Repeat("\u754C", 2731)),
            string.Concat(Enumerable.Repeat("\U0001F600", 2049)),
            new string('a', 7933) + "\U0001F600" + new string('z', 400),
            new string('"', 8193)
        };
        foreach (var payload in oversizedPayloads)
        {
            var fixture = Dispatch(payload);
            check(fixture.Is("generate"), "over-bound generic content reaches generation as explicit partial evidence");
            var source = fixture.Context.Contributions.Single(item => item.Category == "tool-result");
            var (header, body) = Split(fixture.LastContributionText, payloadSeparator);
            using (header)
            {
                check(!source.Complete && header.RootElement.GetProperty("contentTruncated").GetBoolean() &&
                      source.ExactReadHandle == header.RootElement.GetProperty("receipt").GetString(),
                    "real clipping remains incomplete and retains the exact durable receipt");
                check(body.StartsWith(partialMarker, StringComparison.Ordinal) && Encoding.UTF8.GetByteCount(body) <= 8192 &&
                      payload.StartsWith(body[partialMarker.Length..], StringComparison.Ordinal) &&
                      strictUtf8.GetString(strictUtf8.GetBytes(body)) == body && !char.IsHighSurrogate(body[^1]),
                    "clipped text keeps an exact UTF-8 prefix and whole supplementary characters within the existing bound");
            }
        }

        var partial = Dispatch(quoteHeavy, producerTruncated: true);
        var originalPartial = partial.Context.Contributions.Single(item => item.Category == "tool-result");
        var (partialHeader, partialBody) = Split(partial.LastContributionText, payloadSeparator);
        using (partialHeader)
            check(partialBody == quoteHeavy && !originalPartial.Complete &&
                  partialHeader.RootElement.GetProperty("contentTruncated").GetBoolean(),
                "an uncut readable body never upgrades a producer-truncated result to complete evidence");
        var resumed = partial.Fork();
        using var savedPartial = JsonDocument.Parse(partial.Step.State.GetRawText());
        resumed.RestoreState(savedPartial.RootElement.Clone());
        resumed.Model(new { action = "receipt", effectId = originalPartial.ExactReadHandle, offset = 0 });
        resumed.ReceiptPage("retained partial result", WorkflowEffectState.Succeeded);
        check(!resumed.Context.Contributions.Single(item => item.Id == originalPartial.Id).Complete &&
              resumed.Step.State.GetProperty("toolReceipts")[0].GetProperty("truncated").GetBoolean(),
            "saved incomplete provenance stays incomplete after restoration and an exact reference lookup");

        // Recover a genuinely clipped generic result through ordinary retained pages. The
        // serialized receipt is ASCII, so each host-sized character slice has exact byte offsets.
        var recoverable = new string('x', 2 * WorkflowReceiptPage.MaximumPageBytes + 1234) + "\r\n\"retained tail\" C:\\sample\\r\u00E9sum\u00E9.cs \U0001F600";
        var recovered = Dispatch(recoverable);
        var toolSource = recovered.Context.Contributions.Single(item => item.Category == "tool-result");
        var originalLedger = recovered.Step.State.GetProperty("toolReceipts").GetRawText();
        var receiptJson = JsonSerializer.Serialize(new { name = "fixture.echo", content = recoverable, truncated = false }, CerebralJson.Options);
        var totalBytes = Encoding.UTF8.GetByteCount(receiptJson);
        check(totalBytes == receiptJson.Length && totalBytes > 2 * WorkflowReceiptPage.MaximumPageBytes && totalBytes < 3 * WorkflowReceiptPage.MaximumPageBytes,
            "paged fixture uses bounded exact ASCII receipt coordinates for Unicode source content");
        var rebuilt = new StringBuilder();
        for (var offset = 0; offset < totalBytes;)
        {
            recovered.Model(new { action = "receipt", effectId = toolSource.ExactReadHandle, offset });
            check(recovered.Is("receipt") && recovered.Command.Arguments.GetProperty("limit").GetInt32() == WorkflowReceiptPage.MaximumPageBytes &&
                  !recovered.Command.Arguments.TryGetProperty("expectedRevision", out _),
                "confirmed tool recovery uses the existing bounded receipt action without replay");
            var pageText = receiptJson.Substring(offset, Math.Min(recovered.Command.Arguments.GetProperty("limit").GetInt32(), totalBytes - offset));
            var end = offset + pageText.Length;
            int? next = end < totalBytes ? end : null;
            recovered.ReceiptPage(pageText, WorkflowEffectState.Succeeded, nextOffset: next, totalBytes: totalBytes);
            var (header, body) = Split(recovered.LastContributionText, pageSeparator);
            using (header)
            {
                var page = header.RootElement.GetProperty("page");
                check(body == pageText && page.GetProperty("offset").GetInt32() == offset &&
                      page.GetProperty("totalBytes").GetInt32() == totalBytes &&
                      page.GetProperty("effectId").GetString() == toolSource.ExactReadHandle &&
                      page.GetProperty("runId").GetString() == "conversation-run" &&
                      (next is null ? page.GetProperty("nextOffset").ValueKind == JsonValueKind.Null :
                          page.GetProperty("nextOffset").GetInt32() == next) &&
                      !recovered.Context.Contributions.Last().Complete,
                    "exact receipt fragments preserve original identity, offsets, continuation and honest per-page completeness");
            }
            rebuilt.Append(body);
            offset = end;
        }
        using (var completeReceipt = JsonDocument.Parse(rebuilt.ToString()))
            check(rebuilt.ToString() == receiptJson && completeReceipt.RootElement.GetProperty("content").GetString() == recoverable &&
                  !recovered.Context.Contributions.Single(item => item.Id == toolSource.Id).Complete &&
                  recovered.Step.State.GetProperty("toolReceipts").GetRawText() == originalLedger,
                "receipt pages recover the omitted tail exactly without changing original completeness or manufacturing tool effects");

        var overflow = Dispatch(quoteHeavy);
        var overflowLedger = overflow.Step.State.GetProperty("toolReceipts").GetRawText();
        var overflowSources = overflow.Step.State.GetProperty("contributions").GetRawText();
        var overflowRevision = overflow.Context.Revision;
        overflow.RejectContext(ContextProjectionStatus.RequiredOverflow);
        check(overflow.Is("generate") && overflow.Step.State.GetProperty("discoveryNamesOnly").GetBoolean() &&
              overflow.Context.Revision == overflowRevision + 1 &&
              overflow.Step.State.GetProperty("toolReceipts").GetRawText() == overflowLedger &&
              overflow.Step.State.GetProperty("contributions").GetRawText() == overflowSources,
            "a required-overflow diagnostic drops optional discovery descriptions once while preserving all exact input/evidence");
        overflow.RejectContext(ContextProjectionStatus.RequiredOverflow);
        check(overflow.Step.Next is CompleteWorkflow { Success: false } &&
              overflow.Step.State.GetProperty("toolReceipts").GetRawText() == overflowLedger &&
              overflow.Step.State.GetProperty("contributions").GetRawText() == overflowSources,
            "persistent required overflow stops after the one optional-discovery fallback without replaying a semantic tool");
    }
}
