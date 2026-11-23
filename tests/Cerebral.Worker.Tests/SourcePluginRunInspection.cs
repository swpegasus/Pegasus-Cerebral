using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Client;
using Cerebral.Contracts.Agents;
using Cerebral.Conversation;
using Cerebral.Engine.Workflows;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;

namespace Cerebral.Worker.Tests;

/// <summary>Read-only diagnostics for the task-owned Trail Note attempts; no model or plugin calls.</summary>
internal static class SourcePluginRunInspection
{
    private static readonly (string RunId, string ConversationId)[] BoundRuns =
    [
        ("trail-note-fresh-v1-20261008", "8cb908c118724515a7ad83f2292820d0"),
        ("trail-note-original-retry-v2-20261008", "2d080c8aae774beb9a7396bc8f90ba8b"),
        ("trail-note-original-clean-v3-20261008", "c1fadeb9a81f49f28b0fcfadbe47a5b7"),
        ("waypoint-note-fresh-v1-20261009", "051d76d674754751834083d49ffbe964"),
        ("basin-note-fresh-v1-20261009", "c173881df3d940c1ad3190ad38552dda"),
        ("memo-pad-fresh-v1-20261009", "a0893cad3fb641a8a93bbc8833f4eea6"),
        ("field-journal-fresh-v1-20261009", "e54ecbc63c2d4f9a964079048c3c1be8"),
        ("cedar-note-fresh-v1-20261009", "10a615f1cdee43da8b4d0d1b8413d09d"),
        ("juniper-note-fresh-v1-20261009", "fb67972264a84bbc885b868559c058cb"),
        ("spruce-note-fresh-v1-20261009", "62077de69d824ee49407f00443145f03")
    ];

    public static async Task RunAsync(string dataDirectory, string runId, long afterSequence = 0)
    {
        var source = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pegasus", "Cerebral", "Source"));
        if (!OperatingSystem.IsWindows() || !Path.GetFullPath(dataDirectory).Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Inspection is restricted to the existing Source root.");
        var isRoot = BoundRuns.Any(pair => pair.RunId == runId);
        if (afterSequence < 0 || (!isRoot &&
            !PluginControlReferences.TryResolveReviewedPresentationId(runId, out _)))
            throw new ArgumentException("Use either exact task-owned Trail Note run or its bound plugin-tests child and a nonnegative cursor.");

        var file = new FileInfo(Path.Combine(source, "Desktop", "connection.json"));
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length is <= 0 or > 16_384)
            throw new InvalidOperationException("Unexpected Source declaration.");
        using var declaration = JsonDocument.Parse(await File.ReadAllTextAsync(file.FullName));
        var binding = declaration.RootElement;
        if (binding.GetProperty("transport").GetString() != "pipe" ||
            !Path.GetFullPath(binding.GetProperty("localDataDirectory").GetString()!).Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source is not its local owner pipe.");
        var endpoint = binding.GetProperty("localEndpoint").Deserialize<LocalWorkerEndpoint>(CerebralJson.Options)
            ?? throw new InvalidOperationException("Source owner endpoint is absent.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var client = new CerebralClient(endpoint);
        var health = await client.GetAsync<HostInfo>("health", cancellationToken: deadline.Token);
        if (!health.LocalRun || !Path.GetFullPath(health.DataDirectory).Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The endpoint does not own Source.");

        // Verify the live read schemas before using them. No operation discovery bodies are printed.
        foreach (var operation in new[] { "jobs.workflow.get", "jobs.workflow.events", "jobs.workflow.receipt" })
        {
            var descriptor = await client.GetAsync<JsonElement>("sdk.describe", new { id = operation }, cancellationToken: deadline.Token);
            if (Text(descriptor, "id") != operation || Number(descriptor, "version") != 1 ||
                Text(Property(descriptor, "inputSchema"), "type") != "object")
                throw new InvalidOperationException("The required Source read operation schema is unavailable: " + operation);
        }

        WorkflowRun? run = null;
        foreach (var pair in BoundRuns.Where(pair => !isRoot || pair.RunId == runId))
        {
            var root = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = pair.RunId }, cancellationToken: deadline.Token);
            if (root.Id != pair.RunId || root.Submission.RequestId != pair.RunId ||
                root.Submission.ConversationId != pair.ConversationId)
                throw new InvalidOperationException("An accepted Trail Note run identity changed.");
            if (isRoot) { run = root; continue; }

            // Review records retain the parent job even when a later source edit clears
            // the mutable harness presentation field. Follow only admitted owner-approved
            // QA links in this fixed task-owned conversation, never a requested ID alone.
            var reviews = await client.GetAsync<List<InterfaceRequest>>("interface.list",
                new { sessionId = pair.ConversationId, limit = 12 }, cancellationToken: deadline.Token);
            var frontier = new List<WorkflowRun> { root };
            var seen = new HashSet<string>(StringComparer.Ordinal) { root.Id };
            for (var depth = 0; depth < 3 && frontier.Count != 0; depth++)
            {
                var next = new List<WorkflowRun>();
                foreach (var parent in frontier)
                foreach (var review in reviews.Where(item => item.JobId == parent.Id &&
                    item.Source == "workflow" && item.Kind == "plugin" &&
                    item.SessionId == pair.ConversationId && item.ProfileId == parent.Submission.ProfileId &&
                    item.PluginTests is { RunId: not null } &&
                    (item.Status == "completed" && item.Plugin is { SchemaVersion: 1, ControlTestsAllowed: true } &&
                        item.PluginTests.Mode is null ||
                     item.Status == "failed" && item.Plugin is null && item.Resource is null &&
                        item.ClaimId is not null && item.ClaimedAt is not null && item.CompletedAt is not null &&
                        !string.IsNullOrWhiteSpace(item.Error) && item.PackageFingerprint is { Length: 64 } &&
                        item.PluginTests.Mode == InterfacePluginTestModes.RepairBeforeInstall)))
                {
                    var tests = review.PluginTests!;
                    var childId = PluginControlReferences.TestRequestPrefix + review.Id;
                    if (!PluginControlReferences.TryResolveReviewedPresentationId(childId, out var presentationId) ||
                        presentationId != review.Id || tests.RequestId != childId || tests.RunId != childId ||
                        tests.OriginRunId != pair.RunId || tests.Round != depth + 1 ||
                        !seen.Add(childId))
                        throw new InvalidOperationException("The owner-approved QA review lineage is invalid.");
                    var child = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = childId }, cancellationToken: deadline.Token);
                    if (child.Id != childId || child.Submission.RequestId != childId ||
                        child.Submission.ConversationId != pair.ConversationId ||
                        child.Submission.ProfileId != parent.Submission.ProfileId ||
                        child.Submission.DefinitionId != parent.Submission.DefinitionId ||
                        child.Submission.DefinitionVersion != parent.Submission.DefinitionVersion ||
                        child.Submission.Caller is not { Kind: EventActorKind.Service, Id: "cerebral.plugin-tests" })
                        throw new InvalidOperationException("The exact plugin-tests child identity changed.");
                    if (child.Id == runId)
                    {
                        if (run is not null) throw new InvalidOperationException("Ambiguous Trail Note QA lineage.");
                        run = child;
                    }
                    next.Add(child);
                }
                frontier = next;
            }
        }
        if (run is null)
            throw new InvalidOperationException("The requested child is not in either exact Trail Note review chain.");

        var queue = await client.GetAsync<PeerWorkerMetrics>("peer.metrics", cancellationToken: deadline.Token);
        var page = await client.GetAsync<JournalPage>("jobs.workflow.events",
            new { id = run.Id, afterSequence, limit = 40 }, cancellationToken: deadline.Token);
        var events = page.Events.Where(item => item.ConversationId == run.Submission.ConversationId &&
            (item.TurnId == run.Id || item.Provenance.JobId == run.Id)).Select(EventView).ToArray();
        var fieldJournalEdit = await FieldJournalEditViewAsync(client, source, run, page, deadline.Token);
        var recent = Array(Property(run.State, "toolReceipts")).TakeLast(8).ToArray();
        var failed = recent.Where(item => Text(item, "state") is not (null or "Succeeded"))
            .Select(item => Text(item, "effectId")).Where(id => id is not null).Cast<string>();
        var nativeFailures = Array(Property(run.State, "nativeControlFailures"))
            .Select(item => Text(item, "effectId")).Where(id => id is not null).Cast<string>();
        var receiptIds = failed.Reverse().Concat(nativeFailures.Reverse())
            .Distinct(StringComparer.Ordinal).Take(3).ToArray();
        var receipts = new List<object>();
        foreach (var effectId in receiptIds)
        {
            // Read one bounded receipt page; emit only selected file-mutation diagnostics, never its body.
            var receipt = await client.GetAsync<WorkflowReceiptPage>("jobs.workflow.receipt",
                new WorkflowReceiptRequest(run.Id, effectId, Limit: WorkflowReceiptPage.MaximumPageBytes), cancellationToken: deadline.Token);
            if (receipt.RunId != run.Id || receipt.EffectId != effectId)
                throw new InvalidOperationException("Source returned an unrelated effect receipt.");
            receipts.Add(new { receipt.EffectId, state = receipt.State.ToString(), receipt.ProviderReceiptId,
                error = Diagnostic(receipt.ErrorPreview), receipt.RunRevision, receipt.TotalBytes,
                mutationDiagnostic = MutationDiagnostic(receipt),
                nativeControlDiagnostic = NativeControlDiagnostic(receipt), resultBodyOmitted = true });
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            actualSource = true, readOnly = true, modelCalls = 0,
            scheduling = new { queue.Queue.OwnerRunning, queue.Queue.OwnerQueued, queue.Queue.PeerRunning, queue.Queue.PeerQueued },
            run = RunView(run),
            fieldJournalEdit,
            durableBoundary = PureBoundaryView(run),
            journal = new { afterSequence, limit = 40, page.SnapshotSequence, page.NextAfterSequence,
                returnedEvents = page.Events.Count, matchingEvents = events.Length, retentionGap = page.RetentionGap is not null,
                events },
            recentTools = recent.Select(item => new { effectId = Text(item, "effectId"), name = Text(item, "name"),
                state = Text(item, "state"), outcome = Text(item, "outcome"), exitCode = Number(item, "exitCode"),
                truncated = Boolean(item, "truncated") }),
            receipts
        }, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
    }

    private static async Task<object?> FieldJournalEditViewAsync(CerebralClient client, string source,
        WorkflowRun run, JournalPage page, CancellationToken token)
    {
        const string runId = "field-journal-fresh-v1-20261009";
        const string conversationId = "e54ecbc63c2d4f9a964079048c3c1be8";
        const string effectId = "6ee4305bc660492cb8040c09fee91690";
        if (run.Id != runId) return null;
        RequireEdit(run.Submission.ConversationId == conversationId, "The exact edit conversation changed.");
        var exact = page.Events.Where(item => item.ConversationId == conversationId &&
            (item.TurnId == runId || item.Provenance.JobId == runId) && item.CorrelationId == effectId).ToArray();
        var calls = exact.Where(item => item.Type == "tool.call").ToArray();
        if (calls.Length == 0)
            return new { effectId, available = false, reason = "Exact edit call absent from requested bounded journal page." };
        var results = exact.Where(item => item.Type == "tool.result").ToArray();
        RequireEdit(page.RetentionGap is null && calls.Length == 1 && results.Length == 1,
            "The exact edit call/result is missing, duplicated or separated by a retention gap.");
        var call = calls[0]; var result = results[0];
        var callData = call.Data ?? default; var resultData = result.Data ?? default;
        var dispatch = Property(callData, "arguments"); var arguments = Property(dispatch, "arguments");
        RequireEdit(UniqueObject(callData) && UniqueObject(resultData) && UniqueObject(dispatch) && UniqueObject(arguments) &&
            Text(callData, "callId") == effectId && Text(resultData, "callId") == effectId &&
            Text(callData, "toolName") == "cerebral.host:tool" && Text(resultData, "toolName") == "cerebral.host:tool" &&
            Text(dispatch, "name") == "file.edit" && Boolean(resultData, "success") == true &&
            result.Sequence > call.Sequence && result.CreatedAt >= call.CreatedAt,
            "The journal did not prove the exact successful semantic edit.");
        var requestedPath = Text(arguments, "path"); var expectedHash = Text(arguments, "expectedSha256");
        var oldText = Text(arguments, "oldText"); var newText = Text(arguments, "newText");
        RequireEdit(requestedPath is { Length: > 0 and <= 2048 } && requestedPath.All(c => !char.IsControl(c)) &&
            Path.IsPathFullyQualified(requestedPath) && ValidHash(expectedHash) && oldText is not null && newText is not null &&
            Encoding.UTF8.GetByteCount(oldText) + Encoding.UTF8.GetByteCount(newText) <= 128 * 1024,
            "The exact edit arguments are incomplete, malformed or exceed the diagnostic bound.");
        var projectRoot = Path.Combine(source, "Projects", "FieldJournal");
        var target = Path.GetFullPath(requestedPath!);
        RequireEdit(target.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "The exact edit target is outside its task-owned Field Journal project.");

        // This is one immutable, bounded result read. Never replay the edit or reread mutable source
        // to manufacture its provider preimage, destination, replacement count or resulting hash.
        var receipt = await client.GetAsync<WorkflowReceiptPage>("jobs.workflow.receipt",
            new WorkflowReceiptRequest(runId, effectId, Limit: WorkflowReceiptPage.MaximumPageBytes), cancellationToken: token);
        RequireEdit(receipt.RunId == runId && receipt.EffectId == effectId && receipt.State == WorkflowEffectState.Succeeded &&
            receipt.Offset == 0 && !receipt.Truncated && receipt.NextOffset is null &&
            receipt.TotalBytes is > 0 and <= WorkflowReceiptPage.MaximumPageBytes &&
            Encoding.UTF8.GetByteCount(receipt.Json) == receipt.TotalBytes,
            "The exact edit receipt is unrelated, unconfirmed or incomplete.");
        using var envelope = JsonDocument.Parse(receipt.Json, new JsonDocumentOptions { MaxDepth = 24 });
        var root = envelope.RootElement; var data = Property(root, "data"); var evidence = Property(data, "evidence");
        RequireEdit(UniqueObject(root) && UniqueObject(data) && UniqueObject(evidence) &&
            Property(root, "state").Deserialize<WorkflowEffectState>(CerebralJson.Options) == WorkflowEffectState.Succeeded &&
            Text(data, "name") == "file.edit" && Boolean(data, "truncated") == false &&
            Property(evidence, "outcome").Deserialize<ToolExecutionOutcome>(CerebralJson.Options) == ToolExecutionOutcome.Succeeded &&
            Boolean(evidence, "partial") != true &&
            Text(evidence, "requestId") == effectId && ValidCoordinate(Text(evidence, "jobId")) &&
            Text(evidence, "jobId") == receipt.ProviderReceiptId &&
            Text(root, "providerReceiptId") == receipt.ProviderReceiptId && ValidCoordinate(Text(evidence, "providerId")),
            "The edit receipt/provider identity or completeness did not match the exact effect.");
        var content = Text(data, "content");
        RequireEdit(content is not null, "The exact edit receipt has no semantic mutation result.");
        using var semanticDocument = JsonDocument.Parse(content!, new JsonDocumentOptions { MaxDepth = 12 });
        var semantic = semanticDocument.RootElement;
        var returnedPath = Text(semantic, "path"); var returnedHash = Text(semantic, "sha256"); var bytes = Number(semantic, "bytes");
        RequireEdit(UniqueObject(semantic) && returnedPath is { Length: > 0 and <= 2048 } &&
            returnedPath.All(c => !char.IsControl(c)) && Path.IsPathFullyQualified(returnedPath) &&
            Path.GetFullPath(returnedPath).Equals(target, StringComparison.OrdinalIgnoreCase) &&
            ValidHash(returnedHash) && bytes is >= 0 and <= int.MaxValue,
            "The exact edit semantic destination/hash/size did not match its requested target.");
        var count = Number(semantic, "count");
        RequireEdit(Property(semantic, "count").ValueKind == JsonValueKind.Undefined || count is >= 0,
            "The retained replacement count is malformed.");
        return new
        {
            effectId, available = true, callSequence = call.Sequence, resultSequence = result.Sequence,
            call.CreatedAt, completedAt = result.CreatedAt, completeReceipt = true, receipt.RunRevision, receipt.TotalBytes,
            request = new { path = requestedPath, canonicalTarget = target, expectedSha256 = expectedHash,
                oldText = EditTextShape(oldText!), newText = EditTextShape(newText!),
                rawEqual = oldText == newText,
                crLfLfNormalizedEqual = oldText!.Replace("\r\n", "\n", StringComparison.Ordinal) == newText!.Replace("\r\n", "\n", StringComparison.Ordinal),
                uniformNewlineNormalizedEqual = UniformNewlines(oldText!) == UniformNewlines(newText!) },
            returned = new { path = returnedPath, sha256 = returnedHash, bytes, count,
                replacementCountRetained = count is not null,
                expectedHashEqualsReturnedHash = expectedHash!.Equals(returnedHash, StringComparison.OrdinalIgnoreCase) },
            provider = new { id = Text(evidence, "providerId"), requestId = Text(evidence, "requestId"),
                jobId = Text(evidence, "jobId"), state = Text(evidence, "state"), partial = Boolean(evidence, "partial"),
                exitCode = Number(evidence, "exitCode"), receipt.ProviderReceiptId },
            textBodiesOmitted = true, mutableFileRead = false
        };
    }

    private static object EditTextShape(string text)
    {
        var crLf = text.Split("\r\n", StringSplitOptions.None).Length - 1;
        return new { characters = text.Length, utf8Bytes = Encoding.UTF8.GetByteCount(text), crLf,
            loneCr = text.Count(c => c == '\r') - crLf, loneLf = text.Count(c => c == '\n') - crLf,
            normalizedUtf8Bytes = Encoding.UTF8.GetByteCount(UniformNewlines(text)) };
    }
    private static string UniformNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static bool UniqueObject(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == value.EnumerateObject().Count();
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool ValidCoordinate(string? value) => value is { Length: > 0 and <= 256 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':');
    private static void RequireEdit(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }

    private static object? PureBoundaryView(WorkflowRun run)
    {
        if (run.Status != WorkflowStatus.Failed || run.CancelRequested ||
            run.Effect is not { State: WorkflowEffectState.Succeeded } ||
            run.Error != "Workflow data exceeds 128 KiB.")
            return null;
        // A detached pure harness evaluation is diagnostic only. It has no client, runtime,
        // dispatcher or store and its requested action is never committed or executed.
        var harness = new ConversationAgentHarness();
        if (run.Submission.DefinitionId != harness.Descriptor.Id ||
            run.Submission.DefinitionVersion != harness.Descriptor.Version)
            throw new InvalidOperationException("The inspection harness does not match this exact accepted workflow.");
        var step = harness.Advance(new AgentHarnessSnapshot(run));
        var command = step.Next as InvokeWorkflowCommand;
        string? stateError = null, argumentError = null;
        try { WorkflowSchema.Validate(step.State, WorkflowContract.EmptyObject); }
        catch (ArgumentException error) { stateError = Diagnostic(error.Message); }
        if (command is not null)
        {
            try { WorkflowSchema.Validate(command.Arguments, WorkflowContract.EmptyObject); }
            catch (ArgumentException error) { argumentError = Diagnostic(error.Message); }
        }
        return new
        {
            pureEvaluation = true, dispatches = 0, maximumBytes = WorkflowContract.MaximumStateBytes,
            stateHighWaterBytes = WorkflowContract.MaximumStateBytes - 3 * 8192,
            savedStateBytes = Bytes(run.State), savedStateLargestProperties = PropertyBytes(run.State),
            nextStateBytes = Bytes(step.State), nextStateValidationError = stateError,
            nextPhase = Text(step.State, "phase"),
            nextAction = step.Next.GetType().Name,
            commandId = command?.CommandId,
            commandArgumentBytes = command is null ? (int?)null : Bytes(command.Arguments),
            commandArgumentValidationError = argumentError,
            commandLargestProperties = command is null ? null : PropertyBytes(command.Arguments),
            bodyOmitted = true
        };
    }

    private static int Bytes(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? 0 :
        JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false }).Length;
    private static object[] PropertyBytes(JsonElement value) => value.ValueKind != JsonValueKind.Object ? [] :
        value.EnumerateObject().Select(property => new { property.Name, utf8Bytes = Bytes(property.Value) })
            .OrderByDescending(property => property.utf8Bytes).Take(8).Cast<object>().ToArray();

    private static object RunView(WorkflowRun run) => new
    {
        run.Id, conversationId = run.Submission.ConversationId, run.Revision, status = run.Status.ToString(),
        run.Steps, activeBudgetMilliseconds = run.ActiveMilliseconds,
        currentReservationMilliseconds = run.Effect is { State: WorkflowEffectState.Dispatched } dispatch ? dispatch.ReservedExecutionMilliseconds : 0,
        run.CancelRequested, run.CreatedAt, run.UpdatedAt,
        waitingReason = Diagnostic(run.WaitingReason), error = Diagnostic(run.Error), run.FailedEffects,
        workflow = new { run.Submission.DefinitionId, run.Submission.DefinitionVersion },
        harness = new { phase = Text(run.State, "phase"), pendingName = Text(run.State, "pendingName"),
            lastRejectedName = Text(Property(run.State, "lastRejectedTool"), "name"),
            lastRejectedDiagnostic = Diagnostic(Text(Property(run.State, "lastRejectedTool"), "diagnostic")),
            repeatedRejectionCorrections = Number(run.State, "repeatedRejectionCorrections"),
            consecutiveSameRejections = Number(run.State, "consecutiveSameRejections") },
        effect = run.Effect is not { } effect ? null : new
        {
            effect.Id, effect.CommandId, effect.CommandVersion, state = effect.State.ToString(),
            toolName = Text(effect.Arguments, "name"), effect.ProviderReceiptId,
            sdkTopic = Text(Property(effect.Arguments, "arguments"), "topic"),
            sourceRequest = SourceRequestView(Property(effect.Arguments, "arguments")),
            generatedReferences = GenerationReferenceView(effect.Arguments),
            error = Diagnostic(effect.Error)
        },
        nativeControlFailures = Array(Property(run.State, "nativeControlFailures")).Take(8).Select(item => new
        {
            presentationId = Text(item, "presentationId"), effectId = Text(item, "effectId"),
            operation = Text(item, "operation"), completeObservation = Boolean(item, "completeObservation"),
            sourceManifest = Text(item, "sourceManifest"), packageFingerprint = Text(item, "packageFingerprint"),
            diagnostic = Diagnostic(Text(item, "diagnostic"))
        }),
        nativeControlCoverageCount = Array(Property(run.State, "nativeControlCoverage")).Count(),
        pluginProgress = PluginProgress(Property(run.State, "pluginWork")),
        binding = BindingView(run.State),
        failedBuildReferences = FailedBuildReferences(run.State),
        pluginVisualEvidence = PluginVisualEvidence(run),
        resultPresent = run.Result is not null
    };

    private static object? NativeControlDiagnostic(WorkflowReceiptPage receipt)
    {
        if (receipt.Truncated || receipt.NextOffset is not null) return null;
        try
        {
            using var outer = JsonDocument.Parse(receipt.Json);
            var data = Property(outer.RootElement, "data");
            if (Text(data, "name") != "plugin.controls" || Text(data, "content") is not { } content) return null;
            if (!content.TrimStart().StartsWith('{')) return new { message = Diagnostic(content) };
            using var inner = JsonDocument.Parse(content);
            var observation = Property(inner.RootElement, "observation");
            return new { status = Text(inner.RootElement, "status"),
                installedGeneration = Text(inner.RootElement, "installedGeneration"),
                assertion = Text(Property(inner.RootElement, "assertion"), "status"),
                expectedTexts = Array(Property(Property(inner.RootElement, "assertion"), "expectedTexts"))
                    .Take(4).Select(item => Diagnostic(item.GetString())).ToArray(),
                before = ControlSnapshotDiagnostic(Property(observation, "before")),
                after = ControlSnapshotDiagnostic(Property(observation, "after")),
                message = Diagnostic(Text(inner.RootElement, "error")) };
        }
        catch (JsonException) { return null; }
    }

    private static object ControlSnapshotDiagnostic(JsonElement snapshot) => new
    {
        limited = Boolean(snapshot, "limited"),
        texts = Array(Property(snapshot, "texts")).Take(6).Select(item => Diagnostic(item.GetString())).ToArray(),
        fields = Array(Property(snapshot, "fields")).Take(4).Select(field => new
        {
            id = Text(field, "id"), visible = Boolean(field, "visible"), limited = Boolean(field, "limited"),
            textCharacters = Text(field, "text")?.Length,
            textSha256 = Text(field, "text") is { } text ? Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(text))) : null
        }).ToArray()
    };

    private static object? MutationDiagnostic(WorkflowReceiptPage receipt)
    {
        if (receipt.Truncated || receipt.NextOffset is not null) return null;
        try
        {
            using var outer = JsonDocument.Parse(receipt.Json);
            var data = Property(outer.RootElement, "data");
            if (Text(data, "name") is not ("file.write" or "file.edit") ||
                Text(data, "content") is not { } content) return null;
            using var inner = JsonDocument.Parse(content);
            var root = inner.RootElement;
            var error = Property(root, "err");
            return new
            {
                code = Diagnostic(Text(error, "code") ?? Text(root, "error")),
                message = Diagnostic(Text(error, "msg") ?? Text(error, "message") ?? Text(root, "message"))
            };
        }
        catch (JsonException) { return null; }
    }

    private static object? PluginVisualEvidence(WorkflowRun run)
    {
        if (run.Submission.Caller is not { Kind: EventActorKind.Service, Id: "cerebral.plugin-tests" })
            return null;
        // This reads only the immutable host-added QA assignment; it is retained visual evidence,
        // never new task authority or proof of controls, persistence, or token accounting.
        var assignment = run.Submission.AssignmentText ?? "";
        const string completed = "A separate host-owned reasoning-off pass inspected the canonical mounted-view pixels without tools or actions. ";
        const string unavailable = "Host visual status: ";
        var completedAt = assignment.LastIndexOf(completed, StringComparison.Ordinal);
        var unavailableAt = assignment.LastIndexOf(unavailable, StringComparison.Ordinal);
        if (completedAt > unavailableAt)
            return new { state = "succeeded", hostOwnedReasoningOff = true, bodyOmitted = true };
        if (unavailableAt < 0) return null;
        var suffix = assignment[(unavailableAt + unavailable.Length)..];
        var end = suffix.IndexOf(". ", StringComparison.Ordinal);
        var status = end is >= 0 and <= 24 ? suffix[..end] : "unavailable";
        return new { state = status, hostOwnedReasoningOff = true, bodyOmitted = true };
    }

    private static object BindingView(JsonElement state)
    {
        var work = Property(state, "pluginWork");
        var build = Property(work, "build");
        return new
        {
            selected = work.ValueKind == JsonValueKind.Object,
            component = Text(build, "component"),
            origin = Text(build, "origin"),
            root = Text(build, "root"),
            canonicalProject = Text(build, "projectFile"),
            candidateCount = Array(Property(state, "pluginBuildCandidates")).Count(),
            qaSourceCatalogChecked = Property(state, "qaSourceCatalogChecked").ValueKind == JsonValueKind.True,
            approvedSourceRoot = Text(Property(state, "pluginSource"), "root"),
            sourceCatalogEffectId = Text(Property(state, "pluginSource"), "catalogEffectId"),
            exactSourceInventoryEffectId = Text(Property(state, "pluginSource"), "inspectionEffectId"),
            knownFileRevisions = Array(Property(state, "knownFileRevisions")).Select(revision => new
            {
                path = Text(revision, "path"), effectId = Text(revision, "effectId")
            }),
            lastCanonicalBuildEffectId = Text(work, "lastCanonicalBuildEffectId"),
            lastCanonicalBuildGeneration = Number(work, "lastCanonicalBuildGeneration")
        };
    }

    private static object PluginProgress(JsonElement work) => new
    {
        sourceGeneration = Number(work, "sourceGeneration"),
        successfulBuildGeneration = Number(work, "successfulBuildGeneration"),
        buildEffectId = Text(work, "buildEffectId"), verifiedGeneration = Number(work, "verifiedGeneration"),
        verifyEffectId = Text(work, "verifyEffectId"), presentedGeneration = Number(work, "presentedGeneration"),
        presentEffectId = Text(work, "presentEffectId"), presentRequestId = Text(work, "presentRequestId")
    };

    private static object EventView(CerebralEvent item)
    {
        var data = item.Data ?? default;
        return new
        {
            item.Sequence, item.Type, item.CreatedAt, item.CorrelationId,
            commandOrToolName = Text(data, "toolName"),
            delegatedToolName = Text(Property(data, "arguments"), "name"),
            sdkTopic = Text(Property(Property(data, "arguments"), "arguments"), "topic"),
            sourceRequest = SourceRequestView(Property(Property(data, "arguments"), "arguments")),
            generatedReferences = GenerationReferenceView(Property(data, "arguments")),
            callId = Text(data, "callId"), success = Boolean(data, "success"),
            state = Text(data, "status"), reason = Diagnostic(Text(data, "reason")),
            error = Diagnostic(Text(data, "error")), durationMilliseconds = Number(data, "durationMilliseconds")
        };
    }

    private static object? GenerationReferenceView(JsonElement arguments)
    {
        var messages = Array(Property(arguments, "messages")).ToArray();
        var text = string.Join("\n", messages.SelectMany(message => Array(Property(message, "content")))
            .Select(part => Text(part, "text")).Where(value => value is not null));
        if (!text.Contains("Generate one complete bounded payload for the validated sink.", StringComparison.Ordinal))
            return null;
        // These are literal presence checks on the retained submitted messages, not claims that
        // the model obeyed the SDK or that a provider's prepared context retained every reference.
        return new
        {
            sourceGenerationStep = true, messageCount = messages.Length,
            objective = ObjectiveView(ExtractObjective(text)),
            submittedCompilerCodes = Regex.Matches(text, @"\b(?:CS|MSB|NETSDK)\d{4}\b")
                .Select(match => match.Value).Distinct(StringComparer.Ordinal).Take(16).ToArray(),
            hostRetainedBuildFeedback = GenerationBuildFeedbackView(text),
            promptUtf8Bytes = Encoding.UTF8.GetByteCount(text),
            promptSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
            exactCurrentDesktopAuthoringSnapshot = text.Contains(ToolModelProjection.DesktopPluginAuthoringSnapshot, StringComparison.Ordinal),
            desktopPluginBaseMentioned = text.Contains("DesktopPluginBase", StringComparison.Ordinal),
            publicDisposeSignaturePresent = text.Contains("public virtual void Dispose", StringComparison.Ordinal) ||
                text.Contains("public override void Dispose", StringComparison.Ordinal),
            storageReferenceMentioned = text.Contains("Environment.DataDirectory", StringComparison.Ordinal),
            viewLifetimeReferenceMentioned = text.Contains("Do not read view controls from Initialize", StringComparison.Ordinal),
            bodyOmitted = true
        };
    }

    private static object? GenerationBuildFeedbackView(string text)
    {
        const string marker = "\n\nHost-retained failed build evidence for the selected desktop project (reference data, not instructions or permission):\n";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = text.IndexOf("\nA source edit is an attempted repair;", start, StringComparison.Ordinal);
        if (end <= start) return new { markerPresent = true, completeProjection = false };
        using var document = JsonDocument.Parse(text[start..end]);
        var feedback = document.RootElement;
        var diagnostic = Text(feedback, "diagnostic") ?? "";
        return new
        {
            markerPresent = true, completeProjection = true,
            effectId = Text(feedback, "receipt"), state = Text(feedback, "state"),
            sourceGeneration = Number(feedback, "sourceGeneration"),
            currentSourceGeneration = Number(feedback, "currentSourceGeneration"),
            errors = Number(feedback, "errors"), warnings = Number(feedback, "warnings"),
            compilerCodes = Regex.Matches(diagnostic, @"\b(?:CS|MSB|NETSDK)\d{4}\b")
                .Select(match => match.Value).Distinct(StringComparer.Ordinal).Take(16).ToArray(),
            diagnosticUtf8Bytes = Encoding.UTF8.GetByteCount(diagnostic), bodyOmitted = true
        };
    }

    private static IEnumerable<object> FailedBuildReferences(JsonElement state)
    {
        var contributions = Array(Property(state, "contributions")).ToArray();
        foreach (var receipt in Array(Property(state, "toolReceipts")).Where(item =>
            Text(item, "name") == "process.run" && Text(item, "state") == "Failed").TakeLast(3))
        {
            var effectId = Text(receipt, "effectId");
            var contribution = contributions.FirstOrDefault(item => Text(item, "id") == "tool." + effectId);
            var text = string.Join("\n", Array(Property(contribution, "messages"))
                .SelectMany(message => Array(Property(message, "content"))).Select(part => Text(part, "text")));
            var diagnosticStart = text.IndexOf("\nDiagnostic: ", StringComparison.Ordinal);
            var diagnostics = diagnosticStart < 0 ? "" : text[(diagnosticStart + "\nDiagnostic: ".Length)..];
            yield return new
            {
                effectId, retainedContributionPresent = contribution.ValueKind == JsonValueKind.Object,
                contributionCategory = Text(contribution, "category"),
                requirement = Text(contribution, "requirement"),
                compilerCodes = Regex.Matches(diagnostics, @"\b(?:CS|MSB|NETSDK)\d{4}\b")
                    .Select(match => match.Value).Distinct(StringComparer.Ordinal).Take(16).ToArray(),
                projectedCompilerLines = diagnostics.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Count(line => Regex.IsMatch(line, @"\berror\s+(?:CS|MSB|NETSDK)\d{4}\b")),
                brushesMentioned = diagnostics.Contains("Brushes", StringComparison.Ordinal),
                bodyOmitted = true
            };
        }
    }

    private static object? SourceRequestView(JsonElement arguments)
    {
        var instructions = Text(arguments, "instructions");
        return instructions is null ? null : new
        {
            objective = ObjectiveView(instructions),
            referenceCount = Array(Property(arguments, "referencePaths")).Count(),
            expectedSha256Present = Text(arguments, "expectedSha256") is not null,
            bodyOmitted = true
        };
    }

    private static string ExtractObjective(string prompt)
    {
        const string start = "Host-frozen implementation objective:";
        const string end = "Target source evidence:";
        var first = prompt.IndexOf(start, StringComparison.Ordinal);
        if (first < 0) return "";
        first += start.Length;
        var last = prompt.IndexOf(end, first, StringComparison.Ordinal);
        return last < 0 ? "" : prompt[first..last].Trim();
    }

    private static object ObjectiveView(string instructions) => new
    {
        utf8Bytes = Encoding.UTF8.GetByteCount(instructions),
        sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instructions))).ToLowerInvariant(),
        compilerCodes = Regex.Matches(instructions, @"\b(?:CS|MSB|NETSDK)\d{4}\b")
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).Take(16).ToArray(),
        brushesFeedbackMentioned = instructions.Contains("Brushes", StringComparison.Ordinal),
        mediaNamespaceMentioned = instructions.Contains("System.Windows.Media", StringComparison.Ordinal),
        saveMentioned = instructions.Contains("save", StringComparison.OrdinalIgnoreCase),
        clearMentioned = instructions.Contains("clear", StringComparison.OrdinalIgnoreCase),
        persistenceMentioned = instructions.Contains("persist", StringComparison.OrdinalIgnoreCase) ||
            instructions.Contains("remember", StringComparison.OrdinalIgnoreCase) ||
            instructions.Contains("restart", StringComparison.OrdinalIgnoreCase),
        disposeMentioned = instructions.Contains("Dispose", StringComparison.Ordinal),
        viewLifetimeMentioned = instructions.Contains("Initialize", StringComparison.Ordinal) ||
            instructions.Contains("view factory", StringComparison.OrdinalIgnoreCase),
        bodyOmitted = true
    };

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    private static string? Text(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;
    private static long? Number(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.Number } property && property.TryGetInt64(out var number) ? number : null;
    private static bool? Boolean(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.True } ? true :
        Property(value, name) is { ValueKind: JsonValueKind.False } ? false : null;
    private static IEnumerable<JsonElement> Array(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];

    private static string? Diagnostic(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        // Diagnostics can retain endpoint/provider failures. Never echo bearer/key assignments or
        // an embedded source/JSON body; exact receipt IDs remain available for a separate bounded read.
        if (value.TrimStart().StartsWith('{') || value.Contains("-----BEGIN", StringComparison.Ordinal))
            return "[structured or sensitive diagnostic omitted]";
        value = Regex.Replace(value, @"(?i)\bBearer\s+\S+", "Bearer [redacted]");
        value = Regex.Replace(value, @"(?i)\b(api[ _-]?key|authorization|token|password|secret)\b\s*[:=]\s*\S+", "$1=[redacted]");
        value = Regex.Replace(value, @"\bsk-[A-Za-z0-9_-]{12,}", "[redacted]");
        value = Regex.Replace(value, @"https?://\S+", "[endpoint omitted]", RegexOptions.IgnoreCase);
        return value.Length <= 768 ? value : value[..768] + " [limited]";
    }
}
