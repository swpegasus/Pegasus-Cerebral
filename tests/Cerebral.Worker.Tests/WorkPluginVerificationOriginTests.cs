using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

// Real disposable Engine journal/receipt transactions with controlled command outcomes.
// No semantic tool, provider, desktop, installation or owner permission is executed here.
internal static class WorkPluginVerificationOriginTests
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Work verification origin: " + label);
            checks++;
            Console.WriteLine("PASS: " + label);
        }
        static bool Refused(Action action)
        {
            try { action(); return false; }
            catch (UnauthorizedAccessException) { return true; }
        }

        foreach (var controls in new[] { Array.Empty<string>(),
                     new[] { WorkerWorkflowEffects.GenerateCommand },
                     new[] { WorkerWorkflowEffects.CompactCommand },
                     new[] { WorkerWorkflowEffects.ReceiptCommand },
                     new[] { WorkerWorkflowEffects.HistoryCommand },
                     new[] { WorkerWorkflowEffects.CatalogCommand },
                     new[] { WorkerWorkflowEffects.GenerateCommand, WorkerWorkflowEffects.CompactCommand,
                         WorkerWorkflowEffects.GenerateCommand } })
        {
            using var f = new Fixture();
            f.Complete(WorkerWorkflowEffects.ToolCommand, f.Verified());
            foreach (var control in controls)
                f.Complete(control, new(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject));
            f.Present();
            Check(f.Verify() == Fixture.Fingerprint,
                "exact verification survives known model or read-only host control interposition: " + string.Join(",", controls));
        }
        foreach (var name in new[] { "file.read", "file.edit", "process.run", "plugin.contract", "plugin.verify" })
        {
            using var f = new Fixture();
            f.Complete(WorkerWorkflowEffects.ToolCommand, f.Verified());
            f.Complete(WorkerWorkflowEffects.ToolCommand, new(WorkflowEffectState.Succeeded,
                CerebralJson.Element(new WorkerWorkflowEffects.ToolData(name, "{}", false, null))));
            f.Complete(WorkerWorkflowEffects.ReceiptCommand, new(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject));
            f.Present();
            Check(Refused(() => f.Verify()), "later semantic outcome invalidates the old proof: " + name);
        }
        using (var f = new Fixture())
        {
            f.Complete(WorkerWorkflowEffects.ToolCommand, f.Verified());
            f.Complete(WorkerWorkflowEffects.ToolCommand,
                new(WorkflowEffectState.Failed, Error: "Controlled SDK rejection without process exit code."));
            f.Present();
            Check(Refused(() => f.Verify()), "failed later tool cannot resurrect the older successful verification");
        }
        using (var f = new Fixture())
        {
            f.Complete(WorkerWorkflowEffects.ToolCommand, f.Verified());
            f.Complete("fixture.host:other", new(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject));
            f.Present();
            Check(Refused(() => f.Verify()), "unknown command classification cannot be silently skipped");
        }

        foreach (var fault in new[] { "provider", "outcome", "state", "partial", "truncated", "follow-up",
                     "root", "manifest", "fingerprint", "not-verified", "effect-failed" })
        {
            using var f = new Fixture();
            f.Complete(WorkerWorkflowEffects.ToolCommand, f.Verified(fault));
            f.Present();
            Check(Refused(() => f.Verify()), "invalid verification receipt is refused: " + fault);
        }

        foreach (var fault in new[] { "actor", "provenance", "job", "correlation", "call", "name",
                     "success", "truncated", "missing-data", "schema", "content-count", "payload" })
        {
            using var f = new Fixture();
            f.Complete(WorkerWorkflowEffects.ToolCommand, f.Verified());
            var model = f.Complete(WorkerWorkflowEffects.GenerateCommand,
                new(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject));
            var data = model.Data!.Value.Deserialize<ToolResultData>(CerebralJson.Options)!;
            var changed = fault switch
            {
                "actor" => model with { Actor = new(EventActorKind.Agent, WorkerWorkflowEffects.GenerateCommand) },
                "provenance" => model with { Provenance = model.Provenance with { SourceId = "wrong" } },
                "job" => model with { Provenance = model.Provenance with { JobId = "wrong" } },
                "correlation" => model with { CorrelationId = "wrong" },
                "call" => model with { Data = CerebralJson.Element(data with { CallId = "wrong" }) },
                "name" => model with { Data = CerebralJson.Element(data with { ToolName = "wrong" }) },
                "success" => model with { Data = CerebralJson.Element(data with { Success = false }) },
                "truncated" => model with { Data = CerebralJson.Element(data with { Truncated = true }) },
                "missing-data" => model with { Data = null },
                "schema" => model with { Content = [new StructuredContentPart("fixture.wrong", WorkflowContract.EmptyObject)] },
                "content-count" => model with { Content = [.. model.Content, new TextContentPart("extra")] },
                _ => model with { Content = [new StructuredContentPart("workflow.command.result",
                    CerebralJson.Element(new WorkflowCommandResult(WorkflowEffectState.Failed, Error: "drift")))] }
            };
            f.Append(changed);
            f.Present();
            Check(Refused(() => f.Verify()), "unproved model receipt cannot be skipped to borrow older proof: " + fault);
        }
        using (var f = new Fixture())
        {
            f.Complete(WorkerWorkflowEffects.ToolCommand, f.Verified());
            f.Present();
            var old = f.Run;
            f.AppendMetadata("irrelevant concurrent journal event");
            // A run revision change, unlike a journal-only projection, invalidates the captured run.
            f.Workflows.CompleteEffect(old.Id, old.Effect!.Id,
                new(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject));
            Check(Refused(() => f.Verify(old)), "current durable run revision is still required");
        }
        using (var f = new Fixture())
        {
            f.Complete(WorkerWorkflowEffects.ToolCommand, f.Verified());
            var removed = f.AppendMetadata("controlled missing history");
            f.Present();
            f.DeleteOnlyFixtureEvent(removed.Sequence);
            Check(Refused(() => f.Verify()), "a missing event above the verification fails closed");
        }
        using (var f = new Fixture())
        {
            f.Complete(WorkerWorkflowEffects.ToolCommand, f.Verified());
            for (var i = 0; i < 33; i++) f.AppendMetadata("bounded history filler");
            f.Present();
            Check(Refused(() => f.Verify()), "verification beyond the existing 32-event window is unavailable");
        }
        using (var f = new Fixture())
        {
            var foreign = f.Complete(WorkerWorkflowEffects.ToolCommand, f.Verified());
            f.RemoveTurnProof(foreign);
            f.Present();
            Check(Refused(() => f.Verify()), "another turn's verification cannot authorize this run");
        }
        return checks;
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private static readonly MethodInfo VerifyMethod = typeof(WorkflowUiOrigin).GetMethod("VerifyWorkPlugin",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "Cerebral.WorkPluginReceipt-" + Guid.NewGuid().ToString("N"));
        private readonly string database;
        internal EventJournal Journal { get; }
        internal WorkflowStore Workflows { get; }
        internal WorkflowRun Run { get; private set; }
        internal string Root { get; }
        internal string Manifest { get; }

        internal Fixture()
        {
            Root = Path.Combine(directory, "project");
            Directory.CreateDirectory(Root);
            Manifest = Path.Combine(Root, "desktop-plugin.json");
            database = Path.Combine(directory, "engine.db");
            Journal = new(database); Workflows = new(Journal);
            var id = Guid.NewGuid().ToString("N");
            Run = Workflows.Submit(new(id, id, "fixture-profile", "fixture.programming:develop", "1.0.0",
                "Controlled receipt metadata; never execute effects.", null,
                CerebralJson.Element(new { rootPath = Root, presentManifest = "desktop-plugin.json" }),
                WorkflowContract.EmptyObject, StepLimit: 512, Caller: new(EventActorKind.User, "fixture-owner")));
        }

        internal WorkflowCommandResult Verified(string? fault = null)
        {
            var proof = CerebralJson.Element(new { root = fault == "root" ? directory : Root,
                verified = fault != "not-verified", desktop = new {
                    manifest = fault == "manifest" ? Path.Combine(Root, "wrong.json") : Manifest,
                    packageFingerprint = fault == "fingerprint" ? "INVALID" : Fingerprint } });
            var data = new WorkerWorkflowEffects.ToolData(PluginScaffoldToolExtension.VerifyToolName,
                proof.GetRawText(), fault == "truncated", new(fault == "outcome" ? ToolExecutionOutcome.Unknown :
                    ToolExecutionOutcome.Succeeded, fault == "provider" ? "wrong" : "cerebral.plugin-sdk",
                    State: fault == "state" ? "other" : "verified", Partial: fault == "partial"), fault == "follow-up");
            return new(fault == "effect-failed" ? WorkflowEffectState.Failed : WorkflowEffectState.Succeeded,
                CerebralJson.Element(data));
        }

        private void Dispatch(string command)
        {
            if (Run.Status == WorkflowStatus.Queued) Run = Workflows.TryClaim(Run.Id)!;
            var descriptor = new WorkflowCommandDescriptor(command, 1, "Metadata fixture only.",
                WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, []);
            Run = Workflows.ApplyStep(Run.Id, Run.Revision,
                new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(command, 1, WorkflowContract.EmptyObject)), descriptor);
            Run = Workflows.DispatchEffect(Run.Id, Run.Revision);
        }
        internal CerebralEvent Complete(string command, WorkflowCommandResult result)
        {
            Dispatch(command);
            var effect = Run.Effect!.Id;
            Run = Workflows.CompleteEffect(Run.Id, effect, result);
            return Journal.ReadPreviousPage(Run.Submission.ConversationId, limit: 10).Events
                .Single(item => item.Type == CerebralEventTypes.ToolResult && item.CorrelationId == effect);
        }
        internal void Present() => Dispatch(WorkerWorkflowEffects.ToolCommand);
        internal string Verify(WorkflowRun? run = null)
        {
            try { return (string)VerifyMethod.Invoke(null, [Journal, Workflows, run ?? Run, Manifest])!; }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        internal CerebralEvent Append(CerebralEvent template)
        {
            var snapshot = Journal.ReadPreviousPage(Run.Submission.ConversationId, limit: 1).SnapshotSequence;
            return Journal.Append(template with { Id = Guid.NewGuid().ToString("N"), Sequence = 0 }, snapshot).Event;
        }
        internal CerebralEvent AppendMetadata(string text) => Append(new(Guid.NewGuid().ToString("N"),
            Run.Submission.ConversationId, Run.Id, 0, CerebralEventTypes.ServiceUpdate,
            new(EventActorKind.Service, "fixture.metadata"), [new TextContentPart(text)],
            new(ModelInclusion.OnDemand, HumanVisibility.Activity), new(EventSourceKind.Service, "fixture.metadata"), DateTimeOffset.UtcNow));

        internal void DeleteOnlyFixtureEvent(long sequence)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM conversation_events WHERE conversation_id=$conversation AND sequence=$sequence";
            command.Parameters.AddWithValue("$conversation", Run.Submission.ConversationId);
            command.Parameters.AddWithValue("$sequence", sequence);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Fixture gap was not created.");
        }
        internal void RemoveTurnProof(CerebralEvent item)
        {
            // Preserve the sequence instead of introducing a gap: this is a controlled wrong-turn envelope.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE conversation_events SET payload=$payload WHERE conversation_id=$c AND sequence=$s";
            command.Parameters.AddWithValue("$c", item.ConversationId); command.Parameters.AddWithValue("$s", item.Sequence);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(item with { TurnId = "foreign-turn" }));
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Fixture wrong-turn envelope was not created.");
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            var target = Path.GetFullPath(directory);
            if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(target).StartsWith("Cerebral.WorkPluginReceipt-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its owned temporary directory.");
            Directory.Delete(target, recursive: true);
        }
    }
}
