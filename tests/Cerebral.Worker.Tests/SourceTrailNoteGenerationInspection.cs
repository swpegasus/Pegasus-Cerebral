using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;

namespace Cerebral.Worker.Tests;

/// <summary>Exact task-owned Source receipt comparison. It neither invokes models nor writes product state.</summary>
internal static class SourceTrailNoteGenerationInspection
{
    private const string RootRun = "trail-note-original-clean-v3-20261008";
    private const string Conversation = "c1fadeb9a81f49f28b0fcfadbe47a5b7";
    private const string Presentation = "0c82ab4a05e44320bf600c9e73b48bd8";
    private const string QaRun = "plugin-tests-" + Presentation;
    private const string Preparation = "ab9a275bb07c456ba0d9c3267beb8f19";
    private const string Generation = "52044a48af264090b291fc0a0eba5e72";
    private const string Write = "5a176d8f2b474112a6d272af525f4d18";

    public static async Task RunAsync(string dataDirectory)
    {
        var stage = "owner-binding";
        try { await InspectAsync(dataDirectory, value => stage = value); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            // Provider/client exception bodies can contain unrelated payloads. Output no raw error body.
            Console.WriteLine(JsonSerializer.Serialize(new { actualSource = false, readOnly = true,
                modelCalls = 0, stage, errorType = error.GetType().Name,
                error = error is InspectionException ? error.Message : "Exact Source generation inspection did not complete; no effect was dispatched." }, CerebralJson.Options));
            Environment.ExitCode = 1;
        }
    }

    private static async Task InspectAsync(string dataDirectory, Action<string> stage)
    {
        var source = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pegasus", "Cerebral", "Source"));
        Require(OperatingSystem.IsWindows() && EqualPath(dataDirectory, source), "Source root mismatch.");
        var declarationFile = Path.Combine(source, "Desktop", "connection.json");
        RequireSafeFile(declarationFile, source, 16_384);
        using var declaration = JsonDocument.Parse(await File.ReadAllTextAsync(declarationFile));
        var binding = declaration.RootElement;
        Require(Text(binding, "transport") == "pipe" && EqualPath(Text(binding, "localDataDirectory"), source),
            "Source owner pipe mismatch.");
        var endpoint = Property(binding, "localEndpoint").Deserialize<LocalWorkerEndpoint>(CerebralJson.Options)
            ?? throw new InspectionException("Source owner endpoint absent.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var client = new CerebralClient(endpoint);
        var health = await client.GetAsync<HostInfo>("health", cancellationToken: deadline.Token);
        Require(health.LocalRun && EqualPath(health.DataDirectory, source), "Source ownership mismatch.");
        stage("read-schemas");
        foreach (var operation in new[] { "jobs.workflow.get", "jobs.workflow.receipt", "jobs.workflow.events.previous" })
        {
            stage("read-schema:" + operation);
            var description = await client.GetAsync<JsonElement>("sdk.describe", new { id = operation }, cancellationToken: deadline.Token);
            Require(Text(description, "id") == operation && Number(description, "version") == 1 &&
                Text(Property(description, "inputSchema"), "type") == "object", "Read schema mismatch.");
        }

        stage("original-lineage");
        var root = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = RootRun }, cancellationToken: deadline.Token);
        Require(root.Id == RootRun && root.Submission.RequestId == RootRun && root.Submission.ConversationId == Conversation,
            "Exact original lineage mismatch.");
        stage("review-lineage");
        var reviews = await client.GetAsync<List<InterfaceRequest>>("interface.list",
            new { sessionId = Conversation, limit = 12 }, cancellationToken: deadline.Token);
        var review = reviews.SingleOrDefault(item => item.Id == Presentation)
            ?? throw new InspectionException("Exact review absent.");
        Require(review.Source == "workflow" && review.Kind == "plugin" && review.Status == "completed" &&
            review.JobId == RootRun && review.SessionId == Conversation && review.ProfileId == root.Submission.ProfileId &&
            review.Plugin is { SchemaVersion: 1, ControlTestsAllowed: true } &&
            review.PluginTests is { Round: 1 } tests && tests.RequestId == QaRun && tests.RunId == QaRun &&
            tests.OriginRunId == RootRun, "Owner-reviewed QA lineage mismatch.");
        stage("qa-lineage");
        var run = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = QaRun }, cancellationToken: deadline.Token);
        Require(run.Id == QaRun && run.Submission.RequestId == QaRun && run.Submission.ConversationId == Conversation &&
            run.Submission.ProfileId == root.Submission.ProfileId && run.Submission.DefinitionId == root.Submission.DefinitionId &&
            run.Submission.DefinitionVersion == root.Submission.DefinitionVersion &&
            run.Submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.plugin-tests" }, "Accepted QA lineage mismatch.");

        stage("preparation-receipt");
        using var prepare = await ReadReceiptAsync(client, Preparation, deadline.Token);
        stage("generation-receipt");
        using var generated = await ReadReceiptAsync(client, Generation, deadline.Token);
        stage("write-receipt");
        using var written = await ReadReceiptAsync(client, Write, deadline.Token);
        stage("prepared-payload");
        var preparedData = Property(prepare.RootElement, "data");
        Require(Text(preparedData, "name") == "program.source.generate" &&
            Text(Property(preparedData, "evidence"), "providerId") == "cerebral.programming.source" &&
            Text(Property(preparedData, "evidence"), "requestId") == Preparation, "Prepared provider identity mismatch.");
        using var preparedStatus = ParseContent(preparedData);
        var preparedRequest = Property(preparedData, "generation");
        var sink = Property(preparedRequest, "sinkArguments");
        var target = Path.Combine(source, "Projects", "TrailNote", "Desktop", "GeneratedDesktopPlugin.cs");
        Require(Text(preparedRequest, "sinkToolName") == "file.write" && Text(preparedRequest, "contentProperty") == "content" &&
            EqualPath(Text(sink, "path"), target) && Text(sink, "collision") == "replace" &&
            EqualPath(Text(preparedStatus.RootElement, "path"), target), "Prepared sink mismatch.");
        var preimage = RequiredHash(sink, "expectedSha256");
        Require(RequiredHash(preparedStatus.RootElement, "sha256") == preimage, "Prepared preimage mismatch.");

        stage("generated-payload");
        var generatedData = Property(generated.RootElement, "data");
        var generation = Property(generatedData, "generation").Deserialize<ModelGenerationResult>(CerebralJson.Options)
            ?? throw new InspectionException("Generation result absent.");
        Require(!True(generatedData, "outputOmitted") && generation.Finish == ModelGenerationFinish.Stop &&
            generation.Error is null && generation.Text is not null && Encoding.UTF8.GetByteCount(generation.Text) <= 256 * 1024,
            "Generation was incomplete.");
        var payload = generation.Text!;
        stage("written-payload");
        var writtenData = Property(written.RootElement, "data");
        Require(Text(writtenData, "name") == "file.write" && !True(writtenData, "truncated"), "Sink result mismatch.");
        using var writtenStatus = ParseContent(writtenData);
        Require(EqualPath(Text(writtenStatus.RootElement, "path"), target), "Returned destination mismatch.");
        var resultHash = RequiredHash(writtenStatus.RootElement, "sha256");
        var resultBytes = Number(writtenStatus.RootElement, "bytes");
        Require(resultBytes is >= 0 and <= 1024 * 1024, "Returned byte count invalid.");

        // Obtain only the exact sink call from the bounded, pinned conversation journal. Its content
        // is hashed in memory; no prompt, generated source, or prepared-context body is printed.
        stage("frozen-write-arguments");
        JsonElement writeArguments = default;
        long? before = null, snapshot = null;
        for (var pageIndex = 0; pageIndex < 4 && writeArguments.ValueKind == JsonValueKind.Undefined; pageIndex++)
        {
            var arguments = new Dictionary<string, object?> { ["id"] = QaRun, ["limit"] = 200 };
            if (before is not null) arguments["beforeSequence"] = before;
            if (snapshot is not null) arguments["snapshotSequence"] = snapshot;
            var page = await client.GetAsync<JournalPreviousPage>("jobs.workflow.events.previous", arguments, cancellationToken: deadline.Token);
            if (snapshot is null) snapshot = page.SnapshotSequence;
            Require(page.SnapshotSequence == snapshot && page.RetentionGap is null, "Journal snapshot or retention mismatch.");
            foreach (var item in page.Events.Where(item => item.ConversationId == Conversation && item.TurnId == QaRun &&
                item.Type == CerebralEventTypes.ToolCall && Text(item.Data, "callId") == Write))
            {
                Require(writeArguments.ValueKind == JsonValueKind.Undefined && Text(item.Data, "toolName") == "cerebral.host:tool",
                    "Exact sink call ambiguity.");
                var command = Property(item.Data, "arguments");
                Require(Text(command, "name") == "file.write", "Recorded sink tool mismatch.");
                writeArguments = Property(command, "arguments").Clone();
            }
            if (page.NextBeforeSequence is not { } next) break;
            Require(before is null || next < before, "Journal cursor did not advance."); before = next;
        }
        Require(writeArguments.ValueKind == JsonValueKind.Object && EqualPath(Text(writeArguments, "path"), target) &&
            Text(writeArguments, "collision") == "replace" && RequiredHash(writeArguments, "expectedSha256") == preimage &&
            Text(writeArguments, "content") is not null, "Exact frozen sink arguments unavailable.");
        var sinkPayload = Text(writeArguments, "content")!;
        Require(Encoding.UTF8.GetByteCount(sinkPayload) <= 256 * 1024, "Sink payload exceeded inspection bound.");

        stage("current-file");
        RequireSafeFile(target, source, 64 * 1024);
        var current = await File.ReadAllBytesAsync(target, deadline.Token);
        using var reader = new StreamReader(new MemoryStream(current), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var currentText = await reader.ReadToEndAsync(deadline.Token);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var sinkBytes = Encoding.UTF8.GetBytes(sinkPayload);
        var payloadHash = Hash(payloadBytes); var sinkHash = Hash(sinkBytes); var currentHash = Hash(current);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            actualSource = true, readOnly = true, modelCalls = 0, runId = QaRun, run.Revision,
            generation = new { effectId = Generation, utf8Bytes = payloadBytes.Length, characters = payload.Length, sha256 = payloadHash,
                finish = generation.Finish.ToString() },
            preparation = new { effectId = Preparation, sourceBytes = Number(preparedStatus.RootElement, "sourceBytes"), preimageSha256 = preimage },
            write = new { effectId = Write, target, preimageSha256 = preimage, sinkUtf8Bytes = sinkBytes.Length,
                sinkCharacters = sinkPayload.Length, sinkSha256 = sinkHash, resultBytes, resultSha256 = resultHash,
                skipped = True(writtenStatus.RootElement, "skipped"), unchanged = preimage == resultHash,
                disposition = Text(writtenStatus.RootElement, "disposition"), providerId = Text(Property(writtenData, "evidence"), "providerId") },
            currentFile = new { bytes = current.Length, characters = currentText.Length, sha256 = currentHash },
            comparison = new { generationEqualsRecordedSink = payload == sinkPayload, generatedUtf8EqualsCurrentBytes = payloadHash == currentHash,
                recordedSinkUtf8EqualsCurrentBytes = sinkHash == currentHash, generatedTextEqualsCurrentText = payload == currentText,
                recordedSinkTextEqualsCurrentText = sinkPayload == currentText, resultEqualsCurrentFile = resultHash == currentHash },
            bodiesOmitted = true
        }, CerebralJson.Options));
    }

    private static async Task<JsonDocument> ReadReceiptAsync(CerebralClient client, string effectId, CancellationToken token)
    {
        var body = new StringBuilder(); var offset = 0; int? total = null;
        for (var index = 0; index < 16; index++)
        {
            var page = await client.GetAsync<WorkflowReceiptPage>("jobs.workflow.receipt",
                new WorkflowReceiptRequest(QaRun, effectId, offset, WorkflowReceiptPage.MaximumPageBytes), cancellationToken: token);
            Require(page.RunId == QaRun && page.EffectId == effectId && page.State == WorkflowEffectState.Succeeded &&
                page.Offset == offset && page.TotalBytes is > 0 and <= 256 * 1024 &&
                (total is null || total == page.TotalBytes), "Confirmed exact receipt changed or mismatched.");
            total = page.TotalBytes; body.Append(page.Json);
            var bytes = Encoding.UTF8.GetByteCount(page.Json);
            Require(bytes <= WorkflowReceiptPage.MaximumPageBytes, "Receipt page exceeded limit.");
            if (page.NextOffset is null)
            {
                Require(!page.Truncated && offset + bytes == total, "Receipt was incomplete.");
                return JsonDocument.Parse(body.ToString(), new JsonDocumentOptions { MaxDepth = 48 });
            }
            Require(page.Truncated && page.NextOffset == offset + bytes && bytes > 0, "Receipt cursor mismatch.");
            offset = page.NextOffset.Value;
        }
        throw new InspectionException("Receipt inspection page limit exhausted.");
    }

    private static JsonDocument ParseContent(JsonElement data) => JsonDocument.Parse(Text(data, "content")
        ?? throw new InspectionException("Typed receipt content absent."), new JsonDocumentOptions { MaxDepth = 16 });
    private static void RequireSafeFile(string path, string source, long maximum)
    {
        var file = new FileInfo(path);
        Require(file.Exists && file.Length is > 0 && file.Length <= maximum && !file.Attributes.HasFlag(FileAttributes.ReparsePoint),
            "Exact bounded file absent or redirected.");
        var directory = file.Directory;
        while (directory is not null)
        {
            Require(!directory.Attributes.HasFlag(FileAttributes.ReparsePoint), "Source path is redirected.");
            if (EqualPath(directory.FullName, source)) return;
            directory = directory.Parent;
        }
        throw new InspectionException("File is outside Source.");
    }
    private static bool EqualPath(string? left, string right) => left is not null && Path.GetFullPath(left)
        .Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static JsonElement Property(JsonElement? value, string name) => value is { ValueKind: JsonValueKind.Object } parent &&
        parent.TryGetProperty(name, out var item) ? item : default;
    private static string? Text(JsonElement? value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } item ? item.GetString() : null;
    private static long? Number(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Number } item && item.TryGetInt64(out var number) ? number : null;
    private static bool True(JsonElement value, string name) => Property(value, name).ValueKind == JsonValueKind.True;
    private static string RequiredHash(JsonElement value, string name)
    {
        var hash = Text(value, name); Require(hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit), "Hash absent or malformed.");
        return hash!.ToLowerInvariant();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool value, string reason) { if (!value) throw new InspectionException(reason); }
    private sealed class InspectionException(string reason) : Exception(reason);
}
