using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.PersonalityServicePlugin;

static class AdmissionCommandTests
{
    public static async Task RunAsync(string fixture, Action<bool, string> check)
    {
        void Check(bool value, string label) => check(value, "commands: " + label);
        JsonElement Json(object value) => PersonalityContract.Element(value);
        JsonElement Mutate(JsonElement value, Action<JsonObject> mutation)
        {
            var node = JsonNode.Parse(value.GetRawText())!.AsObject(); mutation(node);
            return PersonalityContract.Parse(node.ToJsonString());
        }
        async Task<JsonElement> Command(PersonalityServicePlugin plugin, string name, JsonElement arguments,
            CancellationToken token = default)
        {
            var reply = await plugin.ExecuteAsync(name, arguments, token);
            Check(reply is JsonElement, "admission crosses the service boundary as a detached JSON value");
            return (JsonElement)reply!;
        }
        async Task Reject(PersonalityServicePlugin plugin, string name, JsonElement arguments, string label)
        {
            var refused = false;
            try { await plugin.ExecuteAsync(name, arguments, default); }
            catch (InvalidDataException error)
            {
                refused = error.Message == "The personality admission command payload is invalid, mismatched or unavailable." &&
                    error.InnerException is null;
            }
            Check(refused, label);
        }
        async Task<JsonElement> Settings(PersonalityServicePlugin plugin) =>
            Json((await plugin.ExecuteAsync("personality.settings", Json(new { }), default))!);
        async Task Select(PersonalityServicePlugin plugin, bool enabled, string id)
        {
            var current = await Settings(plugin);
            await plugin.ExecuteAsync("personality.select", Json(new
            {
                expectedRevision = current.GetProperty("revision").GetInt64(),
                expectedSha256 = current.GetProperty("sha256").GetString(), enabled, presetId = id
            }), default);
        }

        var submission = new WorkflowSubmission("command-request", "command-conversation", "command-profile",
            ConversationWorkflowContract.DefinitionId, ConversationWorkflowContract.DefinitionVersion,
            "This private prompt must never cross admission commands.", null,
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, Caller: new(EventActorKind.User, "owner"))
            { ProjectId = "command-project", ConversationSequence = 12 };
        var consumer = new PersonalityConsumer(submission.ProfileId, "lead", PersonalityConsumerKind.UserFacingConversation);
        Dictionary<string, object?> Identity(WorkflowSubmission value, bool accepted)
        {
            var result = new Dictionary<string, object?>
            {
                ["requestId"] = value.RequestId, ["conversationId"] = value.ConversationId,
                ["profileId"] = value.ProfileId, ["definitionId"] = value.DefinitionId,
                ["definitionVersion"] = value.DefinitionVersion
            };
            if (accepted) { result["projectId"] = value.ProjectId; result["conversationSequence"] = value.ConversationSequence; }
            return result;
        }
        object Consumer(PersonalityConsumer value) => new
        {
            profileId = value.ProfileId, role = value.Role,
            kind = JsonNamingPolicy.CamelCase.ConvertName(value.Kind.ToString())
        };
        object? Caller(WorkflowSubmission value) => value.Caller is null ? null : new
        {
            kind = JsonNamingPolicy.CamelCase.ConvertName(value.Caller.Kind.ToString()), id = value.Caller.Id
        };
        JsonElement Capture(WorkflowSubmission? owner = null, PersonalityConsumer? target = null) => Json(new
        {
            schemaVersion = 1,
            identity = Identity(owner ?? submission, false), consumer = Consumer(target ?? consumer),
            caller = Caller(owner ?? submission), parentRunId = (owner ?? submission).ParentRunId
        });
        JsonElement Restore(JsonElement captured, WorkflowSubmission? owner = null, PersonalityConsumer? target = null) => Json(new
        {
            schemaVersion = 1,
            acceptedTarget = Identity(owner ?? submission, true), consumer = Consumer(target ?? consumer),
            caller = Caller(owner ?? submission), parentRunId = (owner ?? submission).ParentRunId,
            seed = captured.GetProperty("seed"), seedSha256 = captured.GetProperty("seedSha256"),
            contextRevision = 7L, exclusionRevision = 3L
        });
        string captureName = PersonalityAdmissionCommands.CaptureOperation, restoreName = PersonalityAdmissionCommands.RestoreOperation;
        var data = Path.Combine(fixture, "command-data");
        using var plugin = new PersonalityServicePlugin();
        plugin.Initialize(new FixtureHost(data));
        Check(plugin.Operations.Contains(captureName) && plugin.Operations.Contains(restoreName) &&
            plugin.Operations.Count == 7 && plugin.ToolExtensions.Count == 0,
            "two advertised host commands add no model tools and preserve five owner operations");
        var arguments = Capture();
        Check(!arguments.GetRawText().Contains(submission.UserText, StringComparison.Ordinal) &&
            !arguments.GetProperty("identity").TryGetProperty("projectId", out _) &&
            !arguments.GetProperty("identity").TryGetProperty("conversationSequence", out _),
            "capture DTO omits prompt, settings, projected project and history");
        var disabled = await Command(plugin, captureName, arguments);
        Check(disabled.GetProperty("status").GetString() == "disabled" &&
            disabled.GetProperty("seed").ValueKind == JsonValueKind.Null &&
            disabled.GetProperty("seedSha256").ValueKind == JsonValueKind.Null &&
            !File.Exists(Path.Combine(data, PersonalityStore.FileName)),
            "disabled command returns explicit bounded omission without storage publication");
        await Select(plugin, true, "cerebral");
        var captured = await Command(plugin, captureName, arguments);
        var seed = captured.GetProperty("seed");
        Check(captured.GetProperty("schemaVersion").GetInt32() == 1 && captured.GetProperty("status").GetString() == "ready" &&
            captured.GetProperty("reason").ValueKind == JsonValueKind.Null &&
            captured.GetProperty("seedSha256").GetString() ==
                PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(seed.GetRawText())) &&
            PersonalityContract.Utf8.GetByteCount(captured.GetRawText()) <= PersonalityAdmissionCommands.MaximumOutputBytes &&
            !captured.GetRawText().Contains(submission.UserText, StringComparison.Ordinal),
            "ready reply supplies only canonical seed and digest inside the output bound");
        var restoreArguments = Restore(captured);
        var restored = await Command(plugin, restoreName, restoreArguments);
        var request = restored.GetProperty("request").Deserialize<ContextProviderRequest>(PersonalityContract.Json)!;
        Check(restored.GetProperty("schemaVersion").GetInt32() == 1 && request.References.Count == 1 &&
            request.References[0].Requirement == ContextRequirement.Optional &&
            request.References[0].SourceId == seed.GetProperty("selection").GetProperty("sourceId").GetString() &&
            request.ContextRevision == 7 && request.ExclusionRevision == 3 &&
            request.MaximumUtf8Bytes == PersonalityContract.MaximumUtf8Bytes,
            "restore supplies the existing bounded optional typed provider request");
        await Select(plugin, true, "succinct");
        var newer = await Command(plugin, captureName, arguments);
        var swapped = Mutate(restoreArguments, node => node["seed"] = JsonNode.Parse(newer.GetProperty("seed").GetRawText()));
        await Reject(plugin, restoreName, swapped, "new capture for same RID cannot replace accepted seed/digest");
        await Select(plugin, false, "succinct");
        var older = await Command(plugin, restoreName, restoreArguments);
        Check(older.GetProperty("request").GetRawText() == restored.GetProperty("request").GetRawText(),
            "selection changes and disable do not recapture accepted source");

        var malformedCapture = new Action<JsonObject>[]
        {
            n => n["schemaVersion"] = 2, n => n["schemaVersion"] = "1", n => n["contract"] = "another",
            n => n["UserText"] = "forbidden", n => n["seed"] = new JsonObject(),
            n => n["identity"] = null, n => n["consumer"] = null,
            n => n["identity"]!.AsObject()["settingsSnapshot"] = new JsonObject(),
            n => n["identity"]!.AsObject()["projectId"] = "premature",
            n => n["identity"]!.AsObject()["requestId"] = "white space",
            n => n["caller"]!.AsObject()["kind"] = 0,
            n => n["caller"]!.AsObject()["kind"] = "User",
            n => n["caller"]!.AsObject()["displayName"] = "Owner",
            n => n["consumer"]!.AsObject()["kind"] = "1",
            n => n["consumer"]!.AsObject()["role"] = new string('r', 65),
            n => n["consumer"]!.AsObject().Remove("profileId")
        };
        foreach (var mutation in malformedCapture)
            await Reject(plugin, captureName, Mutate(arguments, mutation), "closed capture shape/type/version refuses malformed input");
        await Reject(plugin, captureName, PersonalityContract.Parse(arguments.GetRawText().Replace(
            "\"command-request\"", "\"\\uD800\"", StringComparison.Ordinal)),
            "unpaired surrogate identity refuses before canonicalization");
        var malformedRestore = new Action<JsonObject>[]
        {
            n => n["schemaVersion"] = 2, n => n["schemaVersion"] = 1.5, n => n["contract"] = "another",
            n => n["grants"] = new JsonArray("owner"),
            n => n["seed"] = null, n => n["seedSha256"] = new string('F', 64),
            n => n["seedSha256"] = new string('0', 64),
            n => n["seed"]!.AsObject()["version"] = 2,
            n => n["seed"]!.AsObject()["selection"]!.AsObject()["sourceRevision"] = 100,
            n => n["acceptedTarget"]!.AsObject()["requestId"] = "another-request",
            n => n["acceptedTarget"]!.AsObject()["conversationId"] = "another-conversation",
            n => n["acceptedTarget"]!.AsObject()["profileId"] = "another-profile",
            n => n["acceptedTarget"]!.AsObject()["definitionVersion"] = "another-version",
            n => n["caller"] = null,
            n => n["caller"]!.AsObject()["id"] = "another-owner",
            n => n["parentRunId"] = "helper-parent",
            n => n["acceptedTarget"]!.AsObject()["conversationSequence"] = -1,
            n => n["acceptedTarget"]!.AsObject()["conversationSequence"] = "12",
            n => n["acceptedTarget"]!.AsObject()["projectId"] = new JsonObject(),
            n => n["consumer"]!.AsObject()["role"] = "helper",
            n => n["consumer"]!.AsObject()["kind"] = "compactor",
            n => n["contextRevision"] = -1, n => n["contextRevision"] = 1.5,
            n => n["exclusionRevision"] = "3", n => n.Remove("seedSha256")
        };
        foreach (var mutation in malformedRestore)
            await Reject(plugin, restoreName, Mutate(restoreArguments, mutation), "closed restore or mismatched persisted identity refuses explicitly");
        foreach (var name in new[] { captureName, restoreName })
        {
            var value = name == captureName ? arguments : restoreArguments;
            var raw = value.GetRawText();
            await Reject(plugin, name, PersonalityContract.Parse(raw.Insert(1, "\"schemaVersion\":1,")),
                "duplicate envelope fields refuse");
            var identity = value.GetProperty(name == captureName ? "identity" : "acceptedTarget").GetRawText();
            await Reject(plugin, name, PersonalityContract.Parse(raw.Replace(identity,
                identity.Insert(1, "\"requestId\":\"command-request\","), StringComparison.Ordinal)),
                "duplicate nested identity fields refuse");
            var maximum = name == captureName ? PersonalityAdmissionCommands.MaximumCaptureInputBytes :
                PersonalityAdmissionCommands.MaximumRestoreInputBytes;
            await Reject(plugin, name, PersonalityContract.Parse(raw.Insert(1, new string(' ', maximum))),
                "whole input byte bound is enforced before dispatch");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            var canceledCorrectly = false;
            try { await plugin.ExecuteAsync(name, value, canceled.Token); }
            catch (OperationCanceledException) { canceledCorrectly = true; }
            Check(canceledCorrectly, "cancellation propagates through command dispatch");
        }

        var file = Path.Combine(data, PersonalityStore.FileName);
        File.WriteAllText(file, "{\"schemaVersion\":999}");
        foreach (var excluded in new[]
        {
            consumer with { Kind = PersonalityConsumerKind.Helper },
            consumer with { Kind = PersonalityConsumerKind.Compactor },
            consumer with { Kind = PersonalityConsumerKind.Auditor },
            consumer with { Kind = PersonalityConsumerKind.Programming },
            consumer with { Role = "helper" }, consumer with { ProfileId = "another-profile" }
        })
        {
            var result = await Command(plugin, captureName, Capture(target: excluded));
            Check(result.GetProperty("status").GetString() == "notApplicable" &&
                result.GetProperty("reason").GetString() == "scope-excluded" &&
                result.GetProperty("seed").ValueKind == JsonValueKind.Null,
                "semantic non-user-facing scope excluded before corrupt store access");
        }
        foreach (var excluded in new[]
        {
            submission with { Caller = null },
            submission with { Caller = new(EventActorKind.Service, "owner") },
            submission with { Caller = new(EventActorKind.User, "another-owner") },
            submission with { ParentRunId = "parent" },
            submission with { DefinitionId = "another:workflow" }
        })
        {
            var result = await Command(plugin, captureName, Capture(owner: excluded));
            Check(result.GetProperty("status").GetString() == "notApplicable", "semantic lineage excluded before corrupt store access");
        }
        var unavailable = await Command(plugin, captureName, arguments);
        Check(unavailable.GetProperty("status").GetString() == "unavailable" &&
            unavailable.GetProperty("reason").GetString() == "selection-unavailable" &&
            unavailable.GetProperty("seed").ValueKind == JsonValueKind.Null,
            "corrupt optional capture degrades without exposing storage or fallback");
        var corruptRestore = await Command(plugin, restoreName, restoreArguments);
        Check(corruptRestore.GetProperty("request").GetRawText() == restored.GetProperty("request").GetRawText(),
            "restore never reads corrupted live settings");
        using (var locked = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedRestore = await Command(plugin, restoreName, restoreArguments);
            Check(lockedRestore.GetProperty("request").GetRawText() == restored.GetProperty("request").GetRawText(),
                "restore never opens exclusively locked settings");
        }
        File.Delete(file);
        var absentRestore = await Command(plugin, restoreName, restoreArguments);
        Check(absentRestore.GetProperty("request").GetRawText() == restored.GetProperty("request").GetRawText() && !File.Exists(file),
            "restore neither reads nor recreates absent settings");

        using var uninitialized = new PersonalityServicePlugin();
        var uninitializedCapture = await Command(uninitialized, captureName, arguments);
        Check(uninitializedCapture.GetProperty("status").GetString() == "unavailable", "uninitialized capture is explicitly unavailable");
        await Reject(uninitialized, restoreName, restoreArguments, "uninitialized restore refuses");
        plugin.Dispose();
        var disposedCapture = await Command(plugin, captureName, arguments);
        Check(disposedCapture.GetProperty("status").GetString() == "unavailable", "disposed capture does not resurrect service state");
        await Reject(plugin, restoreName, restoreArguments, "disposed restore refuses an old service handle");
    }
}
