using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

internal static partial class IdleCompactionTests
{
    private static async Task CheckpointSourceChecksAsync(Action<bool, string> check)
    {
        static Task<WorkflowCommandResult> Exact(Fixture f, WorkflowRun run, string id, int offset = 0) =>
            f.Bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.HistoryCommand).ExecuteAsync(
                new("checkpoint-read", run.Submission, CerebralJson.Element(
                    new WorkerWorkflowEffects.HistoryArguments(EventId: id, Offset: offset))) { RunId = run.Id }, default);

        static long Format(Fixture f)
        {
            using var connection = new SqliteConnection("Data Source=" + Path.Combine(f.Store.DirectoryPath, "engine.db") + ";Pooling=False");
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt64(command.ExecuteScalar());
        }

        using (var f = new Fixture())
        {
            await f.SeedAsync(large: false);
            var run = await f.SubmitAsync("normal-checkpoint");
            check(Format(f) == 6, "normal checkpoint dispatch starts in the legacy-compatible journal format");
            var original = f.Host.Store.ReadConversationGoal(ConversationWorkflowModule.DefinitionId, "idle-chat").Original;
            var descriptor = f.Bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.CompactCommand && x.Descriptor.Version == 1).Descriptor;
            var active = f.Host.Store.TryClaim(run.Id)!;
            active = f.Host.Store.ApplyStep(active.Id, active.Revision, new(WorkflowContract.EmptyObject,
                new InvokeWorkflowCommand(descriptor.Id, descriptor.Version, CerebralJson.Element(
                    new WorkerWorkflowEffects.GenerateArguments([], Context: new(run.Submission.ConversationSequence, 0, []))))), descriptor);
            active = f.Host.Store.DispatchEffect(active.Id, active.Revision);
            var checkpoint = new ContextCheckpoint(active.Effect!.Id, run.Submission.ConversationSequence, 0,
                [new(original.EventId, original.Sequence)], "Saved exact source attribution.", active.Effect.Id, true)
                { SourceLocators = [new(original.EventId, original.Sequence, "cerebral.host", "conversation", original.EventId, true)] };
            var completed = f.Host.Store.CompleteEffect(active.Id, active.Effect.Id,
                new(WorkflowEffectState.Succeeded, CerebralJson.Element(new { checkpoint })) { ConversationCheckpoint = checkpoint });
            check(Format(f) == 7, "normal adopted source maps mark the incompatible persisted checkpoint format");
            var adoption = f.Host.Journal.ReadExact("idle-chat", checkpoint.Id, f.Host.Journal.GetRevision("idle-chat"));
            check(adoption.Type == "context.compaction" && adoption.Projection.Model == ModelInclusion.OnDemand &&
                  adoption.Sequence > run.Submission.ConversationSequence &&
                  f.Host.Store.GetConversationCheckpoint("idle-chat", long.MaxValue)!.SourceRevision == run.Submission.ConversationSequence,
                "normal adoption creates an exact OnDemand coordinate without changing its admission-fixed coverage cutoff");
            check((await Exact(f, run, checkpoint.Id)).State == WorkflowEffectState.Failed,
                "normal checkpoint event after the current admission boundary is not exposed through history");
            f.Host.Store.Cancel(completed.Id);
            var later = await f.SubmitAsync("later-checkpoint-read");
            var result = await Exact(f, later, checkpoint.Id);
            var fragment = result.Data!.Value.Deserialize<WorkerWorkflowEffects.HistorySourceData>(CerebralJson.Options)!;
            using var document = JsonDocument.Parse(fragment.Json);
            var retained = document.RootElement.GetProperty("data").GetProperty("checkpoint").Deserialize<ContextCheckpoint>(CerebralJson.Options)!;
            check(result.State == WorkflowEffectState.Succeeded && fragment.Sequence == adoption.Sequence &&
                  retained.SourceLocators.SequenceEqual(checkpoint.SourceLocators),
                "a later accepted turn exact-reads host source metadata through the existing history command");
            var receipt = await f.Bridge.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.ReceiptCommand).ExecuteAsync(
                new("old-receipt", later.Submission, CerebralJson.Element(
                    new WorkerWorkflowEffects.ReceiptArguments(checkpoint.Id))) { RunId = later.Id }, default);
            check(receipt.State == WorkflowEffectState.Failed,
                "cross-turn checkpoint history does not widen the current-run action receipt reader");
        }

        using (var f = new Fixture())
        {
            await f.SeedAsync(large: false);
            var legacy = new ContextCheckpoint("legacy-source", 1, 0, [new("old-source", 1)],
                new string('é', 3500) + "🙂", "legacy-source", true);
            f.SetBaseline(legacy, 1);
            var run = await f.SubmitAsync("legacy-source-read");
            var fragments = new StringBuilder();
            WorkerWorkflowEffects.HistorySourceData page;
            var offset = 0;
            var pages = 0;
            do
            {
                var result = await Exact(f, run, legacy.Id, offset);
                if (result.State != WorkflowEffectState.Succeeded) throw new Exception(result.Error);
                page = result.Data!.Value.Deserialize<WorkerWorkflowEffects.HistorySourceData>(CerebralJson.Options)!;
                check(page.Sequence == 0 && page.CheckpointCoverageThroughSequence == 1 &&
                      page.SnapshotSequence == run.Submission.ConversationSequence &&
                      Encoding.UTF8.GetByteCount(page.Json) <= 4096 && page.Offset == offset,
                    "legacy fallback uses explicit no-event sequence and bounded UTF-8 pages");
                fragments.Append(page.Json); offset = page.NextOffset ?? offset;
                if (++pages > 32) throw new Exception("Legacy checkpoint paging did not terminate.");
            } while (page.NextOffset is not null);
            using var parsed = JsonDocument.Parse(fragments.ToString());
            var retained = parsed.RootElement.GetProperty("checkpoint").Deserialize<ContextCheckpoint>(CerebralJson.Options)!;
            check(parsed.RootElement.GetProperty("kind").GetString() == "retained-checkpoint-fallback" &&
                  parsed.RootElement.GetProperty("coverageThroughSequence").GetInt64() == 1 &&
                  retained.Summary == legacy.Summary && retained.SourceLocators.Count == 0 && pages > 1,
                "legacy fallback labels its true provenance and coverage cutoff without inventing attribution");
            var bytes = Encoding.UTF8.GetBytes(fragments.ToString());
            var interior = Array.FindIndex(bytes, b => (b & 0xc0) == 0x80);
            check(interior > 0 && (await Exact(f, run, legacy.Id, interior)).State == WorkflowEffectState.Failed &&
                  (await Exact(f, run, "unrelated-checkpoint")).State == WorkflowEffectState.Failed,
                "legacy reads reject invalid Unicode offsets and unrelated checkpoint identities");
        }

        foreach (var visibility in new[] { ModelInclusion.OnDemand, ModelInclusion.Never })
        {
            using var f = new Fixture();
            await f.SeedAsync(large: false);
            var legacy = new ContextCheckpoint("future-source", 1, 0, [new("covered-source", 1)], "Retained legacy.",
                "future-source", true);
            f.SetBaseline(legacy, 1);
            var run = await f.SubmitAsync("future-source-read");
            f.Host.Journal.Append(new(legacy.Id, "idle-chat", run.Id, 0, "context.compaction",
                new(EventActorKind.Service, "cerebral.engine"), [], new(visibility, HumanVisibility.Activity),
                new(EventSourceKind.Service, "cerebral.engine"), DateTimeOffset.UtcNow,
                CerebralJson.Element(new { checkpoint = legacy })), f.Host.Journal.GetRevision("idle-chat"));
            check((await Exact(f, run, legacy.Id)).State == WorkflowEffectState.Failed,
                "an event outside the accepted boundary never triggers a legacy fallback: " + visibility);
            f.Host.Store.Cancel(run.Id);
            var later = await f.SubmitAsync("visible-source-" + visibility);
            check((await Exact(f, later, legacy.Id)).State ==
                  (visibility == ModelInclusion.OnDemand ? WorkflowEffectState.Succeeded : WorkflowEffectState.Failed),
                "later reads preserve the actual event projection rather than substituting legacy state: " + visibility);
        }

        using (var f = new Fixture())
        {
            await f.SeedAsync(large: true);
            await f.PrepareAsync();
            check(Format(f) == 6, "preparing an idle candidate does not upgrade persisted data");
            var next = await f.SubmitAsync("idle-source-read");
            check(Format(f) == 7, "accepted idle adoption marks the format in the next-input transaction");
            var checkpoint = f.Host.Store.GetConversationCheckpoint("idle-chat", long.MaxValue)!.Checkpoint;
            var result = await Exact(f, next, checkpoint.Id);
            check(result.State == WorkflowEffectState.Succeeded && checkpoint.SourceLocators.Count == checkpoint.CoveredSources.Count,
                "idle adoption remains exact-readable with complete bounded host metadata");
        }
    }
}
