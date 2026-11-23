using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    internal static void VerifyOperationEvidence(Action<bool, string> check)
    {
        const string marker = "\nOperation evidence:\n";
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralOperationEvidence"));
        var source = Path.Combine(directory, "Source.dll");
        var destination = Path.Combine(directory, "Copied.dll");
        var hash = new string('a', 64);
        var pathSchema = CerebralJson.Element(new
        {
            type = "object", properties = new { path = new { type = "string" } },
            required = new[] { "path" }, additionalProperties = false
        });
        var copySchema = CerebralJson.Element(new
        {
            type = "object", properties = new { sourcePath = new { type = "string" }, destinationPath = new { type = "string" } },
            required = new[] { "sourcePath", "destinationPath" }, additionalProperties = false
        });
        var processSchema = CerebralJson.Element(new
        {
            type = "object", properties = new
            {
                executableId = new { type = "string" }, arguments = new { type = "array", items = new { type = "string" } },
                workingDirectory = new { type = "string" }
            },
            required = new[] { "executableId", "arguments", "workingDirectory" }, additionalProperties = false
        });

        Fixture Pending(string name, object arguments)
        {
            var fixture = new Fixture(history: [Past("operation-history", 10, "Earlier completed work.")]);
            fixture.Select(name, name == "file.mkdir" ? pathSchema : name == "file.copy" ? copySchema : processSchema);
            fixture.Model(new { action = "tool", name, arguments });
            check(fixture.Is("tool"), "operation evidence dispatches the selected actual tool");
            return fixture;
        }

        JsonElement Evidence(Fixture fixture)
        {
            check(fixture.Is("generate"), "the operation returns to ordinary model generation");
            var text = fixture.Context.Contributions.SelectMany(item => item.Messages)
                .SelectMany(message => message.Content).OfType<TextContentPart>()
                .Last(part => part.Text.Contains(marker, StringComparison.Ordinal)).Text;
            var offset = text.IndexOf(marker, StringComparison.Ordinal);
            check(offset >= 0 && Encoding.UTF8.GetByteCount(text[offset..]) <= 2048,
                "the whole escaped operation-evidence block fits its aggregate byte cap");
            using var document = JsonDocument.Parse(text[(offset + marker.Length)..]);
            return document.RootElement.Clone();
        }

        string CopyResult(string from, string to, long bytes = 12, string? sha = null) =>
            JsonSerializer.Serialize(new { sourcePath = from, destinationPath = to, bytes, sha256 = sha ?? hash }, CerebralJson.Options);

        var mkdir = Pending("file.mkdir", new { path = directory });
        mkdir.Tool(true, JsonSerializer.Serialize(new { path = directory, ensured = true, existedBefore = true }), omitEvidence: true);
        var mkdirEvidence = Evidence(mkdir);
        check(mkdirEvidence.GetProperty("requestedCoordinates").GetProperty("path").GetString() == directory &&
              mkdirEvidence.GetProperty("confirmedResult").GetProperty("existedBefore").GetBoolean() &&
              mkdirEvidence.GetProperty("receipt").GetString() ==
                  mkdir.Step.State.GetProperty("toolReceipts")[0].GetProperty("effectId").GetString(),
            "a proved no-op directory ensure retains exact request, observation and effect identity");
        check(mkdir.Context.Contributions.All(item => item.Category != "model-output") &&
              mkdir.Step.State.GetProperty("pendingArguments").ValueKind == JsonValueKind.Null,
            "directory continuity does not replay the raw action or keep pending arguments");

        var newlyEnsured = Pending("file.mkdir", new { path = directory });
        newlyEnsured.Tool(true, JsonSerializer.Serialize(new { path = directory, ensured = true, existedBefore = false }));
        var ensureEvidence = Evidence(newlyEnsured);
        check(!ensureEvidence.GetProperty("confirmedResult").GetProperty("existedBefore").GetBoolean() &&
              !ensureEvidence.GetProperty("confirmedResult").TryGetProperty("created", out _),
            "a directory's prior absence is an observation, not an invented exclusive-creation claim");

        var copy = Pending("file.copy", new { sourcePath = source, destinationPath = destination });
        copy.Tool(true, CopyResult(source, destination));
        var copyEvidence = Evidence(copy);
        check(copyEvidence.GetProperty("requestedCoordinates").GetProperty("sourcePath").GetString() == source &&
              copyEvidence.GetProperty("requestedCoordinates").GetProperty("destinationPath").GetString() == destination &&
              copyEvidence.GetProperty("confirmedResult").GetProperty("bytes").GetInt64() == 12 &&
              copyEvidence.GetProperty("confirmedResult").GetProperty("sha256").GetString() == hash,
            "a complete matching copy retains both coordinates and validated file identity");

        copy.RejectContext(ContextProjectionStatus.NeedsCompaction);
        check(copy.Is("compact"), "older source material permits compaction after the copy");
        copy.CompactSuccess();
        var saved = JsonDocument.Parse(copy.Step.State.GetRawText()).RootElement.Clone();
        copy.RestoreState(saved);
        copy.Describe("file.read");
        copy.Catalog(exact: true, inputSchema: pathSchema);
        var recovered = Evidence(copy);
        check(JsonElement.DeepEquals(recovered, copyEvidence),
            "compaction plus serialized recovery preserves the exact latest copy evidence");
        check(copy.Context.Contributions.SelectMany(item => item.Messages).SelectMany(message => message.Content)
                  .OfType<TextContentPart>().Any(part =>
                      part.Text.Contains(JsonSerializer.Serialize(destination, CerebralJson.Options), StringComparison.Ordinal)) &&
              copy.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1,
            "the next model receives the exact destination without another tool effect");

        const string secretArgument = "NeverEchoPrivateProcessArgument";
        const string output = "NeverEchoWholeProcessOutput";
        var process = Pending("process.run", new
        {
            executableId = "dotnet", arguments = new[] { "build", secretArgument }, workingDirectory = directory
        });
        process.Tool(true, JsonSerializer.Serialize(new { exitCode = 0, stdout = output, stderr = "" }), exitCode: 0);
        var processEvidence = Evidence(process);
        check(processEvidence.GetProperty("requestedCoordinates").GetProperty("executableId").GetString() == "dotnet" &&
              processEvidence.GetProperty("requestedCoordinates").GetProperty("workingDirectory").GetString() == directory &&
              processEvidence.GetProperty("argumentsOmitted").GetBoolean() && processEvidence.GetProperty("outputOmitted").GetBoolean() &&
              !processEvidence.TryGetProperty("confirmedResult", out _) &&
              !process.ModelText.Contains(secretArgument, StringComparison.Ordinal) &&
              !process.ModelText.Contains(output, StringComparison.Ordinal) &&
              !process.Step.State.GetRawText().Contains(secretArgument, StringComparison.Ordinal),
            "process continuity retains execution coordinates without argv, full output or a fabricated build target");

        var negatives = new (string Label, Action<Fixture> Complete)[]
        {
            ("failed effect", f => f.Tool(false, CopyResult(source, destination), outcome: ToolExecutionOutcome.Succeeded)),
            ("rejected call", f => f.Tool(false, CopyResult(source, destination), outcome: ToolExecutionOutcome.Rejected)),
            ("unconfirmed outcome", f => f.Tool(true, CopyResult(source, destination), outcome: ToolExecutionOutcome.Unknown)),
            ("partial evidence", f => f.Tool(true, CopyResult(source, destination), partial: true)),
            ("truncated body", f => f.Tool(true, CopyResult(source, destination), truncated: true)),
            ("malformed body", f => f.Tool(true, "{not json")),
            ("mismatched source", f => f.Tool(true, CopyResult(source + "-other", destination))),
            ("mismatched destination", f => f.Tool(true, CopyResult(source, destination + "-other"))),
            ("invalid size", f => f.Tool(true, CopyResult(source, destination, -1))),
            ("invalid hash", f => f.Tool(true, CopyResult(source, destination, sha: "invalid"))),
            ("duplicate property", f => f.Tool(true, CopyResult(source, destination).Replace("{", "{\"bytes\":12,"))),
            ("foreign PAO shape", f => f.Tool(true, JsonSerializer.Serialize(new { ok = true, data = new { source, destination } })))
        };
        var pendingCopy = Pending("file.copy", new { sourcePath = source, destinationPath = destination });
        foreach (var item in negatives)
        {
            var negative = pendingCopy.Fork();
            item.Complete(negative);
            var evidence = Evidence(negative);
            check(!evidence.TryGetProperty("confirmedResult", out _) &&
                  evidence.GetProperty("requestedCoordinates").GetProperty("destinationPath").GetString() == destination,
                item.Label + " preserves the attempt's coordinates without fabricating copy completion");
        }

        var partialMkdir = Pending("file.mkdir", new { path = directory });
        partialMkdir.Tool(true, JsonSerializer.Serialize(new { path = directory, ensured = true, existedBefore = true }), partial: true);
        check(!Evidence(partialMkdir).TryGetProperty("confirmedResult", out _),
            "partial mkdir evidence cannot become a proved no-op observation");
        var differentMkdir = Pending("file.mkdir", new { path = directory });
        differentMkdir.Tool(true, JsonSerializer.Serialize(new { path = directory + "-other", ensured = true, existedBefore = true }));
        check(!Evidence(differentMkdir).TryGetProperty("confirmedResult", out _),
            "a different directory observation cannot claim this request completed");

        var escapedDirectory = Path.Combine(directory, new string('\u754c', 500));
        var escaped = Pending("file.mkdir", new { path = escapedDirectory });
        escaped.Tool(true, JsonSerializer.Serialize(new { path = escapedDirectory, ensured = true, existedBefore = true }));
        var escapedEvidence = Evidence(escaped);
        check(!escapedEvidence.TryGetProperty("requestedCoordinates", out _) &&
              escapedEvidence.GetProperty("requestedCoordinatesOmitted").GetBoolean() &&
              escapedEvidence.GetProperty("note").GetString()!.Contains("Fetch this receipt", StringComparison.Ordinal) &&
              escapedEvidence.GetProperty("confirmedResult").GetProperty("ensured").GetBoolean(),
            "escaped oversized coordinates are omitted whole with exact-receipt guidance, never clipped");

        var oldState = pendingCopy.Step.State.Clone();
        var legacy = pendingCopy.Fork();
        legacy.RestoreState(JsonDocument.Parse(oldState.GetRawText()).RootElement.Clone());
        legacy.Tool(false, "A previous-version pending call failed.", outcome: ToolExecutionOutcome.Rejected);
        check(Evidence(legacy).GetProperty("requestedCoordinates").GetProperty("sourcePath").GetString() == source,
            "existing serialized pending state needs no new required fields to receive operation continuity");
    }
}
