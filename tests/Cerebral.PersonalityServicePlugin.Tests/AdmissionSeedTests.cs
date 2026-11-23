using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.PersonalityServicePlugin;

static class AdmissionSeedTests
{
    public static async Task RunAsync(string fixture, Action<bool, string> check)
    {
        WorkflowSubmission Input(string rid) => new(rid, "seed-chat", "seed-profile",
            ConversationWorkflowContract.DefinitionId, ConversationWorkflowContract.DefinitionVersion,
            "Synthetic seed fixture.", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject,
            Caller: new(EventActorKind.User, "owner"));
        var provisional = Input("seed-request");
        var consumer = new PersonalityConsumer(provisional.ProfileId, "lead", PersonalityConsumerKind.UserFacingConversation);
        var directory = Path.Combine(fixture, "seed-personality-data");
        using var plugin = new PersonalityServicePlugin(); plugin.Initialize(new FixtureHost(directory));
        async Task<JsonElement> Command(string name, object args) =>
            PersonalityContract.Element((await plugin.ExecuteAsync(name, PersonalityContract.Element(args), default))!);
        var settings = await Command("personality.settings", new { });
        await Command("personality.select", new
        {
            expectedRevision = settings.GetProperty("revision").GetInt64(),
            expectedSha256 = settings.GetProperty("sha256").GetString(), enabled = true, presetId = "cerebral"
        });
        var capture = await PersonalityAdmissionEngine.CaptureAsync(plugin, provisional, consumer);
        var seed = capture.Admission!;
        var snapshot = seed.ToSnapshot();
        check(capture.Status == PersonalityAdmissionStatus.Ready && snapshot.GetProperty("version").GetInt32() == 1 &&
            !snapshot.GetRawText().Contains("projectId", StringComparison.Ordinal) &&
            !snapshot.GetRawText().Contains("conversationSequence", StringComparison.Ordinal),
            "seed: durable capture omits transaction-assigned project/history");
        check(Encoding.UTF8.GetByteCount(snapshot.GetRawText()) <= 4096 &&
            !snapshot.GetRawText().Contains(provisional.UserText, StringComparison.Ordinal),
            "seed: versioned durable metadata is bounded and body-free");

        void Reject(Action action, string label)
        {
            var rejected = false;
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
            { rejected = true; }
            check(rejected, "seed: " + label);
        }
        JsonElement Mutated(string area, string field, JsonNode? value)
        {
            var node = JsonNode.Parse(snapshot.GetRawText())!.AsObject();
            if (area == "") node[field] = value;
            else node[area]![field] = value;
            return JsonSerializer.SerializeToElement(node);
        }
        var mutations = new (string Area, string Field, JsonNode? Value)[]
        {
            ("", "contract", JsonValue.Create("another.contract")), ("", "version", JsonValue.Create(2)),
            ("", "extra", JsonValue.Create(true)), ("identity", "projectId", JsonValue.Create("guessed-project")),
            ("identity", "requestId", JsonValue.Create("another-request")),
            ("identity", "profileId", JsonValue.Create("another-profile")),
            ("identity", "consumerRole", JsonValue.Create("helper")),
            ("identity", "consumerKind", JsonValue.Create("compactor")),
            ("identity", "definitionId", JsonValue.Create("another:chat")),
            ("selection", "providerId", JsonValue.Create("another:provider")),
            ("selection", "providerVersion", JsonValue.Create(2)),
            ("selection", "referenceKind", JsonValue.Create("another.kind")),
            ("selection", "contributionId", JsonValue.Create("another:source")),
            ("selection", "sourceRevision", JsonValue.Create(0)),
            ("selection", "sourceId", JsonValue.Create("personality/v1/cerebral/01/" + seed.SourceSha256)),
            ("selection", "sourceSha256", JsonValue.Create(seed.SourceSha256.ToUpperInvariant())),
            ("selection", "settingsRevision", JsonValue.Create(-1)),
            ("selection", "settingsSha256", JsonValue.Create("invalid")),
            ("selection", "requirement", JsonValue.Create("required")),
            ("selection", "priority", JsonValue.Create(1)),
            ("selection", "beforeCurrentInput", JsonValue.Create(false)),
            ("selection", "maximumUtf8Bytes", JsonValue.Create(2049))
        };
        foreach (var mutation in mutations)
            Reject(() => PersonalityAdmission.Restore(Mutated(mutation.Area, mutation.Field, mutation.Value),
                provisional, consumer), "mutated contract/identity/source or optional policy fails closed");
        var duplicated = snapshot.GetRawText().Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal);
        using (var parsed = JsonDocument.Parse(duplicated))
            Reject(() => PersonalityAdmission.Restore(parsed.RootElement, provisional, consumer),
                "duplicate preserved fields fail closed");
        using (var parsed = JsonDocument.Parse("null"))
            Reject(() => PersonalityAdmission.Restore(parsed.RootElement, provisional, consumer),
                "null preserved seed fails closed");
        Reject(() => PersonalityAdmission.Restore(snapshot, provisional, consumer with { Kind = PersonalityConsumerKind.Compactor }),
            "excluded consumer cannot restore owner seed");
        Reject(() => PersonalityAdmission.Restore(snapshot, provisional with { RequestId = "another-request" }, consumer),
            "seed cannot cross a known pre-acceptance request");

        // Exercise the actual compiled SQLite acceptance transaction, not a guessed assignment.
        var database = Path.Combine(fixture, "personality-seed-workflow.db");
        var store = new WorkflowStore(new EventJournal(database));
        var prior = store.Submit(Input("prior-request") with { ProjectId = "assigned-project" });
        store.Cancel(prior.Id); // Exact private queued fixture; creates retained history without effects.
        var frozenSettings = PersonalityContract.Element(new { personality = new { state = "ready", seed = snapshot } });
        var accepted = store.Submit(provisional with { SettingsSnapshot = frozenSettings }).Submission;
        check(provisional.ProjectId is null && provisional.ConversationSequence == 0 &&
            accepted.ProjectId == "assigned-project" && accepted.ConversationSequence > 0,
            "seed: actual WorkflowStore supplies final inherited project and nonzero history boundary");
        var bound = seed.BindAccepted(accepted, consumer, snapshot);
        var request = bound.CreateRequest(accepted, consumer, 4, 2);
        check(request.References[0].SourceId == seed.SourceId && request.References[0].Requirement == ContextRequirement.Optional,
            "seed: provisional capture binds successfully after actual acceptance");
        Reject(() => bound.CreateRequest(accepted with { ProjectId = null }, consumer, 4, 2),
            "bound request rejects changed authoritative project");
        Reject(() => bound.CreateRequest(accepted with { ConversationSequence = accepted.ConversationSequence + 1 }, consumer, 4, 2),
            "bound request rejects changed authoritative history boundary");
        Reject(() => seed.BindAccepted(accepted with { ProfileId = "another-profile" }, consumer, snapshot),
            "final acceptance cannot borrow a seed from another profile");
        Reject(() => seed.BindAccepted(accepted, consumer with { Role = "helper" }, snapshot),
            "final accepted helper role remains excluded");

        settings = await Command("personality.settings", new { });
        await Command("personality.select", new
        {
            expectedRevision = settings.GetProperty("revision").GetInt64(),
            expectedSha256 = settings.GetProperty("sha256").GetString(), enabled = true, presetId = "succinct"
        });
        var fresh = await PersonalityAdmissionEngine.CaptureAsync(plugin, provisional, consumer);
        Reject(() => fresh.Admission!.BindAccepted(accepted, consumer, snapshot),
            "a freshly recaptured source cannot replace the seed persisted by earlier acceptance");

        settings = await Command("personality.settings", new { });
        await Command("personality.select", new
        {
            expectedRevision = settings.GetProperty("revision").GetInt64(),
            expectedSha256 = settings.GetProperty("sha256").GetString(), enabled = false, presetId = "succinct"
        });
        plugin.Dispose();
        var reopened = new WorkflowStore(new EventJournal(database)).Get(accepted.RequestId).Submission;
        var persisted = reopened.SettingsSnapshot.GetProperty("personality").GetProperty("seed");
        var restored = PersonalityAdmission.RestoreAccepted(persisted, reopened, consumer).CreateRequest(reopened, consumer, 4, 2);
        check(PersonalityContract.Element(restored).GetRawText() == PersonalityContract.Element(request).GetRawText(),
            "seed: actual SQLite reopen restores frozen request after disable/disposal with no live settings recapture");

        var unicode = provisional with { RequestId = new string('界', 256), ConversationId = new string('界', 256),
            ProfileId = new string('界', 128) };
        var unicodeConsumer = consumer with { ProfileId = unicode.ProfileId };
        var unicodeNode = JsonNode.Parse(snapshot.GetRawText())!;
        unicodeNode["identity"]!["requestId"] = unicode.RequestId;
        unicodeNode["identity"]!["conversationId"] = unicode.ConversationId;
        unicodeNode["identity"]!["profileId"] = unicode.ProfileId;
        var relaxed = JsonSerializer.Serialize(unicodeNode,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var canonical = JsonSerializer.Serialize(unicodeNode);
        check(Encoding.UTF8.GetByteCount(relaxed) <= 4096 && Encoding.UTF8.GetByteCount(canonical) > 4096,
            "seed: Unicode fixture distinguishes incoming and canonical durable byte bounds");
        using (var parsed = JsonDocument.Parse(relaxed))
            Reject(() => PersonalityAdmission.Restore(parsed.RootElement, unicode, unicodeConsumer),
                "canonical reserialization overflow cannot restore an unpersistable seed");

        var dependency = new PluginDependencyDeclaration("pegasus.personality", null, "1.0.1", "2.0.0", false);
        PluginDependencyPolicy.Validate(dependency);
        check(PluginDependencyPolicy.Compatible(dependency, "1.0.1") &&
            PluginDependencyPolicy.Compatible(dependency, "1.9.0") &&
            !PluginDependencyPolicy.Compatible(dependency, "1.0.0") &&
            !PluginDependencyPolicy.Compatible(dependency, "2.0.0"),
            "seed: existing frozen dependency policy supports minimum-inclusive maximum-exclusive range");
    }
}
