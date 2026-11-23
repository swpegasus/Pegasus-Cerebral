using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic receipt presentation fixtures; no provider dispatch or live inference.</summary>
internal static class ToolModelProjectionFailureTests
{
    public static Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException("Tool model failure projection: " + reason);
            checks++;
        }
        const string generic = "The tool returned failed. Inspect its retained result and evidence.";
        const string job = "confirmed-job";
        const string request = "confirmed-request";
        const string remedy = "Refresh the target identity and operation preconditions; do not force or blindly retry the previous request.";
        var evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Failed, "mcp.cortex.pao", request, job, "failed");
        JsonObject Receipt() => new()
        {
            ["ok"] = false, ["jid"] = job, ["rid"] = request, ["state"] = "failed", ["op"] = "replace",
            ["err"] = "precondition", ["remedy"] = remedy, ["partial"] = false,
            ["data"] = new JsonObject { ["dst"] = @"C:\private\source.cs", ["text"] = "unreturned-source" }
        };
        string Body(JsonObject receipt, bool wrap = true) => wrap
            ? new JsonObject { ["error"] = "pao_transport_error", ["receipt"] = receipt }.ToJsonString()
            : receipt.ToJsonString();
        ToolModelStatus Project(string content, ToolExecutionEvidence? proof = null, string? error = generic,
            bool truncated = false) => ToolModelProjection.Create("effect", "file.edit", "Failed", content,
                truncated, proof ?? evidence, error);
        var oldBody = Body(Receipt());
        var legacy = Project(oldBody);
        Check(legacy.Message == "precondition\nRemedy: " + remedy && legacy.Status == "failed" && legacy.CommandsFailed == 1,
            "the actual old transport wrapper exposes the confirmed code and returned remedy");
        Check(legacy.Message is not null && !legacy.Message.Contains("not_dispatched", StringComparison.Ordinal) &&
              !legacy.Message.Contains("no target bytes", StringComparison.Ordinal) &&
              !legacy.Message.Contains("preimage_changed", StringComparison.Ordinal),
            "a broad legacy precondition is never expanded into a publication or specific guard claim");
        Check(!ToolModelProjection.Render(legacy).Contains("unreturned-source", StringComparison.Ordinal) &&
              !legacy.Message!.Contains(job, StringComparison.Ordinal) && !legacy.Message.Contains(@"C:\private", StringComparison.Ordinal),
            "ordinary failure status excludes request identities, source bodies and operation data");
        Check(Project(Body(Receipt(), wrap: false)).Message == legacy.Message,
            "the same exact failed receipt is readable without the transport wrapper");
        var nullFailure = Receipt(); nullFailure["failure"] = null;
        Check(Project(Body(nullFailure)).Message == legacy.Message, "an absent nullable failure preserves legacy diagnostics");

        const string guardMessage = "The exact old literal was not found; no target bytes were written.";
        JsonObject Failure() => new()
        {
            ["message"] = guardMessage,
            ["diagnostics"] = new JsonArray(new JsonObject { ["code"] = "literal_not_found", ["message"] = guardMessage }),
            ["execution"] = new JsonObject { ["value"] = "not_dispatched", ["source"] = "text-publication-guard" },
            ["retry"] = new JsonObject { ["value"] = "after_correction", ["source"] = "text-publication-guard" },
            ["next"] = "Read the exact target once, correct the indicated precondition, and retry only if still applicable."
        };
        var modernReceipt = Receipt(); modernReceipt["failure"] = Failure();
        var modern = Project(Body(modernReceipt));
        Check(modern.Message?.Contains("literal_not_found: " + guardMessage, StringComparison.Ordinal) == true &&
              modern.Message.Contains("Execution: not_dispatched (text-publication-guard)", StringComparison.Ordinal) &&
              modern.Message.Contains("Retry: after_correction (text-publication-guard)", StringComparison.Ordinal) &&
              modern.Message.Contains("Next: Read the exact target once", StringComparison.Ordinal),
            "richer receipts expose only the exact returned guard, execution and retry facts");
        Check(modern.Message!.Split(guardMessage, StringSplitOptions.None).Length == 2 &&
              !modern.Message.Contains("Remedy:", StringComparison.Ordinal),
            "duplicate failure messages are suppressed and a returned next step supersedes the broad remedy");

        foreach (var outcome in new[] { ToolExecutionOutcome.Unknown, ToolExecutionOutcome.Succeeded, ToolExecutionOutcome.Cancelled })
            Check(Project(Body(modernReceipt.DeepClone().AsObject()), evidence with { Outcome = outcome }).Message == generic,
                "unconfirmed or contradictory host evidence cannot establish a structured failure cause");
        foreach (var field in new[] { "jid", "rid", "state" })
        {
            var foreign = modernReceipt.DeepClone().AsObject(); foreign[field] = "foreign";
            Check(Project(Body(foreign)).Message == generic, "foreign " + field + " does not match the retained host evidence");
        }
        var missingId = modernReceipt.DeepClone().AsObject(); missingId.Remove("jid");
        Check(Project(Body(missingId)).Message == generic, "a missing provider receipt identity cannot supply a structured cause");
        var successful = modernReceipt.DeepClone().AsObject(); successful["ok"] = true;
        Check(Project(Body(successful)).Message == generic, "a successful receipt cannot masquerade as a confirmed failed receipt");
        Check(Project(Body(modernReceipt.DeepClone().AsObject()), evidence with { RequestId = null }).Message == generic,
            "missing retained request identity prevents receipt attribution");
        Check(Project("{\"error\":\"foreign_wrapper\",\"receipt\":" + modernReceipt.ToJsonString() + "}").Message == generic,
            "arbitrary nested receipts are not treated as the ToolDispatcher wrapper");
        var duplicate = Body(modernReceipt.DeepClone().AsObject()).Replace("\"ok\":false", "\"ok\":false,\"ok\":false", StringComparison.Ordinal);
        Check(Project(duplicate).Message == generic, "duplicate receipt keys cannot establish a structured diagnostic");
        var duplicateFailure = Body(modernReceipt.DeepClone().AsObject()).Replace("\"failure\":{", "\"failure\":{\"message\":\"conflicting message\",", StringComparison.Ordinal);
        Check(Project(duplicateFailure).Message == generic, "duplicate structured failure keys cannot establish a diagnostic");
        var invalidFailure = modernReceipt.DeepClone().AsObject(); invalidFailure["failure"] = "foreign shape";
        Check(Project(Body(invalidFailure)).Message == generic, "a malformed typed failure does not establish a diagnostic");
        var invalidDiagnostics = modernReceipt.DeepClone().AsObject(); invalidDiagnostics["failure"]!["diagnostics"] = "foreign shape";
        Check(Project(Body(invalidDiagnostics)).Message == generic, "a malformed diagnostics collection does not establish a diagnostic");
        var denied = Receipt(); denied["state"] = "denied";
        Check(Project(Body(denied), evidence with { State = "denied", Outcome = ToolExecutionOutcome.Rejected }).Status == "rejected",
            "existing confirmed denied receipts keep the rejection outcome");
        Check(Project("{\"error\":\"pao_transport_error\",\"receipt\":").Message == generic,
            "incomplete receipt JSON cannot establish a structured diagnostic");

        var invalidFacts = modernReceipt.DeepClone().AsObject();
        invalidFacts["failure"]!["execution"]!["value"] = "invented_execution";
        invalidFacts["failure"]!["retry"]!["source"] = "";
        var invalidFactMessage = Project(Body(invalidFacts)).Message!;
        Check(!invalidFactMessage.Contains("Execution:", StringComparison.Ordinal) &&
              !invalidFactMessage.Contains("Retry:", StringComparison.Ordinal),
            "execution and retry facts require both a closed value and an actual source stamp");
        var unknownCode = modernReceipt.DeepClone().AsObject();
        unknownCode["failure"]!["diagnostics"]![0]!["code"] = "arbitrary-script-or-code";
        var unknownCodeMessage = Project(Body(unknownCode)).Message!;
        Check(unknownCodeMessage.Contains(guardMessage, StringComparison.Ordinal) &&
              !unknownCodeMessage.Contains("arbitrary-script-or-code", StringComparison.Ordinal),
            "actual provider message is retained without admitting an arbitrary diagnostic code");
        var partial = modernReceipt.DeepClone().AsObject(); partial["partial"] = true;
        Check(Project(Body(partial), evidence with { Partial = true }).Partial &&
              Project(Body(modernReceipt.DeepClone().AsObject()), truncated: true).Partial,
            "failure presentation preserves partial and truncation flags without changing outcomes");
        var rejected = Project(Body(modernReceipt.DeepClone().AsObject()), evidence with { Outcome = ToolExecutionOutcome.Rejected });
        Check(rejected.Status == "rejected" && rejected.CommandsFailed == 1 && rejected.Message == modern.Message,
            "confirmed admission rejection preserves its existing status and factual diagnostic");

        const string compiler = "Panel.cs(7,4): error CS0111: A member is already defined.";
        var compiled = modernReceipt.DeepClone().AsObject(); compiled["data"]!["err_tail"] = compiler;
        Check(Project(Body(compiled)).Message == compiler,
            "actual compiler diagnostics keep precedence over general structured receipt facts");
        const string separatelyPreserved = "Panel.cs(8,1): error CS0103: The name does not exist.";
        Check(Project(Body(modernReceipt.DeepClone().AsObject()), error: separatelyPreserved).Message == separatelyPreserved,
            "a specific separately retained bridge diagnostic is not replaced by general provider metadata");

        var explicitFailure = """{"error":"precondition","message":"The requested timeout exceeds the registered bound."}""";
        Check(Project(explicitFailure, evidence with { Outcome = ToolExecutionOutcome.Rejected }).Message ==
              "precondition: The requested timeout exceeds the registered bound.",
            "a confirmed top-level typed rejection returns its exact bounded message");
        Check(Project("""{"error":"unknown_foreign_code","message":"Unconfirmed diagnosis."}""").Message == generic,
            "unrecognized top-level error codes are not granted a structured diagnostic projection");

        var longReceipt = Receipt(); var longFailure = Failure();
        longFailure["message"] = new string('é', 4096);
        longFailure["diagnostics"] = new JsonArray(Enumerable.Range(0, 12).Select(index => (JsonNode)new JsonObject
        { ["code"] = "preimage_changed", ["message"] = index + " " + string.Concat(Enumerable.Repeat("😀", 1024)) }).ToArray());
        longReceipt["failure"] = longFailure;
        var bounded = Project(Body(longReceipt)).Message!;
        Check(Encoding.UTF8.GetByteCount(bounded) <= 2048 && new UTF8Encoding(false, true).GetByteCount(bounded) <= 2048,
            "structured failure text is bounded in UTF-8 bytes without splitting Unicode scalars");
        Check(!bounded.Contains("4 ", StringComparison.Ordinal), "structured extraction reads at most four diagnostic entries");
        var oversized = Receipt(); oversized["data"]!["text"] = new string('z', 256 * 1024);
        Check(Project(Body(oversized)).Message == generic, "oversized structured receipts retain the ordinary detail lookup instead of expanding context");
        Check(ToolModelProjection.IncludesPayload("mcp.cortex.pao") &&
              ToolModelProjection.ReadablePayload("mcp.cortex.pao", oldBody) == oldBody,
            "direct PAO payload contract remains unchanged");

        Console.WriteLine("Tool model failure projection: " + checks + " deterministic checks passed.");
        return Task.CompletedTask;
    }
}
