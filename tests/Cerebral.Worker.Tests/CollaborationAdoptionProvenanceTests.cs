using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Private SQLite component integration using real WorkflowStore and CollaborationStore persistence.</summary>
public static class CollaborationAdoptionProvenanceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static int CheckCollaborationAdoptionProvenance(string privateRoot)
    {
        Directory.CreateDirectory(privateRoot);
        var checks = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException("adoption_provenance_fixture_failed:" + label); checks++; }
        Fixture Fresh(JsonElement? snapshot = null, bool persistRun = true) => new(privateRoot, snapshot ?? Snapshot(3), persistRun);
        void Legacy(CerebralEvent item)
        {
            Check(item.Provenance.AgentId is null && item.Data is { } data &&
                !data.TryGetProperty("targetProfileId", out _) && !data.TryGetProperty("targetProfileName", out _) &&
                !data.TryGetProperty("targetProfileRole", out _), "legacy_metadata_omitted");
        }
        void Identity(Fixture fixture, CerebralEvent item)
        {
            Check(item.Actor.Kind == EventActorKind.Service && item.Actor.Id == "cerebral.collaboration" &&
                item.Provenance.Source == EventSourceKind.Service && item.Provenance.SourceId == "cerebral.collaboration",
                "service_actor_and_source_preserved");
            Check(item.Provenance.JobId == fixture.Completed.Id && item.ConversationId == fixture.Task.Request.SourceConversationId &&
                item.TurnId == fixture.Task.Id && item.CorrelationId == fixture.Task.Id, "event_run_task_source_coordinates_preserved");
            Check(item.Data!.Value.GetProperty("taskId").GetString() == fixture.Task.Id &&
                item.Data.Value.GetProperty("attemptId").GetString() == fixture.Task.AttemptId &&
                item.Data.Value.GetProperty("targetConversationId").GetString() == fixture.Task.Request.TargetConversationId &&
                item.Data.Value.GetProperty("targetRunRevision").GetInt64() == fixture.Completed.Revision,
                "adoption_data_coordinates_preserved");
            Check(item.Type == "collaboration.result" && item.Content.OfType<TextContentPart>().Single().Text == "Accepted result finding." &&
                item.Projection.Model == ModelInclusion.Evidence && item.Projection.Human == HumanVisibility.Transcript &&
                item.Projection.Retention == RetentionPolicy.Durable, "result_and_projection_preserved");
        }

        foreach (var version in new[] { 2, 3, 4 })
        {
            var fixture = Fresh(Snapshot(version));
            Check(fixture.Completed.Submission.ConversationSequence == 1, "uses_final_accepted_submission_after_history_binding");
            var originalSubmission = fixture.RawSubmission();
            fixture.ChangeMutablePayloadName("Mutable payload replacement", "lead");
            fixture.SeedLiveProfile("Live renamed helper", "lead");
            var adopted = fixture.Adopt();
            var item = fixture.ResultEvent();
            Identity(fixture, item);
            Check(item.Data!.Value.GetProperty("targetProfileId").GetString() == Fixture.ProfileId &&
                item.Provenance.AgentId == Fixture.ProfileId, "same_immutable_profile_id_in_data_and_provenance");
            Check(item.Data.Value.GetProperty("targetProfileName").GetString() == "Frozen Helper" &&
                item.Data.Value.GetProperty("targetProfileRole").GetString() == "helper", "frozen_name_role_not_mutable_or_live_profile");
            Check(!item.Data.Value.GetRawText().Contains(Fixture.PrivateBody, StringComparison.Ordinal) &&
                item.Data.Value.EnumerateObject().Count() == 7, "only_bounded_public_profile_metadata");
            Check(originalSubmission == fixture.RawSubmission(), "adoption_does_not_replace_accepted_submission");
            var reopening = new EventJournal(fixture.Database);
            var saved = reopening.ReadPage(fixture.Task.Request.SourceConversationId, 0, 20).Events.Single(x => x.Type == "collaboration.result");
            Check(saved == item || JsonSerializer.Serialize(saved, Json) == JsonSerializer.Serialize(item, Json), "adoption_event_real_sqlite_persistence");
            fixture.ChangeSubmission(x => x["settingsSnapshot"]!["profile"]!["name"] = "Post-adoption changed input");
            var retry = fixture.Adopt();
            var repeated = fixture.ResultEvent();
            Check(retry.Revision == adopted.Revision && repeated.Id == item.Id && repeated.Sequence == item.Sequence &&
                repeated.Data!.Value.GetProperty("targetProfileName").GetString() == "Frozen Helper" &&
                fixture.ResultEvents().Count == 1, "idempotent_retry_retains_frozen_event_without_recapture");
        }

        var legacyMemory = Fresh(persistRun: false);
        var legacy = legacyMemory.Adopt(); var legacyItem = legacyMemory.ResultEvent();
        Legacy(legacyItem); Identity(legacyMemory, legacyItem);
        Check(legacy.AdoptedAt is not null && legacyMemory.Adopt().Revision == legacy.Revision &&
            legacyMemory.ResultEvents().Count == 1, "legacy_memory_gateway_still_adopts_idempotently");

        foreach (var mutation in new Action<JsonObject>[]
        {
            x => x["settingsSnapshot"] = new JsonObject(),
            x => x["settingsSnapshot"] = null,
            x => x["settingsSnapshot"]!["resolvedSettingsVersion"] = 1,
            x => x["settingsSnapshot"]!["resolvedSettingsVersion"] = 5,
            x => x["settingsSnapshot"]!["resolvedSettingsVersion"] = "3",
            x => x["settingsSnapshot"]!["resolvedSettingsVersion"] = 3.5,
            x => x["settingsSnapshot"]!["profile"] = null,
            x => x["settingsSnapshot"]!["profile"] = new JsonArray(),
            x => x["settingsSnapshot"]!["profile"]!["id"] = "different-profile",
            x => x["settingsSnapshot"]!["profile"]!["id"] = new string('x',129),
            x => x["settingsSnapshot"]!["profile"]!["id"] = "helper\nforged",
            x => ((JsonObject)x["settingsSnapshot"]!["profile"]!).Remove("id"),
            x => x["settingsSnapshot"]!["tools"] = null,
            x => x["settingsSnapshot"]!["skills"] = null,
            x => x["settingsSnapshot"]!["fullAccess"] = "false",
            x => x["settingsSnapshot"]!["routingFingerprint"] = "",
            x => x["settingsSnapshot"]!["sharedInstructions"] = new JsonObject(),
            x => x["requestId"] = "different-attempt",
            x => x["conversationId"] = "different-conversation",
            x => x["profileId"] = "different-profile",
            x => x["projectId"] = "different-project"
        })
        {
            var fixture = Fresh(); fixture.ChangeSubmission(mutation);
            fixture.Adopt(); var item = fixture.ResultEvent(); Legacy(item); Identity(fixture, item);
        }
        foreach (var columnChange in new[]
        {
            "UPDATE workflow_runs SET revision=revision+1", "UPDATE workflow_runs SET status='Waiting'",
            "UPDATE workflow_runs SET request_id='foreign-request'", "UPDATE workflow_runs SET conversation_id='foreign-conversation'",
            "UPDATE workflow_runs SET profile_id='foreign-profile'", "DELETE FROM workflow_runs",
            "UPDATE workflow_runs SET submission='private malformed JSON'",
            "UPDATE workflow_runs SET submission=zeroblob(256)",
            "UPDATE workflow_runs SET submission=CAST(zeroblob(1048577) AS TEXT)"
        })
        {
            var fixture = Fresh(); fixture.Sql(columnChange);
            fixture.Adopt(); var item = fixture.ResultEvent(); Legacy(item); Identity(fixture, item);
        }
        var duplicate = Fresh();
        duplicate.RawSubmission(duplicate.RawSubmission().Replace("\"resolvedSettingsVersion\":3",
            "\"resolvedSettingsVersion\":3,\"resolvedSettingsVersion\":3", StringComparison.Ordinal));
        duplicate.Adopt(); Legacy(duplicate.ResultEvent());
        var duplicateProfile = Fresh();
        duplicateProfile.RawSubmission(duplicateProfile.RawSubmission().Replace("\"id\":\"helper-frozen\"",
            "\"id\":\"helper-frozen\",\"id\":\"helper-frozen\"", StringComparison.Ordinal));
        duplicateProfile.Adopt(); Legacy(duplicateProfile.ResultEvent());

        foreach (var change in new Action<JsonObject>[]
        {
            x => x["settingsSnapshot"]!["profile"]!["name"] = 17,
            x => x["settingsSnapshot"]!["profile"]!["name"] = "",
            x => x["settingsSnapshot"]!["profile"]!["name"] = new string('x',257),
            x => x["settingsSnapshot"]!["profile"]!["name"] = "Frozen\nForged",
            x => ((JsonObject)x["settingsSnapshot"]!["profile"]!).Remove("name")
        })
        {
            var fixture = Fresh(); fixture.ChangeSubmission(change); fixture.Adopt(); var item = fixture.ResultEvent();
            Check(item.Provenance.AgentId == Fixture.ProfileId && item.Data!.Value.GetProperty("targetProfileId").GetString() == Fixture.ProfileId &&
                !item.Data.Value.TryGetProperty("targetProfileName", out _) && item.Data.Value.GetProperty("targetProfileRole").GetString() == "helper",
                "invalid_name_omitted_independently_of_proven_id_role");
        }
        foreach (var change in new Action<JsonObject>[]
        {
            x => x["settingsSnapshot"]!["profile"]!["role"] = new JsonObject(),
            x => x["settingsSnapshot"]!["profile"]!["role"] = " ",
            x => x["settingsSnapshot"]!["profile"]!["role"] = new string('x',65),
            x => x["settingsSnapshot"]!["profile"]!["role"] = "helper\rlead",
            x => ((JsonObject)x["settingsSnapshot"]!["profile"]!).Remove("role")
        })
        {
            var fixture = Fresh(); fixture.ChangeSubmission(change); fixture.Adopt(); var item = fixture.ResultEvent();
            Check(item.Provenance.AgentId == Fixture.ProfileId && item.Data!.Value.GetProperty("targetProfileId").GetString() == Fixture.ProfileId &&
                item.Data.Value.GetProperty("targetProfileName").GetString() == "Frozen Helper" &&
                !item.Data.Value.TryGetProperty("targetProfileRole", out _), "invalid_role_omitted_independently_of_proven_id_name");
        }
        var unicode = Fresh(Snapshot(3, name:"Élise ⚙", role:"helper"));
        unicode.Adopt(); Check(unicode.ResultEvent().Data!.Value.GetProperty("targetProfileName").GetString() == "Élise ⚙", "bounded_unicode_preserved");
        var bothInvalid = Fresh(); bothInvalid.ChangeSubmission(x =>
        {
            x["settingsSnapshot"]!["profile"]!["name"] = "\u0000";
            x["settingsSnapshot"]!["profile"]!["role"] = "\u001f";
        });
        bothInvalid.Adopt(); var idOnly = bothInvalid.ResultEvent();
        Check(idOnly.Provenance.AgentId == Fixture.ProfileId && idOnly.Data!.Value.GetProperty("targetProfileId").GetString() == Fixture.ProfileId &&
            !idOnly.Data.Value.TryGetProperty("targetProfileName",out _) && !idOnly.Data.Value.TryGetProperty("targetProfileRole",out _),
            "valid_header_can_retain_only_proven_id");

        var oversizedSnapshot = Fresh();
        oversizedSnapshot.ChangeSubmission(x => x["settingsSnapshot"]!["sharedInstructions"] = new string('x',WorkflowContract.MaximumStateBytes+1));
        oversizedSnapshot.Adopt(); Legacy(oversizedSnapshot.ResultEvent());
        return checks;
    }

    private static JsonElement Snapshot(int version, string name="Frozen Helper", string role="helper")
    {
        var value = new JsonObject
        {
            ["resolvedSettingsVersion"] = version,
            ["profile"] = new JsonObject { ["id"] = Fixture.ProfileId, ["name"] = name, ["role"] = role,
                ["workerId"] = "local", ["modelWorkerId"] = "local", ["instructions"] = Fixture.PrivateBody },
            ["sharedInstructions"] = Fixture.PrivateBody, ["roleInstructions"] = Fixture.PrivateBody,
            ["model"] = null, ["tools"] = new JsonArray(), ["capabilities"] = new JsonArray(),
            ["allowedTools"] = new JsonArray(), ["allowedRoots"] = new JsonArray(), ["fullAccess"] = false,
            ["routingFingerprint"] = new string('a',64), ["packageBindings"] = new JsonArray(),
            ["compactor"] = null
        };
        if (version >= 3) value["skills"] = new JsonArray();
        // Optional later private bodies are never copied into adoption metadata.
        if (version == 4) value["personalityAdmission"] = new JsonObject { ["body"] = Fixture.PrivateBody };
        return JsonSerializer.SerializeToElement(value, Json);
    }

    private sealed class Fixture
    {
        internal const string ProfileId = "helper-frozen";
        internal const string PrivateBody = "private accepted prompt/model/tool/persona fixture body never projected";
        internal string Database { get; }
        internal EventJournal Journal { get; }
        internal CollaborationStore Collaboration { get; }
        internal WorkflowRun Completed { get; }
        internal CollaborationTask Task { get; }
        private readonly CollaborationAdoptRequest request;

        internal Fixture(string privateRoot, JsonElement snapshot, bool persistRun)
        {
            var root = Path.Combine(Path.GetFullPath(privateRoot), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); Database = Path.Combine(root,"engine.db");
            Journal = new(Database); Collaboration = new(Journal);
            var source = new ConversationSummary("source","Source","lead","project","prior-source-run",WorkflowStatus.Completed,DateTimeOffset.UtcNow);
            var target = new ConversationSummary("target","Target",ProfileId,"project","prior-target-run",WorkflowStatus.Completed,DateTimeOffset.UtcNow);
            var task = Collaboration.Submit(new CollaborationTaskRequest("request-"+Guid.NewGuid().ToString("N"),source.Id,target.Id,
                "Return the accepted finding.",[],[]),source,target,null);
            var submission = new WorkflowSubmission(task.AttemptId,target.Id,ProfileId,"fixture.workflow","1.0.0","Accepted request.",null,
                JsonSerializer.SerializeToElement(new {targetProfileName="forged input name"},Json),snapshot,
                Caller:new(EventActorKind.Service,"cerebral.collaboration")) { ProjectId="project" };
            if (persistRun)
            {
                Journal.Append(new CerebralEvent("prior-target-event",target.Id,"prior-turn",0,"message",
                    new(EventActorKind.User,"owner"),[new TextContentPart("Prior target history.")],
                    new(ModelInclusion.Required,HumanVisibility.Transcript),new(EventSourceKind.User),DateTimeOffset.UtcNow),0);
                var workflows = new WorkflowStore(Journal);
                var run = workflows.Submit(submission);
                task = Collaboration.AttachAttempt(task.Id,task.Revision,run);
                var claimed = workflows.TryClaim(run.Id) ?? throw new InvalidOperationException("fixture_claim_failed");
                Completed = workflows.ApplyStep(claimed.Id,claimed.Revision,new WorkflowStep(WorkflowContract.EmptyObject,
                    new CompleteWorkflow(JsonSerializer.SerializeToElement(new {summary="Accepted result finding.",targetProfileName="forged result name"},Json))));
            }
            else
            {
                // Mirrors existing Legacy FakeGateway: the target run lives in memory, not workflow_runs.
                Completed = new WorkflowRun {Id=task.AttemptId,Submission=submission,Status=WorkflowStatus.Completed,
                    Revision=4,Result=JsonSerializer.SerializeToElement(new {summary="Accepted result finding."},Json),
                    CreatedAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow};
                task = Collaboration.AttachAttempt(task.Id,task.Revision,Completed);
            }
            Task = Collaboration.Synchronize(task.Id,task.Revision,Completed);
            request = new("adopt-"+Guid.NewGuid().ToString("N"),Task.Id,Task.Revision,Task.AttemptId,Completed.Revision);
        }
        internal CollaborationTask Adopt() => Collaboration.Adopt(request);
        internal IReadOnlyList<CerebralEvent> ResultEvents() => Journal.ReadPage(Task.Request.SourceConversationId,0,20)
            .Events.Where(x=>x.Type=="collaboration.result").ToArray();
        internal CerebralEvent ResultEvent() => ResultEvents().Single();
        internal string RawSubmission()
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText="SELECT submission FROM workflow_runs WHERE id=$id";command.Parameters.AddWithValue("$id",Completed.Id);
            return (string)command.ExecuteScalar()!;
        }
        internal void RawSubmission(string value)
        {
            using var connection=Open();using var command=connection.CreateCommand();
            command.CommandText="UPDATE workflow_runs SET submission=$value WHERE id=$id";
            command.Parameters.AddWithValue("$value",value);command.Parameters.AddWithValue("$id",Completed.Id);command.ExecuteNonQuery();
        }
        internal void ChangeSubmission(Action<JsonObject> change)
        {var value=JsonNode.Parse(RawSubmission())!.AsObject();change(value);RawSubmission(value.ToJsonString());}
        internal void ChangeMutablePayloadName(string name,string role)
        {
            using var connection=Open();using var command=connection.CreateCommand();
            command.CommandText="SELECT payload FROM workflow_runs WHERE id=$id";command.Parameters.AddWithValue("$id",Completed.Id);
            var value=JsonNode.Parse((string)command.ExecuteScalar()!)!.AsObject();
            value["submission"]!["settingsSnapshot"]!["profile"]!["name"]=name;
            value["submission"]!["settingsSnapshot"]!["profile"]!["role"]=role;
            command.CommandText="UPDATE workflow_runs SET payload=$payload WHERE id=$id";
            command.Parameters.AddWithValue("$payload",value.ToJsonString());command.ExecuteNonQuery();
        }
        internal void SeedLiveProfile(string name,string role)
        {
            // This separate private records DB is intentionally never passed to Engine or the producer.
            using var connection=new SqliteConnection(new SqliteConnectionStringBuilder
                {DataSource=Path.Combine(Path.GetDirectoryName(Database)!,"cerebral.db"),Pooling=false}.ToString());
            connection.Open();using var command=connection.CreateCommand();
            command.CommandText="CREATE TABLE records(kind TEXT,id TEXT,body TEXT);INSERT INTO records VALUES('agent',$id,$body)";
            command.Parameters.AddWithValue("$id",ProfileId);
            command.Parameters.AddWithValue("$body",JsonSerializer.Serialize(new {id=ProfileId,name,role},Json));command.ExecuteNonQuery();
        }
        internal void Sql(string sql)
        {using var connection=Open();using var command=connection.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
        private SqliteConnection Open()
        {var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Database,Pooling=false}.ToString());connection.Open();return connection;}
    }
}
