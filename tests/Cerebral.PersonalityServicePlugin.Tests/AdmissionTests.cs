using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Context;
using Cerebral.Engine.Workflows;
using Cerebral.PersonalityServicePlugin;

static class AdmissionTests
{
    public static async Task RunAsync(string fixture, Action<bool, string> check)
    {
        WorkflowSubmission Owner() => new("admission-request", "admission-chat", "admission-profile",
            ConversationWorkflowContract.DefinitionId, ConversationWorkflowContract.DefinitionVersion,
            "Synthetic owner request.", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject,
            Caller: new(EventActorKind.User, "owner")) { ProjectId = "admission-project", ConversationSequence = 7 };
        var owner = Owner();
        var consumer = new PersonalityConsumer(owner.ProfileId, "lead", PersonalityConsumerKind.UserFacingConversation);
        async Task<JsonElement> Command(PersonalityServicePlugin plugin, string op, object args) =>
            PersonalityContract.Element((await plugin.ExecuteAsync(op, PersonalityContract.Element(args), default))!);
        async Task<JsonElement> Settings(PersonalityServicePlugin plugin) => await Command(plugin, "personality.settings", new { });
        async Task<JsonElement> Select(PersonalityServicePlugin plugin, JsonElement settings, bool enabled, string id) =>
            await Command(plugin, "personality.select", new
            {
                expectedRevision = settings.GetProperty("revision").GetInt64(),
                expectedSha256 = settings.GetProperty("sha256").GetString(), enabled, presetId = id
            });
        async Task<JsonElement> Save(PersonalityServicePlugin plugin, JsonElement settings, string text) =>
            (await Command(plugin, "personality.save", new
            {
                expectedRevision = settings.GetProperty("revision").GetInt64(),
                expectedSha256 = settings.GetProperty("sha256").GetString(),
                id = "admission-custom", name = "Synthetic style", text
            })).GetProperty("settings");
        void Reject(Action action, string label)
        {
            var rejected = false;
            try { action(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException)
            { rejected = true; }
            check(rejected, "admission: " + label);
        }

        var absent = await PersonalityAdmissionEngine.CaptureAsync(null, owner, consumer);
        check(absent.Status == PersonalityAdmissionStatus.Unavailable && absent.Admission is null &&
            absent.Reason == "component-unavailable", "admission: absent optional component supplies no replacement");

        using var plugin = new PersonalityServicePlugin();
        var directory = Path.Combine(fixture, "admission-data");
        plugin.Initialize(new FixtureHost(directory));
        var disabled = await PersonalityAdmissionEngine.CaptureAsync(plugin, owner, consumer);
        check(disabled.Status == PersonalityAdmissionStatus.Disabled && disabled.Admission is null,
            "admission: disabled default produces no invocation");
        foreach (var kind in new[] { PersonalityConsumerKind.Helper, PersonalityConsumerKind.Compactor,
            PersonalityConsumerKind.Auditor, PersonalityConsumerKind.Programming, (PersonalityConsumerKind)99 })
        {
            var excluded = await PersonalityAdmissionEngine.CaptureAsync(null, owner, consumer with { Kind = kind });
            check(excluded.Status == PersonalityAdmissionStatus.NotApplicable && excluded.Admission is null,
                "admission: non-user-facing consumer excluded before component access");
        }
        foreach (var excludedConsumer in new[] { consumer with { Role = "helper" },
            consumer with { ProfileId = "another-profile" } })
            check((await PersonalityAdmissionEngine.CaptureAsync(null, owner, excludedConsumer)).Status ==
                PersonalityAdmissionStatus.NotApplicable, "admission: helper or mismatched profile excluded before access");
        foreach (var excludedOwner in new[]
        {
            owner with { ParentRunId = "parent" }, owner with { Caller = new(EventActorKind.Agent, "owner") },
            owner with { Caller = new(EventActorKind.Service, "owner") }, owner with { Caller = null },
            owner with { Caller = new(EventActorKind.User, "other") }, owner with { DefinitionId = "fixture:compactor" }
        })
            check((await PersonalityAdmissionEngine.CaptureAsync(null, excludedOwner, consumer)).Status ==
                PersonalityAdmissionStatus.NotApplicable, "admission: non-owner or child lineage excluded before access");

        var settings = await Select(plugin, await Settings(plugin), true, "cerebral");
        var generic = await PersonalityAdmissionEngine.CaptureAsync(plugin, owner, consumer);
        check(generic.Status == PersonalityAdmissionStatus.Ready && generic.Admission is not null,
            "admission: enabled built-in captures exact stable source");
        settings = await Save(plugin, settings, "Use the first synthetic stable style.");
        settings = await Select(plugin, settings, true, "admission-custom");
        var captured = await PersonalityAdmissionEngine.CaptureAsync(plugin, owner, consumer);
        var binding = captured.Admission!;
        var bound = binding.BindAccepted(owner, consumer, binding.ToSnapshot());
        var request = bound.CreateRequest(owner, consumer, 4, 2);
        check(captured.Status == PersonalityAdmissionStatus.Ready && binding.SourceRevision == 1 &&
            binding.SettingsRevision == settings.GetProperty("revision").GetInt64() &&
            binding.SettingsSha256 == settings.GetProperty("sha256").GetString(),
            "admission: source and selection CAS are captured without style bodies");
        check(request.References.Single().Requirement == ContextRequirement.Optional &&
            request.References.Single().BeforeCurrentInput && request.MaximumUtf8Bytes == 2048 &&
            ((IList<ContextProviderReference>)request.References).IsReadOnly,
            "admission: bounded optional request has immutable references");
        Reject(() => ((IList<ContextProviderReference>)request.References).Add(request.References[0]),
            "reference list cannot be mutated after capture");

        var frozen = PersonalityContract.Element(request).GetRawText();
        settings = await Save(plugin, settings, "Use the second synthetic stable style.");
        var newer = await PersonalityAdmissionEngine.CaptureAsync(plugin, owner, consumer);
        check(newer.Admission!.SourceRevision == 2 && newer.Admission.SourceId != binding.SourceId,
            "admission: later save affects only new captures");
        settings = await Select(plugin, settings, false, "cerebral");
        check((await PersonalityAdmissionEngine.CaptureAsync(plugin, owner, consumer)).Status == PersonalityAdmissionStatus.Disabled &&
            PersonalityContract.Element(bound.CreateRequest(owner, consumer, 4, 2)).GetRawText() == frozen,
            "admission: later disable does not change accepted source");
        var nextRequest = bound.CreateRequest(owner, consumer, 8, 3);
        check(nextRequest.ContextRevision == 8 && nextRequest.ExclusionRevision == 3 &&
            nextRequest.References[0] == request.References[0],
            "admission: host context revisions can advance without selecting new personality");

        var restored = JsonSerializer.Deserialize<ContextProviderRequest>(frozen, PersonalityContract.Json)!;
        var registry = new WorkflowRegistry([plugin]);
        var provider = registry.GetContextProvider(PersonalityContract.ProviderId, 1);
        _ = ContextProviderNormalizer.ReadRequest(provider.Provider, PersonalityContract.Element(restored));
        var effect = new WorkflowCommandContext("admission-effect", owner, PersonalityContract.Element(restored))
            { RunId = "admission-run" };
        var resolved = await provider.ExecuteAsync(effect, default);
        var normalized = ContextProviderNormalizer.Normalize(provider.Provider, restored,
            ContextProviderNormalizer.ReadResolution(resolved.Data!.Value), effect.EffectId, resolved.ProviderReceiptId!);
        check(resolved.State == WorkflowEffectState.Succeeded && normalized.Window.Contributions.Single().SourceRevision == 1 &&
            normalized.Window.Contributions.Single().ExactReadHandle is not null &&
            ContextValidation.GetCoveredManifest(normalized.Window).Count == 0,
            "admission: serialized frozen request resolves through actual registry/provider/normalizer and excludes compaction");
        check(JsonSerializer.SerializeToUtf8Bytes(resolved.Data.Value, PersonalityContract.Json).Length <= 2048,
            "admission: complete resolved output stays inside the serialized budget");
        using (var restarted = new PersonalityServicePlugin())
        {
            restarted.Initialize(new FixtureHost(directory));
            var oldProvider = (IContextProviderCommandHandler)restarted.Commands.Single();
            check((await oldProvider.ReconcileAsync(effect, default)).Data!.Value.GetRawText() ==
                resolved.Data.Value.GetRawText(), "admission: exact frozen source survives plugin restart");
        }
        foreach (var different in new[]
        {
            owner with { RequestId = "other-request" }, owner with { ConversationId = "other-chat" },
            owner with { ProfileId = "other-profile" }, owner with { ProjectId = null },
            owner with { DefinitionVersion = "999.0.0" }, owner with { ConversationSequence = 8 },
            owner with { ParentRunId = "helper-parent" }, owner with { Caller = new(EventActorKind.Agent, "owner") },
            owner with { DefinitionId = "fixture:compactor" }
        })
            Reject(() => bound.CreateRequest(different, consumer, 4, 2), "capture cannot cross accepted target or lineage");
        Reject(() => bound.CreateRequest(owner, consumer with { Kind = PersonalityConsumerKind.Compactor }, 4, 2),
            "compactor cannot borrow a captured owner style");
        Reject(() => bound.CreateRequest(owner, consumer with { Role = "helper" }, 4, 2),
            "helper cannot borrow a captured owner style");
        Reject(() => bound.CreateRequest(owner, consumer, -1, 2), "negative context revision rejected");
        Reject(() => bound.CreateRequest(owner, consumer, 4, -1), "negative exclusion revision rejected");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var propagated = false;
            try { await PersonalityAdmissionEngine.CaptureAsync(plugin, owner, consumer, canceled.Token); }
            catch (OperationCanceledException) { propagated = true; }
            check(propagated, "admission: cancellation does not become optional-unavailable");
        }
        foreach (var invalid in new[] { owner with { RequestId = "contains whitespace" },
            owner with { ConversationId = "contains" + (char)10 + "control" }, owner with { RequestId = new string('x', 257) } })
        {
            var rejected = false;
            try { await PersonalityAdmissionEngine.CaptureAsync(plugin, invalid, consumer); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "admission: malformed accepted target fails closed");
        }
        File.WriteAllText(Path.Combine(directory, PersonalityStore.FileName),
            """{"schemaVersion":999,"revision":0,"enabled":false,"selectedPresetId":"cerebral","versions":[]}""");
        var invalidData = await PersonalityAdmissionEngine.CaptureAsync(plugin, owner, consumer);
        check(invalidData.Status == PersonalityAdmissionStatus.Unavailable && invalidData.Admission is null &&
            invalidData.Reason == "selection-unavailable", "admission: valid JSON with invalid schema degrades explicitly");
        File.WriteAllText(Path.Combine(directory, PersonalityStore.FileName), "{ invalid synthetic state");
        var corrupt = await PersonalityAdmissionEngine.CaptureAsync(plugin, owner, consumer);
        check(corrupt.Status == PersonalityAdmissionStatus.Unavailable && corrupt.Admission is null &&
            corrupt.Reason == "selection-unavailable", "admission: corrupt optional storage supplies bounded omission only");
        var missing = await provider.ExecuteAsync(effect, default);
        check(missing.State == WorkflowEffectState.Failed && missing.Data!.Value.GetProperty("failureKind").GetString() ==
            "source-unavailable", "admission: unavailable exact source is not replaced with latest or default style");
        plugin.Dispose();
        var disposed = await PersonalityAdmissionEngine.CaptureAsync(plugin, owner, consumer);
        check(disposed.Status == PersonalityAdmissionStatus.Unavailable && disposed.Admission is null,
            "admission: disposed component degrades without resurrecting its source");
    }
}
