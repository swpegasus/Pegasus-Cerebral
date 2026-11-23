#if PROVIDER_ASSEMBLY_CHECKS
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Conversation;
using Cerebral.Core;
using Cerebral.Engine.Context;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Cerebral.PersonalityServicePlugin;
using Microsoft.Data.Sqlite;
using ServicePlugin = Cerebral.PersonalityServicePlugin.PersonalityServicePlugin;

namespace Cerebral.PersonalityPlugin.Tests;

/// <summary>Private actual-provider/Engine integration fixture; never a Worker or model execution host.</summary>
internal static class ProviderAssemblyChecks
{
    private const string TaskId = "10000000-0000-4000-8000-000000000002";
    private const string Workspace = @"C:\Development\Pegasus-Cerebral";
    private static readonly string Family = Path.Combine(Workspace, @"build\Plugins\SettingsPresets");
    private static readonly string ExpectedRoot = Path.Combine(Family, @"personality-fixtures\provider-assembly-working");
    private const string CurrentInput = "Accepted provider assembly fixture request.";
    private const string BodyA = "Fixture style A. Preserve this original captured source and its exact identity.";
    private const string BodyB = "Fixture style B. This later desired source must not replace the captured source.";
    private const string AId = "fixture-style-a", BId = "fixture-style-b";
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options) { WriteIndented = true };
    private static readonly List<string> Labels = [];
    private static readonly List<object> Cases = [];
    private static string Stage = "not-started";
    private static string? FailedCheck;
    private static bool rootOwned;
    private static int retainedUnknowns;

    private enum Mode { Ready, DisabledB, AppendedB, MissingSource, BadHash, BadRevision,
        ForeignCaller, ParentScope, Reopen, MissingReceipt, WrongResolution, Unknown, NullOutcome }

    private static async Task<int> Main(string[] args)
    {
        // Compilation must not be mistaken for execution; this mode is deliberately explicit.
        if (args.Length != 2 || args[0] != "--provider-assembly-checks") return 2;
        var root = Path.GetFullPath(args[1]);
        var reportPath = Path.Combine(Family, "personality-provider-assembly-verification.json");
        var logPath = Path.Combine(Family, "personality-provider-assembly-tests.log");
        var passed = false;
        string? failureType = null;
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Stage = "preflight";
            Check(string.Equals(root, ExpectedRoot, StringComparison.OrdinalIgnoreCase),
                "only the exact new private fixture root is admitted");
            GuardAncestors(Path.GetDirectoryName(root)!);
            Check(!Directory.Exists(root) && !File.Exists(root), "fresh fixture root is absent");
            Check(!File.Exists(reportPath) && !File.Exists(logPath) &&
                !File.Exists(reportPath + ".part"), "fresh report and log sinks do not overwrite evidence");
            var task = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
            Check(task is null || task == TaskId, "available task identity matches the exact owner");
            VerifyInputs();
            SQLitePCL.Batteries_V2.Init();
            Directory.CreateDirectory(root);
            rootOwned = true;
            using var work = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            foreach (var mode in Enum.GetValues<Mode>())
                await RunCaseAsync(root, mode, work.Token);
            Check(retainedUnknowns == 2, "Unknown and null callback outcomes retain two distinct private unresolved effects");
            passed = true;
            Stage = "complete-with-retained-unknown-fixture-evidence";
        }
        catch (Exception error)
        {
            failureType = error.GetType().Name;
        }
        finally
        {
            // This process owns only fixture SQLite connections. Clearing its pools releases file handles,
            // not logical Unknown effects; those databases and source/effect evidence stay on disk.
            SqliteConnection.ClearAllPools();
            if (rootOwned) GuardTree(root);
            var report = new
            {
                schemaVersion = 1, taskId = TaskId,
                status = passed ? "passed-provider-assembly-fixture-with-retained-unknown-evidence" : "failed",
                assertions = Labels.Count, labels = Labels, cases = Cases, reachedStage = Stage,
                failure = failureType is null ? null : new { type = failureType, check = FailedCheck },
                elapsedSeconds = started.Elapsed.TotalSeconds, retainedUnknownEffects = passed ? retainedUnknowns : (int?)null, knownRetainedUnknownEffects = retainedUnknowns,
                unknownEffectsResolved = false, fixtureRootRemoved = false, rootOwned,
                boundary = "Actual local plugin resolver, Engine runtime/SQLite normalization, generic203 and production Engine Compose; constructed host/settings/base request and deterministic recording adapter.",
                deadline = new { workSeconds = 60, cooperativeOnly = true, independentHardWallClockVerified = false },
                limits = new[]
                {
                    "No Worker, real generation, native preparation/tokenizer, model/provider-network, Source or installed/default/manifest action.",
                    "Deterministic preparation metadata proves no model capacity or token count.",
                    "Prepared Generate effects are committed but never dispatched; fixture-only definition wrapper cancels only its runtime token.",
                    "Expected Unknown/null outcomes remain Waiting/Unknown with private SQLite evidence retained; resource closure is not resolution.",
                    "Host identity, central-settings shim and base request are constructed fixture context, not Worker admission or public provisioning.",
                    "No original312 capture replay or changed current312 graph; no production policy or consumer modification."
                }
            };
            if (rootOwned)
            {
                WriteNew(logPath, string.Join(Environment.NewLine, Labels.Select(x => "PASS: " + x)) + Environment.NewLine);
                WriteNew(reportPath + ".part", JsonSerializer.Serialize(report, Json) + Environment.NewLine);
                File.Move(reportPath + ".part", reportPath, overwrite: false);
            }
        }
        Console.WriteLine($"ProviderAssemblyFixture status={(passed ? "passed-with-retained-unknown" : "failed")} assertions={Labels.Count}; no inference.");
        return passed ? 0 : 1;
    }

    private static async Task RunCaseAsync(string root, Mode mode, CancellationToken workToken)
    {
        Stage = mode + ":setup";
        workToken.ThrowIfCancellationRequested();
        var caseRoot = Path.Combine(root, mode.ToString());
        Check(!Directory.Exists(caseRoot) && !File.Exists(caseRoot), mode + " case directory is create-new");
        Directory.CreateDirectory(caseRoot);
        var retain = true; // Any unexpected failure preserves its owned diagnostic state.
        using var host = new FixtureHost(Path.Combine(caseRoot, "plugin-data"));
        await host.SaveAsync(true, AId, [DefinitionA()], workToken);
        await host.PrepareAsync(workToken);
        var requestId = "provider-assembly-" + mode.ToString().ToLowerInvariant();
        var facts = new WorkflowContextFacts(new(requestId, "provider-assembly-conversation", "fixture-lead",
            ConversationAgentHarness.Id, ConversationAgentHarness.ContextContributionVersion,
            new(EventActorKind.User, "owner"), null), "lead", 1, "local", "fixture-installation");
        var facet = host.Plugin.ContextContributions.Single();
        var captured = await facet.CaptureAsync(facts, workToken);
        Check(captured.Disposition == WorkflowContextDisposition.Ready && captured.Request is not null,
            mode + " captures actual package-owned Ready A");
        var capturedHash = Digest(captured);
        var original = captured.Request!;
        var reference = original.References.Single();
        Check(reference.SourceId.Split('/')[4] == Hash(BodyA) &&
            reference.Requirement == ContextRequirement.Optional && reference.BeforeCurrentInput,
            mode + " captured source pins exact A text hash, optional classification and placement");

        var sourceBefore = await host.SourcesAsync(workToken);
        var settingsBefore = host.Central.Current.Revision;
        if (mode is Mode.DisabledB or Mode.AppendedB)
        {
            // Current desired values and the whole source-store CAS are not old-source resolution fences.
            await host.SaveAsync(false, BId, mode == Mode.AppendedB ? [DefinitionB()] : [DefinitionA(), DefinitionB()], workToken);
            if (mode == Mode.AppendedB) await host.PrepareAsync(workToken);
            var sourceAfter = await host.SourcesAsync(workToken);
            Check(host.Central.Current.Revision > settingsBefore, mode + " current settings CAS may advance");
            if (mode == Mode.AppendedB)
                Check(sourceAfter.GetProperty("revision").GetInt64() > sourceBefore.GetProperty("revision").GetInt64() &&
                    sourceAfter.GetProperty("sha256").GetString() != sourceBefore.GetProperty("sha256").GetString(),
                    "append advances store CAS without replacing retained A");
            var omittedFacts = facts with { Identity = facts.Identity with { RequestId = requestId + "-omitted" } };
            var omitted = await facet.CaptureAsync(omittedFacts, workToken);
            Check(omitted.Disposition == WorkflowContextDisposition.Omitted && omitted.Request is null,
                mode + " new disabled capture is separately Omitted");
            await CheckOmittedAsync(caseRoot, omitted, omittedFacts, workToken);
        }
        facet.Validate(facts, facts, captured, workToken);
        var restored = await facet.RestoreAsync(new(facts, null, 0, 100, 0), captured, workToken);
        Check(SameTemplate(original, restored) && Digest(captured) == capturedHash,
            mode + " original accepted identity/payload/template remains immutable");
        if (mode == Mode.MissingSource)
        {
            var sourceFile = Path.Combine(host.DataDirectory, "personality-context.json");
            GuardTree(caseRoot);
            File.Delete(sourceFile); // Exact owned negative input only; never a retained prior failure tree.
        }
        var requested = mode switch
        {
            Mode.BadHash => restored with { References = [reference with
                { SourceId = reference.SourceId[..^64] + new string('0', 64) }] },
            Mode.BadRevision => restored with { References = [reference with
                { ExpectedRevision = reference.ExpectedRevision + 1 }] },
            _ => restored
        };
        var caller = mode == Mode.ParentScope ? new EventActor(EventActorKind.Service, "cerebral.coordination") :
            mode == Mode.ForeignCaller ? new EventActor(EventActorKind.User, "foreign") : facts.Identity.Caller;
        var fixtureParentId = requestId + "-parent";
        var submission = Submission(requestId, facts) with { Caller = caller,
            ParentRunId = mode == Mode.ParentScope ? fixtureParentId : null };
        var dbPath = Path.Combine(caseRoot, "engine.db");
        var store = new WorkflowStore(new EventJournal(dbPath));
        using var runtimeToken = CancellationTokenSource.CreateLinkedTokenSource(workToken);
        var innerDefinition = new AgentWorkflowDefinition(ConversationAgentHarness.CreateContextContributionEdition());
        var stop = new StopAtGenerateDefinition(innerDefinition, runtimeToken);
        var provider = new ObservedProvider((IContextProviderCommandHandler)host.Plugin.Commands.Single(), mode);
        var history = History(new { schemaVersion = 1, items = new[] {
            new { id = facet.Descriptor.Id, status = "ready", request = requested } } });
        var commands = HostCommands(history);
        var registry = Registry(stop, provider, commands);
        var runtime = new WorkflowRuntime(store, registry); // Separate private fixture; no live Engine/controller is attached.
        if (mode == Mode.ParentScope)
        {
            // Exercise the provider only after real store lineage admission. No live controller or parent effect runs.
            var parent = store.Submit(Submission(fixtureParentId, facts) with
                { ConversationId = facts.Identity.ConversationId + "-parent", ParentRunId = null });
            Check(!parent.IsTerminal && parent.Effect is null, "constructed live root parent satisfies real store admission");
        }
        store.Submit(submission);
        Stage = mode + ":actual-runtime";
        var run = await runtime.RunAsync(submission.RequestId, runtimeToken.Token);
        Check(provider.Calls == 1 && provider.Reconciliations == 0,
            mode + " provider intent dispatches once without reconciliation or retry");
        Check(commands.Generate.Calls == 0 && commands.Compact.Calls == 0,
            mode + " no generation or compactor handler dispatch");
        var providerContext = provider.BeforeOutcome ?? throw new InvalidOperationException("No committed provider callback context was observed.");

        if (mode is Mode.Unknown or Mode.NullOutcome)
        {
            Stage = mode + ":retain-unresolved";
            Check(run.Status == WorkflowStatus.Waiting && !run.IsTerminal &&
                run.Effect is { State: WorkflowEffectState.Unknown, ProviderReceiptId: null, Result: null },
                mode + " actual Runtime retains Waiting/Unknown rather than synthetic terminal failure");
            var effect = run.Effect!;
            Check(effect.CommandId == original.ProviderId && effect.CommandVersion == original.ProviderVersion &&
                provider.BeforeOutcome is not null && effect.Id == providerContext.EffectId &&
                JsonElement.DeepEquals(effect.Arguments, provider.BeforeOutcome.Arguments),
                mode + " unresolved effect keeps exact committed identity and arguments");
            Check(NoStyle(run.State) && stop.ProviderOutcomesConsumed == 0 &&
                stop.GeneratedStep is null && provider.InnerCalls == 0,
                mode + " Unknown is held before consumer adoption or planner boundary");
            var unresolved = store.GetEffectResult(run.Id, effect.Id);
            Check(unresolved.State == WorkflowEffectState.Unknown && unresolved.Data is null &&
                unresolved.ProviderReceiptId is null, mode + " actual uncertain SQLite receipt remains unconfirmed");
            var reopened = new WorkflowStore(new EventJournal(dbPath)).Get(run.Id);
            Check(JsonElement.DeepEquals(Element(run), Element(reopened)),
                mode + " reopening preserves exact unresolved run/effect/state");
            WriteNew(Path.Combine(caseRoot, "unresolved-effect-evidence.json"),
                JsonSerializer.Serialize(new { taskId = TaskId, mode = mode.ToString(), run,
                    resourceClosureDoesNotResolveEffect = true, noRetryOrReconcile = true }, Json) + Environment.NewLine);
            retainedUnknowns++;
            Cases.Add(Proof(mode, run, provider, stop, null, true));
            return;
        }

        if (mode is Mode.MissingSource or Mode.BadHash or Mode.BadRevision or Mode.ForeignCaller or
            Mode.ParentScope or Mode.MissingReceipt or Mode.WrongResolution)
        {
            // A committed terminal step clears the current effect; the exact provider result remains in SQLite.
            Check(run.Status == WorkflowStatus.Failed && run.IsTerminal && run.Effect is null,
                mode + " confirmed failure stops generic203 even for Optional");
            var persistedFailure = store.GetEffectResult(run.Id, providerContext.EffectId);
            Check(persistedFailure.State == WorkflowEffectState.Failed,
                mode + " exact persisted normalized provider outcome is Failed");
            var failedAttempt = run.State.GetProperty("contextContributions").GetProperty("items")[0];
            Check(run.State.GetProperty("phase").GetString() == "context-contribution-failed" &&
                stop.ProviderOutcomesConsumed == 1 && failedAttempt.GetProperty("attempted").GetBoolean() &&
                failedAttempt.GetProperty("outcome").GetString() == nameof(WorkflowEffectState.Failed) &&
                failedAttempt.GetProperty("effectId").GetString() == providerContext.EffectId &&
                OptionalReceipt(failedAttempt) == persistedFailure.ProviderReceiptId,
                mode + " generic failed attempt matches the persisted effect and receipt including null");
            Check(NoStyle(run.State) && stop.GeneratedStep is null,
                mode + " no source replacement, automatic omission success, or planner call");
            if (mode == Mode.ParentScope)
            {
                var parent = store.Cancel(fixtureParentId);
                Check(parent.Status == WorkflowStatus.Cancelled && parent.Effect is null,
                    "unexecuted private parent cancelled without dispatching any parent effect");
            }
            Cases.Add(Proof(mode, run, provider, stop, null, false, persistedFailure));
            retain = false;
        }
        else
        {
            Stage = mode + ":prepared-generate";
            Check(runtimeToken.IsCancellationRequested && stop.GeneratedStep is { Next: InvokeWorkflowCommand } &&
                run.Effect is { CommandId: "cerebral.host:generate", State: WorkflowEffectState.Prepared },
                mode + " genuine Generate step commits Prepared after private token cancellation");
            var generate = (InvokeWorkflowCommand)stop.GeneratedStep!.Next;
            Check(JsonElement.DeepEquals(run.State, stop.GeneratedStep.State) &&
                JsonElement.DeepEquals(run.Effect!.Arguments, generate.Arguments) &&
                run.Effect.CommandVersion == generate.CommandVersion && !run.IsTerminal,
                mode + " unchanged genuine step/state/arguments committed with zero Generate dispatch");
            Check(provider.Raw is { State: WorkflowEffectState.Succeeded } &&
                provider.Raw.ProviderReceiptId == provider.BeforeOutcome!.EffectId && provider.InnerCalls == 1,
                mode + " actual plugin supplies its real effect-ID receipt");
            var receipt = store.GetEffectResult(run.Id, providerContext.EffectId);
            var result = receipt.Data!.Value.Deserialize<ContextProviderResult>(CerebralJson.Options)!;
            var adopted = result.Window.Contributions.Single();
            var raw = provider.Raw!.Data!.Value.Deserialize<ContextProviderResolution>(CerebralJson.Options)!.Sources.Single();
            Check(Text(raw.Messages) == Text(adopted.Messages) &&
                Text(raw.Messages).Contains(BodyA, StringComparison.Ordinal) &&
                !Text(raw.Messages).Contains(BodyB, StringComparison.Ordinal),
                mode + " exact real plugin A body survives host normalization with no B");
            Check(adopted.Id == reference.Id && adopted.Owner == original.ProviderId &&
                adopted.SourceRevision == reference.ExpectedRevision && adopted.Requirement == reference.Requirement &&
                adopted.Priority == reference.Priority && adopted.BeforeCurrentInput == reference.BeforeCurrentInput,
                mode + " host normalization retains source classification/revision/placement");
            VerifyHandle(adopted.ExactReadHandle!, original, reference, providerContext.EffectId, receipt.ProviderReceiptId!);
            var item = run.State.GetProperty("contextContributions").GetProperty("items")[0];
            Check(item.GetProperty("effectId").GetString() == providerContext.EffectId &&
                item.GetProperty("providerReceiptId").GetString() == receipt.ProviderReceiptId,
                mode + " generic consumer persists actual effect and provider receipt identity");
            var window = generate.Arguments.GetProperty("context").Deserialize<ContextWindow>(CerebralJson.Options)!;
            Check(window.Contributions.Count(x => x.Id == adopted.Id) == 1 &&
                JsonElement.DeepEquals(Element(window.Contributions.Single(x => x.Id == adopted.Id)), Element(adopted)),
                mode + " committed Generate window contains exactly the adopted immutable A source");
            if (mode == Mode.Reopen)
            {
                var reopenedStore = new WorkflowStore(new EventJournal(dbPath));
                var reopened = reopenedStore.Get(run.Id);
                Check(JsonElement.DeepEquals(Element(run), Element(reopened)) &&
                    JsonElement.DeepEquals(Element(receipt), Element(reopenedStore.GetEffectResult(run.Id, providerContext.EffectId))),
                    "reopen retains committed source/receipt/Prepared Generate without any resumed runtime call");
            }
            // This token is independent of the deliberately cancelled private runtime token.
            using var plannerToken = CancellationTokenSource.CreateLinkedTokenSource(workToken);
            Check(!plannerToken.IsCancellationRequested, mode + " planner has a distinct uncancelled fixture token");
            var model = new RecordingAdapter();
            var messages = generate.Arguments.GetProperty("messages").Deserialize<ModelInputMessage[]>(CerebralJson.Options)!;
            var leading = messages.TakeWhile(x => x.Role == "system").Count();
            var basisMessages = new[] { Message("fixture host policy", "system") }
                .Concat(messages.Take(leading)).Append(Message(CurrentInput))
                .Concat(messages.Skip(leading)).ToArray();
            var basis = new ModelGenerationRequest("fixture-unregistered-model", basisMessages, 4096, 512);
            Stage = mode + ":production-compose";
            await using (var prepared = await ContextPlanner.PrepareAsync(model, basis, window, cancellationToken: plannerToken.Token))
            {
                Check(prepared.Projection.Status == ContextProjectionStatus.Ready &&
                    model.Request is not null && model.Created == 1 && model.GenerateCalls == 0,
                    mode + " production planner composes once with deterministic metadata and zero inference");
                var assembled = model.Request!.Messages.ToArray();
                var styleIndices = assembled.Select((message, index) => (message, index))
                    .Where(x => Text([x.message]) == Text(adopted.Messages)).ToArray();
                var currentIndex = Array.FindIndex(assembled, x => Text([x]) == CurrentInput);
                var systems = basisMessages.TakeWhile(x => x.Role == "system").ToArray();
                Check(styleIndices.Length == 1 && styleIndices[0].message.Role == "user" &&
                    styleIndices[0].index == systems.Length && currentIndex > styleIndices[0].index &&
                    systems.Select(Element).Zip(assembled.Take(systems.Length).Select(Element))
                        .All(pair => JsonElement.DeepEquals(pair.First, pair.Second)) &&
                    !Text(assembled).Contains(BodyB, StringComparison.Ordinal),
                    mode + " actual Compose preserves A once/user-role/before current input and exact leading systems");
            }
            Check(model.Disposed == model.Created && model.GenerateCalls == 0,
                mode + " unused recording preparation lease disposed without inference");
            Cases.Add(Proof(mode, run, provider, stop, model, false));
            retain = false;
        }
        if (!retain)
        {
            host.Dispose();
            SqliteConnection.ClearAllPools();
            GuardTree(caseRoot);
            Directory.Delete(caseRoot, recursive: true);
        }
    }

    private static async Task CheckOmittedAsync(string caseRoot, WorkflowContextSelection omitted,
        WorkflowContextFacts facts, CancellationToken workToken)
    {
        var store = new WorkflowStore(new EventJournal(Path.Combine(caseRoot, "omitted.db")));
        using var token = CancellationTokenSource.CreateLinkedTokenSource(workToken);
        var stop = new StopAtGenerateDefinition(new AgentWorkflowDefinition(
            ConversationAgentHarness.CreateContextContributionEdition()), token);
        var commands = HostCommands(History(new { schemaVersion = 1, items = new[] {
            new { id = PersonalityContract.ContributionId, status = "omitted", request = (ContextProviderRequest?)null } } }));
        var registry = new WorkflowRegistry([new Module("pegasus.conversation", [stop], []),
            new Module("cerebral.host", [], commands.All)]);
        var submission = Submission(facts.Identity.RequestId, facts);
        store.Submit(submission);
        var run = await new WorkflowRuntime(store, registry).RunAsync(submission.RequestId, token.Token);
        Check(omitted.Request is null && stop.ProviderOutcomesConsumed == 0 && NoStyle(run.State) &&
            run.Effect is { CommandId: "cerebral.host:generate", State: WorkflowEffectState.Prepared } &&
            commands.Generate.Calls == 0, "new Omitted capture reaches Prepared Generate with no provider or generation dispatch");
    }

    private static object Proof(Mode mode, WorkflowRun run, ObservedProvider provider,
        StopAtGenerateDefinition stop, RecordingAdapter? adapter, bool unresolved,
        WorkflowCommandResult? persistedFailure = null) => new
    {
        caseId = mode.ToString(), actualRuntime = true, callbackOutcomeInjection = provider.Injected,
        providerDispatches = provider.Calls, actualPluginCalls = provider.InnerCalls, reconciliations = provider.Reconciliations,
        runId = run.Id, status = run.Status.ToString(), stateSha256 = Digest(run.State),
        effectId = run.Effect?.Id, effectState = run.Effect?.State.ToString(),
        argumentsSha256 = run.Effect is { } effect ? Digest(effect.Arguments) : null,
        providerEffectId = provider.BeforeOutcome?.EffectId, providerReceiptId = provider.Raw?.ProviderReceiptId,
        persistedFailure = persistedFailure is null ? null : new
        {
            effectId = provider.BeforeOutcome!.EffectId, state = persistedFailure.State.ToString(),
            providerReceiptId = persistedFailure.ProviderReceiptId,
            dataSha256 = persistedFailure.Data is { } data ? Digest(data) : null,
            rawPluginOutcomeState = provider.Raw?.State.ToString(),
            genericPhase = run.State.GetProperty("phase").GetString(),
            genericAttemptOutcome = run.State.GetProperty("contextContributions").GetProperty("items")[0].GetProperty("outcome").GetString(),
            genericAttemptEffectId = run.State.GetProperty("contextContributions").GetProperty("items")[0].GetProperty("effectId").GetString(),
            genericAttemptProviderReceiptId = OptionalReceipt(run.State.GetProperty("contextContributions").GetProperty("items")[0])
        },
        genericProviderOutcomesConsumed = stop.ProviderOutcomesConsumed,
        genuineGenerateReturned = stop.GeneratedStep is not null, generateHandlerCalls = 0,
        plannerCalls = adapter?.Created ?? 0, inferenceCalls = adapter?.GenerateCalls ?? 0,
        leasesCreated = adapter?.Created ?? 0, leasesDisposed = adapter?.Disposed ?? 0,
        assembledRequestSha256 = adapter?.Request is { } request ? Digest(request) : null,
        deterministicPreparationMetadata = adapter is not null,
        unresolvedEffectPreserved = unresolved, unknownResolvedClaimed = false
    };

    private sealed class StopAtGenerateDefinition(IWorkflowDefinition actual, CancellationTokenSource runtimeToken) : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor => actual.Descriptor;
        public WorkflowStep? GeneratedStep { get; private set; }
        public int ProviderOutcomesConsumed { get; private set; }
        public WorkflowStep Advance(WorkflowRun snapshot)
        {
            if (snapshot.Signal.Kind == WorkflowSignalKind.EffectResult &&
                snapshot.Effect?.CommandId == PersonalityContract.ProviderId) ProviderOutcomesConsumed++;
            var step = actual.Advance(snapshot);
            if (step.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:generate" })
            {
                GeneratedStep = step;
                // Runtime commits this UNCHANGED step, then observes cancellation before dispatch.
                // Never cancel a Worker, Source controller, model token or the separate planner token.
                runtimeToken.Cancel();
            }
            return step;
        }
    }

    private sealed class ObservedProvider(IContextProviderCommandHandler actual, Mode mode) : IContextProviderCommandHandler
    {
        public ContextProviderDescriptor Provider => actual.Provider;
        public WorkflowCommandDescriptor Descriptor => actual.Descriptor;
        public int Calls, InnerCalls, Reconciliations;
        public WorkflowCommandContext? BeforeOutcome;
        public WorkflowCommandResult? Raw;
        public bool Injected => mode is Mode.Unknown or Mode.NullOutcome or Mode.MissingReceipt or Mode.WrongResolution;
        public async Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token)
        {
            Calls++; BeforeOutcome = context;
            if (mode == Mode.NullOutcome) return null!; // Deliberate callback fixture; Runtime must preserve Unknown.
            if (mode == Mode.Unknown) return new(WorkflowEffectState.Unknown, Error: "Constructed unresolved callback outcome.");
            InnerCalls++;
            Raw = await actual.ExecuteAsync(context, token);
            if (mode == Mode.MissingReceipt) return Raw with { ProviderReceiptId = null };
            if (mode == Mode.WrongResolution && Raw.Data is { } data)
            {
                var resolution = data.Deserialize<ContextProviderResolution>(CerebralJson.Options)!;
                return Raw with { Data = Element(resolution with { ProviderId = "fixture.foreign:resolve" }) };
            }
            return Raw;
        }
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token)
        { Reconciliations++; throw new InvalidOperationException("Fixture never reconciles or retries an unresolved operation."); }
    }

    private sealed record Module(string Id, IReadOnlyList<IWorkflowDefinition> Workflows,
        IReadOnlyList<IWorkflowCommandHandler> Commands) : IWorkflowModule;

    private sealed class FixedCommand(string id, JsonElement result, bool forbidden = false) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new(id, 1, "Bounded constructed fixture host callback.",
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, [], WorkflowReplayPolicy.Idempotent, 5);
        public int Calls;
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token)
        {
            Calls++;
            if (forbidden) throw new InvalidOperationException("No fixture generation or compactor handler may execute.");
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Succeeded, result.Clone()));
        }
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token) =>
            throw new InvalidOperationException("Fixture callbacks do not reconcile.");
    }
    private sealed record Commands(FixedCommand History, FixedCommand Catalog, FixedCommand Generate, FixedCommand Compact)
    { public IWorkflowCommandHandler[] All => [History, Catalog, Generate, Compact]; }

    private static Commands HostCommands(JsonElement history) => new(
        new("cerebral.host:history", history),
        new("cerebral.host:catalog", Element(new { tools = Array.Empty<object>(), helpers = Array.Empty<object>(), truncated = false })),
        new("cerebral.host:generate", WorkflowContract.EmptyObject, true),
        new("cerebral.host:compact", WorkflowContract.EmptyObject, true));
    private static WorkflowRegistry Registry(IWorkflowDefinition definition, ObservedProvider provider, Commands commands) =>
        new([new Module("pegasus.conversation", [definition], []), new Module("cerebral.host", [], commands.All),
            new Module("pegasus.personality", [], [provider])]);
    private static JsonElement History(object admissions) => Element(new
    { context = new ContextWindow(100, 0, []), hasOlder = false, beforeSequence = (long?)null,
        snapshotSequence = 100L, contextContributions = admissions });
    private static WorkflowSubmission Submission(string id, WorkflowContextFacts facts) =>
        new(id, facts.Identity.ConversationId, facts.Identity.ProfileId, facts.Identity.DefinitionId,
            facts.Identity.DefinitionVersion, CurrentInput, null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject,
            Caller: facts.Identity.Caller) { ActiveTimeLimitSeconds = 30, ParentRunId = facts.Identity.ParentRunId };

    private sealed class RecordingAdapter : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("provider-assembly-recording-fixture", "1.0.0", ["text"]);
        public ModelGenerationRequest? Request;
        public int Created, Disposed, GenerateCalls;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Request = request; Created++;
            return Task.FromResult<IPreparedModelGeneration>(new Lease(this,
                new("fixture", Descriptor.AdapterId, Descriptor.Version, request.ModelId, 42,
                    request.ContextLimit, request.ResponseLimit, "deterministic fixture; not a native token count")));
        }
        private sealed class Lease(RecordingAdapter owner, PreparedModelMetadata metadata) : IPreparedModelGeneration
        {
            private bool disposed;
            public PreparedModelMetadata Metadata => metadata;
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            { owner.GenerateCalls++; throw new InvalidOperationException("Provider assembly fixture must never infer."); }
            public ValueTask DisposeAsync()
            { if (!disposed) { disposed = true; owner.Disposed++; } return ValueTask.CompletedTask; }
        }
    }

    private sealed class FixtureHost : IServicePluginHost, IServicePluginSettingsHost, IDisposable
    {
        public ServicePlugin Plugin { get; } = new();
        public FixtureCentral Central { get; } = new();
        public string PluginId => PersonalityContract.PluginId;
        public string PackageDirectory => AppContext.BaseDirectory;
        public string DataDirectory { get; }
        public IScopedPluginSettings Settings => Central;
        public FixtureHost(string directory)
        { DataDirectory = directory; Directory.CreateDirectory(directory); Plugin.Initialize(this); }
        public void Publish(string eventType, string? jobId, object? data) { }
        public async Task SaveAsync(bool enabled, string selected, PersonalityCustomDefinition[] definitions, CancellationToken token) =>
            _ = await Plugin.ExecuteAsync("personality.preferences.save", Element(new
            { schemaVersion = 2, expectedRevision = Central.Current.Revision,
                values = PersonalitySettingsCodec.Write(new(enabled, selected, definitions)) }), token);
        public async Task PrepareAsync(CancellationToken token)
        {
            var sources = await SourcesAsync(token);
            _ = await Plugin.ExecuteAsync("personality.source.prepare", Element(new
            { schemaVersion = 1, expectedSettingsRevision = Central.Current.Revision,
                expectedStoreRevision = sources.GetProperty("revision").GetInt64(),
                expectedStoreSha256 = sources.GetProperty("sha256").GetString() }), token);
        }
        public async Task<JsonElement> SourcesAsync(CancellationToken token) =>
            Element(await Plugin.ExecuteAsync("personality.sources.get", WorkflowContract.EmptyObject, token));
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; Plugin.Dispose(); } }
    }

    // Deliberately constructed scoped-settings host. The actual package policy still admits every value.
    private sealed class FixtureCentral : IScopedPluginSettings
    {
        private readonly PersonalitySettingsPolicy policy = new();
        public PluginSettingsState Current = new(new(PersonalityContract.PluginId, "global"), 0, null, [], "fixture-package-policy");
        public PluginSettingsState Get(string scope = "global", string? scopeId = null)
        { if (scope != "global" || scopeId is not null) throw new InvalidOperationException("Unexpected fixture scope."); return Current; }
        public PluginSettingsSaveResult Save(int version, JsonElement values, long expected,
            string scope = "global", string? scopeId = null)
        {
            _ = Get(scope, scopeId);
            if (expected != Current.Revision) throw new InvalidOperationException("Fixture central CAS changed.");
            var incoming = new PluginSettingsDocument(PersonalityContract.PluginId, scope, scopeId,
                PersonalitySettingsPolicy.PackageVersion, version, values);
            var admission = policy.Admit(incoming, Current.Active?.Values, false);
            if (!admission.Accepted) throw new InvalidDataException("Actual package policy refused fixture settings.");
            var active = incoming with { SettingsVersion = admission.SettingsVersion, Values = admission.Values.Clone() };
            var changed = Current.Active is null || !JsonElement.DeepEquals(Current.Active.Values, active.Values);
            var portable = policy.ExportPortable(active);
            Current = Current with { Revision = Current.Revision + (changed ? 1 : 0), Active = active,
                PortableValues = portable.Values, OmittedPortableFields = portable.OmittedFields };
            return new(Current, changed);
        }
    }

    private static PersonalityCustomDefinition DefinitionA() => new(AId, "Fixture A", BodyA);
    private static PersonalityCustomDefinition DefinitionB() => new(BId, "Fixture B", BodyB);
    private static ModelInputMessage Message(string text, string role = "user") => new(role, [new TextContentPart(text)]);
    private static string Text(IEnumerable<ModelInputMessage> messages) =>
        string.Join("\n", messages.SelectMany(x => x.Content).OfType<TextContentPart>().Select(x => x.Text));
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);
    private static string Digest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options)));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool SameTemplate(ContextProviderRequest left, ContextProviderRequest right) =>
        JsonElement.DeepEquals(Element(left with { ContextRevision = 0, ExclusionRevision = 0 }),
            Element(right with { ContextRevision = 0, ExclusionRevision = 0 }));
    private static string? OptionalReceipt(JsonElement attempt)
    {
        // The consumer omits this nullable member when a failed attempt has no receipt.
        if (!attempt.TryGetProperty("providerReceiptId", out var receipt) ||
            receipt.ValueKind == JsonValueKind.Null) return null;
        if (receipt.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("An optional provider receipt must be a string or null.");
        return receipt.GetString();
    }
    private static bool NoStyle(JsonElement state) =>
        !state.TryGetProperty("contributions", out var contributions) ||
        !contributions.EnumerateArray().Any(x => x.GetProperty("id").GetString() == PersonalityContract.ContributionId);
    private static void VerifyHandle(string handle, ContextProviderRequest request, ContextProviderReference reference,
        string effect, string receipt)
    {
        var parts = handle.Split('.');
        Check(parts.Length == 7 && parts[0] == "cerebral-context:v1" &&
            Decode(parts[1]) == request.ProviderId && parts[2] == request.ProviderVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            Decode(parts[3]) == effect && Decode(parts[4]) == receipt && Decode(parts[5]) == reference.SourceId &&
            parts[6] == reference.ExpectedRevision!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "host-minted read handle retains actual provider/effect/receipt/exact source coordinates");
    }
    private static string Decode(string token)
    {
        var text = token.Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '=')));
    }
    private static void Check(bool value, string description)
    {
        if (!value) { FailedCheck = description; throw new InvalidOperationException(description); }
        Labels.Add(description);
    }
    private static void WriteNew(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false, true));
        writer.Write(text);
    }
    private static void GuardAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Fixture ancestor is a reparse point.");
    }
    private static void GuardTree(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(ExpectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, ExpectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fixture target is outside its exact owned root.");
        GuardAncestors(full);
        var pending = new Stack<string>();
        pending.Push(full);
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Fixture tree is a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
    }

    private static void VerifyInputs()
    {
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cerebral.PersonalityServicePlugin.dll"] = "135e7ae53911e1632fb0cc02599abf67b53f92e260fcc3d9b777a1fb19461005",
            ["Cerebral.Conversation.dll"] = "20329828d61df72e75950fc9488284093573c3c2d875757efff7c068f7eb84a0",
            ["Cerebral.Conversation.pdb"] = "a51c24f660525d27c3ede336bab5d519db1c74f291167ecc4695166afbacb1a2",
            ["Cerebral.Core.dll"] = "4f29f7057d1794470b1ccec9841d4484c2e6f5f3439ddeb4d0979b9d8425eb57",
            ["Cerebral.Contracts.dll"] = "c7f3b337f939cdc9b61c656947beaa5e3cceb6d0c73df029d072265595a91fa3",
            ["Cerebral.Engine.dll"] = "ead588fed7bb7ba901abdc1c86012298fab98ff20266f17ca62c31a270db84cf",
            ["Cerebral.Engine.Contracts.dll"] = "d212f94bfc7b69930ccd5c33a7bf9a5fd3c3ce23f6dcd8e3072d7ebde15ef89d",
            ["Cerebral.Client.dll"] = "87394e5b366281bd815f58aedfd7a7af8a4dc35666118aa2630142927b95a667",
            ["Cerebral.Tools.dll"] = "28436a1bda189d8c0240fdd166474e0092d2cc38e1d38536c1c445e7b142d7a6",
            ["Microsoft.Data.Sqlite.dll"] = "1078d41c6c0f2cee48a8b706e3a54ce145705993f19f703d2b3bdf2abcc59088",
            ["e_sqlite3.dll"] = "b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e",
            ["SQLitePCLRaw.core.dll"] = "50c999a1ab2afeacc93bce5ac2948d3bb630d99d23efc3cfcbd8bc11d3426eb7",
            ["SQLitePCLRaw.batteries_v2.dll"] = "bc69777001eb4db81c580f62835396f67e74aaa381eef446193a904499d5310b",
            ["SQLitePCLRaw.provider.e_sqlite3.dll"] = "02ad639f45a26de1919ffbb6696bdb399ad01afc149970a163c3867162f310b3"
        };
        foreach (var input in inputs)
        {
            using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, input.Key));
            Check(Convert.ToHexStringLower(SHA256.HashData(stream)) == input.Value,
                "accepted byte pin matches " + input.Key);
        }
        Check(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(
            Path.Combine(Family, @"artifacts\bin\PersonalityProviderAssemblyChecks\Release\net10.0-windows"),
            StringComparison.OrdinalIgnoreCase), "only the named existing output-family entry is admitted");
    }
}
#endif
