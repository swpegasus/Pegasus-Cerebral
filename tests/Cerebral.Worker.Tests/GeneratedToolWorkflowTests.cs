using System.Text.Json;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

internal static class GeneratedToolWorkflowTests
{
    public static void Run()
    {
        var assertions = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            assertions++;
            Console.WriteLine("PASS: " + name);
        }

        var sinkArguments = CerebralJson.Element(new
        {
            bookId = "book-fixture",
            chapterId = 1,
            sceneId = 2,
            operationId = "fixture-generated-passage"
        });
        var request = new ToolGenerationRequest(
            "Write one bounded prose passage and return prose only.",
            "book.chapter.append",
            sinkArguments,
            MaxTokens: 768,
            CompleteAssignment: false,
            MaxAttempts: 3);

        GeneratedToolWorkflow.Validate(request);
        const string payload = "Mara crossed the failing gantry while the station counted down in a voice that sounded far too pleased with itself.";
        var dispatched = GeneratedToolWorkflow.WithContent(request, payload);
        Check(JsonElement.DeepEquals(dispatched, ToolGenerationPlan.WithContent(request, payload)) &&
            GeneratedToolWorkflow.AttemptTokenLimit(request, 1) == ToolGenerationPlan.AttemptTokenLimit(request, 1),
            "legacy wrapper delegates generation shaping and budgets to the same public Core utility");
        Check(dispatched.GetProperty("content").GetString() == payload &&
              dispatched.GetProperty("operationId").GetString() == "fixture-generated-passage",
            "raw generation is inserted only at the validated sink boundary");

        var activity = GeneratedToolWorkflow.ActivityArguments(request, dispatched, payload.Length);
        Check(activity.GetProperty("content").GetString()!.Contains("generated payload omitted", StringComparison.Ordinal) &&
              !activity.GetRawText().Contains(payload, StringComparison.Ordinal),
            "generated manuscript text is omitted from activity and active conversation context");

        var evidence = ExecutionReliability.FromTool(request.SinkToolName, dispatched,
            new ToolResult(true, "{\"chapterWords\":180,\"sceneWords\":180}"), 1);
        Check(!evidence.Command!.Contains(payload, StringComparison.Ordinal) &&
              !evidence.Summary!.Contains(payload, StringComparison.Ordinal),
            "durable execution evidence retains a fingerprint and receipt without retaining generated prose");

        var prepopulatedRejected = false;
        try
        {
            GeneratedToolWorkflow.Validate(request with
            {
                SinkArguments = CerebralJson.Element(new { operationId = "bad", content = "already present" })
            });
        }
        catch (InvalidDataException) { prepopulatedRejected = true; }
        Check(prepopulatedRejected, "a plugin cannot smuggle pre-populated content through the raw generation contract");

        var budgetRejected = false;
        try { GeneratedToolWorkflow.Validate(request with { MaxTokens = 63 }); }
        catch (InvalidDataException) { budgetRejected = true; }
        Check(budgetRejected, "generated payload token budgets remain bounded at the service boundary");

        var attemptsRejected = false;
        try { GeneratedToolWorkflow.Validate(request with { MaxAttempts = 7 }); }
        catch (InvalidDataException) { attemptsRejected = true; }
        Check(attemptsRejected, "generated payload retries remain strictly bounded at the service boundary");

        var ceilingRequest = request with
        {
            SinkArguments = CerebralJson.Element(new
            {
                bookId = "book-fixture", chapterId = 1, passageId = 3, operationId = "fixture-revision",
                minimumWords = 293, maximumWords = 600
            }),
            MaxTokens = 1350
        };
        Check(GeneratedToolWorkflow.AttemptTokenLimit(ceilingRequest, 1) == 1350 &&
              GeneratedToolWorkflow.AttemptTokenLimit(ceilingRequest, 2) == 930 &&
              GeneratedToolWorkflow.AttemptTokenLimit(ceilingRequest, 3) == 750,
            "a generated sink word ceiling leaves the first completion intact and physically bounds retries to their smaller requested word range");
        var correction = GeneratedToolWorkflow.RetryGuidance(ceilingRequest,
            "The generated chapter revision contains 805 words and exceeds its validated 600-word ceiling.");
        Check(correction.Contains("fresh complete replacement", StringComparison.Ordinal) &&
               correction.Contains("hard ceiling of 600 words", StringComparison.Ordinal) &&
               correction.Contains("at least 293 words", StringComparison.Ordinal) &&
              correction.Contains("write about 390 words", StringComparison.Ordinal) &&
              correction.Contains("stop no later than 465 words", StringComparison.Ordinal) &&
              correction.Contains("do not repeat or lightly edit", StringComparison.Ordinal),
            "a rejected generated payload receives explicit fresh-redraft and hard-ceiling guidance");
        var finalCorrection = GeneratedToolWorkflow.RetryGuidance(ceilingRequest,
            "The replacement reached its response token limit.", attempt: 3);
        Check(finalCorrection.Contains("write about 300 words", StringComparison.Ordinal) &&
              finalCorrection.Contains("stop no later than 375 words", StringComparison.Ordinal),
            "a later retry receives a smaller concrete completion target instead of relying on another vague shortening request");

        Console.WriteLine($"Generated tool workflow: {assertions} deterministic checks passed; no model or filesystem mutation.");
    }
}
