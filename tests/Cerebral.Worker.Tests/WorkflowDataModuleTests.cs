using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Pure transitions and a real Engine/SQLite store with a deterministic typed tool handler.
/// These fixtures never call an actual tool provider, load a model, or alter installed Source state.</summary>
internal static class WorkflowDataModuleTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Project = "project-fixture";

    public static async Task RunAsync()
    {
        var assertions = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: " + label);
            assertions++; Console.WriteLine("PASS: " + label);
        }
        var module = new WorkflowDataModule();
        var definition = module.Workflows.Single();
        Check(module.Id == "cerebral.workflow-data" && module.Commands.Count == 0 &&
            definition.Descriptor.Id == "cerebral.workflow-data:execute" && definition.Descriptor.Version == "1.0",
            "data interpreter is one fixed definition and contributes no provider/model handlers");
        var edition = Edition();
        var run = Run(edition);
        var first = definition.Advance(run);
        var firstCommand = (InvokeWorkflowCommand)first.Next;
        var firstArguments = firstCommand.Arguments.Deserialize<WorkerWorkflowEffects.ToolArguments>(Json)!;
        Check(firstCommand.CommandId == WorkerWorkflowEffects.ToolCommand && firstCommand.CommandVersion == 1 &&
            firstArguments.Name == "file.read" && firstArguments.Arguments.GetProperty("path").GetString() == "fixture.txt" &&
            firstArguments.Arguments.GetProperty("literal").GetString() == "$input is ordinary string data",
            "first transition resolves only explicit references and uses existing host-tool command v1");
        Check(run.Submission.RequestId != run.Submission.Input.GetProperty("sourceRequestId").GetString() &&
            first.Next is InvokeWorkflowCommand, "source assignment ID is separate from new engine execution ID");

        Check(Failed(definition.Advance(run with { Submission = run.Submission with { SettingsSnapshot = Data(new { }) } })),
            "input cannot substitute for a missing host-frozen edition");
        Check(Failed(definition.Advance(run with { Submission = run.Submission with { ProjectId = "foreign" } })),
            "frozen edition cannot cross the host project binding");
        var input = run.Submission.Input.Deserialize<WorkflowDataRunInput>(Json)!;
        Check(Failed(definition.Advance(run with { Submission = run.Submission with { Input = Data(input with { Hash = new string('a', 64) }) } })),
            "changed input hash is rejected before dispatch");
        Check(Failed(definition.Advance(run with { Submission = run.Submission with { Input = Data(input with { Id = "another.edition" }) } })),
            "input edition identity must match the host-frozen exact edition");
        var originalEntry = run.Submission.SettingsSnapshot.GetProperty("workflowData").Deserialize<WorkflowDataEntry>(Json)!;
        var alteredEntry = originalEntry with { Edition = edition with { Description = "Changed after admission" } };
        Check(Failed(definition.Advance(run with { Submission = run.Submission with { SettingsSnapshot = Data(new { workflowData = alteredEntry }) } })),
            "frozen content alteration with the old hash is rejected");
        Check(Failed(definition.Advance(run with { Submission = run.Submission with { Input = Data(input with { Arguments = Data(new { path = 7 }) }) } })),
            "edition input schema rejects incompatible stage input before dispatch");
        var externalEditionInput = Data(new { input.SourceRequestId, input.Id, input.Version, input.Hash, input.Arguments, workflowData = originalEntry });
        Check(Failed(definition.Advance(run with { Submission = run.Submission with { Input = externalEditionInput } })),
            "strict fixed run schema rejects a caller-supplied workflowData edition");
        var duplicateInput = Parse(run.Submission.Input.GetRawText().TrimEnd()[..^1] + ",\"hash\":\"" + input.Hash + "\"}");
        Check(Failed(definition.Advance(run with { Submission = run.Submission with { Input = duplicateInput } })),
            "duplicate run input properties cannot create ambiguous authority");

        var firstResult = Tool("file.read", "first exact content");
        var returned = Returned(run, first, firstResult);
        var second = definition.Advance(returned);
        var secondCommand = (InvokeWorkflowCommand)second.Next;
        var secondArgs = secondCommand.Arguments.Deserialize<WorkerWorkflowEffects.ToolArguments>(Json)!;
        Check(secondArgs.Name == "file.inspect" && secondArgs.Arguments.GetProperty("prior").GetString() == "first exact content" &&
            JsonElement.DeepEquals(second.State.GetProperty("results").GetProperty("first"), firstResult),
            "later stage selects exact completed standard ToolData and retains the whole result");
        var secondResult = Tool("file.inspect", "second exact content");
        var complete = definition.Advance(Returned(run, second, secondResult));
        Check(complete.Next is CompleteWorkflow { Success: true } success &&
            success.Result.GetProperty("first").GetString() == "first exact content" &&
            success.Result.GetProperty("last").GetString() == "second exact content" &&
            complete.State.GetProperty("index").GetInt32() == 2,
            "all confirmed stages produce only the output template after output-schema validation");

        foreach (var state in new[] { WorkflowEffectState.Failed, WorkflowEffectState.Cancelled, WorkflowEffectState.Unknown })
            Check(Failed(definition.Advance(Returned(run, first, firstResult, state))),
                "non-successful " + state + " result never executes a later stage");
        Check(Failed(definition.Advance(Returned(run, first, null))), "missing successful result cannot advance");
        Check(Failed(definition.Advance(returned with { Signal = new(WorkflowSignalKind.Start, WorkflowContract.EmptyObject) })),
            "a result without the exact EffectResult signal cannot advance");
        Check(Failed(definition.Advance(returned with { Effect = returned.Effect! with { CommandId = WorkerWorkflowEffects.GenerateCommand } })),
            "a model result cannot substitute for the frozen tool stage");
        Check(Failed(definition.Advance(returned with { Effect = returned.Effect! with { CommandVersion = 2 } })),
            "a different host command version cannot satisfy the stage");
        Check(Failed(definition.Advance(returned with { Effect = returned.Effect! with { Arguments = Data(new { name = "file.read", arguments = new { path = "changed" } }) } })),
            "matching tool name with changed stage arguments is rejected");
        Check(Failed(definition.Advance(Returned(run, first, Tool("file.inspect", "wrong")))),
            "standard ToolData must echo the frozen stage tool name");
        Check(Failed(definition.Advance(Returned(run, first, Data(new { name = "file.read", content = "missing flags" })))),
            "missing standard ToolData fields cannot masquerade as complete output");
        Check(Failed(definition.Advance(Returned(run, first, Tool("file.read", "partial", truncated: true)))),
            "truncated output is retained by the engine effect but cannot satisfy a data stage");
        var completeEvidence = new ToolExecutionEvidence(ToolExecutionOutcome.Succeeded, "fixture.provider",
            "fixture-request", "fixture-job", "done", Partial: false);
        var completeEvidenceData = CerebralJson.Element(new WorkerWorkflowEffects.ToolData("file.read", "complete evidence", false, completeEvidence));
        Check(definition.Advance(Returned(run, first, completeEvidenceData)).Next is InvokeWorkflowCommand,
            "real host enum-string serialization with explicit complete successful evidence remains supported");
        var partialEvidence = completeEvidence with { Partial = true };
        var partialEvidenceData = CerebralJson.Element(new WorkerWorkflowEffects.ToolData("file.read", "explicitly partial fixture output", false, partialEvidence));
        Check(Failed(definition.Advance(Returned(run, first, partialEvidenceData))),
            "successful effect with Partial evidence and no truncation cannot advance");
        foreach (var outcome in new[] { ToolExecutionOutcome.Failed, ToolExecutionOutcome.Rejected, ToolExecutionOutcome.Cancelled, ToolExecutionOutcome.Unknown })
        {
            var conflicting = CerebralJson.Element(new WorkerWorkflowEffects.ToolData("file.read", "conflicting outcome", false,
                completeEvidence with { Outcome = outcome }));
            Check(Failed(definition.Advance(Returned(run, first, conflicting))),
                "successful effect cannot override explicit non-successful " + outcome + " evidence");
        }
        var missingOutcomeData = Data(new { name = "file.read", content = "missing outcome", truncated = false,
            evidence = new { providerId = "fixture.provider", partial = false }, followUpRequired = false });
        Check(Failed(definition.Advance(Returned(run, first, missingOutcomeData))),
            "missing explicit evidence outcome cannot default to successful enum zero");
        var aliasedEvidence = Parse("""{"name":"file.read","content":"ambiguous outcome","truncated":false,"evidence":{"outcome":"failed","Outcome":"succeeded","providerId":"fixture.provider","partial":false},"followUpRequired":false}""");
        Check(Failed(definition.Advance(Returned(run, first, aliasedEvidence))),
            "differently cased evidence fields cannot overwrite the explicit failed outcome");
        var partialPrefixResult = CerebralJson.Element(new WorkerWorkflowEffects.ToolData("file.read", "first exact content", false, partialEvidence));
        var invalidPrefix = Returned(run, second, secondResult) with
        {
            State = Data(new { index = 1, results = new Dictionary<string, JsonElement> { ["first"] = partialPrefixResult } })
        };
        Check(Failed(definition.Advance(invalidPrefix)), "recovered completed-stage prefix rejects explicit partial evidence");
        Check(Failed(definition.Advance(Returned(run, first, Tool("file.read", "follow up", followUp: true)))),
            "follow-up-required tool result does not falsely complete a stage");
        var generation = new ToolGenerationRequest("Fixture generation is not executed.", "file.write",
            Data(new { path = "fixture.txt" }), MaxTokens: 128);
        Check(Failed(definition.Advance(Returned(run, first, Data(new WorkerWorkflowEffects.ToolData("file.read", "request", false, null, Generation: generation))))),
            "model-generation request is explicitly unsupported rather than starting inference");
        Check(Failed(definition.Advance(Returned(run, first, Data(new WorkerWorkflowEffects.ToolData("file.read", "extra requirements", false, null,
            RequiredSuccessfulTools: ["file.write"]))))), "additional required-success tools cannot expand the edition");
        Check(Failed(definition.Advance(run with { CancelRequested = true })), "owner cancellation yields failed pure completion without another effect");
        Check(Failed(definition.Advance(run with { State = Data(new { index = 1, results = new { } }) })),
            "saved index must equal an exact completed-stage prefix");

        var invalidOutput = Edition() with { OutputSchema = Parse("""{"type":"integer"}""") };
        var badOutputRun = Run(invalidOutput);
        var badFirst = definition.Advance(badOutputRun);
        var badSecond = definition.Advance(Returned(badOutputRun, badFirst, firstResult));
        Check(Failed(definition.Advance(Returned(badOutputRun, badSecond, secondResult))),
            "edition output schema prevents successful completion with an incompatible output value");
        var largeFirst = definition.Advance(Returned(run, first, Tool("file.read", new string('x', 70_000))));
        Check(largeFirst.Next is InvokeWorkflowCommand, "bounded complete result remains available to a subsequent data stage");
        Check(Failed(definition.Advance(Returned(run, largeFirst, Tool("file.inspect", new string('y', 70_000))))),
            "aggregate retained results exceeding the state budget fail without truncating into success");

        var testParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.Combine(testParent, "cerebral-workflow-data-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalogStore = new StateStore(Path.Combine(root, "catalog"));
            var catalog = new WorkflowDataCatalog(catalogStore);
            var registered = catalog.Register(Project, edition);
            Check(registered.Created && registered.Entry.Hash == WorkflowDataCatalog.Hash(edition),
                "private SQLite catalog records the exact fixture edition before admission");
            var journalPath = Path.Combine(root, "engine.db");
            var store = new WorkflowStore(new EventJournal(journalPath));
            var handler = new FixtureToolHandler();
            var registry = new WorkflowRegistry([module, new FixtureHostModule(handler)]);
            var runtime = new WorkflowRuntime(store, registry, (_, _) => true);
            var accepted = store.Submit(Submission(edition, "complete"));
            var finished = await runtime.RunAsync(accepted.Id);
            Check(finished.Status == WorkflowStatus.Completed && handler.Executions == 2 &&
                finished.Result?.GetProperty("last").GetString() == "inspect:read:fixture.txt",
                "real Engine and SQLite persist two fixture tool effects and the resolved output");
            Check((await runtime.RunAsync(finished.Id)).Revision == finished.Revision && handler.Executions == 2,
                "reopening a completed fixture run never replays its tool effects");

            var recoveryRun = store.Submit(Submission(edition, "prepared-restart"));
            recoveryRun = store.TryClaim(recoveryRun.Id)!;
            var prepared = definition.Advance(recoveryRun);
            recoveryRun = store.ApplyStep(recoveryRun.Id, recoveryRun.Revision, prepared, handler.Descriptor);
            var savedEffectId = recoveryRun.Effect!.Id;
            var replacement = edition with { Version = "1.1", Description = "New immutable catalog edition",
                Output = Parse("""{"first":{"$stage":{"id":"second","pointer":"/content"}},"last":{"$stage":{"id":"second","pointer":"/content"}}}""") };
            var newEdition = catalog.Register(Project, replacement);
            Check(newEdition.Created && newEdition.Entry.Hash != registered.Entry.Hash &&
                store.Get(recoveryRun.Id).Submission.SettingsSnapshot.GetProperty("workflowData").GetProperty("hash").GetString() == registered.Entry.Hash,
                "registering a newer catalog edition cannot change already accepted frozen data");
            // Task-owned fault fixture only: remove exactly the original immutable edition row, preserving other records.
            // Production catalog has no removal API; execution must still rely on its host-frozen accepted snapshot.
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(catalogStore.DirectoryPath, "cerebral.db"), Mode = SqliteOpenMode.ReadWrite
            }.ToString()))
            {
                connection.Open(); using var remove = connection.CreateCommand();
                remove.CommandText = "DELETE FROM records WHERE kind='workflow-data' AND json_extract(body,'$.hash')=$hash;";
                remove.Parameters.AddWithValue("$hash", registered.Entry.Hash);
                Check(remove.ExecuteNonQuery() == 1, "private fault fixture removes exactly one original catalog edition row");
            }
            var catalogUnavailable = false;
            try { _ = catalog.Get(Project, edition.Id, edition.Version, registered.Entry.Hash); }
            catch (KeyNotFoundException) { catalogUnavailable = true; }
            Check(catalogUnavailable && catalog.Get(Project, replacement.Id, replacement.Version, newEdition.Entry.Hash).Hash == newEdition.Entry.Hash,
                "original catalog row is unavailable while the separate new edition remains intact");
            var reopened = new WorkflowStore(new EventJournal(journalPath));
            reopened.RecoverInterrupted();
            var restarted = new WorkflowRuntime(reopened, new WorkflowRegistry([new WorkflowDataModule(), new FixtureHostModule(handler)]), (_, _) => true);
            var before = handler.Executions;
            var restored = await restarted.RunAsync(recoveryRun.Id);
            Check(restored.Status == WorkflowStatus.Completed && handler.Executions == before + 2 &&
                handler.EffectIds[before] == savedEffectId &&
                restored.Result?.GetProperty("first").GetString() == "read:fixture.txt",
                "SQLite restart executes the original prepared effect identity and frozen edition exactly once");

            var unknown = reopened.Submit(Submission(edition, "unknown"));
            unknown = reopened.TryClaim(unknown.Id)!;
            unknown = reopened.ApplyStep(unknown.Id, unknown.Revision, definition.Advance(unknown), handler.Descriptor);
            unknown = reopened.DispatchEffect(unknown.Id, unknown.Revision);
            var unknownEffect = unknown.Effect!.Id;
            reopened.RecoverInterrupted();
            before = handler.Executions;
            var held = await restarted.RunAsync(unknown.Id);
            Check(held.Status == WorkflowStatus.Waiting && held.Effect is { State: WorkflowEffectState.Unknown, ReplayPolicy: WorkflowReplayPolicy.Never } &&
                held.Effect.Id == unknownEffect && handler.Executions == before,
                "actual Engine holds an interrupted Never tool effect and performs no replay");

            handler.Outcome = new(WorkflowEffectState.Failed, Error: "Fixed fixture failure.");
            var failed = await restarted.RunAsync(reopened.Submit(Submission(edition, "failed-handler")).Id);
            Check(failed.Status == WorkflowStatus.Failed && handler.Executions == before + 1 &&
                failed.State.GetProperty("index").GetInt32() == 0,
                "actual Engine consumes a failed fixture receipt without dispatching the next stage");
            handler.Outcome = new(WorkflowEffectState.Succeeded, Tool("file.read", "partial fixture output", truncated: true));
            before = handler.Executions;
            var truncated = await restarted.RunAsync(reopened.Submit(Submission(edition, "truncated-handler")).Id);
            // Pure completion clears the current effect; its exact receipt remains in the separate durable receipt table.
            var truncatedEffectId = handler.EffectIds[before];
            var receiptStore = new WorkflowStore(new EventJournal(journalPath));
            var truncatedReceipt = receiptStore.GetEffectResult(truncated.Id, truncatedEffectId, out var receiptRunRevision);
            Check(truncated.Status == WorkflowStatus.Failed && handler.Executions == before + 1 &&
                truncated.Effect is null && truncated.State.GetProperty("index").GetInt32() == 0 &&
                truncated.Result?.GetProperty("success").GetBoolean() == false &&
                truncatedReceipt.State == WorkflowEffectState.Succeeded &&
                truncatedReceipt.Data?.GetProperty("name").GetString() == "file.read" &&
                truncatedReceipt.Data?.GetProperty("truncated").GetBoolean() == true &&
                truncatedReceipt.Data?.GetProperty("content").GetString() == "partial fixture output" &&
                receiptRunRevision == truncated.Revision,
                "actual Engine preserves the exact truncated fixture receipt after clearing the current effect and rejects data completion");

            handler.Outcome = new(WorkflowEffectState.Succeeded, partialEvidenceData);
            before = handler.Executions;
            var partial = await restarted.RunAsync(reopened.Submit(Submission(edition, "partial-evidence-handler")).Id);
            var partialEffectId = handler.EffectIds[before];
            var partialReceipt = new WorkflowStore(new EventJournal(journalPath)).GetEffectResult(partial.Id, partialEffectId, out var partialRunRevision);
            Check(partial.Status == WorkflowStatus.Failed && handler.Executions == before + 1 &&
                partial.Effect is null && partial.State.GetProperty("index").GetInt32() == 0 &&
                partial.Result?.GetProperty("success").GetBoolean() == false &&
                partialReceipt.State == WorkflowEffectState.Succeeded && partialReceipt.Data is { } savedPartial &&
                JsonElement.DeepEquals(savedPartial, partialEvidenceData) &&
                savedPartial.GetProperty("truncated").GetBoolean() == false &&
                savedPartial.GetProperty("evidence").GetProperty("partial").GetBoolean() &&
                partialRunRevision == partial.Revision,
                "actual Engine retains exact successful-but-partial fixture evidence and the data module fails without executing a later stage");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var cleanupPath = Path.GetFullPath(root);
            var cleanupParent = Path.GetDirectoryName(cleanupPath);
            if (!Path.IsPathFullyQualified(root) || cleanupParent is null ||
                !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cleanupParent)), testParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The fixture cleanup path escaped its exact temporary parent.");
            Directory.Delete(cleanupPath, recursive: true);
        }
        Console.WriteLine($"Workflow data module: {assertions} pure/Engine-SQLite fixture assertions; no actual provider, model, installed Source or owner settings calls.");
    }

    private static WorkflowDataEdition Edition() => new(WorkflowDataCatalog.Schema, "fixture.linear", "1.0",
        "Two deterministic typed fixture stages.",
        Parse("""{"type":"object","properties":{"path":{"type":"string","minLength":1,"maxLength":128}},"required":["path"],"additionalProperties":false}"""),
        Parse("""{"type":"object","properties":{"first":{"type":"string"},"last":{"type":"string"}},"required":["first","last"],"additionalProperties":false}"""),
        ["files"],
        [
            new("first", "file.read", Parse("""{"path":{"$input":"/path"},"literal":"$input is ordinary string data"}""")),
            new("second", "file.inspect", Parse("""{"path":{"$input":"/path"},"prior":{"$stage":{"id":"first","pointer":"/content"}}}"""))
        ],
        Parse("""{"first":{"$stage":{"id":"first","pointer":"/content"}},"last":{"$stage":{"id":"second","pointer":"/content"}}}"""));

    private static WorkflowSubmission Submission(WorkflowDataEdition edition, string requestId = "execution")
    {
        var hash = WorkflowDataCatalog.Hash(edition);
        return new(requestId, "data-conversation-" + requestId, "fixture", WorkflowDataModule.DefinitionId, WorkflowDataModule.DefinitionVersion,
            "Execute the isolated data fixture.", null,
            Data(new WorkflowDataRunInput("source-assignment", edition.Id, edition.Version, hash, Data(new { path = "fixture.txt" }))),
            Data(new { workflowData = new WorkflowDataEntry(Project, hash, edition) })) { ProjectId = Project, StepLimit = 8 };
    }
    private static WorkflowRun Run(WorkflowDataEdition edition) => new() { Id = "execution", Submission = Submission(edition), Status = WorkflowStatus.Running };
    private static WorkflowRun Returned(WorkflowRun original, WorkflowStep prepared, JsonElement? result,
        WorkflowEffectState state = WorkflowEffectState.Succeeded)
    {
        var command = (InvokeWorkflowCommand)prepared.Next;
        return original with { State = prepared.State.Clone(), Signal = new(WorkflowSignalKind.EffectResult, WorkflowContract.EmptyObject),
            Effect = new("fixture-effect", command.CommandId, command.CommandVersion, command.Arguments.Clone(), WorkflowReplayPolicy.Never, state, result?.Clone()) };
    }
    private static JsonElement Tool(string name, string content, bool truncated = false, bool followUp = false) =>
        Data(new WorkerWorkflowEffects.ToolData(name, content, truncated, null, followUp));
    private static bool Failed(WorkflowStep step) => step.Next is CompleteWorkflow { Success: false };
    private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, Json);
    private static JsonElement Parse(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    private sealed class FixtureHostModule(FixtureToolHandler handler) : IWorkflowModule
    {
        public string Id => WorkerWorkflowEffects.ModuleId;
        public IReadOnlyList<IWorkflowDefinition> Workflows => [];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [handler];
    }
    private sealed class FixtureToolHandler : IWorkflowCommandHandler
    {
        public int Executions { get; private set; }
        public List<string> EffectIds { get; } = [];
        public WorkflowCommandResult? Outcome { get; set; }
        public WorkflowCommandDescriptor Descriptor { get; } = new(WorkerWorkflowEffects.ToolCommand, 1,
            "Deterministic fixture only; no actual tool dispatch.",
            Parse("""{"type":"object","properties":{"name":{"type":"string"},"arguments":{"type":"object"},"workingDirectory":{}},"required":["name","arguments"],"additionalProperties":false}"""),
            Parse("""{"type":"object"}"""), ["files"], WorkflowReplayPolicy.Never);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Executions++; EffectIds.Add(context.EffectId);
            if (Outcome is not null) return Task.FromResult(Outcome);
            var args = context.Arguments.Deserialize<WorkerWorkflowEffects.ToolArguments>(Json)!;
            var content = args.Name == "file.read" ? "read:" + args.Arguments.GetProperty("path").GetString() :
                "inspect:" + args.Arguments.GetProperty("prior").GetString();
            return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Succeeded, Tool(args.Name, content), ProviderReceiptId: context.EffectId));
        }
    }
}
