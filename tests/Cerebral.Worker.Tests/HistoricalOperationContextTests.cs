using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Isolated journal/host projection and deterministic policy checks. No provider calls or inference.</summary>
public static class HistoricalOperationContextTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-historical-operations-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Historical operations: " + message); checks++; }
        try
        {
            var store = new StateStore(Path.Combine(root, "data")); var configuration = new Configuration(store); configuration.Get();
            var profile = new AgentProfile { Id = "owner", Name = "Fixture", ModelId = "fixture-model", AutomaticCompaction = false,
                FollowsModelBudgets = false, ContextTokens = 16384, ResponseTokens = 512,
                AllowedRoots = [root], AllowedTools = ["*"], Capabilities = ["files"] };
            store.Put("agent", profile.Id, profile);
            store.Put("model", profile.ModelId, new ModelRecord { Id = profile.ModelId, Path = Path.Combine(root, "fixture.gguf"),
                AutomaticContextSizing = false, ContextTokens = 16384, MaximumContextTokens = 16384, RecommendedContextTokens = 16384 });
            var journal = new EventJournal(Path.Combine(store.DirectoryPath, "engine.db")); var workflows = new WorkflowStore(journal);
            var tools = new Tools(); var models = new Models(); var bridge = new WorkerWorkflowEffects(store, configuration, models, tools);
            _ = new WorkflowRegistry([bridge]);
            var history = bridge.Commands.Single(handler => handler.Descriptor.Id == WorkerWorkflowEffects.HistoryCommand);
            var descriptor = bridge.Commands.Single(handler => handler.Descriptor.Id == WorkerWorkflowEffects.ToolCommand).Descriptor;
            async Task<WorkflowRun> Submit(string id, string chat) => workflows.Submit(new(id, chat, profile.Id,
                "fixture:conversation", "1.0.0", "EXACT CURRENT REQUEST", null, J(new { }),
                await WorkerExecutionSnapshot.CaptureAsync(store, profile, configuration, tools, [], default), StepLimit: 512));
            WorkflowRun Outcome(WorkflowRun run, string name, string content, WorkflowEffectState state = WorkflowEffectState.Succeeded,
                bool truncated = false, bool? partial = null)
            {
                var claimed = workflows.TryClaim(run.Id)!;
                var prepared = workflows.ApplyStep(run.Id, claimed.Revision, new(WorkflowContract.EmptyObject,
                    new InvokeWorkflowCommand(WorkerWorkflowEffects.ToolCommand, 1, J(new WorkerWorkflowEffects.ToolArguments(name,
                        J(new { requestedCanary = "REQUEST-IS-NOT-PROOF" }))))), descriptor);
                var dispatched = workflows.DispatchEffect(run.Id, prepared.Revision);
                var outcome = state switch { WorkflowEffectState.Succeeded => ToolExecutionOutcome.Succeeded,
                    WorkflowEffectState.Failed => ToolExecutionOutcome.Failed, _ => ToolExecutionOutcome.Unknown };
                return workflows.CompleteEffect(run.Id, dispatched.Effect!.Id, new(state,
                    J(new WorkerWorkflowEffects.ToolData(name, content, truncated,
                        new(outcome, "fixture.provider", dispatched.Effect.Id, Partial: partial, ExitCode: state == WorkflowEffectState.Succeeded ? 0 : null)))), 1);
            }
            void Finish(WorkflowRun run)
            {
                var claimed = workflows.TryClaim(run.Id)!;
                workflows.ApplyStep(run.Id, claimed.Revision, new(WorkflowContract.EmptyObject,
                    new CompleteWorkflow(J(new { summary = "ASSISTANT-CLAIM-IS-NOT-PROOF" }))));
            }
            async Task<WorkerWorkflowEffects.HistoryData> Page(WorkflowRun run, object args)
            {
                var result = await history.ExecuteAsync(new("history-fixture", run.Submission, J(args)) { RunId = run.Id }, default);
                if (result.State != WorkflowEffectState.Succeeded) throw new InvalidOperationException(result.Error);
                WorkflowSchema.Validate(result.Data!.Value, history.Descriptor.OutputSchema);
                return result.Data.Value.Deserialize<WorkerWorkflowEffects.HistoryData>(CerebralJson.Options)!;
            }
            JsonDocument ArtifactBody(ContextContribution contribution)
            {
                var payload = Text(contribution);
                var document = JsonDocument.Parse(payload[(payload.IndexOf('\n') + 1)..]);
                var indexedRows = document.RootElement.GetProperty("rows").EnumerateArray()
                    .ToDictionary(row => row.GetProperty("eventId").GetString()!, StringComparer.Ordinal);
                var references = document.RootElement.GetProperty("recordedArtifacts").EnumerateArray().ToArray();
                Check(references.Length <= 8 && references.All(reference =>
                    indexedRows.TryGetValue(reference.GetProperty("eventId").GetString()!, out var row) &&
                    row.TryGetProperty("artifactCoordinates", out _) &&
                    (!reference.TryGetProperty("uncertainAfterEventId", out var barrier) || barrier.ValueKind == JsonValueKind.Null ||
                     indexedRows.ContainsKey(barrier.GetString()!))),
                    "artifact references and uncertainty handles resolve only to whole retained rows");
                Check(references.All(reference => !reference.TryGetProperty("path", out _) &&
                    !reference.TryGetProperty("sha256", out _)),
                    "the index does not duplicate exact paths or hashes from its evidence rows");
                return document;
            }
            JsonElement ReferenceForEffect(JsonElement body, string effect)
            {
                var indexedRows = body.GetProperty("rows").EnumerateArray().ToDictionary(
                    row => row.GetProperty("eventId").GetString()!, StringComparer.Ordinal);
                return body.GetProperty("recordedArtifacts").EnumerateArray().Single(reference =>
                    indexedRows[reference.GetProperty("eventId").GetString()!].GetProperty("effectId").GetString() == effect);
            }
            bool Uncertain(JsonElement reference) => reference.TryGetProperty("uncertainAfterEventId", out var barrier) && barrier.ValueKind != JsonValueKind.Null;
            JsonElement RowForEffect(JsonElement body, string effect) => body.GetProperty("rows").EnumerateArray()
                .Single(row => row.GetProperty("effectId").GetString() == effect);
            var hash = new string('a', 64); var firstPath = Path.Combine(root, "first.txt"); var secondPath = Path.Combine(root, "second.txt");
            var prior = await Submit("prior", "chat");
            prior = Outcome(prior, "file.write", JsonSerializer.Serialize(new { path = firstPath, bytes = 4, sha256 = hash, lines = 1, words = 1 }));
            var firstEffect = prior.Effect!.Id;
            prior = Outcome(prior, "file.write", "FAILED-BODY-CANARY " + secondPath, WorkflowEffectState.Failed);
            var failedEffect = prior.Effect!.Id;
            prior = Outcome(prior, "file.copy", JsonSerializer.Serialize(new { sourcePath = firstPath, destinationPath = secondPath, bytes = 4, sha256 = hash }));
            var copyEffect = prior.Effect!.Id;
            prior = Outcome(prior, "plugin.projects", "HISTORICAL-INVENTORY-CANARY");
            prior = Outcome(prior, "file.write", JsonSerializer.Serialize(new { path = Path.Combine(root, "truncated.txt"), bytes = 3, sha256 = hash }), truncated: true);
            prior = Outcome(prior, "file.write", JsonSerializer.Serialize(new { path = Path.Combine(root, "partial.txt"), bytes = 3, sha256 = hash }), partial: true);
            prior = Outcome(prior, "file.write", "{\"path\":\"C:\\\\bad\",\"path\":\"C:\\\\other\",\"bytes\":3,\"sha256\":\"" + hash + "\"}");
            prior = Outcome(prior, "file.mkdir", JsonSerializer.Serialize(new { path = Path.Combine(root, "directory"), ensured = true, existedBefore = true }));
            Finish(prior);
            var current = await Submit("current", "chat"); var page = await Page(current, new { limit = 1 });
            var summary = page.Context.Contributions.Single(source => source.Category == "historical-operations");
            var text = Text(summary); using var body = JsonDocument.Parse(text[(text.IndexOf('\n') + 1)..]);
            var rows = body.RootElement.GetProperty("rows").EnumerateArray().ToArray();
            Check(summary.Requirement == ContextRequirement.Required && summary.BeforeCurrentInput && !summary.Complete &&
                summary.ExactReadHandle is null && summary.Owner == WorkerWorkflowEffects.ModuleId && summary.SourceRevision == current.Submission.ConversationSequence,
                "historical summary is a host-owned Required pre-current contribution with no synthetic exact-read handle");
            Check(JsonSerializer.SerializeToUtf8Bytes(summary, TransportJson).Length <= 8 * 1024 && rows.Length == 8,
                $"the complete serialized contribution is bounded and retains independent operations (rows={rows.Length}, bytes={JsonSerializer.SerializeToUtf8Bytes(summary, TransportJson).Length})");
            Check(rows.Single(row => row.GetProperty("effectId").GetString() == firstEffect).GetProperty("artifactCoordinates").GetProperty("path").GetString() == firstPath &&
                rows.Single(row => row.GetProperty("effectId").GetString() == copyEffect).GetProperty("artifactCoordinates").GetProperty("destinationPath").GetString() == secondPath &&
                rows.Single(row => row.GetProperty("effectId").GetString() == failedEffect).GetProperty("recordedState").GetString() == "Failed",
                "confirmed typed results retain exact artifact coordinates and a distinct failed effect");
            Check(!text.Contains("REQUEST-IS-NOT-PROOF", StringComparison.Ordinal) && !text.Contains("ASSISTANT-CLAIM-IS-NOT-PROOF", StringComparison.Ordinal) &&
                !text.Contains("HISTORICAL-INVENTORY-CANARY", StringComparison.Ordinal) && !text.Contains("FAILED-BODY-CANARY", StringComparison.Ordinal) &&
                !text.Contains("truncated.txt", StringComparison.Ordinal) && !text.Contains("partial.txt", StringComparison.Ordinal) && !text.Contains("C:\\bad", StringComparison.Ordinal),
                "requests, assistant claims, old inventory and partial/malformed artifact bodies do not become operation proof");
            using (var indexed = ArtifactBody(summary))
                Check(indexed.RootElement.GetProperty("recordedArtifacts").GetArrayLength() == 3 &&
                    Uncertain(ReferenceForEffect(indexed.RootElement, firstEffect)) &&
                    RowForEffect(indexed.RootElement, copyEffect).GetProperty("artifactCoordinates").GetProperty("destinationPath").GetString() == secondPath &&
                    indexed.RootElement.GetProperty("artifactCoverageIncomplete").GetBoolean(),
                    "independent file/destination/ensure references retain historical facts and declare unscoped later uncertainty");
            var originalEventId = rows.Single(row => row.GetProperty("effectId").GetString() == firstEffect).GetProperty("eventId").GetString()!;
            var exact = await history.ExecuteAsync(new("exact-fixture", current.Submission, J(new { eventId = originalEventId, offset = 0 })) { RunId = current.Id }, default);
            Check(exact.State == WorkflowEffectState.Succeeded && exact.Data!.Value.GetProperty("json").GetString()!.Contains(firstEffect, StringComparison.Ordinal),
                "listed real event IDs retrieve retained evidence through unchanged history fragments");
            var nextPage = await Page(current, new { beforeSequence = page.BeforeSequence, limit = 1 });
            Check(nextPage.Context.Contributions.All(source => source.Category != "historical-operations"),
                "later history pages do not repeat the required summary");
            checks += ConversationHarnessTests.HistoricalOperationsPreserveGuards(summary, current.Submission.ConversationSequence);

            var checkpoint = new ContextCheckpoint("retained-checkpoint", current.Submission.ConversationSequence, 0,
                [new(originalEventId, rows.Single(row => row.GetProperty("effectId").GetString() == firstEffect).GetProperty("sequence").GetInt64()),
                 new(summary.Id, summary.SourceRevision)], "Earlier sources were compacted.", "fixture-compaction", BeforeCurrentInput: true);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = journalPath(store) }.ToString()))
            {
                connection.Open(); using var save = connection.CreateCommand();
                save.CommandText = "INSERT INTO conversation_context_checkpoints(conversation_id,source_revision,run_id,effect_id,checkpoint,adopted_at) VALUES($chat,$revision,$run,$effect,$checkpoint,$at)";
                save.Parameters.AddWithValue("$chat", "chat"); save.Parameters.AddWithValue("$revision", current.Submission.ConversationSequence);
                save.Parameters.AddWithValue("$run", prior.Id); save.Parameters.AddWithValue("$effect", "fixture-compaction");
                save.Parameters.AddWithValue("$checkpoint", JsonSerializer.Serialize(checkpoint, CerebralJson.Options));
                save.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); save.ExecuteNonQuery();
            }
            var compacted = await Page(current, new { limit = 1 });
            var regenerated = compacted.Context.Contributions.Single(source => source.Category == "historical-operations");
            Check(compacted.Context.Checkpoint is not null && regenerated.Id != summary.Id &&
                Text(regenerated).Contains(originalEventId, StringComparison.Ordinal) && Text(regenerated).Contains("recordedArtifacts", StringComparison.Ordinal),
                "compaction coverage and synthetic ID collisions do not erase immutable operation evidence");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = journalPath(store) }.ToString()))
            {
                connection.Open(); using var exclude = connection.CreateCommand();
                exclude.CommandText = "UPDATE conversation_events SET payload=json_set(payload,'$.excludedFromContext',json('true')) WHERE event_id=$id";
                exclude.Parameters.AddWithValue("$id", originalEventId); exclude.ExecuteNonQuery();
            }
            var excluded = (await Page(current, new { limit = 1 })).Context.Contributions.Single(source => source.Category == "historical-operations");
            Check(!Text(excluded).Contains(originalEventId, StringComparison.Ordinal) && !Text(excluded).Contains(firstEffect, StringComparison.Ordinal),
                "a fresh historical summary honors source exclusions even with a retained checkpoint");
            using (var excludedBody = ArtifactBody(excluded))
                Check(excludedBody.RootElement.GetProperty("artifactCoverageIncomplete").GetBoolean(),
                    "an excluded outcome exposes only a count gap, never its identity or content");
            workflows.Cancel(current.Id);
            var crowded = await Submit("crowded-prior", "crowded");
            var longPath = Path.Combine(root, new string('q', 1700));
            for (var i = 0; i < 20; i++) crowded = Outcome(crowded, "file.write", JsonSerializer.Serialize(new { path = longPath + i, bytes = 2, sha256 = hash }));
            Finish(crowded); var crowdedCurrent = await Submit("crowded-current", "crowded");
            var bounded = (await Page(crowdedCurrent, new { limit = 1 })).Context.Contributions.Single(source => source.Category == "historical-operations");
            Check(JsonSerializer.SerializeToUtf8Bytes(bounded, TransportJson).Length <= 8 * 1024 &&
                Text(bounded).Contains("omittedOutcomes", StringComparison.Ordinal) && !Text(bounded).Contains(longPath, StringComparison.Ordinal),
                "large coordinates are omitted whole and summary size/item omissions are declared");
            var empty = await Submit("empty", "empty");
            Check((await Page(empty, new { })).Context.Contributions.All(source => source.Category != "historical-operations"),
                "clean non-operation histories do not acquire an empty required summary");
            for (var i = 0; i < WorkflowStore.MaximumConversationOperationScan + 1; i++)
                journal.Append(new("window-" + i, "window", "earlier", 0, CerebralEventTypes.Message, new(EventActorKind.User),
                    [new TextContentPart("Earlier")], new(), new(EventSourceKind.User), DateTimeOffset.UtcNow), journal.GetRevision("window"));
            var window = await Submit("window-current", "window");
            var limited = (await Page(window, new { limit = 1 })).Context.Contributions.Single(source => source.Category == "historical-operations");
            Check(Text(limited).Contains("\"scanLimited\":true", StringComparison.Ordinal) && Text(limited).Contains("no operation occurred", StringComparison.Ordinal),
                "a scan-limited zero-row summary disclaims exhaustive operation knowledge");
            using (var crowdedBody = ArtifactBody(bounded))
                Check(crowdedBody.RootElement.GetProperty("artifactCoverageIncomplete").GetBoolean(),
                    "item, byte and whole-coordinate omissions cannot claim complete coverage");

            var reduced = await Submit("reduced-prior", "reduced");
            reduced = Outcome(reduced, "file.write", JsonSerializer.Serialize(new { path = firstPath, bytes = 4, sha256 = hash }));
            var obsoleteWrite = reduced.Effect!.Id;
            reduced = Outcome(reduced, "file.edit", JsonSerializer.Serialize(new { path = firstPath, bytes = 5, sha256 = new string('b', 64) }));
            var latestEdit = reduced.Effect!.Id;
            reduced = Outcome(reduced, "file.copy", JsonSerializer.Serialize(new { sourcePath = firstPath, destinationPath = secondPath, bytes = 5, sha256 = new string('b', 64) }));
            var destinationCopy = reduced.Effect!.Id;
            reduced = Outcome(reduced, "file.mkdir", JsonSerializer.Serialize(new { path = Path.Combine(root, "ensure"), ensured = true, existedBefore = true }));
            reduced = Outcome(reduced, "file.mkdir", JsonSerializer.Serialize(new { path = Path.Combine(root, "ensure"), ensured = true, existedBefore = true }));
            var latestEnsure = reduced.Effect!.Id;
            Finish(reduced);
            var passiveCurrent = await Submit("passive-current", "reduced");
            using (var passiveBody = ArtifactBody((await Page(passiveCurrent, new { limit = 1 })).Context.Contributions.Single(source => source.Category == "historical-operations")))
            {
                var bodyValue = passiveBody.RootElement;
                Check(bodyValue.GetProperty("rows").GetArrayLength() == 5 && bodyValue.GetProperty("recordedArtifacts").GetArrayLength() == 3 &&
                    !Uncertain(ReferenceForEffect(bodyValue, latestEdit)) &&
                    RowForEffect(bodyValue, destinationCopy).GetProperty("artifactCoordinates").GetProperty("destinationPath").GetString() == secondPath &&
                    RowForEffect(bodyValue, latestEnsure).GetProperty("tool").GetString() == "file.mkdir" &&
                    bodyValue.GetProperty("recordedArtifacts").EnumerateArray().All(reference =>
                        bodyValue.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("eventId").GetString() == reference.GetProperty("eventId").GetString())
                            .GetProperty("effectId").GetString() != obsoleteWrite),
                    "latest write/edit, copy destination and repeated ensure are independent; passive reads do not refresh or invalidate target bytes");
            }
            workflows.Cancel(passiveCurrent.Id);
            reduced = await Submit("recovery-prior", "reduced");
            reduced = Outcome(reduced, "process.run", "{\"exitCode\":0}"); var processBarrier = reduced.Effect!.Id;
            reduced = Outcome(reduced, "file.write", JsonSerializer.Serialize(new { path = firstPath, bytes = 6, sha256 = new string('c', 64) }));
            var recoveredFirst = reduced.Effect!.Id; Finish(reduced);
            var recoveryCurrent = await Submit("recovery-current", "reduced");
            using (var recovered = ArtifactBody((await Page(recoveryCurrent, new { limit = 1 })).Context.Contributions.Single(source => source.Category == "historical-operations")))
            {
                var bodyValue = recovered.RootElement;
                var staleCopy = ReferenceForEffect(bodyValue, destinationCopy);
                var barrierRow = bodyValue.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("effectId").GetString() == processBarrier);
                Check(!Uncertain(ReferenceForEffect(bodyValue, recoveredFirst)) &&
                    Uncertain(staleCopy) &&
                    staleCopy.GetProperty("uncertainAfterEventId").GetString() == barrierRow.GetProperty("eventId").GetString(),
                    "exit-zero arbitrary execution is an unscoped barrier; later A success cannot repair B");
            }

            foreach (var passive in new[] { "file.read", "file.inspect", "file.list", "file.search", "math.calculate", "plugin.projects", "plugin.contract" })
            {
                var passiveChat = "passive-" + passive;
                var passiveRun = await Submit("prior-" + passiveChat, passiveChat);
                passiveRun = Outcome(passiveRun, "file.write", JsonSerializer.Serialize(new { path = firstPath, bytes = 4, sha256 = hash }));
                var retainedFile = passiveRun.Effect!.Id;
                passiveRun = Outcome(passiveRun, passive, "{}"); Finish(passiveRun);
                var passiveSnapshot = await Submit("current-" + passiveChat, passiveChat);
                using var passiveEvidence = ArtifactBody((await Page(passiveSnapshot, new { limit = 1 })).Context.Contributions.Single(source => source.Category == "historical-operations"));
                Check(!Uncertain(ReferenceForEffect(passiveEvidence.RootElement, retainedFile)),
                    "a fixed passive target-file tool leaves recorded evidence unchanged: " + passive);
            }

            var badMutations = new[]
            {
                (State: WorkflowEffectState.Unknown, Content: "unknown", Truncated: false, Partial: (bool?)null),
                (State: WorkflowEffectState.Failed, Content: "failed", Truncated: false, Partial: (bool?)true),
                (State: WorkflowEffectState.Succeeded, Content: JsonSerializer.Serialize(new { path = secondPath, bytes = 1, sha256 = hash }), Truncated: true, Partial: (bool?)null),
                (State: WorkflowEffectState.Succeeded, Content: JsonSerializer.Serialize(new { path = secondPath, bytes = 1, sha256 = hash }), Truncated: false, Partial: (bool?)true),
                (State: WorkflowEffectState.Succeeded, Content: "{\"path\":\"A\",\"path\":\"B\"}", Truncated: false, Partial: (bool?)null),
                (State: WorkflowEffectState.Succeeded, Content: JsonSerializer.Serialize(new { path = longPath, bytes = 1, sha256 = hash }), Truncated: false, Partial: (bool?)null)
            };
            for (var i = 0; i < badMutations.Length; i++)
            {
                var bad = badMutations[i]; var chat = "bad-mutation-" + i;
                var attempted = await Submit("prior-" + chat, chat);
                attempted = Outcome(attempted, "file.write", JsonSerializer.Serialize(new { path = firstPath, bytes = 4, sha256 = hash }));
                var confirmedFile = attempted.Effect!.Id;
                attempted = Outcome(attempted, "file.write", bad.Content, bad.State, bad.Truncated, bad.Partial);
                var uncertainMutation = attempted.Effect!.Id;
                ContextContribution badContribution;
                if (bad.State == WorkflowEffectState.Unknown)
                {
                    Check(attempted.Status == WorkflowStatus.Waiting && attempted.Effect?.State == WorkflowEffectState.Unknown,
                        "Unknown remains unresolved and blocks a new accepted request; the projection test never resumes it");
                    // Exercise this private projection at an exact test-only journal boundary.
                    // Do not bypass production admission or pretend a blocked continuation was accepted.
                    var boundary = attempted.Submission with { RequestId = "projection-" + attempted.Id,
                        ConversationSequence = journal.GetRevision(chat) };
                    badContribution = (ContextContribution)typeof(WorkerWorkflowEffects).GetMethod("HistoricalOperations",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                        .Invoke(null, [journal, boundary, Array.Empty<ContextContribution>(), null, CancellationToken.None])!;
                }
                else
                {
                    Finish(attempted);
                    var badCurrent = await Submit("current-" + chat, chat);
                    badContribution = (await Page(badCurrent, new { limit = 1 })).Context.Contributions.Single(source => source.Category == "historical-operations");
                }
                using var badBody = ArtifactBody(badContribution);
                var uncertainRow = badBody.RootElement.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("effectId").GetString() == uncertainMutation);
                var confirmedReference = ReferenceForEffect(badBody.RootElement, confirmedFile);
                Check(Uncertain(confirmedReference) &&
                    confirmedReference.GetProperty("uncertainAfterEventId").GetString() == uncertainRow.GetProperty("eventId").GetString() &&
                    badBody.RootElement.GetProperty("recordedArtifacts").GetArrayLength() == 1 &&
                    !uncertainRow.TryGetProperty("artifactCoordinates", out _),
                    "failed/Unknown/partial/truncated/duplicate/oversized results preserve only the earlier fact plus the uncertainty receipt");
            }
            var incompatible = await Submit("incompatible-prior", "incompatible");
            incompatible = Outcome(incompatible, "file.mkdir", JsonSerializer.Serialize(new { path = firstPath, ensured = true, existedBefore = true }));
            var directoryFact = incompatible.Effect!.Id;
            incompatible = Outcome(incompatible, "file.write", JsonSerializer.Serialize(new { path = firstPath, bytes = 4, sha256 = hash }));
            var fileFact = incompatible.Effect!.Id; Finish(incompatible);
            var incompatibleCurrent = await Submit("incompatible-current", "incompatible");
            using (var kinds = ArtifactBody((await Page(incompatibleCurrent, new { limit = 1 })).Context.Contributions.Single(source => source.Category == "historical-operations")))
                Check(kinds.RootElement.GetProperty("recordedArtifacts").GetArrayLength() == 2 &&
                    Uncertain(ReferenceForEffect(kinds.RootElement, directoryFact)) &&
                    !Uncertain(ReferenceForEffect(kinds.RootElement, fileFact)),
                    "different kinds at one exact path do not imply simultaneous applicability");

            var eight = await Submit("index-limit-prior", "index-limit");
            for (var i = 0; i < 12; i++) eight = Outcome(eight, "file.mkdir", JsonSerializer.Serialize(new { path = Path.Combine(root, "d" + i), ensured = true, existedBefore = true }));
            Finish(eight); var indexCurrent = await Submit("index-limit-current", "index-limit");
            var indexedContribution = (await Page(indexCurrent, new { limit = 1 })).Context.Contributions.Single(source => source.Category == "historical-operations");
            using (var indexBody = ArtifactBody(indexedContribution))
                Check(indexBody.RootElement.GetProperty("recordedArtifacts").GetArrayLength() is > 0 and <= 8 &&
                    indexBody.RootElement.GetProperty("artifactCoverageIncomplete").GetBoolean() &&
                    (indexBody.RootElement.GetProperty("omittedOutcomes").GetInt32() > 0 ||
                     indexBody.RootElement.GetProperty("omittedArtifactReferences").GetInt32() > 0) &&
                    JsonSerializer.SerializeToUtf8Bytes(indexedContribution, TransportJson).Length <= 8 * 1024,
                    "the serialized byte budget may retain fewer than eight references; every omission stays explicit");
            // Test the independent eight-entry reducer cap before the stricter envelope byte cap.
            var rowMethod = typeof(WorkerWorkflowEffects).GetMethod("HistoricalOperationRow",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var untrimmedRows = workflows.ReadConversationCommandOutcomes("index-limit",
                indexCurrent.Submission.ConversationSequence, WorkerWorkflowEffects.ToolCommand).Events
                .Select(item => (Dictionary<string, object?>)rowMethod.Invoke(null, [item])!).ToArray();
            object?[] reduceArguments = [untrimmedRows, 0];
            var reducedIndex = (Array)typeof(WorkerWorkflowEffects).GetMethod("HistoricalArtifactReferences",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, reduceArguments)!;
            Check(untrimmedRows.Length == 12 && reducedIndex.Length == 8 && (int)reduceArguments[1]! == 4,
                "the private reducer retains at most eight of twelve independent complete artifacts before envelope trimming");

            Check(tools.Executions == 0 && models.Preparations == 0, "history summary executes no semantic tool or model preparation");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(root).StartsWith("cerebral-historical-operations-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside owned historical-operation fixture.");
            Directory.Delete(root, true);
        }
        Console.WriteLine($"Historical operation context: {checks} isolated SQLite/deterministic-policy checks passed; no inference or live providers.");
    }
    private static string journalPath(StateStore store) => Path.Combine(store.DirectoryPath, "engine.db");
    private static readonly JsonSerializerOptions TransportJson = new(CerebralJson.Options)
        { WriteIndented = false, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);
    private static string Text(ContextContribution source) => string.Concat(source.Messages.SelectMany(message => message.Content).OfType<TextContentPart>().Select(part => part.Text));
    private sealed class Tools : IToolDispatcher
    {
        public int Executions;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) => Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        { Executions++; throw new InvalidOperationException("History must not execute tools."); }
    }
    private sealed class Models : IModelRuntimeAdapter
    {
        public int Preparations;
        public ModelAdapterDescriptor Descriptor { get; } = new("historical-fixture", "1.0.0", ["text", "structured"]);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        { Preparations++; throw new InvalidOperationException("History must not prepare a model."); }
    }
}
