using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Focused deterministic marker admission checks. No model, service
/// package, disk mutation or live Worker is needed for this fixture.</summary>
public static class SourceBoundBackgroundWorkerChecks
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            checks++;
        }
        void Reject(JsonElement value, string label)
        {
            try { _ = WorkerExecutionSnapshot.Read(value); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or JsonException)
            { checks++; return; }
            throw new InvalidOperationException(label);
        }
        static JsonElement Parse(string json) { using var parsed = JsonDocument.Parse(json); return parsed.RootElement.Clone(); }

        var source = new SourceBoundWorkflowSourceFacts(1, "owner-fixture", "example.plugin",
            "1.0.0", "generation-fixture", "pegasus.conversation:chat", "1.0.1",
            "source-conversation", "source-profile", "source-request", "source-event", 1,
            new string('a', 64), null, "New conversation", 0, 1, "title-profile", 1);
        var marker = new SourceBoundWorkflowBinding(1, "title-request", "title-conversation", source);
        var profile = new AgentProfile { Id = "title-profile", Revision = 1,
            WorkerId = "local", ModelWorkerId = "local" };
        var package = new ServicePluginPackageBinding("example.plugin", "1.0.0", true, [],
            null, "generation-fixture");
        var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version,
            profile, "", "", null, [], [], [], [], false, "fixture-routing", [package], [])
            { SourceBoundBackground = marker };
        var current = WorkerExecutionSnapshot.Bounded(snapshot);
        Check(WorkerExecutionSnapshot.Read(current).SourceBoundBackground == marker,
            "edition six retains the exact host marker");

        var remoteProfile = new AgentProfile { Id = "title-profile", Revision = 1,
            WorkerId = "local", ModelWorkerId = "gb10", ModelId = "remote-model" };
        var remote = snapshot with
        {
            Profile = remoteProfile,
            Model = new WorkerExecutionSnapshot.ModelBinding("remote-model", "fixture-model.gguf",
                null, [], 4096, 1, 0, ModelWorkerId: "gb10",
                ModelWorkerInstallationId: "fixture-gb10-installation")
        };
        var remoteWire = WorkerExecutionSnapshot.Bounded(remote);
        Check(WorkerExecutionSnapshot.Read(remoteWire).SourceBoundBackground == marker &&
            WorkerExecutionSnapshot.Read(remoteWire).Profile.ModelWorkerId == "gb10",
            "source-bound marker preserves the ordinary verified remote model route");
        var missingRemoteOwner = JsonNode.Parse(remoteWire.GetRawText())!.AsObject();
        missingRemoteOwner["model"]!["modelWorkerInstallationId"] = null;
        Reject(Parse(missingRemoteOwner.ToJsonString()),
            "remote model still requires its frozen installation identity");

        var absent = JsonNode.Parse(current.GetRawText())!.AsObject();
        absent.Remove("sourceBoundBackground");
        Check(WorkerExecutionSnapshot.Read(Parse(absent.ToJsonString())).SourceBoundBackground is null,
            "historical edition six without marker remains valid");
        var legacy = JsonNode.Parse(current.GetRawText())!.AsObject();
        legacy["resolvedSettingsVersion"] = 5;
        legacy.Remove("approvedPluginSettings");
        legacy.Remove("toolReceiptConsumer");
        legacy.Remove("selectedToolOwners");
        Reject(Parse(legacy.ToJsonString()), "edition five must refuse the new marker");
        var nullMarker = JsonNode.Parse(current.GetRawText())!.AsObject();
        nullMarker["sourceBoundBackground"] = null;
        Check(WorkerExecutionSnapshot.Read(Parse(nullMarker.ToJsonString())).SourceBoundBackground is null,
            "explicit null marker carries no source-bound authority");
        var wrongProfile = JsonNode.Parse(current.GetRawText())!.AsObject();
        wrongProfile["sourceBoundBackground"]!["source"]!["selectedProfileId"] = "other-profile";
        Reject(Parse(wrongProfile.ToJsonString()), "marker cannot borrow another profile");
        var wrongPackage = JsonNode.Parse(current.GetRawText())!.AsObject();
        wrongPackage["sourceBoundBackground"]!["source"]!["packageGeneration"] = "other-generation";
        Reject(Parse(wrongPackage.ToJsonString()), "marker cannot borrow another package generation");
        var badHash = JsonNode.Parse(current.GetRawText())!.AsObject();
        badHash["sourceBoundBackground"]!["source"]!["sourceInputSha256"] = new string('A', 64);
        Reject(Parse(badHash.ToJsonString()), "source hash must be canonical lowercase SHA256");
        var compact = JsonNode.Parse(current.GetRawText())!.ToJsonString();
        var duplicate = compact.Replace("\"sourceBoundBackground\":{\"schemaVersion\":1,",
            "\"sourceBoundBackground\":{\"schemaVersion\":1,\"schemaVersion\":1,");
        Check(duplicate != compact, "duplicate fixture reached the marker");
        Reject(Parse(duplicate), "duplicate marker fields refuse");
        return checks;
    }
}
