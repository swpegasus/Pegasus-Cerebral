using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    internal static void VerifyMutationEvidence(Action<bool, string> check)
    {
        const string marker = "\nApplied mutation:\n";
        const int maximumEvidenceBytes = 2048;
        var beforeHash = new string('a', 64);
        var afterHash = new string('b', 64);
        var target = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralMutationEvidence", "Source.cs"));
        var readSchema = CerebralJson.Element(new
        {
            type = "object",
            properties = new { path = new { type = "string" }, startLine = new { type = "integer" }, lineCount = new { type = "integer" } },
            required = new[] { "path" }, additionalProperties = false
        });
        var editSchema = CerebralJson.Element(new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string" }, oldText = new { type = "string" }, newText = new { type = "string" },
                expectedSha256 = new { type = "string", minLength = 64, maxLength = 64 }
            },
            required = new[] { "path", "oldText", "newText", "expectedSha256" }, additionalProperties = false
        });
        var writeSchema = CerebralJson.Element(new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string" }, content = new { type = "string" },
                collision = new { type = "string", @enum = new[] { "fail", "skip", "replace" }, @default = "fail" },
                expectedSha256 = new { type = "string", minLength = 64, maxLength = 64 }
            },
            required = new[] { "path", "content" }, additionalProperties = false
        });

        string FileDetail(string path, string sha256) => JsonSerializer.Serialize(new
        {
            path, sha256, bytes = 96, lines = 3, words = 8
        }, CerebralJson.Options);

        Fixture PendingMutation(string name, string path, object arguments, string? priorRead = null)
        {
            var fixture = new Fixture();
            if (priorRead is not null)
            {
                fixture.Select("file.read", readSchema);
                fixture.Model(new { action = "tool", name = "file.read", arguments = new { path, startLine = 1, lineCount = 3 } });
                fixture.Tool(true, JsonSerializer.Serialize(new
                {
                    path, sha256 = beforeHash, startLine = 1, totalLines = 3, content = priorRead
                }, CerebralJson.Options));
            }
            fixture.Select(name, name == "file.edit" ? editSchema : writeSchema);
            fixture.Model(new { action = "tool", name, arguments });
            check(fixture.Is("tool"), "mutation-evidence fixture dispatches its exact requested tool");
            return fixture;
        }

        JsonElement Evidence(Fixture fixture)
        {
            check(fixture.Is("generate"), "a completed mutation returns to ordinary model generation");
            var text = fixture.LastContributionText;
            var offset = text.IndexOf(marker, StringComparison.Ordinal);
            check(offset >= 0, "a complete actual mutation has an applied-mutation evidence block");
            var suffix = text[offset..];
            check(Encoding.UTF8.GetByteCount(suffix) <= maximumEvidenceBytes,
                "the entire serialized mutation-evidence block, including its marker, fits the UTF-8 cap");
            using var document = JsonDocument.Parse(text[(offset + marker.Length)..]);
            return document.RootElement.Clone();
        }

        var oldText = "private string? name = \"caf\u00e9\";\n";
        var newText = "private string name = \"caf\u00e9\";\n";
        var small = PendingMutation("file.edit", target,
            new { path = target, oldText, newText, expectedSha256 = beforeHash }, "// previous bounded read\n" + oldText);
        small.Tool(true, FileDetail(target, afterHash));
        var smallEvidence = Evidence(small);
        check(smallEvidence.GetProperty("delta").GetProperty("oldText").GetString() == oldText &&
              smallEvidence.GetProperty("delta").GetProperty("newText").GetString() == newText &&
              smallEvidence.GetProperty("oldCharacters").GetInt32() == oldText.Length &&
              smallEvidence.GetProperty("newCharacters").GetInt32() == newText.Length,
            "a small confirmed edit preserves its exact old/new delta and character counts");
        check(smallEvidence.GetProperty("tool").GetString() == "file.edit" &&
              smallEvidence.GetProperty("path").GetString() == target &&
              smallEvidence.GetProperty("sha256").GetString() == afterHash &&
              smallEvidence.GetProperty("receipt").GetString() ==
                  small.Step.State.GetProperty("toolReceipts")[1].GetProperty("effectId").GetString(),
            "applied edit identity names the exact tool, target, current hash, and durable receipt");
        check(smallEvidence.GetProperty("note").GetString()!.Contains("Prior reads for this target predate", StringComparison.Ordinal) &&
              small.Context.Contributions.Take(small.Context.Contributions.Count - 1)
                  .SelectMany(source => source.Messages).SelectMany(message => message.Content).OfType<TextContentPart>()
                  .Any(part => part.Text.Contains("// previous bounded read", StringComparison.Ordinal)),
            "retained older source text is accompanied by an explicit causal notice and fresh-range guidance");
        check(small.Step.State.GetProperty("toolReceipts").GetArrayLength() == 2 &&
              small.Step.State.GetProperty("pendingArguments").ValueKind == JsonValueKind.Null &&
              small.Context.Contributions.All(source => source.Category != "model-output"),
            "mutation evidence leaves receipt counts intact and does not retain the raw model action");

        var escapedOld = new string('\\', 550);
        var escapedNew = new string('"', 550);
        check(Encoding.UTF8.GetByteCount(escapedOld) + Encoding.UTF8.GetByteCount(escapedNew) < maximumEvidenceBytes,
            "the escaping regression uses a delta whose raw UTF-8 bytes fit");
        var escaping = PendingMutation("file.edit", target,
            new { path = target, oldText = escapedOld, newText = escapedNew, expectedSha256 = beforeHash }, "Earlier bounded read.\n");
        escaping.Tool(true, FileDetail(target, afterHash));
        var escapingEvidence = Evidence(escaping);
        check(!escapingEvidence.TryGetProperty("delta", out _) && escapingEvidence.GetProperty("deltaOmitted").GetBoolean() &&
              escapingEvidence.GetProperty("oldCharacters").GetInt32() == escapedOld.Length &&
              escapingEvidence.GetProperty("newCharacters").GetInt32() == escapedNew.Length,
            "JSON escaping is charged against the aggregate cap before exposing an edit delta");

        var multibyteOld = new string('\u754c', 500);
        var multibyteNew = new string('\u8a9e', 500);
        check(multibyteOld.Length + multibyteNew.Length < maximumEvidenceBytes &&
              Encoding.UTF8.GetByteCount(multibyteOld) + Encoding.UTF8.GetByteCount(multibyteNew) > maximumEvidenceBytes,
            "the Unicode regression distinguishes character counts from actual UTF-8 bytes");
        var multibyte = PendingMutation("file.edit", target,
            new { path = target, oldText = multibyteOld, newText = multibyteNew, expectedSha256 = beforeHash }, "Earlier bounded read.\n");
        multibyte.Tool(true, FileDetail(target, afterHash));
        var multibyteEvidence = Evidence(multibyte);
        check(!multibyteEvidence.TryGetProperty("delta", out _) && multibyteEvidence.GetProperty("deltaOmitted").GetBoolean() &&
              multibyteEvidence.GetProperty("path").GetString() == target &&
              multibyteEvidence.GetProperty("sha256").GetString() == afterHash &&
              multibyteEvidence.GetProperty("note").GetString()!.Contains("fresh bounded range", StringComparison.Ordinal),
            "a large edit retains bounded identity and fresh-read guidance without replaying its delta");

        var writeBody = "// UniqueLargeWriteMutationBody\n" + new string('w', 8000);
        var write = PendingMutation("file.write", target,
            new { path = target, content = writeBody, collision = "replace", expectedSha256 = beforeHash }, "Earlier bounded read.\n");
        write.Tool(true, FileDetail(target, afterHash));
        var writeEvidence = Evidence(write);
        check(writeEvidence.GetProperty("contentOmitted").GetBoolean() &&
              !writeEvidence.TryGetProperty("delta", out _) &&
              !write.LastContributionText.Contains("UniqueLargeWriteMutationBody", StringComparison.Ordinal) &&
              !write.ModelText.Contains("UniqueLargeWriteMutationBody", StringComparison.Ordinal) &&
              !write.Step.State.GetRawText().Contains("UniqueLargeWriteMutationBody", StringComparison.Ordinal),
            "a large ordinary write records causal identity without retaining or replaying its source body");

        var longTarget = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralMutationEvidence",
            string.Join(Path.DirectorySeparatorChar.ToString(), Enumerable.Repeat("segment", 225)), "Source.cs"));
        var longPathWrite = PendingMutation("file.write", longTarget, new { path = longTarget, content = "short source", collision = "fail" });
        longPathWrite.Tool(true, FileDetail(longTarget, afterHash));
        var longPathEvidence = Evidence(longPathWrite);
        check(!longPathEvidence.TryGetProperty("path", out _) && longPathEvidence.GetProperty("pathOmitted").GetBoolean() &&
              longPathEvidence.GetProperty("targetPathSha256").GetString() ==
                  Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(longTarget))).ToLowerInvariant() &&
              longPathEvidence.GetProperty("note").GetString()!.Contains("Fetch this receipt for the exact path", StringComparison.Ordinal),
            "an oversized escaped target retains an exact path hash and receipt guidance instead of a chopped coordinate");

        var generated = new Fixture(warm: false);
        generated.History([]);
        generated.Catalog(forcedNames: ["program.source.generate", "file.write"]);
        generated.Select("program.source.generate", CerebralJson.Element(new
        {
            type = "object", properties = new { path = new { type = "string" } },
            required = new[] { "path" }, additionalProperties = false
        }));
        generated.Model(new { action = "tool", name = "program.source.generate", arguments = new { path = target } });
        generated.Tool(true, "Bounded source generation plan retained.", followUp: true,
            generation: new ToolGenerationRequest("Produce the complete requested source.", "file.write",
                CerebralJson.Element(new { path = target, collision = "replace", expectedSha256 = beforeHash }),
                MaxTokens: 4096, CompleteAssignment: false, MaxAttempts: 2));
        var generatedBody = "// UniqueGeneratedMutationSourceBody\n" + new string('g', 8000);
        generated.RawGeneration(generatedBody);
        check(generated.Is("tool") &&
              generated.Command.Arguments.GetProperty("arguments").GetProperty("content").GetString() == generatedBody,
            "the generated source reaches its exact sink before its completed result is projected");
        generated.Tool(true, FileDetail(target, afterHash));
        var generatedEvidence = Evidence(generated);
        check(generatedEvidence.GetProperty("tool").GetString() == "file.write" &&
              generatedEvidence.GetProperty("contentOmitted").GetBoolean() &&
              generatedEvidence.GetProperty("path").GetString() == target &&
              generatedEvidence.GetProperty("sha256").GetString() == afterHash &&
              generated.Step.State.GetProperty("toolReceipts").GetArrayLength() == 2 &&
              generated.Step.State.GetProperty("pendingGeneration").ValueKind == JsonValueKind.Null &&
              !generated.ModelText.Contains("UniqueGeneratedMutationSourceBody", StringComparison.Ordinal) &&
              !generated.Step.State.GetRawText().Contains("UniqueGeneratedMutationSourceBody", StringComparison.Ordinal),
            "a generated sink uses the same causal projection without retaining generated source");

        var negativePending = PendingMutation("file.edit", target,
            new { path = target, oldText = "before", newText = "after", expectedSha256 = beforeHash }, "Earlier bounded read.\n");
        var negativeCases = new (string Label, Action<Fixture> Complete)[]
        {
            ("failed workflow effect", fixture => fixture.Tool(false, FileDetail(target, afterHash), outcome: ToolExecutionOutcome.Succeeded)),
            ("rejected tool", fixture => fixture.Tool(false, FileDetail(target, afterHash), outcome: ToolExecutionOutcome.Rejected)),
            ("unconfirmed tool outcome", fixture => fixture.Tool(true, FileDetail(target, afterHash), outcome: ToolExecutionOutcome.Unknown)),
            ("partial tool evidence", fixture => fixture.Tool(true, FileDetail(target, afterHash), partial: true)),
            ("truncated result", fixture => fixture.Tool(true, FileDetail(target, afterHash), truncated: true)),
            ("malformed mutation result", fixture => fixture.Tool(true, "{not json")),
            ("different returned target", fixture => fixture.Tool(true, FileDetail(target + ".other", afterHash))),
            ("invalid returned hash", fixture => fixture.Tool(true, FileDetail(target, "invalid"))),
            ("missing file details", fixture => fixture.Tool(true, JsonSerializer.Serialize(new { path = target, sha256 = afterHash }, CerebralJson.Options))),
            ("negative file detail", fixture => fixture.Tool(true, JsonSerializer.Serialize(new { path = target, sha256 = afterHash, bytes = -1, lines = 3, words = 8 }, CerebralJson.Options)))
        };
        foreach (var item in negativeCases)
        {
            var negative = negativePending.Fork();
            item.Complete(negative);
            check(negative.Is("generate") && !negative.LastContributionText.Contains(marker, StringComparison.Ordinal) &&
                  negative.Step.State.GetProperty("toolReceipts").GetArrayLength() == 2,
                item.Label + " cannot fabricate applied-mutation evidence or drop its actual receipt");
        }

        var skipped = PendingMutation("file.write", target, new { path = target, content = "Never applied", collision = "skip" });
        skipped.Tool(true, JsonSerializer.Serialize(new { path = target, skipped = true }, CerebralJson.Options));
        check(skipped.Is("generate") && !skipped.LastContributionText.Contains(marker, StringComparison.Ordinal),
            "a confirmed skipped write does not claim an applied mutation");

        var readOnly = new Fixture();
        readOnly.Select("file.read", readSchema);
        readOnly.Model(new { action = "tool", name = "file.read", arguments = new { path = target } });
        readOnly.Tool(true, JsonSerializer.Serialize(new
        {
            path = target, sha256 = afterHash, startLine = 1, totalLines = 3, content = "Current read only.\n",
            bytes = 96, lines = 3, words = 8
        }, CerebralJson.Options));
        check(readOnly.Is("generate") && !readOnly.LastContributionText.Contains(marker, StringComparison.Ordinal),
            "a successful file read cannot create mutation evidence even with complete file details");
    }
}
