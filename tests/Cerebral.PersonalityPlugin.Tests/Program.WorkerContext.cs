using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.PersonalityPlugin.Tests;

internal static partial class Program
{
    private sealed partial class WorkerPipeRun
    {
        // Retain before dispatch so outer cleanup can reconcile a lost submission acknowledgement.
        private readonly HashSet<string> contextRunIds = new(StringComparer.Ordinal);
        private IReadOnlyList<string> ContextRunIds => contextRunIds.ToArray();
        private const string ContextProfileId = "personality-fixture-lead";
        private const string ContextModelId = "personality-fixture-unregistered-model";
        private byte[]? contextProfileBytes;
        private int contextProfileRevision;
        private int contextHostRevision;
        private bool ContextPhaseReached,ContextSeedReached,ContextSeedVerified;
        private string ContextSeedWriteStage="not-started";
        private byte[]? contextStoredProfileBytes;
        private string? contextStoredProfileSha256;
        private int ContextProfileRevision => contextProfileRevision;
        private string? ContextProfileUtf8Sha256 => contextStoredProfileSha256;
        private int ContextProfileUtf8Bytes => contextStoredProfileBytes?.Length ?? 0;

        private async Task RunContextPhaseAsync()
        {
            ContextPhaseReached=true;
            WorkToken.ThrowIfCancellationRequested();
            await SeedContextProfileWhileStoppedAsync();
            await BeginWorkerAsync();
            await AssertContextEnvironmentAsync();

            var before = await Read<JsonElement>("personality.preferences.transfer.get", new { });
            var currentState = before.GetProperty("state");
            Assert(currentState.GetProperty("pending").GetArrayLength() == 0,
                "context phase requires deliberate Phase1 admission of every retained input");
            var currentValues = currentState.GetProperty("active").GetProperty("values");
            Assert(currentValues.GetProperty("enabled").GetBoolean(), "context Ready baseline is enabled");
            var sources = await Read<JsonElement>("personality.sources.get", new { });

            var first = await SubmitContextOnceAsync("ready");
            AssertFrozenContext(first);
            var firstSnapshot = first.Submission.SettingsSnapshot.Clone();
            var firstAdmission = PersonalityContextAdmission(firstSnapshot);
            var selection = CerebralJson.Read<WorkflowContextSelection>(firstAdmission.GetProperty("selection"));
            Assert(selection.Disposition == WorkflowContextDisposition.Ready && selection.Reason is null &&
                   selection.Payload is not null && selection.PayloadDigest is not null && selection.Request is not null,
                "actual host captures a Ready opaque Personality selection");
            var payload = selection.Payload!.Value;
            Assert(ContextDigest(payload) == selection.PayloadDigest, "package payload digest independently matches");
            Assert(payload.GetProperty("settingsRevision").GetInt64() == currentState.GetProperty("revision").GetInt64() &&
                   payload.GetProperty("settingsSha256").GetString() == ContextDigest(currentValues),
                "accepted contribution pins exact current central values");
            var seedSelection = payload.GetProperty("seed").GetProperty("selection");
            Assert(seedSelection.GetProperty("settingsRevision").GetInt64() == sources.GetProperty("revision").GetInt64() &&
                   seedSelection.GetProperty("settingsSha256").GetString() == sources.GetProperty("sha256").GetString(),
                "accepted seed pins exact private source store");
            var request = selection.Request!;
            Assert(request.ProviderId == "pegasus.personality:context.resolve" && request.ProviderVersion == 1 &&
                   request.MaximumUtf8Bytes == 2048 && request.ContextRevision == 0 && request.ExclusionRevision == 0 &&
                   request.References.Count == 1, "accepted request is the bounded body-free Personality template");
            var reference = request.References.Single();
            Assert(reference.Id == "pegasus.personality:style" && reference.Kind == "personality.behavior" &&
                   reference.SourceId == seedSelection.GetProperty("sourceId").GetString() &&
                   reference.ExpectedRevision == seedSelection.GetProperty("sourceRevision").GetInt64() &&
                   reference.Requirement == ContextRequirement.Optional && reference.Priority == 0 &&
                   reference.BeforeCurrentInput, "accepted exact source reference matches the opaque seed");
            var seedIdentity = payload.GetProperty("seed").GetProperty("identity");
            Assert(seedIdentity.GetProperty("requestId").GetString() == first.Id &&
                   seedIdentity.GetProperty("conversationId").GetString() == first.Submission.ConversationId &&
                   seedIdentity.GetProperty("profileId").GetString() == ContextProfileId,
                "package seed uses actual accepted request/conversation/profile identity");

            var firstReadback = await Read<WorkflowRun>("jobs.workflow.get", new { id = first.Id });
            Assert(firstReadback.Revision == first.Revision &&
                   JsonElement.DeepEquals(firstReadback.Submission.SettingsSnapshot, firstSnapshot),
                "actual workflow get preserves the exact accepted snapshot");
            await CancelContextRunAsync(first.Id, firstSnapshot);
            await AssertContextEnvironmentAsync();

            // Disable through the package's no-ID lane; do not change definitions or prepare another source.
            var disabled = CerebralJson.Element(new
            {
                enabled = false,
                selectedPresetId = currentValues.GetProperty("selectedPresetId").GetString(),
                customPresets = currentValues.GetProperty("customPresets")
            });
            var disable = await Call("personality.preferences.transfer.save",
                new { schemaVersion = 2, expectedRevision = currentState.GetProperty("revision").GetInt64(), values = disabled });
            var disabledReadback = await Read<JsonElement>("personality.preferences.transfer.get", new { });
            Assert(JsonElement.DeepEquals(disable.Data.GetProperty("state"), disabledReadback) &&
                   JsonElement.DeepEquals(disabledReadback.GetProperty("state").GetProperty("active").GetProperty("values"), disabled) &&
                   disabledReadback.GetProperty("state").GetProperty("pending").GetArrayLength() == 0,
                "explicit disabled preference mutation has exact pipe readback");
            Assert(JsonElement.DeepEquals(sources, await Read<JsonElement>("personality.sources.get", new { })),
                "disabled request does not mutate the private source store");

            var second = await SubmitContextOnceAsync("omitted");
            AssertFrozenContext(second);
            var omitted = CerebralJson.Read<WorkflowContextSelection>(
                PersonalityContextAdmission(second.Submission.SettingsSnapshot).GetProperty("selection"));
            Assert(omitted.Disposition == WorkflowContextDisposition.Omitted && omitted.Reason == "disabled" &&
                   omitted.Payload is null && omitted.PayloadDigest is null && omitted.Request is null,
                "actual host captures disabled Personality as Omitted without provider body resolution");
            await CancelContextRunAsync(second.Id, second.Submission.SettingsSnapshot);
            var retainedFirst = await Read<WorkflowRun>("jobs.workflow.get", new { id = first.Id });
            Assert(retainedFirst.Status == WorkflowStatus.Cancelled && retainedFirst.Effect is null &&
                   JsonElement.DeepEquals(retainedFirst.Submission.SettingsSnapshot, firstSnapshot),
                "later disabled capture leaves the first accepted Ready snapshot immutable");
            await AssertContextEnvironmentAsync();
        }

        private async Task SeedContextProfileWhileStoppedAsync()
        {
            ContextSeedReached=true;
            // Outer Phase1 owns and proves process shutdown before calling this method.
            Assert(client is null, "context fixture seed starts after owner client disposal");
            WorkToken.ThrowIfCancellationRequested();
            NoLinks(Data);
            NoLinks(Path.Combine(Data, "cerebral.db"));
            var state = new StateStore(Data);
            try
            {
                Assert(state.List<AgentProfile>("agent").Count == 0 &&
                       state.List<ModelRecord>("model").Count == 0 &&
                       state.List<JsonElement>("connection").Count == 0,
                    "stopped fixture has no agent/model/connection registrations before seed");
                var host = state.Get<HostSettings>("settings", "host") ??
                    throw new InvalidDataException("The stopped private fixture host is unavailable.");
                Assert(host.InstallationId == InstallationId && host.Workers.Count == 0 && host.McpServers.Count == 0,
                    "stopped fixture retains its installation and no remote/provider registrations");
                AssertExpectedExecutableMetadata(host);
                var profile = new AgentProfile
                {
                    Id = ContextProfileId, Revision = 1, Name = "Personality capture fixture",
                    Purpose = "Private accepted-snapshot capture without engine or inference execution.",
                    Role = "lead", ModelId = ContextModelId, WorkerId = "local", ModelWorkerId = "local",
                    FollowsDefaultModel = true, FollowsModelBudgets = false, IdleMinutes = 0,
                    AutomaticCompaction = false, CompactionProfileId = null,
                    MaxSteps = 2, MaxSeconds = 10, AllowedRoots = [Project],
                    AllowedTools = [], Capabilities = [], RequiredModelCapabilities = [], PluginSettings = []
                };
                // Nonempty matching binding avoids Configuration.Get's legacy blank-default migration.
                host.DefaultModelId = ContextModelId;
                host.DefaultModelWorkerId = "local";
                host.Revision = checked(host.Revision + 1);
                WorkToken.ThrowIfCancellationRequested();
                ContextSeedWriteStage="host-write-dispatched";
                state.Put("settings", "host", host);
                ContextSeedWriteStage="host-written-profile-write-dispatched";
                state.Put("agent", profile.Id, profile);
                ContextSeedWriteStage="host-and-profile-written";
                contextHostRevision = host.Revision;
                contextProfileRevision = profile.Revision;
                contextProfileBytes = JsonSerializer.SerializeToUtf8Bytes(profile, CerebralJson.Options);
                var row = await ReadContextProfileRowAsync().ConfigureAwait(false);
                contextStoredProfileBytes = row.Payload;
                contextStoredProfileSha256 = row.Sha256;
                Assert(row.Revision == profile.Revision &&
                       row.Payload.AsSpan().SequenceEqual(contextProfileBytes),
                    "seed records exact stopped-DB agent UTF-8 payload bytes, hash and revision");
                Assert(state.Get<ModelRecord>("model", ContextModelId) is null &&
                       state.List<ModelRecord>("model").Count == 0,
                    "explicit default/profile binding remains unregistered and creates no ModelRecord");
                ContextSeedVerified=true;
                ContextSeedWriteStage="verified";
            }
            finally
            {
                // Release only this fixture's pool before the owned child reopens the same database.
                using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = Path.Combine(Data, "cerebral.db"), Pooling = true }.ToString());
                SqliteConnection.ClearPool(pool);
            }
        }

        private async Task AssertContextEnvironmentAsync()
        {
            WorkToken.ThrowIfCancellationRequested();
            var host = await Read<HostSettings>("settings.get", new { });
            Assert(host.InstallationId == InstallationId && host.Revision == contextHostRevision &&
                   host.DefaultModelId == ContextModelId && host.DefaultModelWorkerId == "local" &&
                   host.Workers.Count == 0 && host.McpServers.Count == 0,
                "restart/capture leaves fixture host revision and exact nonlegacy unregistered binding unchanged");
            AssertExpectedExecutableMetadata(host);
            var profiles = await Read<List<AgentProfile>>("agents.list", new { });
            Assert(profiles.Count == 1 && profiles[0].Id == ContextProfileId &&
                   profiles[0].Revision == contextProfileRevision &&
                   JsonSerializer.SerializeToUtf8Bytes(profiles[0], CerebralJson.Options).AsSpan().SequenceEqual(contextProfileBytes),
                "restart/capture leaves exact seeded profile revision and serialized bytes unchanged");
            var storedProfile = await ReadContextProfileRowAsync().ConfigureAwait(false);
            Assert(storedProfile.Revision == contextProfileRevision &&
                   storedProfile.Sha256 == contextStoredProfileSha256 &&
                   storedProfile.Payload.AsSpan().SequenceEqual(contextStoredProfileBytes),
                "readonly SQLite proves stored agent UTF-8 bytes, hash and revision unchanged after restart/capture");
            Assert((await Read<List<ModelRecord>>("models.list", new { })).Count == 0,
                "fixture model catalog stays empty");
            var engine = await Read<JsonElement>("engine.status", new { });
            Assert(engine.GetProperty("paused").GetBoolean() &&
                   engine.GetProperty("active").ValueKind == JsonValueKind.Null &&
                   engine.GetProperty("activeEvaluations").GetInt32() == 0 &&
                   engine.GetProperty("committedDecisions").GetInt64() == 0 &&
                   !engine.GetProperty("candidatePending").GetBoolean(),
                "empty engine remains paused with zero evaluations and committed decisions");
            var phaseIndex = ++contextEnvironmentIndex;
            await VerifyClosedAccounting("context-environment",phaseIndex);
            var slots = await Read<JsonElement>("metrics.slots.status", new { });
            ObserveEnvironment("metrics.slots.status",slots,"context-environment",phaseIndex);
            Assert(ManagedSlotsGuard(slots,InstallationId),
                "observed empty managed slots preserve paused execution and the fixture resource-pause permission");
            var execution = await Read<JsonElement>("metrics.execution.status", new { });
            ObserveEnvironment("metrics.execution.status",execution,"context-environment",phaseIndex);
            Assert(ExecutionGuard(execution,InstallationId),
                "scheduler owns no execution or maintenance work");
            var generation = await Read<JsonElement>("metrics.generation", new { });
            ObserveEnvironment("metrics.generation",generation,"context-environment",phaseIndex);
            Assert(GenerationGuard(generation),
                "fixture records no active or completed generation lanes");
        }

        private Task<(byte[] Payload, string Sha256, int Revision)> ReadContextProfileRowAsync()
            => ReadContextProfileRowValueAsync(Data, WorkToken);

        private static async Task<(byte[] Payload, string Sha256, int Revision)> ReadContextProfileRowValueAsync(string data, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            NoLinks(data);
            NoLinks(Path.Combine(data, "cerebral.db"));
            // This intentionally inspects only the authorized private fixture's exact agent row.
            // Read-only mode, no pooling or PRAGMA writes; a live Worker remains its sole writer.
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(data, "cerebral.db"), Mode = SqliteOpenMode.ReadOnly,
                Pooling = false, DefaultTimeout = 5
            }.ToString());
            await connection.OpenAsync(cancellation).ConfigureAwait(false);
            using (var encoding = connection.CreateCommand())
            {
                encoding.CommandTimeout = 5;
                encoding.CommandText = "PRAGMA encoding";
                if ((await encoding.ExecuteScalarAsync(cancellation).ConfigureAwait(false)) as string != "UTF-8")
                    throw new InvalidDataException("The private fixture database must store UTF-8 payload bytes.");
            }
            using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = "SELECT length(CAST(body AS BLOB)), CAST(body AS BLOB), json_extract(body,'$.revision') FROM records WHERE kind='agent' AND id=$id";
            command.Parameters.AddWithValue("$id", ContextProfileId);
            await using var reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellation).ConfigureAwait(false))
                throw new InvalidDataException("The exact private fixture agent row is unavailable.");
            if (reader.IsDBNull(0))
                throw new InvalidDataException("The private fixture agent payload is null.");
            var length = reader.GetInt64(0);
            if (length is < 1 or > 128 * 1024)
                throw new InvalidDataException("The private fixture agent payload exceeds its byte bound.");
            // Both values come from this row; materialize the computed BLOB only after its byte bound.
            var payload = reader.GetFieldValue<byte[]>(1);
            if (payload.LongLength != length)
                throw new InvalidDataException("The private fixture agent payload read was incomplete.");
            var revision = checked((int)reader.GetInt64(2));
            if (revision < 1 || await reader.ReadAsync(cancellation).ConfigureAwait(false))
                throw new InvalidDataException("The private fixture agent identity/revision is inconsistent.");
            cancellation.ThrowIfCancellationRequested();
            return (payload, Convert.ToHexStringLower(SHA256.HashData(payload)), revision);
        }

        private async Task<WorkflowRun> SubmitContextOnceAsync(string purpose)
        {
            var id = "personality-" + purpose + "-" + Guid.NewGuid().ToString("N");
            var conversation = "personality-chat-" + Guid.NewGuid().ToString("N");
            contextRunIds.Add(id);
            var request = new SubmitWorkflowRequest(id, conversation, ContextProfileId,
                ConversationWorkflowContract.DefinitionId, ConversationWorkflowContract.DefinitionVersion,
                "Private fixture capture only; execution remains paused.", null,
                CerebralJson.Element(new { workingDirectory = Project }), StepLimit: 2);
            var run = await Read<WorkflowRun>("jobs.workflow.submit", request);
            Assert(run.Id == id && run.Submission.RequestId == id &&
                   run.Submission.ConversationId == conversation && run.Submission.ProfileId == ContextProfileId &&
                   run.Submission.Caller is { Kind: EventActorKind.User, Id: "owner" } &&
                   run.Submission.ParentRunId is null && run.Submission.ActiveTimeLimitSeconds == 10,
                "actual owner workflow submission retains exact host-authored identity and limits");
            Assert(run.Status == WorkflowStatus.Queued && run.Steps == 0 && run.Effect is null &&
                   run.ActiveMilliseconds == 0 && !run.CancelRequested,
                "accepted snapshot is queued with no engine steps or effects");
            return run;
        }

        private void AssertFrozenContext(WorkflowRun run)
        {
            var raw = run.Submission.SettingsSnapshot;
            var snapshot = WorkerExecutionSnapshot.Read(raw);
            Assert(snapshot.ResolvedSettingsVersion == 5 && snapshot.Profile.Id == ContextProfileId &&
                   snapshot.Profile.Revision == contextProfileRevision && snapshot.Profile.Role == "lead" &&
                   snapshot.Profile.ModelId == ContextModelId && snapshot.Profile.WorkerId == "local" &&
                   snapshot.Profile.ModelWorkerId == "local" && snapshot.Model is null && snapshot.Compactor is null &&
                   snapshot.NativeContinuity is null && snapshot.CortexScope is null &&
                   !snapshot.FullAccess && snapshot.Tools.Count == 0 && snapshot.AllowedTools.Count == 0 &&
                   snapshot.Capabilities.Count == 0 && snapshot.ContextContributionInstallationId == InstallationId,
                "actual edition5 snapshot has the fixture profile and no model/tools/Cortex/full-access bindings");
        }

        private JsonElement PersonalityContextAdmission(JsonElement snapshot)
        {
            var items = snapshot.GetProperty("contextContributions").EnumerateArray().ToArray();
            var matches = items.Where(item => item.GetProperty("packageId").GetString() == "pegasus.personality").ToArray();
            Assert(matches.Length == 1 && matches[0].GetProperty("packageVersion").GetString() == "1.2.1" &&
                   matches[0].GetProperty("generation").GetString() is { Length: > 0 and <= 128 } &&
                   matches[0].GetProperty("descriptor").GetProperty("id").GetString() == "pegasus.personality:style",
                "actual Personality package/version/generation owns exactly one accepted contribution");
            return matches.Single();
        }

        private async Task CancelContextRunAsync(string id, JsonElement snapshot)
        {
            var cancelled = await Read<WorkflowRun>("jobs.workflow.cancel", new { id });
            var readback = await Read<WorkflowRun>("jobs.workflow.get", new { id });
            Assert(cancelled.Id == id && cancelled.Status == WorkflowStatus.Cancelled && cancelled.CancelRequested &&
                   cancelled.Effect is null && cancelled.Steps == 0 &&
                   readback.Revision == cancelled.Revision && readback.Status == WorkflowStatus.Cancelled &&
                   readback.Effect is null && JsonElement.DeepEquals(readback.Submission.SettingsSnapshot, snapshot),
                "exact queued run cancellation is terminal, read back, and has no effect");
        }

        private static string ContextDigest(JsonElement value) =>
            Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))));
    }
}
