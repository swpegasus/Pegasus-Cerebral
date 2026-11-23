using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Conversation;
using Cerebral.Core;
using Cerebral.Engine.Context;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Actual pure conversation policy and Engine normalization with deterministic host receipts.
/// A private Registry/SQLite bridge commits intents and receipts, then stops before generation or
/// compaction execution. No provider retrieval, model adapter, tools, package Initialize or network.</summary>
public static class ConversationContextContributionTests
{
    private const string Alpha = "fixture.alpha:resolve", Beta = "fixture.beta:resolve";
    private const string AlphaItem = "fixture.alpha:context", BetaItem = "fixture.beta:context";
    private const string AlphaBody = "ALPHA_GENERIC_REFERENCE_BODY", BetaBody = "BETA_GENERIC_REFERENCE_BODY";
    private const string OpaqueReceipt = "opaque provider receipt / v2 / α";
    private const string NoExecutionDescription = "No-execution fixture boundary.";
    private static readonly JsonSerializerOptions CompactJson = new(CerebralJson.Options) { WriteIndented = false };
    private static int nextFixture;

    public static int Run(string fixtureParent)
    {
        if (!Path.IsPathFullyQualified(fixtureParent))
            throw new ArgumentException("Supply the exact absolute private conversation-fixture parent.", nameof(fixtureParent));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixtureParent));
        Directory.CreateDirectory(parent);
        ScopedFilePolicy.RejectLinks(parent);
        var root = Path.Combine(parent, "conversation-context-" + Guid.NewGuid().ToString("N"));
        RequireOwned(root, parent);
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Conversation context fixture: " + label);
            checks++;
        }
        try
        {
            var edition = ConversationAgentHarness.CreateContextContributionEdition();
            Check(new ConversationAgentHarness().Descriptor.Version == "1.0.1" &&
                  edition.Descriptor.Id == ConversationAgentHarness.Id &&
                  edition.Descriptor.Version == "1.0.2-generic-context.1" &&
                  ConversationAgentHarness.ContextContributionVersion == "1.0.2-generic-context.1",
                "the opt-in edition has distinct identity while the default accepted harness remains 1.0.1");
            using var candidateEntry = new ContextContributionConversationServicePlugin();
            using var defaultEntry = new ConversationServicePlugin();
            var adapterRegistry = new WorkflowRegistry(
                [new Module(ConversationServicePlugin.ModuleId, candidateEntry.Harnesses.Select(harness =>
                    (IWorkflowDefinition)new AgentWorkflowDefinition(harness)).ToArray(), [])]);
            Check(defaultEntry.Harnesses.Count == 2 &&
                  defaultEntry.Harnesses.Select(harness => harness.Descriptor.Version).Order(StringComparer.Ordinal)
                      .SequenceEqual(new[] { "1.0.1", ConversationAgentHarness.ToolContextProjectionVersion }.Order(StringComparer.Ordinal)) &&
                  candidateEntry.Harnesses.Count == 2 && adapterRegistry.Definitions.Count == 2 &&
                  adapterRegistry.GetDefinition(edition.Descriptor.Id, "1.0.1").Descriptor.Version == "1.0.1" &&
                  adapterRegistry.GetDefinition(edition.Descriptor.Id, edition.Descriptor.Version).Descriptor.Version == edition.Descriptor.Version,
                "the passive existing-SPI candidate exports both exact editions through real Registry adapters while the ordinary entry coexists with its distinct receipt-aware edition");
            Check(!edition.Descriptor.InputSchema.GetProperty("properties").TryGetProperty("contextContributions", out _),
                "public input has no host-context admission channel");
            var publicInput = new Fixture(input: new { contextContributions = View(Ready(AlphaItem, AlphaRequest())) });
            Check(publicInput.Refused, "public input cannot inject a generic host context selection");

            var absent = new Fixture(requestId: "base-equivalence"); absent.History(null, include: false);
            var explicitNull = new Fixture(requestId: "base-equivalence"); explicitNull.History(null);
            Check(absent.Is("cerebral.host:catalog") && explicitNull.Is("cerebral.host:catalog") &&
                  JsonElement.DeepEquals(absent.Step.State, explicitNull.Step.State),
                "missing and null initial metadata preserve the same base transition and state");
            var empty = new Fixture(); empty.History(View());
            Check(empty.Is("cerebral.host:catalog") && empty.Items.GetArrayLength() == 0,
                "an explicit empty host collection enters no provider phase");
            foreach (var status in new[] { "omitted", "unavailable", "blocked", "unsupported" })
            {
                var omitted = new Fixture(); omitted.History(View(Item(AlphaItem, status, null)));
                Check(omitted.Is("cerebral.host:catalog") && omitted.Items.GetArrayLength() == 1 &&
                      omitted.Items[0].GetProperty("status").GetString() == status &&
                      !omitted.Items[0].GetProperty("attempted").GetBoolean(),
                    "declared non-ready disposition requests no provider: " + status);
            }

            var ready = new Fixture();
            ready.History(View(Ready(AlphaItem, AlphaRequest()), Ready(BetaItem, BetaRequest())));
            Check(ready.Is(Alpha) && ready.Command.CommandVersion == 2 && ready.Items[0].GetProperty("attempted").GetBoolean() &&
                  !ready.Items[1].GetProperty("attempted").GetBoolean(),
                "the first exact provider-v2 intent is marked once before later items or generation");
            var firstArguments = ready.Command.Arguments.Clone();
            var firstRequest = firstArguments.Deserialize<ContextProviderRequest>(CerebralJson.Options)!;
            Check(firstRequest.ContextRevision == ready.Revision && firstRequest.ExclusionRevision == ready.Exclusion &&
                  SameTemplate(firstRequest, AlphaRequest()) &&
                  JsonElement.DeepEquals(ready.Items[0].GetProperty("request"), Element(AlphaRequest())) &&
                  JsonElement.DeepEquals(firstArguments, ready.Step.State.GetProperty("pendingArguments")),
                "only current revisions rebase while provider version, full references and frozen template remain exact");
            ready.ProviderSuccess();
            Check(ready.Is(Beta) && ready.Command.CommandVersion == 3 &&
                  ready.Items[0].GetProperty("outcome").GetString() == "Succeeded" &&
                  ready.Items[0].GetProperty("providerReceiptId").GetString() == OpaqueReceipt &&
                  ready.Items[0].GetProperty("effectId").GetString() != OpaqueReceipt &&
                  ready.Items[1].GetProperty("attempted").GetBoolean(),
                "an arbitrary bounded receipt is retained separately from effect identity before the next item");
            ready.ProviderSuccess();
            Check(ready.Is("cerebral.host:catalog") &&
                  ready.Items.EnumerateArray().All(item => item.GetProperty("attempted").GetBoolean() &&
                      item.GetProperty("outcome").GetString() == "Succeeded") &&
                  ready.ProviderIntents.SequenceEqual(new[] { Alpha, Beta }),
                "every selected ready item resolves in captured order exactly once before catalog and first generation");
            ready.Catalog();
            Check(ready.Is("cerebral.host:generate") &&
                  ready.Context.Contributions.Where(source => source.Owner == Alpha).Select(source => source.Id)
                      .SequenceEqual(new[] { "fixture.alpha:source-a", "fixture.alpha:source-b" }) &&
                  ready.Context.Contributions.Count(source => source.Owner == Beta) == 1 &&
                  ready.Context.Contributions.Any(source => source.Owner == Alpha && source.Requirement == ContextRequirement.Required) &&
                  ready.Context.Contributions.Any(source => source.Owner == Alpha && source.Requirement == ContextRequirement.Optional) &&
                  ready.Context.Contributions.Where(source => source.Owner == Alpha || source.Owner == Beta)
                      .All(source => source.ExactReadHandle!.StartsWith("opaque-handle/", StringComparison.Ordinal)),
                "multiple normalized references retain their classifications and canonical order with opaque handles");
            Check(!ready.MessageText.Contains(AlphaBody, StringComparison.Ordinal) &&
                  !ready.MessageText.Contains(BetaBody, StringComparison.Ordinal) &&
                  !ContextValidation.GetCoveredManifest(ready.Context).Any(source => source.Id.StartsWith("fixture.", StringComparison.Ordinal)),
                "provider bodies remain context references without entering stable messages or optional/required compaction coverage");
            ready.ModelFinal();
            Check(ready.Step.Next is CompleteWorkflow { Success: true }, "ordinary confirmed final completion remains available");

            var zero = new Fixture();
            zero.History(View(Ready(BetaItem, BetaRequest())));
            zero.ProviderSuccess(zeroSources: true);
            zero.Catalog();
            Check(zero.Is("cerebral.host:generate") && !zero.Context.Contributions.Any(source => source.Owner == Beta) &&
                  zero.Items[0].GetProperty("outcome").GetString() == "Succeeded" && zero.ProviderIntents.Count == 1,
                "confirmed zero-source success for optional references remains one successful attempt without replacement");

            var genericShape = new Fixture(); genericShape.History(View(Ready(BetaItem, BetaRequest())));
            genericShape.ProviderSuccess(data => ChangeSource(data, source => source with
            {
                Complete = false,
                Messages = [new("assistant", [new StructuredContentPart("fixture.arbitrary", Parse("""{"A":1,"a":2}"""))]),
                    new("user", [new TextContentPart("Bounded excerpt reference.")])]
            }));
            genericShape.Catalog();
            Check(genericShape.Is("cerebral.host:generate") && genericShape.Context.Contributions.Single(source => source.Owner == Beta) is
                  { Complete: false, Messages.Count: 2, BeforeCurrentInput: false } &&
                  genericShape.Context.Contributions.Single(source => source.Owner == Beta).Messages[0].Content.Single() is StructuredContentPart structured &&
                  structured.Value.GetProperty("A").GetInt32() == 1 && structured.Value.GetProperty("a").GetInt32() == 2,
                "neutral normalized context preserves an attributed structured excerpt with distinct arbitrary A/a keys and multiple non-system roles");
            var boundaryReceipt = new Fixture(); boundaryReceipt.History(View(Ready(BetaItem, BetaRequest())));
            boundaryReceipt.ProviderSuccess(receipt: new string('é', 256));
            Check(boundaryReceipt.Is("cerebral.host:catalog") &&
                  Encoding.UTF8.GetByteCount(boundaryReceipt.Items[0].GetProperty("providerReceiptId").GetString()!) == 512,
                "an opaque receipt at the exact five-hundred-twelve-byte UTF-8 boundary is retained");

            var rawResolution = Resolution(BetaRequest());
            var exactRawBytes = JsonSerializer.SerializeToUtf8Bytes(rawResolution, CompactJson).Length;
            var expanded = new Fixture();
            expanded.History(View(Ready(BetaItem, BetaRequest() with { MaximumUtf8Bytes = exactRawBytes })));
            expanded.ProviderSuccess(opaqueHandles: false);
            Check(expanded.Is("cerebral.host:catalog") &&
                  Encoding.UTF8.GetByteCount(expanded.LastCompletedData.GetRawText()) > exactRawBytes &&
                  expanded.Items[0].GetProperty("outcome").GetString() == "Succeeded",
                "genuinely normalized host metadata may exceed an exactly fitting raw-provider byte limit without rejecting or clipping its projection");

            var sameProvider = new Fixture();
            var anotherAlpha = AlphaRequest() with
            {
                References = [AlphaRequest().References[1] with { Id = "fixture.alpha:other", SourceId = "another-private-source" }]
            };
            sameProvider.History(View(Ready(AlphaItem, AlphaRequest()), Ready("fixture.alpha:other-context", anotherAlpha)));
            sameProvider.ProviderSuccess(); sameProvider.ProviderSuccess(); sameProvider.Catalog();
            Check(sameProvider.Is("cerebral.host:generate") && sameProvider.ProviderIntents.SequenceEqual(new[] { Alpha, Alpha }) &&
                  sameProvider.Context.Contributions.Count(source => source.Owner == Alpha) == 3,
                "distinct captured items may use the same provider without coalescing or duplicating references");

            var duplicateTemplateAdmission = new Fixture();
            duplicateTemplateAdmission.History(View(
                Ready(AlphaItem, AlphaRequest()), Ready("fixture.alpha:duplicate-context", AlphaRequest())));
            Check(duplicateTemplateAdmission.Refused && duplicateTemplateAdmission.ProviderIntents.Count == 0,
                "distinct contribution IDs cannot admit the same exact whole provider request twice");

            const string sharedId = "fixture.shared:source";
            var sharedAlpha = AlphaRequest() with { References = [AlphaRequest().References[1] with { Id = sharedId }] };
            var sharedBeta = BetaRequest() with { References = [BetaRequest().References[0] with { Id = sharedId }] };
            var firstZero = new Fixture();
            firstZero.History(View(Ready(AlphaItem, sharedAlpha), Ready(BetaItem, sharedBeta)));
            firstZero.ProviderSuccess(zeroSources: true); firstZero.ProviderSuccess(); firstZero.Catalog();
            Check(firstZero.Is("cerebral.host:generate") && firstZero.ProviderIntents.SequenceEqual(new[] { Alpha, Beta }) &&
                  firstZero.Context.Contributions.Single(source => source.Id == sharedId).Owner == Beta,
                "the same potential reference ID in different templates remains valid when the first optional request returns zero sources");
            var secondZero = new Fixture();
            secondZero.History(View(Ready(AlphaItem, sharedAlpha), Ready(BetaItem, sharedBeta)));
            secondZero.ProviderSuccess(); secondZero.ProviderSuccess(zeroSources: true); secondZero.Catalog();
            Check(secondZero.Is("cerebral.host:generate") &&
                  secondZero.Context.Contributions.Single(source => source.Id == sharedId).Owner == Alpha &&
                  secondZero.Items.EnumerateArray().All(item => item.GetProperty("outcome").GetString() == "Succeeded"),
                "an optional zero-source request may overlap an existing live ID without replacing that source");
            var actualCollision = new Fixture();
            actualCollision.History(View(Ready(AlphaItem, sharedAlpha), Ready(BetaItem, sharedBeta)));
            actualCollision.ProviderSuccess(); actualCollision.ProviderSuccess(); actualCollision.Readvance();
            Check(actualCollision.Refused && actualCollision.ProviderIntents.Count == 2 &&
                  actualCollision.SavedSources.Count(source => source.Id == sharedId) == 1 &&
                  actualCollision.SavedSources.Single(source => source.Id == sharedId).Owner == Alpha,
                "two actual returned sources with the same live ID refuse without overwriting the first or replaying either provider");

            var malformedViews = new object[]
            {
                new { schemaVersion = 2, items = Array.Empty<object>() },
                new { schemaVersion = 1, items = Enumerable.Range(0, 5).Select(index => Item("fixture.alpha:item-" + index, "omitted", null)).ToArray() },
                View(Ready(AlphaItem, AlphaRequest()), Ready(AlphaItem, AlphaRequest())),
                View(Item(AlphaItem, "Ready", null)),
                View(Item(AlphaItem, "ready", null)),
                View(Item(AlphaItem, "omitted", AlphaRequest())),
                View(Ready(AlphaItem, AlphaRequest() with { ProviderVersion = 0 })),
                View(Ready(AlphaItem, AlphaRequest() with { References = [] })),
                View(Ready(AlphaItem, AlphaRequest() with { MaximumUtf8Bytes = 0 })),
                Parse("""{"schemaVersion":1,"schemaVersion":1,"items":[]}""")
            };
            foreach (var view in malformedViews)
            {
                var bad = new Fixture(); bad.History(view);
                Check(bad.Refused && bad.ProviderIntents.Count == 0,
                    "malformed closed host metadata refuses before a provider or model intent");
            }
            var hugeRequest = AlphaRequest() with
            {
                References = Enumerable.Range(0, 64).Select(index => new ContextProviderReference(
                    "fixture.alpha:large-" + index, "fixture.note", new string('s', 256), 1)).ToArray()
            };
            var hugeView = View(Ready(AlphaItem, hugeRequest));
            Check(Encoding.UTF8.GetByteCount(Element(hugeView).GetRawText()) > 16 * 1024 &&
                  Encoding.UTF8.GetByteCount(Element(hugeView).GetRawText()) < 48 * 1024,
                "the oversized metadata fixture isolates the sixteen-KiB collection bound from the outer history bound");
            var huge = new Fixture(); huge.History(hugeView);
            Check(huge.Refused && huge.ProviderIntents.Count == 0,
                "body-free selection metadata above sixteen KiB refuses before dispatch");

            foreach (var effectState in new[]
            {
                WorkflowEffectState.Failed, WorkflowEffectState.Cancelled, WorkflowEffectState.Unknown, WorkflowEffectState.Dispatched, WorkflowEffectState.Prepared
            })
            {
                var failed = new Fixture(); failed.History(View(Ready(AlphaItem, AlphaRequest())));
                failed.Result(effectState, Element(new { failureKind = "source-unavailable" }));
                Check(failed.Refused && failed.Items[0].GetProperty("attempted").GetBoolean() &&
                      failed.Items[0].GetProperty("outcome").GetString() == effectState.ToString(),
                    "unconfirmed or failed provider stops without interpreting a package failure codec: " + effectState);
                failed.Readvance();
                Check(failed.Refused && failed.ProviderIntents.Count == 1,
                    "saved failed or uncertain attempt never causes recapture or another provider intent: " + effectState);
            }
            foreach (var mutate in new Func<ContextProviderResult, ContextProviderResult>[]
            {
                data => data with { ProviderId = Beta },
                data => data with { ProviderVersion = 1 },
                data => data with { Window = data.Window with { Revision = data.Window.Revision + 1 } },
                data => data with { Window = data.Window with { ExclusionRevision = data.Window.ExclusionRevision + 1 } },
                data => data with { Window = data.Window with { Contributions = data.Window.Contributions.Where(source => source.Requirement != ContextRequirement.Required).ToArray() } },
                data => ChangeSource(data, source => source with { Id = "fixture.alpha:unrequested" }),
                data => ChangeSource(data, source => source with { Owner = Beta }),
                data => ChangeSource(data, source => source with { Category = "fixture.foreign" }),
                data => ChangeSource(data, source => source with { SourceRevision = 90 }, index: 1),
                data => ChangeSource(data, source => source with { Requirement = ContextRequirement.Reducible }),
                data => ChangeSource(data, source => source with { Priority = 123 }),
                data => ChangeSource(data, source => source with { BeforeCurrentInput = false }),
                data => ChangeSource(data, source => source with { ExactReadHandle = "" }),
                data => ChangeSource(data, source => source with { Messages = [new("system", [new TextContentPart(AlphaBody)])] })
            })
            {
                var bad = new Fixture(); bad.History(View(Ready(AlphaItem, AlphaRequest())));
                bad.ProviderSuccess(mutate);
                Check(bad.Refused && bad.ProviderIntents.Count == 1,
                    "stale or malformed normalized result fails closed without retry or replacement");
            }
            foreach (var receipt in new[] { "", new string('é', 257) })
            {
                var bad = new Fixture(); bad.History(View(Ready(AlphaItem, AlphaRequest())));
                bad.ProviderSuccess(receipt: receipt);
                Check(bad.Refused && bad.ProviderIntents.Count == 1,
                    "confirmed success requires a nonblank receipt within its UTF-8 bound");
            }
            var staleArguments = new Fixture(); staleArguments.History(View(Ready(AlphaItem, AlphaRequest())));
            var staleRequest = staleArguments.Command.Arguments.Deserialize<ContextProviderRequest>(CerebralJson.Options)!;
            staleArguments.ProviderSuccess(arguments: Element(staleRequest with
            {
                References = [staleRequest.References[0] with { SourceId = "changed-private-source" }, staleRequest.References[1]]
            }));
            Check(staleArguments.Refused && staleArguments.ProviderIntents.Count == 1,
                "whole pending-reference equality rejects a changed provider-private source locator");
            var wrongVersion = new Fixture(); wrongVersion.History(View(Ready(AlphaItem, AlphaRequest())));
            wrongVersion.ProviderSuccess(commandVersion: 1);
            Check(wrongVersion.Refused && wrongVersion.ProviderIntents.Count == 1,
                "provider-v2 intent cannot adopt a version-one effect receipt");

            foreach (var corruption in new[] { "missing", "null", "unattempted" })
            {
                var corrupted = new Fixture(); corrupted.History(View(Ready(AlphaItem, AlphaRequest())));
                var state = JsonNode.Parse(corrupted.Step.State.GetRawText())!;
                if (corruption == "missing") state.AsObject().Remove("contextContributions");
                else if (corruption == "null") state["contextContributions"] = null;
                else state["contextContributions"]!["items"]![0]!["attempted"] = false;
                corrupted.RestoreState(Element(state)); corrupted.ProviderSuccess();
                Check(corrupted.RestorationRefused && corrupted.ProviderIntents.Count == 1,
                    "restored generic provider phase requires its complete marker and durable one-attempt flag");
            }
            var duplicateTemplateRestore = new Fixture();
            duplicateTemplateRestore.History(View(Ready(AlphaItem, AlphaRequest()), Ready(BetaItem, BetaRequest())));
            var originalPending = duplicateTemplateRestore.Step.State.GetProperty("pendingArguments").Clone();
            var duplicateTemplateState = JsonNode.Parse(duplicateTemplateRestore.Step.State.GetRawText())!;
            var templateItems = duplicateTemplateState["contextContributions"]!["items"]!.AsArray();
            templateItems[1]!["request"] = templateItems[0]!["request"]!.DeepClone();
            var duplicateTemplateFrame = Element(duplicateTemplateState);
            duplicateTemplateRestore.RestoreState(duplicateTemplateFrame); duplicateTemplateRestore.Readvance();
            Check(JsonElement.DeepEquals(originalPending, duplicateTemplateFrame.GetProperty("pendingArguments")) &&
                  duplicateTemplateRestore.RestorationRefused && duplicateTemplateRestore.CommandCallbacks == 0 &&
                  duplicateTemplateRestore.ProviderIntents.SequenceEqual(new[] { Alpha }),
                "restored duplicate whole templates refuse while the first pending arguments remain consistent and no second intent is requested");

            var duplicateAttempt = new Fixture(); duplicateAttempt.History(View(Ready(AlphaItem, AlphaRequest())));
            duplicateAttempt.RestoreState(Parse(duplicateAttempt.Step.State.GetRawText().Replace(
                "\"attempted\":true", "\"attempted\":true,\"attempted\":false", StringComparison.Ordinal)));
            duplicateAttempt.ProviderSuccess();
            Check(duplicateAttempt.RestorationRefused && duplicateAttempt.ProviderIntents.Count == 1,
                "duplicate saved one-attempt metadata refuses before a matching provider result");
            var healthy = new Fixture(); healthy.History(View(Ready(AlphaItem, AlphaRequest())));
            healthy.ProviderSuccess(); healthy.RestoreState(healthy.Step.State); healthy.Catalog();
            Check(healthy.Is("cerebral.host:generate") && healthy.ProviderIntents.Count == 1,
                "a fresh harness instance resumes matching saved received state without another provider intent");

            foreach (var removeRequired in new[] { true, false })
            {
                var corrupted = new Fixture(); corrupted.History(View(Ready(AlphaItem, AlphaRequest())));
                corrupted.ProviderSuccess();
                var state = JsonNode.Parse(corrupted.Step.State.GetRawText())!;
                var sources = state["contributions"]!.AsArray();
                var requiredIndex = Enumerable.Range(0, sources.Count).Single(index =>
                    sources[index]!["id"]!.GetValue<string>() == "fixture.alpha:source-b");
                if (removeRequired) sources.RemoveAt(requiredIndex);
                else sources[requiredIndex]!["requirement"] = "optional";
                corrupted.RestoreState(Element(state)); corrupted.Catalog();
                Check(corrupted.RestorationRefused && corrupted.ProviderIntents.Count == 1,
                    "restored success cannot strip or demote a required contributed source before the next host effect");
            }
            var unversionedRequired = AlphaRequest() with
                { References = [AlphaRequest().References[0] with { ExpectedRevision = null }] };
            var validUnversioned = new Fixture();
            validUnversioned.History(View(Ready(AlphaItem, unversionedRequired), Ready(BetaItem, BetaRequest())));
            validUnversioned.ProviderSuccess();
            Check(validUnversioned.Is(Beta) && validUnversioned.SavedSources.Single(source =>
                      source.Id == "fixture.alpha:source-b") is { SourceRevision: 4, Requirement: ContextRequirement.Required } &&
                  validUnversioned.CommandCallbacks == 0,
                "genuine normalization accepts a required source with unknown captured revision before selecting the next ready item");
            foreach (var corruption in new[] { "negative-revision", "null-messages", "empty-messages", "null-contributions" })
            {
                var corrupted = new Fixture();
                corrupted.History(View(Ready(AlphaItem, unversionedRequired), Ready(BetaItem, BetaRequest())));
                corrupted.ProviderSuccess();
                var state = JsonNode.Parse(corrupted.Step.State.GetRawText())!;
                if (corruption == "null-contributions") state["contributions"] = null;
                else
                {
                    var source = state["contributions"]!.AsArray().Single(item =>
                        item!["id"]!.GetValue<string>() == "fixture.alpha:source-b")!;
                    if (corruption == "negative-revision") source["sourceRevision"] = -1;
                    else if (corruption == "null-messages") source["messages"] = null;
                    else source["messages"] = new JsonArray();
                }
                corrupted.RestoreState(Element(state));
                // The first receipt atomically selected the second intent, which remains
                // undispatched. Restore validation runs without producing any second result.
                corrupted.Readvance();
                Check(corrupted.RestorationRefused && corrupted.CommandCallbacks == 0 &&
                      corrupted.ProviderIntents.SequenceEqual(new[] { Alpha, Beta }),
                    "malformed saved unversioned-required context refuses before another result or new intent: " + corruption);
            }
            foreach (var category in new[] { "tool-result", "steering-1" })
            {
                var currentRequired = AlphaRequest() with
                {
                    References = [AlphaRequest().References[0] with
                        { Id = "fixture.alpha:current-required", Kind = category, Priority = 3, BeforeCurrentInput = false }]
                };
                var appended = new Fixture(input: category == "tool-result"
                    ? new { requiredSuccessfulTools = new[] { "fixture.echo" } } : null);
                appended.History(View(Ready(AlphaItem, currentRequired))); appended.ProviderSuccess();
                if (category == "tool-result")
                {
                    appended.Catalog(withTool: true); appended.Catalog(withTool: true);
                    appended.ModelAction("""{"action":"tool","name":"fixture.echo","arguments":{}}""");
                    Check(appended.Is("cerebral.host:tool"), "the required-source append fixture reaches an ordinary fake tool receipt");
                    appended.ToolSuccess();
                }
                else
                {
                    appended.Steer(); appended.Catalog();
                }
                Check(appended.Is("cerebral.host:generate") &&
                      appended.Context.Contributions.Single(source => source.Id == "fixture.alpha:current-required") is
                          { Requirement: ContextRequirement.Required, BeforeCurrentInput: false, Owner: Alpha } &&
                      !ContextValidation.GetCoveredManifest(appended.Context).Any(source => source.Id == "fixture.alpha:current-required"),
                    "a same-category host tool or steering append preserves provider-owned current required context: " + category);
            }

            var forced = new Fixture(forceCompaction: true, history: [Prior()]);
            forced.History(View(Ready(AlphaItem, AlphaRequest()), Ready(BetaItem, BetaRequest())));
            Check(forced.Is(Alpha), "generic ready resolution precedes explicitly requested initial compaction");
            forced.ProviderSuccess(); forced.ProviderSuccess();
            Check(forced.Is("cerebral.host:compact") && forced.ProviderIntents.Count == 2 &&
                  forced.Context.Contributions.Count(source => source.Owner == Alpha || source.Owner == Beta) == 3 &&
                  ContextValidation.GetCoveredManifest(forced.Context).Single().Id == "prior",
                "all ready items resolve before forced compaction and neutral source classifications control coverage");
            forced.Compact(); forced.Catalog();
            Check(forced.Is("cerebral.host:generate") && forced.ProviderIntents.Count == 2 &&
                  forced.Context.Contributions.Count(source => source.Owner == Alpha || source.Owner == Beta) == 3,
                "confirmed compaction retains contributed sources and receipts without resolution replay");

            var historySteering = new Fixture(); historySteering.Steer();
            historySteering.History(View(Ready(AlphaItem, AlphaRequest())));
            Check(historySteering.Is(Alpha) &&
                  historySteering.Command.Arguments.Deserialize<ContextProviderRequest>(CerebralJson.Options) is { } rebased &&
                  rebased.ContextRevision == historySteering.Revision && rebased.ExclusionRevision == historySteering.Exclusion &&
                  SameTemplate(rebased, AlphaRequest()) &&
                  JsonElement.DeepEquals(historySteering.Command.Arguments, historySteering.Step.State.GetProperty("pendingArguments")),
                "steering during initial history rebases only the new undispatched provider request and pending arguments");
            var inFlight = new Fixture();
            inFlight.History(View(Ready(AlphaItem, AlphaRequest()), Ready(BetaItem, BetaRequest())));
            var inFlightArguments = inFlight.Command.Arguments.Clone();
            inFlight.Steer(); inFlight.ProviderSuccess();
            Check(inFlight.Is(Beta) && JsonElement.DeepEquals(inFlight.LastCompletedArguments, inFlightArguments) &&
                  inFlight.Items[0].GetProperty("outcome").GetString() == "Succeeded" &&
                  SameTemplate(inFlight.Command.Arguments.Deserialize<ContextProviderRequest>(CerebralJson.Options)!, BetaRequest()) &&
                  inFlight.Command.Arguments.GetProperty("contextRevision").GetInt64() == inFlight.Revision,
                "in-flight receipt is adopted against its original request while only the next selected item is rebased");
            inFlight.ProviderSuccess(); inFlight.Catalog();
            Check(inFlight.Is("cerebral.host:generate") && inFlight.ProviderIntents.Count == 2 &&
                  inFlight.Context.Contributions.Count(source => source.Owner == Alpha || source.Owner == Beta) == 3 &&
                  inFlight.Context.Contributions.Any(source => source.Category == "steering-1"),
                "latest steering and each contributed source remain exact without duplicate selection");

            var laterHistory = new Fixture(hasOlder: true);
            laterHistory.History(View(Ready(AlphaItem, AlphaRequest())));
            laterHistory.ProviderSuccess(); laterHistory.Catalog();
            laterHistory.ModelAction("""{"action":"history","beforeSequence":50}""");
            Check(laterHistory.Is("cerebral.host:history"), "the recapture fixture reaches a permitted older-page lookup");
            laterHistory.History(View(Ready(BetaItem, BetaRequest())), later: true);
            Check(laterHistory.Refused && laterHistory.ProviderIntents.Count == 1,
                "later history cannot introduce a fresh capture or replace the winning initial selection");

            var legacy = new Fixture(legacy: true);
            var legacyRequest = LegacyRequest();
            legacy.History(null, include: false, personality: new { status = "ready", request = legacyRequest });
            var legacyPending = legacy.Step.State.Clone();
            Check(legacy.Is("pegasus.personality:context.resolve") && legacy.Command.CommandVersion == 1 &&
                  !legacyPending.TryGetProperty("contextContributions", out _),
                "the default legacy identity keeps its existing pending personality effect without a generic marker");
            legacy.RestoreState(legacyPending); legacy.LegacyProviderSuccess();
            Check(legacy.Is("cerebral.host:catalog") && legacy.ProviderIntents.Count == 1,
                "fresh default harness restores the old pending domain receipt without opting into generic behavior");

            var mixed = new Fixture(); mixed.History(View(Ready(AlphaItem, AlphaRequest())));
            var mixedState = JsonNode.Parse(mixed.Step.State.GetRawText())!;
            mixedState["personality"] = JsonNode.Parse(legacyPending.GetRawText())!["personality"]!.DeepClone();
            mixed.RestoreState(Element(mixedState)); mixed.ProviderSuccess();
            Check(mixed.RestorationRefused && mixed.ProviderIntents.Count == 1,
                "the generic edition refuses mixed legacy-domain state before adopting a neutral provider receipt");
            var legacyPhase = new Fixture(); legacyPhase.History(View());
            var legacyPhaseState = JsonNode.Parse(legacyPhase.Step.State.GetRawText())!;
            legacyPhaseState["phase"] = "personality";
            legacyPhase.RestoreState(Element(legacyPhaseState)); legacyPhase.Catalog();
            Check(legacyPhase.RestorationRefused && legacyPhase.ProviderIntents.Count == 0,
                "an empty generic marker cannot restore a legacy personality phase through the new edition");

            var path = Path.Combine(root, "private-engine.db");
            var journal = new EventJournal(path);
            var store = new WorkflowStore(journal);
            var durable = new Fixture(store: store);
            durable.History(View(Ready(AlphaItem, AlphaRequest()), Ready(BetaItem, BetaRequest())));
            var committedIntent = store.Get(durable.Submission.RequestId);
            Check(committedIntent.Effect is { State: WorkflowEffectState.Prepared, CommandId: Alpha, CommandVersion: 2 } &&
                  committedIntent.State.GetProperty("contextContributions").GetProperty("items")[0].GetProperty("attempted").GetBoolean() &&
                  JsonElement.DeepEquals(committedIntent.Effect.Arguments, durable.Command.Arguments),
                "actual private SQLite commits the once-attempted provider-v2 intent and exact arguments before any execution");
            var alphaEffect = committedIntent.Effect!.Id;
            durable.ProviderSuccess(opaqueHandles: false);
            var receiptResult = store.GetEffectResult(durable.Submission.RequestId, alphaEffect);
            var reopened = new WorkflowStore(new EventJournal(path));
            var reopenedRun = reopened.Get(durable.Submission.RequestId);
            Check(receiptResult.State == WorkflowEffectState.Succeeded && receiptResult.ProviderReceiptId == OpaqueReceipt &&
                  reopenedRun.Effect is { State: WorkflowEffectState.Prepared, CommandId: Beta, CommandVersion: 3 } &&
                  JsonElement.DeepEquals(reopenedRun.State, durable.Step.State) &&
                  receiptResult.Data!.Value.Deserialize<ContextProviderResult>(CerebralJson.Options)!.Window.Contributions
                      .All(source => source.ExactReadHandle!.StartsWith("cerebral-context:v1.", StringComparison.Ordinal)),
                "actual normalization and committed opaque receipt survive SQLite reopen before the next provider");
            var duplicate = reopened.CompleteEffect(durable.Submission.RequestId, alphaEffect, receiptResult);
            Check(duplicate.Revision == reopenedRun.Revision && JsonElement.DeepEquals(duplicate.State, reopenedRun.State) &&
                  duplicate.Effect!.Id == reopenedRun.Effect!.Id,
                "repeating the same confirmed receipt changes no durable revision, state, or next intent");
            durable.ProviderSuccess(opaqueHandles: false); durable.Catalog();
            var generationIntent = store.Get(durable.Submission.RequestId);
            Check(generationIntent.Effect is { State: WorkflowEffectState.Prepared, CommandId: "cerebral.host:generate" } &&
                  durable.ProviderIntents.SequenceEqual(new[] { Alpha, Beta }) && durable.CommandCallbacks == 0 &&
                  new WorkflowStore(new EventJournal(path)).Get(durable.Submission.RequestId).State
                      .GetProperty("contextContributions").GetProperty("items").EnumerateArray()
                      .All(item => item.GetProperty("outcome").GetString() == "Succeeded"),
                "the real Registry/store bridge stops at first generation selection with no host/provider/model callback");
            Console.WriteLine("PASS ConversationContextContributionTests: " + checks +
                " (pure harness/normalizer and private SQLite intents/receipts; no inference or provider retrieval)");
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            RequireOwned(root, parent);
            ScopedFilePolicy.RejectLinks(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private static object Item(string id, string status, ContextProviderRequest? request) => new { id, status, request };
    private static object Ready(string id, ContextProviderRequest request) => Item(id, "ready", request);
    private static object View(params object[] items) => new { schemaVersion = 1, items };
    private static ContextProviderRequest AlphaRequest() => new(Alpha, 2,
        [new("fixture.alpha:source-b", "fixture.fact", "private/source-b-v2", 9, ContextRequirement.Required, 7, true),
         new("fixture.alpha:source-a", "fixture.note", "private/source-a-v2", null, ContextRequirement.Optional, 2, true)],
        8192, 17, 8);
    private static ContextProviderRequest BetaRequest() => new(Beta, 3,
        [new("fixture.beta:source", "fixture.note", "private/source-beta", 6, ContextRequirement.Optional, 11, false)],
        8192, 23, 9);
    private static ContextProviderRequest LegacyRequest() => new("pegasus.personality:context.resolve", 1,
        [new("pegasus.personality:style", "personality.behavior", "personality/v1/synthetic/1/" + new string('a', 64),
            1, ContextRequirement.Optional, 0, true)], 2048, 100, 0);
    private static bool SameTemplate(ContextProviderRequest actual, ContextProviderRequest template) =>
        JsonElement.DeepEquals(Element(actual with { ContextRevision = template.ContextRevision, ExclusionRevision = template.ExclusionRevision }), Element(template));
    private static ContextProviderResult ChangeSource(ContextProviderResult data, Func<ContextContribution, ContextContribution> change, int index = 0) =>
        data with { Window = data.Window with { Contributions = data.Window.Contributions.Select((source, ordinal) => ordinal == index ? change(source) : source).ToArray() } };
    private static ContextContribution Prior() => new("prior", "cerebral.host", "history", 10, ContextRequirement.Reducible,
        [new("user", [new TextContentPart("Earlier working context.")])], "event-prior", BeforeCurrentInput: true);
    private static PreparedModelMetadata Metadata(int tokens) =>
        new("fixture-instance", "fixture-adapter", "1", "fixture-model", tokens, 2048, 256, "deterministic-fixture") { SafetyReserveTokens = 128 };
    private static ContextProviderResolution Resolution(ContextProviderRequest request) => new(request.ProviderId, request.ProviderVersion,
        request.References.Select(reference => new ContextProviderSource(reference.Id, reference.Kind,
            reference.ExpectedRevision ?? 4, reference.Requirement,
            [new("user", [new TextContentPart(request.ProviderId == Alpha ? AlphaBody : BetaBody)])],
            Priority: reference.Priority, BeforeCurrentInput: reference.BeforeCurrentInput)).ToArray());
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, CompactJson);
    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static void RequireOwned(string root, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.GetFullPath(root);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, comparison) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^conversation-context-[a-f0-9]{32}$"))
            throw new InvalidOperationException("Refusing cleanup outside the exact owned conversation fixture.");
    }

    private sealed class Fixture
    {
        private ConversationAgentHarness harness;
        private readonly bool legacy, hasOlder;
        private readonly ContextContribution[] history;
        private readonly List<WorkflowSteeringEntry> steering = [];
        private readonly WorkflowStore? store;
        private readonly WorkflowRegistry registry;
        private readonly List<NoCallsCommand> commands;
        private int sequence;
        public WorkflowSubmission Submission { get; }
        public WorkflowStep Step { get; private set; } = null!;
        public InvokeWorkflowCommand Command => (InvokeWorkflowCommand)Step.Next;
        public JsonElement Items => Step.State.GetProperty("contextContributions").GetProperty("items");
        public long Revision => Step.State.GetProperty("contextRevision").GetInt64();
        public long Exclusion => Step.State.GetProperty("exclusionRevision").GetInt64();
        public ContextWindow Context => Command.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
        public ContextContribution[] SavedSources => Step.State.GetProperty("contributions").Deserialize<ContextContribution[]>(CerebralJson.Options)!;
        public string MessageText => string.Join("\n", Command.Arguments.GetProperty("messages").Deserialize<ModelInputMessage[]>(CerebralJson.Options)!
            .SelectMany(message => message.Content).OfType<TextContentPart>().Select(part => part.Text));
        public bool Refused => Step.Next is CompleteWorkflow { Success: false };
        public bool RestorationRefused => Step.Next is CompleteWorkflow { Success: false, Error: { } error } &&
            error.StartsWith("The saved conversation state could not be restored:", StringComparison.Ordinal);
        public List<string> ProviderIntents { get; } = [];
        public JsonElement LastCompletedArguments { get; private set; }
        public JsonElement LastCompletedData { get; private set; }
        public int CommandCallbacks => commands.Sum(command => command.Calls);
        private string NextEffectId => store?.Get(Submission.RequestId).Effect?.Id ?? "fixture-effect-" + (sequence + 1);

        public Fixture(bool legacy = false, bool forceCompaction = false, ContextContribution[]? history = null,
            bool hasOlder = false, object? input = null, WorkflowStore? store = null, string? requestId = null)
        {
            this.legacy = legacy; this.hasOlder = hasOlder; this.history = history ?? []; this.store = store;
            harness = FreshHarness();
            commands =
            [
                new NoCallsCommand("cerebral.host:history", 1), new NoCallsCommand("cerebral.host:catalog", 1),
                new NoCallsCommand("cerebral.host:generate", 1), new NoCallsCommand("cerebral.host:compact", 1),
                new NoCallsProvider(Alpha, 2), new NoCallsProvider(Beta, 3),
                new NoCallsCommand("pegasus.personality:context.resolve", 1), new NoCallsCommand("cerebral.host:tool", 1)
            ];
            registry = new WorkflowRegistry(
            [
                new Module(ConversationServicePlugin.ModuleId, [new AgentWorkflowDefinition(harness)], []),
                new Module("cerebral.host", [], commands.Where(command => command.Descriptor.Id.StartsWith("cerebral.host:", StringComparison.Ordinal)).ToArray()),
                new Module("fixture.alpha", [], [commands[4]]), new Module("fixture.beta", [], [commands[5]]),
                new Module("pegasus.personality", [], [commands[6]])
            ]);
            Submission = new(requestId ?? "generic-conversation-" + Interlocked.Increment(ref nextFixture), "fixture-conversation", "lead",
                harness.Descriptor.Id, harness.Descriptor.Version, "Answer the accepted request.", null,
                Element(input ?? new { }), WorkflowContract.EmptyObject)
                { Caller = new(EventActorKind.User, "fixture-owner"), ConversationCompactionRequestId = forceCompaction ? "forced" : null };
            if (store is null) SetStep(harness.Advance(new(new WorkflowRun { Id = Submission.RequestId, Submission = Submission, Status = WorkflowStatus.Running })));
            else
            {
                var accepted = store.Submit(Submission);
                var run = store.TryClaim(accepted.Id) ?? throw new InvalidOperationException("Private run was not claimable.");
                SetStep(registry.GetDefinition(Submission.DefinitionId, Submission.DefinitionVersion).Advance(run), run);
            }
        }
        private ConversationAgentHarness FreshHarness() => legacy ? new() : ConversationAgentHarness.CreateContextContributionEdition();
        public bool Is(string id) => Step.Next is InvokeWorkflowCommand command && command.CommandId == id;
        public void RestoreState(JsonElement state) { harness = FreshHarness(); Step = Step with { State = state.Clone() }; }
        public void History(object? view, bool include = true, object? personality = null, bool later = false)
        {
            var page = new Dictionary<string, object?>
            {
                ["context"] = new ContextWindow(later ? 90 : 100, 0, later
                    ? [new ContextContribution("older-page", "cerebral.host", "history", 5, ContextRequirement.Reducible,
                        [new("user", [new TextContentPart("Earlier page.")])], "older-event", BeforeCurrentInput: true)]
                    : history),
                ["hasOlder"] = hasOlder, ["beforeSequence"] = hasOlder ? (later ? 25L : 50L) : null, ["snapshotSequence"] = 100L
            };
            if (include) page["contextContributions"] = view;
            if (personality is not null) page["personality"] = personality;
            Result(WorkflowEffectState.Succeeded, Element(page));
        }
        public void ProviderSuccess(Func<ContextProviderResult, ContextProviderResult>? mutate = null, string? receipt = null,
            JsonElement? arguments = null, bool zeroSources = false, bool opaqueHandles = true, int? commandVersion = null)
        {
            var request = Command.Arguments.Deserialize<ContextProviderRequest>(CerebralJson.Options)!;
            var descriptor = registry.GetContextProvider(request.ProviderId, request.ProviderVersion).Provider;
            request = ContextProviderNormalizer.ReadRequest(descriptor, Element(request));
            var raw = Resolution(request);
            if (zeroSources) raw = raw with { Sources = [] };
            // Normalize with a valid receipt first, then inject malformed receipt only at the consumer boundary.
            var normalized = ContextProviderNormalizer.Normalize(descriptor, request, raw, NextEffectId, OpaqueReceipt);
            if (opaqueHandles) normalized = normalized with { Window = normalized.Window with
                { Contributions = normalized.Window.Contributions.Select(source => source with { ExactReadHandle = "opaque-handle/" + source.Id }).ToArray() } };
            Result(WorkflowEffectState.Succeeded, Element(mutate?.Invoke(normalized) ?? normalized),
                receipt ?? OpaqueReceipt, arguments, commandVersion);
        }
        public void LegacyProviderSuccess()
        {
            var request = Command.Arguments.Deserialize<ContextProviderRequest>(CerebralJson.Options)!;
            var source = new ContextContribution("pegasus.personality:style", "pegasus.personality:context.resolve", "personality.behavior", 1,
                ContextRequirement.Optional, [new("user", [new TextContentPart("LEGACY_DOMAIN_BODY")])],
                "cerebral-context:v1.fixture", BeforeCurrentInput: true);
            Result(WorkflowEffectState.Succeeded, Element(new ContextProviderResult(request.ProviderId, 1,
                new(request.ContextRevision, request.ExclusionRevision, [source]))), NextEffectId);
        }
        public void Catalog(bool withTool = false) => Result(WorkflowEffectState.Succeeded, Element(new
        {
            tools = withTool ? new object[] { new { name = "fixture.echo", description = "Deterministic fixture tool.",
                capability = "fixture.read", inputSchema = WorkflowContract.EmptyObject } } : Array.Empty<object>(),
            truncated = false, helpers = Array.Empty<object>()
        }));
        public void ToolSuccess() => Result(WorkflowEffectState.Succeeded, Element(new
        {
            name = Command.Arguments.GetProperty("name").GetString(), content = "Confirmed deterministic fixture tool outcome.",
            truncated = false, evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Succeeded, "fixture.tool-host")
        }));
        public void ModelFinal() => ModelAction("""{"action":"final","text":"done"}""");
        public void ModelAction(string json)
        {
            var window = Context;
            var projection = new ContextProjection(ContextProjectionStatus.Ready, window.Revision, window.ExclusionRevision,
                window.Contributions.Select(source => new ContextSourceReference(source.Id, source.SourceRevision)).ToArray(), [], Metadata(128));
            Result(WorkflowEffectState.Succeeded, Element(new { generation = new ModelGenerationResult(ModelGenerationFinish.Stop, json), context = projection }));
        }
        public void Compact()
        {
            var window = Context;
            var checkpoint = new ContextCheckpoint("fixture-checkpoint", window.Revision, window.ExclusionRevision,
                ContextValidation.GetCoveredManifest(window), "Confirmed bounded summary.", NextEffectId, ContextValidation.CheckpointBeforeCurrentInput(window))
                { SourceLocators = ContextValidation.GetCoveredLocators(window) };
            Result(WorkflowEffectState.Succeeded, Element(new { checkpoint, before = Metadata(1800), after = Metadata(256) }));
        }
        public void Steer() => steering.Add(new("fixture-steering", 1, "Use the latest exact direction.", null, DateTimeOffset.UnixEpoch));
        public void Readvance() => SetStep(harness.Advance(new(new WorkflowRun
        { Id = Submission.RequestId, Submission = Submission, State = Step.State, Status = WorkflowStatus.Running, Steering = steering.ToArray() })));
        public void Result(WorkflowEffectState state, JsonElement data, string? receipt = null, JsonElement? arguments = null, int? commandVersion = null)
        {
            var command = Command;
            var id = NextEffectId;
            sequence++;
            LastCompletedArguments = (arguments ?? command.Arguments).Clone();
            LastCompletedData = data.Clone();
            if (store is null)
            {
                var effect = new WorkflowEffect(id, command.CommandId, commandVersion ?? command.CommandVersion,
                    LastCompletedArguments, WorkflowReplayPolicy.Never, state, data, ProviderReceiptId: receipt);
                SetStep(harness.Advance(new(new WorkflowRun
                {
                    Id = Submission.RequestId, Submission = Submission, State = Step.State, Status = WorkflowStatus.Running,
                    Steering = steering.ToArray(), Signal = new(WorkflowSignalKind.EffectResult, data), Effect = effect
                })));
            }
            else
            {
                var run = store.Get(Submission.RequestId);
                store.DispatchEffect(run.Id, run.Revision, executionMilliseconds: 1);
                store.CompleteEffect(run.Id, id, new(state, data, ProviderReceiptId: receipt), elapsedMilliseconds: 1);
                run = store.TryClaim(run.Id) ?? throw new InvalidOperationException("Completed private receipt was not claimable.");
                SetStep(registry.GetDefinition(Submission.DefinitionId, Submission.DefinitionVersion).Advance(run), run);
            }
        }
        private void SetStep(WorkflowStep step, WorkflowRun? run = null)
        {
            Step = step;
            if (step.Next is InvokeWorkflowCommand command &&
                (command.CommandId == Alpha || command.CommandId == Beta || command.CommandId == "pegasus.personality:context.resolve"))
                ProviderIntents.Add(command.CommandId);
            if (store is not null)
                store.ApplyStep(run!.Id, run.Revision, step, step.Next is InvokeWorkflowCommand pending
                    ? registry.GetCommand(pending.CommandId, pending.CommandVersion).Descriptor : null);
        }
    }

    private sealed record Module(string Id, IReadOnlyList<IWorkflowDefinition> Workflows, IReadOnlyList<IWorkflowCommandHandler> Commands) : IWorkflowModule;
    private class NoCallsCommand(string id, int version, WorkflowReplayPolicy replayPolicy = WorkflowReplayPolicy.Never) : IWorkflowCommandHandler
    {
        public int Calls;
        public WorkflowCommandDescriptor Descriptor { get; } = new(id, version, NoExecutionDescription,
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, [], replayPolicy);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("This fixture stops at intent selection; no command callback may run.");
        }
    }
    private sealed class NoCallsProvider(string id, int version) : NoCallsCommand(id, version, WorkflowReplayPolicy.Idempotent), IContextProviderCommandHandler
    {
        public ContextProviderDescriptor Provider { get; } = new(id, version, NoExecutionDescription,
            ["fixture.fact", "fixture.note", "tool-result", "steering-1"], 128, 8192);
    }
}
