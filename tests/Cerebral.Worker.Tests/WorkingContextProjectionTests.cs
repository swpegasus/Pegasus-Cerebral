using System.Text;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class WorkingContextProjectionTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        var body = new string('x', 80_000) + "COMPLETE_RECORD_TAIL";
        var result = "Tool mcp.cortex.cortex_read; success=True; truncated=False\n" +
            CerebralJson.Element(new { isError = false, structuredContent = new { record = new { content = body } } }).GetRawText();
        var message = new SessionMessage { Sequence = 12, Role = "user", Origin = "tool", ToolName = "mcp.cortex.cortex_read", Content = result };
        var session = new TaskSession { Messages = [
            new() { Sequence = 11, Role = "user", Origin = "user", Content = "Use the relevant record." }, message ] };
        var projection = WorkingContext.Assemble(session, "instructions").Last().Content;
        Check(Encoding.UTF8.GetByteCount(projection) <= WorkingContext.MaxToolContextBytes &&
            projection.Contains("source message 12", StringComparison.Ordinal) && projection.Contains(message.ToolName, StringComparison.Ordinal),
            "large Cortex result gets a bounded model projection with exact source provenance");
        Check(projection.Contains("incomplete", StringComparison.Ordinal) && projection.Contains("remaining saved text omitted", StringComparison.Ordinal) &&
            !projection.Contains("COMPLETE_RECORD_TAIL", StringComparison.Ordinal),
            "model excerpt explicitly distinguishes partial context from the complete stored read");
        Check(message.Content == result && session.Messages[1].Content.Contains("COMPLETE_RECORD_TAIL", StringComparison.Ordinal),
            "context projection preserves the complete saved tool response for UI inspection");
        var compaction = WorkingContext.WithProvenance(message);
        Check(Encoding.UTF8.GetByteCount(compaction) <= WorkingContext.MaxToolContextBytes + 512 &&
            compaction.Contains("Model-context excerpt", StringComparison.Ordinal),
            "compactor input uses the same bounded tool projection");

        message.Content = string.Concat(Enumerable.Repeat("\U0001F600", 12000));
        projection = WorkingContext.Assemble(session, "instructions").Last().Content;
        var validUnicode = true;
        try { new UTF8Encoding(false, true).GetBytes(projection); } catch (EncoderFallbackException) { validUnicode = false; }
        Check(validUnicode && Encoding.UTF8.GetByteCount(projection) <= WorkingContext.MaxToolContextBytes,
            "multibyte tool output is bounded without splitting Unicode characters");
        message.Content = "Tool file.read; success=False; truncated=False\nFile not found.";
        Check(WorkingContext.Assemble(session, "instructions").Last().Content == message.Content,
            "ordinary small tool results retain their complete original text");
        message.ExcludedFromContext = true;
        Check(WorkingContext.Assemble(session, "instructions").Count == 2,
            "excluded tool records cannot reappear through context projection");
        message.ExcludedFromContext = false; message.ToolName = null; message.Origin = "user"; message.Content = new string('u', 10000);
        Check(WorkingContext.Assemble(session, "instructions").Last().Content == message.Content,
            "real user input remains verbatim under existing profile token-budget handling");
        foreach (var origin in new[] { "helper", "service" })
        {
            message.Origin = origin; message.Content = string.Concat(Enumerable.Repeat("\u03bb\U0001F600", 6000)) + "SAVED_RECORD_TAIL";
            var saved = message.Content;
            projection = WorkingContext.Assemble(session, "instructions").Last().Content;
            Check(Encoding.UTF8.GetByteCount(projection) <= WorkingContext.MaxToolContextBytes &&
                projection.Contains("origin=" + origin, StringComparison.Ordinal) &&
                projection.Contains("source message 12", StringComparison.Ordinal) &&
                projection.Contains(origin == "helper" ? "saved helper result" : "saved service record", StringComparison.Ordinal) &&
                !projection.Contains("SAVED_RECORD_TAIL", StringComparison.Ordinal),
                "oversized " + origin + " record without a tool name gets a truthful bounded projection");
            Check(message.Content == saved && WorkingContext.WithProvenance(message).EndsWith(projection, StringComparison.Ordinal),
                origin + " projection preserves complete storage and is reused for compaction");
            Check(new UTF8Encoding(false, true).GetByteCount(projection) <= WorkingContext.MaxToolContextBytes,
                origin + " projection preserves Unicode scalar boundaries");
            message.Content = "Short " + origin + " record.";
            Check(WorkingContext.Assemble(session, "instructions").Last().Content == message.Content,
                "small " + origin + " record remains complete");
        }
        message.Role = "assistant"; message.Origin = "assistant"; message.Content = new string('a', 10000);
        Check(WorkingContext.Assemble(session, "instructions").Last().Content == message.Content,
            "ordinary assistant content remains unchanged by tool and service projection");
        Console.WriteLine($"Working context projection: {count} deterministic checks passed; no model or provider invoked.");
    }
}
