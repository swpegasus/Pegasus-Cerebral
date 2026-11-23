using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.IO.Pipes;
using System.Reflection;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Client;
using Cerebral.Engine.Workflows;
using Cerebral.Engine.Persistence;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

internal static class OwnerVerificationBudgetChecks
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Owner verification budget: " + message);
            checks++;
        }
        static string Hash(char value) => new(value, 64);
        var recipe = Hash('a');
        var root = Path.Combine(Path.GetTempPath(), "verification-budget-checks");
        var source = Path.Combine(root, "Plugin.cs");
        var output = Path.Combine(root, "Plugin.dll");
        var first = new[]
        {
            new OwnerVerificationBudget.InputPin("targetSource", source, Hash('b'), 12),
            new OwnerVerificationBudget.InputPin("targetAssembly", output, Hash('c'), 20)
        };
        var identityA = OwnerVerificationBudget.InputIdentity(recipe, first);
        Check(identityA == OwnerVerificationBudget.InputIdentity(recipe, first.Reverse().ToArray()),
            "role ordering cannot create a different tested-input identity");
        Check(identityA == OwnerVerificationBudget.InputIdentity(recipe, first),
            "same bytes and paths preserve identity independent of output report identifiers");
        var manyInputs = Enumerable.Range(0, 33).Select(index =>
            new OwnerVerificationBudget.InputPin("input" + index, Path.Combine(root, "input" + index + ".cs"),
                Hash('b'), 1)).ToArray();
        Check(OwnerVerificationBudget.InputIdentity(recipe, manyInputs) ==
              OwnerVerificationBudget.InputIdentity(recipe, manyInputs.Reverse().ToArray()),
            "a 33-file admitted target closure retains one stable identity without the old 32-file ceiling");
        var second = first.ToArray();
        second[0] = first[0] with { Sha256 = Hash('d') };
        var identityB = OwnerVerificationBudget.InputIdentity(recipe, second);
        Check(identityB != identityA && OwnerVerificationBudget.InputIdentity(recipe, first) == identityA,
            "changed source opens B while a revert returns to A");
        var baseline = new OwnerVerificationBudget.Attempt(recipe, identityA, "failed-a1",
            WorkflowEffectState.Failed, ProcessDispatched: true, Complete: true);
        var attempts = new[] { baseline };
        Check(OwnerVerificationBudget.Evaluate(recipe, identityA, attempts).Kind ==
              OwnerVerificationBudget.DecisionKind.Admit,
            "one real failed baseline leaves one bounded same-input attempt");
        var secondFailure = baseline with { EffectId = "failed-a2" };
        attempts = [baseline, secondFailure];
        var exhausted = OwnerVerificationBudget.Evaluate(recipe, identityA, attempts);
        Check(exhausted.Kind == OwnerVerificationBudget.DecisionKind.CorrectInputs &&
              exhausted.FailedAttempts == 2 && exhausted.EffectId == "failed-a2",
            "two actual failures exhaust only A and cite its last retained receipt");
        Check(OwnerVerificationBudget.Evaluate(recipe, identityB, attempts).Kind ==
              OwnerVerificationBudget.DecisionKind.Admit,
            "a meaningful B correction receives its own allowance");
        Check(OwnerVerificationBudget.Evaluate(recipe, identityA, attempts).Kind ==
              OwnerVerificationBudget.DecisionKind.CorrectInputs,
            "A to B to A cannot reset A's consumed allowance");
        var refused = baseline with { EffectId = "preflight", ProcessDispatched = false };
        Check(OwnerVerificationBudget.Evaluate(recipe, identityB, [refused]).Kind ==
              OwnerVerificationBudget.DecisionKind.Admit,
            "a no-process refusal cannot consume a checker execution allowance");
        var passed = baseline with { EffectId = "passed-b", InputSha256 = identityB, State = WorkflowEffectState.Succeeded };
        var duplicate = OwnerVerificationBudget.Evaluate(recipe, identityB, [baseline, passed]);
        Check(duplicate.Kind == OwnerVerificationBudget.DecisionKind.ReuseSuccess && duplicate.EffectId == "passed-b",
            "successful current inputs block another checker execution despite a new report GUID");
        var uncertain = baseline with { EffectId = "unknown-b", InputSha256 = identityB,
            State = WorkflowEffectState.Unknown, Complete = false };
        Check(OwnerVerificationBudget.Evaluate(recipe, identityB, [uncertain]).Kind ==
              OwnerVerificationBudget.DecisionKind.ReconcilePrior,
            "an uncertain effect requires exact reconciliation before another execution");
        Check(OwnerVerificationBudget.Evaluate(recipe, identityB, [passed, uncertain]).Kind ==
              OwnerVerificationBudget.DecisionKind.ReconcilePrior,
            "a later uncertain effect cannot be hidden by an earlier successful receipt");
        var malformed = false;
        try { _ = OwnerVerificationBudget.InputIdentity(recipe, [first[0], first[0]]); }
        catch (ArgumentException) { malformed = true; }
        Check(malformed, "duplicate file roles or paths refuse before budget decisions");

        var checker = Path.Combine(root, "checker");
        var target = Path.Combine(root, "target");
        // The current Programming checker is concrete test data. Production Worker code
        // admits its role map and report contract from the authenticated owner recipe.
        var roleSpecs = new (string Role, OwnerVerificationAdmission.Scope Scope, string RelativePath)[]
        {
            ("checkerAssembly", OwnerVerificationAdmission.Scope.Checker, "BehaviorProbeV2.dll"),
            ("checkerDeps", OwnerVerificationAdmission.Scope.Checker, "BehaviorProbeV2.deps.json"),
            ("checkerRuntimeConfig", OwnerVerificationAdmission.Scope.Checker, "BehaviorProbeV2.runtimeconfig.json"),
            ("checkerProject", OwnerVerificationAdmission.Scope.Checker, "BehaviorProbeV2.csproj"),
            ("checkerSource", OwnerVerificationAdmission.Scope.Checker, "Program.cs"),
            ("checkerProtocolSource", OwnerVerificationAdmission.Scope.Checker, "ProbeContract.cs"),
            ("checkerStagingSource", OwnerVerificationAdmission.Scope.Checker, "StagedInputs.cs"),
            ("checkerLoadingSource", OwnerVerificationAdmission.Scope.Checker, "PinnedPluginLoader.cs"),
            ("checkerCasesSource", OwnerVerificationAdmission.Scope.Checker, "ReceiptPreviewCases.cs"),
            ("checkerReportSource", OwnerVerificationAdmission.Scope.Checker, "ProbeReports.cs"),
            ("targetSource", OwnerVerificationAdmission.Scope.Target, "Desktop/GeneratedDesktopPlugin.cs"),
            ("targetProject", OwnerVerificationAdmission.Scope.Target, "Desktop/Pegasus.Plugin.pegasus.receipt-total-preview-590b05.Desktop.csproj"),
            ("targetSourceManifest", OwnerVerificationAdmission.Scope.Target, "Desktop/desktop-plugin.json"),
            ("targetManifest", OwnerVerificationAdmission.Scope.Target, "Desktop/bin/Release/net10.0-windows/desktop-plugin.json"),
            ("targetAssembly", OwnerVerificationAdmission.Scope.Target, "Desktop/bin/Release/net10.0-windows/Pegasus.Plugin.pegasus.receipt-total-preview-590b05.Desktop.dll"),
            ("targetDeps", OwnerVerificationAdmission.Scope.Target, "Desktop/bin/Release/net10.0-windows/Pegasus.Plugin.pegasus.receipt-total-preview-590b05.Desktop.deps.json"),
            ("targetSdk", OwnerVerificationAdmission.Scope.Target, "sdk/desktop/Cerebral.Desktop.Plugins.dll")
        };
        var names = new[] { "manifest entry exists", "initial total", "decimal sample",
            "invalid input retains last valid total", "invalid first physical line guidance",
            "blank lines preserve physical numbering", "overflow gives guidance and retains valid total",
            "recovery after invalid or overflow",
            "invalid-only input names physical line and retains last valid total",
            "overflow guidance explains range and recovery" };
        var contract = new OwnerVerificationAdmission.Contract(
            "pegasus.cerebral.behavior-probe-descriptor/1", "pegasus.cerebral.behavior-probe-closure/1", "2.0.0",
            "pegasus.cerebral.behavior-probe-summary/2", "pegasus.cerebral.behavior-probe-report/2",
            "checkerAssembly", ["targetAssembly", "targetSdk"], names);
        var files = roleSpecs.Select(spec =>
            new OwnerVerificationAdmission.File(spec.Role, spec.Scope, spec.RelativePath,
                Path.Combine(spec.Scope == OwnerVerificationAdmission.Scope.Checker ? checker : target,
                    spec.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                spec.Scope == OwnerVerificationAdmission.Scope.Checker ? Hash('1') : null,
                spec.Scope == OwnerVerificationAdmission.Scope.Checker ? 10 : null)).ToArray();
        var admission = new OwnerVerificationAdmission(2, "fixture-recipe", "fixture-request", "fixture-chat",
            "verification-fixture", "pegasus.conversation:chat", "1.0.6-repeat-guard.1", "dotnet",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
            [], [Path.Combine(checker, "BehaviorProbeV2.dll")], checker, checker, target, files, contract, 2);
        admission.Validate();
        var tupleGate = typeof(WorkerWorkflows).GetMethod("RequireOwnerVerificationTuple",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The owner verification tuple gate is unavailable.");
        bool Admits(OwnerVerificationAdmission recipeInput, SubmitWorkflowRequest submissionInput)
        {
            try { tupleGate.Invoke(null, [submissionInput, recipeInput]); return true; }
            catch (TargetInvocationException error) when (error.InnerException is UnauthorizedAccessException)
            { return false; }
        }
        var oldTuple = new SubmitWorkflowRequest(admission.RequestId, admission.ConversationId,
            admission.ProfileId, admission.DefinitionId, admission.DefinitionVersion,
            "Check current inputs.", null, WorkflowContract.EmptyObject);
        var combinedAdmission = admission with
            { DefinitionVersion = Cerebral.Conversation.ConversationAgentHarness.RepeatPreparedVersion };
        var combinedTuple = oldTuple with { DefinitionVersion = combinedAdmission.DefinitionVersion };
        var verifiedAdmission = admission with
            { DefinitionVersion = Cerebral.Conversation.ConversationAgentHarness.OwnerVerifiedPreparedVersion };
        var verifiedTuple = oldTuple with { DefinitionVersion = verifiedAdmission.DefinitionVersion };
        Check(Admits(admission, oldTuple) && Admits(combinedAdmission, combinedTuple) &&
              Admits(verifiedAdmission, verifiedTuple),
            "repeat, combined and owner-verified definitions retain exact owner recipe admission");
        Check(!Admits(verifiedAdmission, combinedTuple) &&
              !Admits(verifiedAdmission, verifiedTuple with { RequestId = "foreign-request" }) &&
              !Admits(verifiedAdmission with { DefinitionVersion = "1.0.8-owner-verified-prepared.2" },
                  verifiedTuple with { DefinitionVersion = "1.0.8-owner-verified-prepared.2" }),
            "owner-verified admission refuses wrong edition, request and unexported successor version");
        Check(!Admits(combinedAdmission, oldTuple) &&
            !Admits(admission with { DefinitionVersion = "1.0.4-prepared-intervention.1" },
                oldTuple with { DefinitionVersion = "1.0.4-prepared-intervention.1" }) &&
            !Admits(admission with { DefinitionVersion = "1.0.1" },
                oldTuple with { DefinitionVersion = "1.0.1" }) &&
            !Admits(combinedAdmission, combinedTuple with { ProfileId = "foreign-profile" }),
            "version, recipe and owner tuple mismatches refuse without widening admission");
        var historyContext = new WorkflowCommandContext("history-effect",
            new WorkflowSubmission(admission.RequestId, admission.ConversationId, admission.ProfileId,
                admission.DefinitionId, admission.DefinitionVersion, "Check current inputs.", null,
                WorkflowContract.EmptyObject, WorkflowContract.EmptyObject), WorkflowContract.EmptyObject)
            { RunId = admission.RequestId };
        var historyRefusal = WorkerWorkflowEffects.CheckerHistoryRefusal(historyContext, admission);
        var historyData = CerebralJson.Read<WorkerWorkflowEffects.ToolData>(historyRefusal.Data!.Value);
        Check(historyRefusal.State == WorkflowEffectState.Failed &&
              historyRefusal.ProviderReceiptId is null &&
              historyRefusal.Error?.Contains("saved checker intent history", StringComparison.Ordinal) == true &&
              historyData.Verification is { ProcessDispatched: false, Complete: true, EffectId: "history-effect" } &&
              historyData.Verification.Inputs.Count == 0 &&
              historyData.Evidence is { Outcome: ToolExecutionOutcome.Rejected, State: "not-dispatched" },
            "known history failures return an actionable host-owned no-process receipt, not a model/provider error");
        var exactCall = CerebralJson.Element(new { executableId = "dotnet",
            arguments = new[] { Path.Combine(checker, "BehaviorProbeV2.dll") },
            workingDirectory = checker, timeoutSeconds = 180 });
        Check(admission.MatchesProcessRun(exactCall) && admission.ReservesProcessRun(exactCall),
            "the frozen model-facing checker invocation remains admitted and reserved");
        var wrongCheckerDirectory = CerebralJson.Element(new { executableId = "dotnet",
            arguments = admission.ModelArguments, workingDirectory = target, timeoutSeconds = 180,
            expectedExitCode = 0 });
        Check(admission.ReservesProcessRun(wrongCheckerDirectory) &&
              !admission.MatchesProcessRun(wrongCheckerDirectory),
            "the exact checker image with the target working directory is reserved before ordinary dispatch");
        var malformedRefusal = WorkerWorkflowEffects.MalformedOwnerVerificationProcessRun(
            historyContext, admission);
        var malformedData = CerebralJson.Read<WorkerWorkflowEffects.ToolData>(malformedRefusal.Data!.Value);
        Check(malformedRefusal.State == WorkflowEffectState.Failed &&
              malformedData.Evidence is { Outcome: ToolExecutionOutcome.Rejected,
                  ProviderId: "cerebral.owner-verification", State: "not-dispatched" } &&
              malformedData.Verification is { ProcessDispatched: false, Complete: true } &&
              malformedRefusal.Error?.Contains("working directory", StringComparison.Ordinal) == true,
            "a malformed reserved checker request returns an actionable no-process host refusal");
        var historyRoot = Path.Combine(Path.GetTempPath(), "verification-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(historyRoot);
        try
        {
            var journal = new EventJournal(Path.Combine(historyRoot, "engine.db"));
            var workflows = new WorkflowStore(journal);
            var submitted = new WorkflowSubmission(admission.RequestId, admission.ConversationId,
                admission.ProfileId, admission.DefinitionId, admission.DefinitionVersion,
                "Check the current project.", null, WorkflowContract.EmptyObject,
                WorkflowContract.EmptyObject, StepLimit: 4);
            var accepted = workflows.Submit(submitted);
            Check(accepted.Submission.ConversationSequence == 0,
                "a fresh accepted workflow freezes the genuine zero conversation fence");
            var intent = new CerebralEvent("zero-intent", accepted.Submission.ConversationId, accepted.Id,
                0, CerebralEventTypes.ToolCall, new(EventActorKind.Agent, accepted.Submission.ProfileId),
                [], new(), new(EventSourceKind.Service, "cerebral.engine", JobId: accepted.Id),
                DateTimeOffset.UtcNow,
                CerebralJson.Element(new { callId = "zero-effect",
                    toolName = WorkerWorkflowEffects.ToolCommand,
                    arguments = new { name = "process.run", arguments = exactCall },
                    commandVersion = 1 }),
                CorrelationId: "zero-effect");
            var committed = journal.Append(intent, journal.GetRevision(accepted.Submission.ConversationId));
            var page = journal.ReadPreviousPage(accepted.Submission.ConversationId);
            var foundCurrent = page.Events.Any(item => item.Id == committed.Event.Id &&
                item.CorrelationId == "zero-effect");
            Check(committed.Event.Sequence > accepted.Submission.ConversationSequence &&
                page.NextBeforeSequence is null && page.RetentionGap is null && foundCurrent,
                "the real journal exhausts naturally after a current checker intent and accepted input");
            var currentContext = new WorkflowCommandContext("zero-effect", accepted.Submission,
                WorkflowContract.EmptyObject) { RunId = accepted.Id };
            Check(WorkerWorkflowEffects.PriorVerificationAttempts(journal, workflows, currentContext,
                    admission, CancellationToken.None).Count == 0,
                "the actual checker history scanner admits a first intent at the zero fence");
            Check(WorkerWorkflowEffects.CompleteHistoryAtZeroOrigin(
                accepted.Submission.ConversationSequence, page, foundCurrent),
                "a complete fresh zero-origin page reaches the accepted history fence");
            Check(!WorkerWorkflowEffects.CompleteHistoryAtZeroOrigin(0, page, false),
                "a missing current checker intent cannot be excused by zero-origin exhaustion");
            Check(!WorkerWorkflowEffects.CompleteHistoryAtZeroOrigin(1, page, foundCurrent),
                "natural exhaustion cannot replace a nonzero accepted history fence");
            Check(!WorkerWorkflowEffects.CompleteHistoryAtZeroOrigin(0,
                page with { RetentionGap = new JournalRetentionGap(1, 20, 1, 1, DateTimeOffset.UtcNow) },
                foundCurrent), "a retention gap cannot become complete zero-origin checker history");
            var missingCurrent = false;
            try
            {
                _ = WorkerWorkflowEffects.PriorVerificationAttempts(journal, workflows,
                    currentContext with { EffectId = "missing-effect" }, admission, CancellationToken.None);
            }
            catch (InvalidDataException) { missingCurrent = true; }
            Check(missingCurrent, "the actual scanner refuses when the current checker intent is absent");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(historyRoot, recursive: true);
        }
        Check(!admission.MatchesProcessRun(CerebralJson.Element(new { executableId = "dotnet",
            arguments = new[] { Path.Combine(checker, "BehaviorProbeV2.dll"), "--extra" },
            workingDirectory = checker })), "extra model arguments cannot enter the owner checker route");
        Check(!admission.MatchesProcessRun(CerebralJson.Element(new { executableId = "dotnet",
            arguments = new[] { Path.Combine(checker, "BehaviorProbeV2.dll") },
            workingDirectory = checker, expectedExitCode = 1 })),
            "a model cannot redefine checker success with expectedExitCode");
        Check(admission.ReservesProcessRun(CerebralJson.Element(new { executableId = "dotnet",
                  arguments = new[] { Path.Combine(checker, "BehaviorProbeV2.dll"), "--extra" },
                  workingDirectory = checker })) &&
              admission.ReservesProcessRun(CerebralJson.Element(new { executableId = "dotnet",
                  arguments = admission.ModelArguments, workingDirectory = checker,
                  timeoutSeconds = 180, expectedExitCode = 1 })) &&
              admission.ReservesProcessRun(CerebralJson.Element(new { executableId = "dotnet",
                  arguments = admission.ModelArguments, workingDirectory = checker })) &&
              admission.ReservesProcessRun(CerebralJson.Element(new { executableId = "dotnet",
                  arguments = admission.ModelArguments, workingDirectory = checker,
                  timeoutSeconds = 180, unexpected = true })) &&
              !admission.ReservesProcessRun(CerebralJson.Element(new { executableId = "dotnet",
                  arguments = new[] { "build", Path.Combine(target, "Plugin.csproj") },
                  workingDirectory = target })),
            "inexact checker shape remains reserved while ordinary builds do not");
        using (var duplicateJson = JsonDocument.Parse("{\"executableId\":\"dotnet\",\"executableId\":\"other\",\"arguments\":" +
            JsonSerializer.Serialize(admission.ModelArguments) + "}"))
            Check(admission.ReservesProcessRun(duplicateJson.RootElement) &&
                  !admission.MatchesProcessRun(duplicateJson.RootElement),
                "duplicate JSON properties cannot hide a frozen checker reference from the reservation");
        Check(OwnerVerificationAdmission.Read(CerebralJson.Element(admission)).RecipeSha256 == admission.RecipeSha256,
            "closed versioned owner recipe round-trips without model input");
        var unknownContract = JsonNode.Parse(CerebralJson.Element(admission).GetRawText())!.AsObject();
        unknownContract["checkerContract"]!.AsObject()["unreviewedRule"] = true;
        var unknownContractRefused = false;
        try { _ = OwnerVerificationAdmission.Read(CerebralJson.Element(unknownContract)); }
        catch (InvalidDataException) { unknownContractRefused = true; }
        Check(unknownContractRefused, "an unknown checker-contract field refuses before owner admission");
        var transportInput = new SubmitWorkflowRequest(admission.RequestId, admission.ConversationId,
            admission.ProfileId, admission.DefinitionId, admission.DefinitionVersion,
            "Check the built behavior.", null, WorkflowContract.EmptyObject)
        { OwnerVerification = CerebralJson.Element(admission) };
        var transportJson = CerebralJson.Element(transportInput);
        var parsed = CommandDispatcher.ReadOwnerVerificationRequest(transportJson);
        Check(parsed.Input.ProfileId == admission.ProfileId &&
              parsed.Admission.RecipeSha256 == admission.RecipeSha256,
            "closed owner transport retains the non-null selected helper and exact recipe");
        var alias = JsonNode.Parse(transportJson.GetRawText())!.AsObject();
        alias["ProfileId"] = alias["profileId"]!.DeepClone();
        Check(Refuses(alias), "case-variant duplicate transport identity refuses before host admission");
        var extra = JsonNode.Parse(transportJson.GetRawText())!.AsObject();
        extra["unreviewed"] = true;
        Check(Refuses(extra), "unknown owner transport field refuses before host admission");
        Check(CommandDispatcher.HasOwnerVerificationField(new CommandRequest("jobs.workflow.submit", transportJson)),
            "generic owner command identifies the recipe before state or file access");
        Check(CommandDispatcher.HasOwnerVerificationField(new CommandRequest("jobs.workflow.submit", transportJson,
                admission.ProfileId)),
            "agent-profile command cannot bypass owner recipe detection");
        var clientCalls = 0;
        var unverifiedClient = new WorkflowConversationClient(admission.ConversationId,
            (_, _) => { clientCalls++; return Task.FromResult(CommandResult.Fail("unexpected")); });
        var clientRefused = false;
        try
        {
            _ = unverifiedClient.SubmitAsync(new WorkflowConversationSubmission(admission.RequestId,
                admission.ProfileId, admission.DefinitionId, admission.DefinitionVersion,
                "Check the built behavior.", WorkflowContract.EmptyObject)
            { OwnerVerification = CerebralJson.Element(admission) });
        }
        catch (UnauthorizedAccessException) { clientRefused = true; }
        Check(clientRefused && clientCalls == 0,
            "unverified SDK client refuses an owner recipe before a transport call");
        var endpoint = new LocalWorkerEndpoint("verification-fixture-" + Guid.NewGuid().ToString("N"),
            new string('1', 32));
        var exe = Path.Combine(root, "Worker.exe");
        var argv = new[] { exe, "--local-run", "--data", root, "--pipe-name", endpoint.PipeName,
            "--pipe-instance", endpoint.InstanceId, "--engines", root };
        var expectedOwner = new LocalWorkerExpectedOwner(endpoint, 1234, 123456789,
            exe, "S-1-5-21-1", 1, root, argv);
        Check(expectedOwner.NativeArgv.SequenceEqual(argv),
            "expected SDK owner freezes the selected development Worker argument vector");
        argv[3] = Path.Combine(root, "changed");
        Check(expectedOwner.NativeArgv[3] == root,
            "later caller argument mutation cannot redirect an expected owner root");
        var wrongRoot = false;
        try { _ = new LocalWorkerExpectedOwner(endpoint, 1234, 123456789,
            exe, "S-1-5-21-1", 1, root, argv); }
        catch (ArgumentException) { wrongRoot = true; }
        Check(wrongRoot, "inconsistent development Worker data root refuses");
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet", "dotnet.exe");
        var workerDll = Path.Combine(root, "app", "Cerebral.Worker.dll");
        var flags = new[] { "--local-run", "--model-admission-closed", "--data", root,
            "--pipe-name", endpoint.PipeName, "--pipe-instance", endpoint.InstanceId,
            "--engines", root };
        var direct = new[] { dotnet, workerDll }.Concat(flags).ToArray();
        var explicitExec = new[] { dotnet, "exec", workerDll }.Concat(flags).ToArray();
        LocalWorkerExpectedOwner DotnetOwner(string[] arguments) =>
            new(endpoint, 1234, 123456789, dotnet, "S-1-5-21-1", 1, root, arguments);
        bool RejectDotnet(string[] arguments)
        {
            try { _ = DotnetOwner(arguments); return false; }
            catch (ArgumentException) { return true; }
        }
        Check(DotnetOwner(direct).NativeArgv.SequenceEqual(direct),
            "framework-dependent direct Worker DLL route retains the full expected argv");
        Check(DotnetOwner(explicitExec).NativeArgv.SequenceEqual(explicitExec),
            "framework-dependent exec Worker DLL route retains the full expected argv");
        Check(RejectDotnet(new[] { dotnet, "--info", workerDll }.Concat(flags).ToArray()),
            "dotnet host switches cannot precede the Worker entry");
        Check(RejectDotnet(new[] { dotnet, "exec", "--runtimeconfig", workerDll }.Concat(flags).ToArray()),
            "dotnet exec runtime switches cannot precede the Worker entry");
        Check(RejectDotnet(new[] { dotnet, Path.Combine(root, "app", "Other.dll") }.Concat(flags).ToArray()),
            "a different managed entry cannot claim the Worker owner");
        Check(RejectDotnet(new[] { dotnet, "Cerebral.Worker.dll" }.Concat(flags).ToArray()),
            "a relative Worker entry cannot claim the reviewed closure");
        Check(RejectDotnet(new[] { dotnet, workerDll, workerDll }.Concat(flags).ToArray()),
            "an extra Worker entry operand cannot become a flag");
        Check(RejectDotnet(direct.Concat(new[] { "--data", root }).ToArray()),
            "duplicate data-root flags refuse on the dotnet route");
        Check(RejectDotnet(new[] { dotnet, workerDll }.Concat(flags.Where(flag => flag != "--local-run")).ToArray()),
            "dotnet route without explicit local-run refuses");
        var changedDotnetRoot = direct.ToArray();
        changedDotnetRoot[5] = Path.Combine(root, "changed");
        Check(RejectDotnet(changedDotnetRoot),
            "dotnet route with a changed source data root refuses");
        if (OperatingSystem.IsWindows())
        {
            var parsedArgv = LocalWorkerNativeArguments.ParseNativeCommandLine(
                "\"C:\\selected Worker.exe\" --data \"C:\\selected data\"");
            Check(parsedArgv.SequenceEqual(new[] { "C:\\selected Worker.exe", "--data", "C:\\selected data" }),
                "shared native argument parser preserves quoted paths");
            using var fakePipe = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var fakeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var connected = fakePipe.WaitForConnectionAsync(fakeDeadline.Token);
            using var client = new LocalWorkerClient(expectedOwner);
            var wrongPeer = false;
            try { _ = client.CommandAsync(new CommandRequest("health", CerebralJson.Element(new { })),
                fakeDeadline.Token).GetAwaiter().GetResult(); }
            catch (IOException) { wrongPeer = true; }
            connected.GetAwaiter().GetResult();
            Check(wrongPeer,
                "expected-owner SDK refuses a different same-user pipe server before command dispatch");
        }
        Check(LocalUserOwnerBinding.TryCapture(["--local-run", "--pipe-name", endpoint.PipeName,
                "--pipe-instance", endpoint.InstanceId], root, endpoint) is null,
            "development owner binding requires explicit source data root");
        Check(LocalUserOwnerBinding.TryCapture(["--installed-pipe", "--local-run", "--data", root,
                "--pipe-name", endpoint.PipeName, "--pipe-instance", endpoint.InstanceId], root, endpoint) is null,
            "installed mode cannot become a user-session owner binding");
        var submitSchema = WorkerWorkflows.OperationDescriptors.Single(item => item.Id == "jobs.workflow.submit").InputSchema;
        Check(submitSchema.GetProperty("properties").TryGetProperty("ownerVerification", out var recipeSchema) &&
              recipeSchema.GetProperty("additionalProperties").ValueKind == JsonValueKind.False &&
              submitSchema.GetProperty("required").EnumerateArray().All(item => item.GetString() != "ownerVerification"),
            "owner recipe is optional and its discovered schema is closed");
        Check(recipeSchema.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == 2 &&
              recipeSchema.GetProperty("required").EnumerateArray().Any(item => item.GetString() == "checkerContract") &&
              recipeSchema.GetProperty("properties").GetProperty("checkerContract")
                  .GetProperty("additionalProperties").ValueKind == JsonValueKind.False,
            "discovered owner transport names the edition-two closed checker contract");
        static bool Refuses(JsonNode value)
        {
            try { _ = CommandDispatcher.ReadOwnerVerificationRequest(CerebralJson.Element(value)); return false; }
            catch (InvalidDataException) { return true; }
        }
        var profile = new AgentProfile { Id = "verification-fixture", WorkerId = "local", ModelWorkerId = "local" };
        var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, profile,
            "", "", null, [], [], [], [], false, "fixture-routing", [], []);
        var oldWire = WorkerExecutionSnapshot.Bounded(snapshot);
        var joined = WorkerExecutionSnapshot.BindOwnerVerification(oldWire, admission);
        Check(WorkerExecutionSnapshot.Read(joined).OwnerVerification?.RecipeSha256 == admission.RecipeSha256 &&
              WorkerExecutionSnapshot.Read(joined).SourceSettingsVersion == WorkerExecutionSnapshot.OwnerVerificationVersion,
            "owner recipe is frozen only in a distinct versioned snapshot");
        var budgetHost = new WorkerWorkflowEffects(null!, null!, null!, null!);
        var toolHandler = budgetHost.Commands.Single(item => item.Descriptor.Id == WorkerWorkflowEffects.ToolCommand);
        WorkflowCommandContext BudgetContext(JsonElement request) => new("budget-effect",
            historyContext.Submission with { SettingsSnapshot = joined },
            CerebralJson.Element(new WorkerWorkflowEffects.ToolArguments("process.run", request)))
        { RunId = admission.RequestId };
        Check(toolHandler.GetFailureBudgetKey(BudgetContext(wrongCheckerDirectory)) is null &&
              toolHandler.GetFailureBudgetKey(BudgetContext(exactCall)) is null,
            "malformed no-process checker requests cannot consume the ordinary launch failure budget");
        var chatSnapshot = snapshot with
        {
            ResolvedSettingsVersion = WorkerExecutionSnapshot.ChatPermissionsVersion,
            ChatPermissions = ChatToolPermissions.Baseline(profile, "fixture-chat", null)
        };
        var combined = WorkerExecutionSnapshot.BindOwnerVerification(
            WorkerExecutionSnapshot.Bounded(chatSnapshot), admission);
        var combinedRead = WorkerExecutionSnapshot.Read(combined);
        Check(combinedRead.SourceSettingsVersion == WorkerExecutionSnapshot.ChatPermissionsVersion &&
              combinedRead.OwnerVerification?.RecipeSha256 == admission.RecipeSha256 &&
              combinedRead.ChatPermissions?.ConversationId == "fixture-chat",
            "edition-eight freezes the owner recipe and chat permission binding together");
        var routeRoot = Path.Combine(Path.GetTempPath(), "owner-near-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(routeRoot);
        try
        {
            Directory.CreateDirectory(root);
            var routeStore = new StateStore(Path.Combine(routeRoot, "state"));
            var routeConfiguration = new Configuration(routeStore);
            routeConfiguration.Get();
            var routeProfile = new AgentProfile
            {
                Id = admission.ProfileId, WorkerId = "local", ModelWorkerId = "local",
                ModelId = "near-route-model", AllowedTools = ["process.run"],
                Capabilities = ["process.execute"], AllowedRoots = [root]
            };
            routeStore.Put("agent", routeProfile.Id, routeProfile);
            routeStore.Put("model", "near-route-model", new ModelRecord
            {
                Id = "near-route-model", Path = Path.Combine(routeRoot, "fixture.gguf"),
                ContextTokens = 4096, MaximumContextTokens = 4096, RecommendedContextTokens = 4096
            });
            var routeTools = new NearMatchCountingTools();
            var captured = WorkerExecutionSnapshot.CaptureAsync(routeStore, routeProfile,
                routeConfiguration, routeTools,
                [new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])],
                default).GetAwaiter().GetResult();
            var chatBase = WorkerExecutionSnapshot.Read(captured) with
            {
                ResolvedSettingsVersion = WorkerExecutionSnapshot.ChatPermissionsVersion,
                ChatPermissions = ChatToolPermissions.Baseline(routeProfile, admission.ConversationId, null)
            };
            var routeSettings = WorkerExecutionSnapshot.BindOwnerVerification(
                WorkerExecutionSnapshot.Bounded(chatBase), admission);
            var routeSubmission = new WorkflowSubmission(admission.RequestId, admission.ConversationId,
                admission.ProfileId, admission.DefinitionId, admission.DefinitionVersion,
                "Check current source.", null, WorkflowContract.EmptyObject, routeSettings);
            var routeHandler = new WorkerWorkflowEffects(routeStore, routeConfiguration, null!, routeTools)
                .Commands.Single(item => item.Descriptor.Id == WorkerWorkflowEffects.ToolCommand);
            var routeContext = new WorkflowCommandContext("near-route-effect", routeSubmission,
                CerebralJson.Element(new WorkerWorkflowEffects.ToolArguments("process.run",
                    wrongCheckerDirectory, root))) { RunId = admission.RequestId };
            var routeResult = routeHandler.ExecuteAsync(routeContext, default).GetAwaiter().GetResult();
            var routeData = routeResult.Data is { } resultData
                ? CerebralJson.Read<WorkerWorkflowEffects.ToolData>(resultData) : null;
            Check(routeResult.State == WorkflowEffectState.Failed &&
                  routeData?.Evidence is { Outcome: ToolExecutionOutcome.Rejected,
                      ProviderId: "cerebral.owner-verification", State: "not-dispatched" } &&
                  routeTools.Executions == 0 && routeTools.Reconciliations == 0,
                "actual tool handler refuses the wrong checker directory without calling the dispatcher");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(routeRoot, recursive: true);
        }
        var forgedOwnerEdition = JsonNode.Parse(joined.GetRawText())!.AsObject();
        forgedOwnerEdition["chatPermissions"] = JsonNode.Parse(
            CerebralJson.Element(chatSnapshot.ChatPermissions).GetRawText());
        var forgedChatRefused = false;
        try { _ = WorkerExecutionSnapshot.Read(CerebralJson.Element(forgedOwnerEdition)); }
        catch (InvalidDataException) { forgedChatRefused = true; }
        Check(forgedChatRefused, "retained edition-seven owner snapshot refuses a later chat binding");
        var noLocalDefinition = new WorkflowDefinitionDescriptor("fixture.owner:check", "1.0.0",
            "Owner verification fixture", WorkflowContract.EmptyObject, []);
        Check(WorkerWorkflows.RequireLocalGenerationConsumerSnapshot(
                  WorkerExecutionSnapshot.Read(oldWire), noLocalDefinition) is null &&
              WorkerWorkflows.RequireLocalGenerationConsumerSnapshot(
                  WorkerExecutionSnapshot.Read(joined), noLocalDefinition) is null,
            "ordinary and owner verification current editions admit an unchanged absent local consumer");
        var legacyLocalConsumerRefused = false;
        try
        {
            WorkerWorkflows.RequireLocalGenerationConsumerSnapshot(
                WorkerExecutionSnapshot.Read(oldWire) with { SourceSettingsVersion = 5 }, noLocalDefinition);
        }
        catch (InvalidDataException) { legacyLocalConsumerRefused = true; }
        Check(legacyLocalConsumerRefused,
            "legacy snapshot cannot enter current local consumer admission even without a choice");
        var declaredLocalConsumer = new WorkflowLocalGenerationConsumer(1,
            WorkflowLocalGenerationConsumer.NativeGenerateCommandId,
            WorkflowLocalGenerationConsumer.NativeGenerateCommandVersion,
            WorkflowLocalGenerationChoice.NativeTextRoute);
        var changedLocalConsumerRefused = false;
        try
        {
            WorkerWorkflows.RequireLocalGenerationConsumerSnapshot(WorkerExecutionSnapshot.Read(joined),
                noLocalDefinition with { LocalGenerationConsumer = declaredLocalConsumer });
        }
        catch (InvalidDataException) { changedLocalConsumerRefused = true; }
        Check(changedLocalConsumerRefused,
            "owner verification snapshot refuses a later local consumer declaration");
        var injectedOwnerLocalRefused = false;
        try
        {
            WorkerWorkflows.RequireLocalGenerationConsumerSnapshot(
                WorkerExecutionSnapshot.Read(joined) with { LocalGenerationConsumer = declaredLocalConsumer },
                noLocalDefinition with { LocalGenerationConsumer = declaredLocalConsumer });
        }
        catch (InvalidDataException) { injectedOwnerLocalRefused = true; }
        Check(injectedOwnerLocalRefused,
            "owner verification edition refuses an injected matching local consumer");
        var combinedOwnerLocalRefused = false;
        try
        {
            WorkerExecutionSnapshot.BindOwnerVerification(
                WorkerExecutionSnapshot.Bounded(snapshot with { LocalGenerationConsumer = declaredLocalConsumer }),
                admission);
        }
        catch (InvalidDataException) { combinedOwnerLocalRefused = true; }
        Check(combinedOwnerLocalRefused,
            "owner recipe cannot combine with an explicit local generation consumer");
        WorkerWorkflows.RequireEquivalentOwnerVerificationRetained(CerebralJson.Element(admission), joined);
        Check(true, "an exact owner recipe retry joins the retained frozen snapshot");
        var otherAdmission = admission with { RecipeId = "different-reviewed-recipe" };
        var collisionRefused = false;
        try { WorkerWorkflows.RequireEquivalentOwnerVerificationRetained(CerebralJson.Element(otherAdmission), joined); }
        catch (WorkflowConflictException) { collisionRefused = true; }
        Check(collisionRefused, "a different owner recipe cannot reuse the retained request identity");
        var ordinaryCollisionRefused = false;
        try { WorkerWorkflows.RequireEquivalentOwnerVerificationRetained(null, joined); }
        catch (WorkflowConflictException) { ordinaryCollisionRefused = true; }
        Check(ordinaryCollisionRefused, "an ordinary retry cannot disguise a retained owner recipe");
        WorkerWorkflows.RequireEquivalentOwnerVerificationRetained(null, oldWire);
        Check(true, "ordinary retry keeps its prior admission path");
        using (var barrier = new Barrier(2))
        {
            var commitGate = new object();
            JsonElement? retained = null;
            var contenders = new[] { admission, otherAdmission };
            var race = contenders.Select(candidate => Task.Factory.StartNew(() =>
            {
                // Both owner-authenticated contenders finish snapshot capture before either
                // reaches the locked commit, as in the real asynchronous admission race.
                var candidateSnapshot = WorkerExecutionSnapshot.BindOwnerVerification(oldWire, candidate);
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Owner recipe race fixture did not release both contenders.");
                lock (commitGate)
                {
                    if (retained is null) { retained = candidateSnapshot; return true; }
                    try
                    {
                        WorkerWorkflows.RequireEquivalentOwnerVerificationRetained(
                            CerebralJson.Element(candidate), retained.Value);
                        return true;
                    }
                    catch (WorkflowConflictException) { return false; }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Task.WaitAll(race, TimeSpan.FromSeconds(10));
            Check(race.Count(task => task.Result) == 1 && retained is { } saved &&
                  contenders.Any(candidate => WorkerExecutionSnapshot.Read(saved).OwnerVerification?.RecipeSha256 ==
                      candidate.RecipeSha256),
                "two captured owner recipes racing for one RID admit one and reject the changed-recipe loser at commit");
        }
        var injected = JsonNode.Parse(oldWire.GetRawText())!.AsObject();
        injected["ownerVerification"] = JsonNode.Parse(JsonSerializer.Serialize(admission, CerebralJson.Options));
        var refusedOld = false;
        try { _ = WorkerExecutionSnapshot.Read(CerebralJson.Element(injected)); }
        catch (InvalidDataException) { refusedOld = true; }
        Check(refusedOld, "legacy edition six rejects an injected owner verification field");

        var stageInputRoot = Path.Combine(Path.GetTempPath(), "verification-stage-checks-" + Guid.NewGuid().ToString("N"));
        var sourceChecker = Path.Combine(stageInputRoot, "checker");
        var sourceTarget = Path.Combine(stageInputRoot, "target");
        Directory.CreateDirectory(sourceChecker);
        Directory.CreateDirectory(sourceTarget);
        try
        {
            var stagedFiles = new List<OwnerVerificationAdmission.File>();
            foreach (var file in files)
            {
                var originalPath = Path.Combine(file.Scope == OwnerVerificationAdmission.Scope.Checker
                    ? sourceChecker : sourceTarget, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (file.Role is "checkerAssembly" or "checkerDeps" or "checkerRuntimeConfig")
                    originalPath = Path.Combine(sourceChecker, "bin", "Release", "net10.0-windows",
                        file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
                var bytes = System.Text.Encoding.UTF8.GetBytes("fixed-" + file.Role);
                File.WriteAllBytes(originalPath, bytes);
                stagedFiles.Add(file with { OriginalPath = originalPath,
                    Sha256 = file.Scope == OwnerVerificationAdmission.Scope.Checker
                        ? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() : null,
                    Bytes = file.Scope == OwnerVerificationAdmission.Scope.Checker ? bytes.Length : null });
            }
            var stagedAdmission = admission with
            {
                CheckerRoot = sourceChecker, TargetRoot = sourceTarget,
                WorkingDirectory = sourceChecker,
                ModelArguments = [stagedFiles.Single(file => file.Role == "checkerAssembly").OriginalPath],
                Files = stagedFiles
            };
            var projectDirectory = Path.Combine(stageInputRoot, "Projects");
            Directory.CreateDirectory(projectDirectory);
            var outer = new WorkerWorkflowEffects.ToolArguments("process.run", CerebralJson.Element(new
            {
                executableId = stagedAdmission.ExecutableId,
                arguments = stagedAdmission.ModelArguments,
                workingDirectory = stagedAdmission.WorkingDirectory,
                timeoutSeconds = OwnerVerificationAdmission.CheckerTimeoutSeconds,
                expectedExitCode = 0
            }), projectDirectory);
            var execution = new ToolExecutionContext(new AgentProfile { AllowedRoots = [stageInputRoot] },
                "conversation", "run", projectDirectory)
            {
                EnforceProfileScope = true,
                PathAccess = new(ScopedPathAccessPolicy.Version,
                    [new ScopedPathGrant(projectDirectory, ScopedPathAccess.Write)])
            };
            Check(stagedAdmission.MatchesProcessRun(outer.Arguments),
                "normal outer workflow directory does not change the exact inner checker command");
            static bool AdmitsOuter(WorkerWorkflowEffects.ToolArguments call, ToolExecutionContext scope)
            {
                try { WorkerWorkflowEffects.RequireOwnerVerificationOuterWorkingDirectory(call, scope); return true; }
                catch (ArgumentException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
                catch (FileNotFoundException) { return false; }
            }
            Check(AdmitsOuter(outer, execution),
                "normal nonnull project working directory is admitted under the frozen write scope");
            Check(AdmitsOuter(outer with { WorkingDirectory = null }, execution),
                "legacy absent outer working directory remains admitted");
            Check(!AdmitsOuter(outer with { WorkingDirectory = sourceChecker }, execution),
                "a real directory outside the frozen conversation grant is refused");
            Check(!AdmitsOuter(outer, execution with
                { PathAccess = new(ScopedPathAccessPolicy.Version,
                    [new ScopedPathGrant(projectDirectory, ScopedPathAccess.Read)]) }),
                "a read-only conversation grant cannot authorize a process working directory");
            Check(!AdmitsOuter(outer with { WorkingDirectory = stagedFiles[0].OriginalPath }, execution),
                "a scoped file cannot stand in for a working directory");
            Check(!AdmitsOuter(outer with { WorkingDirectory = "Projects" }, execution),
                "a relative outer directory cannot be resolved against its own untrusted value");
            static bool RefusesAdmission(OwnerVerificationAdmission value)
            {
                try { value.Validate(); return false; }
                catch (InvalidDataException) { return true; }
            }
            var escapedPath = Path.Combine(stageInputRoot, "outside.dll");
            var escaped = stagedAdmission with
            {
                ModelArguments = [escapedPath],
                Files = stagedFiles.Select(file => file.Role == "checkerAssembly"
                    ? file with { OriginalPath = escapedPath } : file).ToArray()
            };
            Check(RefusesAdmission(escaped), "checker source cannot escape its admitted original root");
            Check(RefusesAdmission(stagedAdmission with { Files = stagedFiles.Select(file =>
                    file.Role == "checkerDeps" ? file with { RelativePath = "../BehaviorProbeV2.deps.json" } : file).ToArray() }),
                "private stage path traversal refuses before file staging");
            Check(RefusesAdmission(stagedAdmission with { Files = stagedFiles.Select(file =>
                    file.Role == "checkerDeps" ? file with { RelativePath = "BehaviorProbeV2.dll" } : file).ToArray() }),
                "two role names cannot collide at the same staged path");
            Check(RefusesAdmission(stagedAdmission with { Files = stagedFiles.Select(file =>
                    file.Role == "checkerDeps" ? file with { Scope = (OwnerVerificationAdmission.Scope)3 } : file).ToArray() }),
                "unknown file scope cannot bypass checker or target root admission");
            Check(RefusesAdmission(stagedAdmission with { CheckerContract = contract with { EntryRole = "targetAssembly" } }),
                "only an admitted checker-scope DLL may be the entry image");
            var legacyEditionRefused = false;
            try { _ = OwnerVerificationAdmission.Read(CerebralJson.Element(stagedAdmission with { SchemaVersion = 1 })); }
            catch (InvalidDataException error) { legacyEditionRefused = error.Message.Contains("Unsupported owner verification recipe contract", StringComparison.Ordinal); }
            Check(legacyEditionRefused, "unreleased edition-one owner recipes fail closed with migration guidance");
            using (var stage = OwnerVerificationStage.Prepare(stagedAdmission))
            {
                stage.RequireUnchanged();
                Check(stage.Pins.Count == stagedFiles.Count && File.Exists(stage.DescriptorPath),
                    "host stages only the admitted closure and writes its private descriptor");
                Check(stage.Pins.Single(pin => pin.Role == "checkerAssembly").StagedPath ==
                      Path.Combine(stage.CheckerRoot, "BehaviorProbeV2.dll") &&
                      stagedFiles.Single(file => file.Role == "checkerAssembly").OriginalPath.Contains(
                          Path.Combine("bin", "Release", "net10.0-windows"), StringComparison.Ordinal),
                    "nested pinned checker output maps to the unchanged flat private checker stage");
                using var descriptor = JsonDocument.Parse(File.ReadAllBytes(stage.DescriptorPath));
                var descriptorRoot = descriptor.RootElement;
                Check(descriptorRoot.EnumerateObject().Select(property => property.Name)
                          .OrderBy(name => name, StringComparer.Ordinal)
                          .SequenceEqual(new[] { "checkerRoot", "checkerVersion", "files", "resultsRoot", "schema", "targetRoot" }) &&
                      descriptorRoot.GetProperty("schema").GetString() == stagedAdmission.CheckerContract.DescriptorSchema &&
                      descriptorRoot.GetProperty("checkerVersion").GetString() == stagedAdmission.CheckerContract.CheckerVersion &&
                      descriptorRoot.GetProperty("checkerRoot").GetString() == stage.CheckerRoot &&
                      descriptorRoot.GetProperty("targetRoot").GetString() == stage.TargetRoot &&
                      descriptorRoot.GetProperty("resultsRoot").GetString() == stage.ResultsRoot,
                    "actual staged descriptor has the frozen checker's closed root keys and exact version/roots");
                var descriptorFiles = descriptorRoot.GetProperty("files").EnumerateArray().ToArray();
                var exactStagedPins = descriptorFiles.Length == stage.Pins.Count;
                for (var index = 0; exactStagedPins && index < descriptorFiles.Length; index++)
                {
                    var file = descriptorFiles[index];
                    var pin = stage.Pins[index];
                    exactStagedPins = file.EnumerateObject().Select(property => property.Name)
                            .OrderBy(name => name, StringComparer.Ordinal)
                            .SequenceEqual(new[] { "bytes", "path", "role", "sha256" }) &&
                        file.GetProperty("role").GetString() == pin.Role &&
                        file.GetProperty("path").GetString() == pin.RelativePath &&
                        file.GetProperty("sha256").GetString() == pin.Sha256 &&
                        file.GetProperty("bytes").GetInt64() == pin.Bytes;
                }
                Check(exactStagedPins && descriptorFiles.Select(file => file.GetProperty("role").GetString())
                          .Distinct(StringComparer.Ordinal).Count() == 17,
                    "actual staged descriptor carries all 17 exact role/path/hash/length facts without duplicates");
                Check(descriptor.RootElement.GetProperty("files").GetArrayLength() == 17 &&
                      descriptor.RootElement.GetProperty("files")[0].EnumerateObject()
                          .Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)
                          .SequenceEqual(new[] { "bytes", "path", "role", "sha256" }),
                    "host descriptor uses the exact checker contract fields and 17 roles");
                var expectedIdentity = OwnerVerificationBudget.InputIdentity(stagedAdmission.RecipeSha256,
                    stage.Pins.Where(pin => pin.Scope == "target")
                        .Select(pin => new OwnerVerificationBudget.InputPin(pin.Role, pin.OriginalPath,
                            pin.Sha256, pin.Bytes)).ToArray());
                Check(stage.InputSha256 == expectedIdentity,
                    "host input identity comes from actual retained target bytes");
                var checkerImage = stage.Pins.Single(pin => pin.Role == "checkerAssembly");
                var reported = new
                {
                    schema = "pegasus.cerebral.behavior-probe-report/2", checkerVersion = "2.0.0",
                    tier = "Actual pinned compiled bytes, unshown STA WPF controls, in-process routed click events",
                    descriptorSha256 = stage.DescriptorSha256,
                    checkerClosureSha256 = stage.ClosureDigest("checker"),
                    targetClosureSha256 = stage.ClosureDigest("target"),
                    stagedPins = stage.Pins.Select(pin => new { Role = pin.Role, Path = pin.RelativePath,
                        Sha256 = pin.Sha256, Bytes = pin.Bytes }).ToArray(),
                    checkerImage = new { Role = checkerImage.Role, Sha256 = checkerImage.Sha256,
                        Bytes = checkerImage.Bytes, AssemblyIdentity = "fixture", ModuleVersionId = Guid.NewGuid().ToString("N") },
                    actualLoadedBytes = stage.Pins.Where(pin => pin.Role is "targetAssembly" or "targetSdk")
                        .Select(pin => new { Role = pin.Role, Sha256 = pin.Sha256, Bytes = pin.Bytes,
                            AssemblyIdentity = "fixture", ModuleVersionId = Guid.NewGuid().ToString("N") }).ToArray(),
                    stagedClosureUnchanged = true,
                    checks = names.Select(name => new { Name = name, Passed = true,
                        expected = "expected", actual = "actual", exception = (string?)null,
                        actualTruncated = false, exceptionTruncated = (bool?)null }).ToArray(),
                    totalCount = 10, passed = true, passedCount = 10, failedCount = 0,
                    failureDiagnostic = "", installed = false, mainRendered = false, windowShown = false,
                    observedUtc = DateTimeOffset.UtcNow,
                    scope = "Staged closure only. Original-input freshness, executable trust, grants and execution attestation belong to the host."
                };
                var reportBytes = JsonSerializer.SerializeToUtf8Bytes(reported);
                var reportPath = Path.Combine(stage.ResultsRoot, Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllBytes(reportPath, reportBytes);
                var reportHash = Convert.ToHexString(SHA256.HashData(reportBytes)).ToLowerInvariant();
                var summary = JsonSerializer.Serialize(new
                {
                    schema = "pegasus.cerebral.behavior-probe-summary/2", checkerVersion = "2.0.0",
                    status = "passed", complete = true, totalCount = 10, passed = true,
                    passedCount = 10, failedCount = 0, failureDiagnostic = "",
                    descriptorSha256 = stage.DescriptorSha256,
                    checkerClosureSha256 = stage.ClosureDigest("checker"),
                    targetClosureSha256 = stage.ClosureDigest("target"),
                    stagedClosureUnchanged = true,
                    report = new { path = reportPath, sha256 = reportHash, bytes = reportBytes.Length }
                });
                var attested = WorkerWorkflowEffects.ValidateCheckerReport(stage, stagedAdmission, 0, summary);
                Check(attested.PassedCount == 10 && attested.Sha256 == reportHash,
                    "host joins complete summary, report hash, closure and actual-loaded pin facts");
                static bool RefusesReport(OwnerVerificationStage stage,
                    OwnerVerificationAdmission value, string stdout)
                {
                    try { _ = WorkerWorkflowEffects.ValidateCheckerReport(stage, value, 0, stdout); return false; }
                    catch (InvalidDataException) { return true; }
                }
                Check(RefusesReport(stage, stagedAdmission with { CheckerContract = contract with
                    { AssertionNames = ["changed assertion", .. names.Skip(1)] } }, summary),
                    "checker-reported assertion names must match the ordered owner recipe");
                Check(RefusesReport(stage, stagedAdmission with { CheckerContract = contract with
                    { AssertionNames = names.Skip(1).ToArray() } }, summary),
                    "checker-reported count must match the owner recipe");
                Check(RefusesReport(stage, stagedAdmission with { CheckerContract = contract with
                    { LoadedRoles = ["targetAssembly"] } }, summary),
                    "checker-reported loaded assemblies must match the complete owner role set");
                Check(RefusesReport(stage, stagedAdmission with { CheckerContract = contract with
                    { ReportSchema = "another.report/1" } }, summary),
                    "checker-reported edition must match the owner recipe");
                var outputSchema = new WorkerWorkflowEffects(null!, null!, null!, null!).Commands
                    .Single(command => command.Descriptor.Id == WorkerWorkflowEffects.ToolCommand).Descriptor.OutputSchema;
                var receipt = new WorkerWorkflowEffects.OwnerVerificationReceipt(1, stagedAdmission.RecipeSha256,
                    stage.InputSha256, "effect-fixture", true, true, 0,
                    stage.Pins.Where(pin => pin.Scope == "target")
                        .Select(pin => new WorkerWorkflowEffects.VerificationInputPin(pin.Role,
                            pin.OriginalPath, pin.Sha256, pin.Bytes)).ToArray(), reportHash);
                var passedData = CerebralJson.Element(new WorkerWorkflowEffects.ToolData("process.run",
                    "Passed 10/10", false, new(ToolExecutionOutcome.Succeeded, "cerebral.owner-verification"),
                    Verification: receipt, VerificationReport: attested.Report));
                WorkflowSchema.Validate(passedData, outputSchema);
                Check(CerebralJson.Read<WorkerWorkflowEffects.ToolData>(passedData).Verification?.ReportSha256 == reportHash,
                    "actual ToolHandler output schema and persistence bound accept a complete host-attested pass");
                var failedData = CerebralJson.Element(new WorkerWorkflowEffects.ToolData("process.run",
                    "Failed 0/10", false, new(ToolExecutionOutcome.Failed, "cerebral.owner-verification"),
                    Verification: receipt with { ExitCode = 1 }));
                WorkflowSchema.Validate(failedData, outputSchema);
                Check(CerebralJson.Read<WorkerWorkflowEffects.ToolData>(failedData).Verification?.ExitCode == 1,
                    "actual ToolHandler schema retains a completed failed checker receipt");
                var outputDescriptor = new WorkerWorkflowEffects(null!, null!, null!, null!).Commands
                    .Single(command => command.Descriptor.Id == WorkerWorkflowEffects.ToolCommand).Descriptor;
                var uncertainCode = WorkerWorkflowEffects.CheckerUncertaintyCode(
                    WorkerWorkflowEffects.CheckerUncertaintyPhase.ReportValidation,
                    new InvalidDataException("private checker path must not escape"), false, 2);
                Check(uncertainCode == "checker-unsupported-exit" &&
                      !uncertainCode.Contains("private checker path", StringComparison.Ordinal),
                    "unsupported child exit retains a safe reason without exception text");
                Check(WorkerWorkflowEffects.CheckerUncertaintyCode(
                        WorkerWorkflowEffects.CheckerUncertaintyPhase.HostInvocation,
                        new OperationCanceledException("private timeout detail"), true, null) == "host-timeout" &&
                      WorkerWorkflowEffects.CheckerUncertaintyCode(
                        WorkerWorkflowEffects.CheckerUncertaintyPhase.PostRunClosure,
                        new InvalidDataException("private closure path"), false, 0) == "postrun-closure-invalid-data" &&
                      WorkerWorkflowEffects.CheckerUncertaintyCode(
                        WorkerWorkflowEffects.CheckerUncertaintyPhase.ReportValidation,
                        new JsonException("private summary"), false, 0) == "report-validation-json",
                    "uncertainty reason separates timeout, staged closure and summary validation");
                var unknownData = CerebralJson.Element(new WorkerWorkflowEffects.ToolData("process.run",
                    "Incomplete host attestation (" + uncertainCode + ")", false,
                    new(ToolExecutionOutcome.Unknown, "cerebral.owner-verification", State: "uncertain"),
                    Verification: receipt with { Complete = false, ExitCode = 2, ReportSha256 = null }));
                WorkflowSchema.Validate(unknownData, outputSchema);
                var retainedUnknown = RunOutputThroughRuntime(outputDescriptor,
                    new(WorkflowEffectState.Unknown, unknownData), "uncertain-checker");
                Check(retainedUnknown.State == WorkflowEffectState.Unknown &&
                      retainedUnknown.Data is { } unknownWire &&
                      CerebralJson.Read<WorkerWorkflowEffects.ToolData>(unknownWire).Verification is { Complete: false, ExitCode: 2 },
                    "unknown effect retains confirmed child exit without admitting a checker result");
                var refusedData = CerebralJson.Element(new WorkerWorkflowEffects.ToolData("process.run",
                    "No checker started", false, new(ToolExecutionOutcome.Rejected, "cerebral.owner-verification"),
                    Verification: receipt with { ProcessDispatched = false, ExitCode = null,
                        InputSha256 = new string('0', 64), Inputs = [] , ReportSha256 = null }));
                WorkflowSchema.Validate(refusedData, outputSchema);
                Check(CerebralJson.Read<WorkerWorkflowEffects.ToolData>(refusedData).Verification?.ProcessDispatched == false,
                    "actual ToolHandler schema retains a no-process preflight refusal");
                var ordinaryData = CerebralJson.Element(new WorkerWorkflowEffects.ToolData("file.read",
                    "ordinary", false, new(ToolExecutionOutcome.Succeeded, "fixture")));
                WorkflowSchema.Validate(ordinaryData, outputSchema);
                Check(!ordinaryData.TryGetProperty("verification", out _) &&
                      !ordinaryData.TryGetProperty("verificationReport", out _),
                    "ordinary tool wire omits the two owner-only fields and remains admitted");
                foreach (var (label, state, data) in new[]
                {
                    ("passed", WorkflowEffectState.Succeeded, passedData),
                    ("failed", WorkflowEffectState.Failed, failedData),
                    ("preflight", WorkflowEffectState.Failed, refusedData),
                    ("ordinary", WorkflowEffectState.Succeeded, ordinaryData)
                })
                {
                    var saved = RunOutputThroughRuntime(outputDescriptor, new(state, data), label);
                    Check(saved.State == state && saved.Data is { } retained &&
                          JsonElement.DeepEquals(retained, data),
                        "runtime validates and retains the real ToolHandler schema for " + label +
                        " (state=" + saved.State + ", result=" + (saved.Data is null ? "absent" : "present") + ")");
                }
                var falsePassRefused = false;
                try { _ = WorkerWorkflowEffects.ValidateCheckerReport(stage, stagedAdmission, 1, summary); }
                catch (InvalidDataException) { falsePassRefused = true; }
                Check(falsePassRefused, "a failed child exit cannot adopt a claimed passing report");
                var staleReportRefused = false;
                try { _ = WorkerWorkflowEffects.ValidateCheckerReport(stage, stagedAdmission, 0,
                    summary.Replace(reportHash, new string('f', 64), StringComparison.Ordinal)); }
                catch (InvalidDataException) { staleReportRefused = true; }
                Check(staleReportRefused, "changed report digest refuses host attestation");
                var expandedReport = JsonNode.Parse(attested.Report.GetRawText())!.AsObject();
                expandedReport["unreviewed"] = true;
                var expandedBytes = JsonSerializer.SerializeToUtf8Bytes(expandedReport);
                var expandedSha = Convert.ToHexString(SHA256.HashData(expandedBytes)).ToLowerInvariant();
                var expandedSummary = JsonNode.Parse(summary)!.AsObject();
                expandedSummary["report"]!["sha256"] = expandedSha;
                expandedSummary["report"]!["bytes"] = expandedBytes.Length;
                File.WriteAllBytes(reportPath, expandedBytes);
                var unknownReportRefused = false;
                try { _ = WorkerWorkflowEffects.ValidateCheckerReport(stage, stagedAdmission, 0,
                    expandedSummary.ToJsonString()); }
                catch (InvalidDataException) { unknownReportRefused = true; }
                finally { File.WriteAllBytes(reportPath, reportBytes); }
                Check(unknownReportRefused, "host rejects unknown checker report fields before creating a success receipt");
                var writeRefused = false;
                try { File.WriteAllText(stagedFiles[0].OriginalPath, "tamper"); }
                catch (IOException) { writeRefused = true; }
                if (OperatingSystem.IsWindows())
                    Check(writeRefused, "retained original read handle blocks ordinary writes on Windows");
                if (OperatingSystem.IsWindows())
                {
                    using (var heldReport = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        stage.Dispose();
                        Check(stage.CleanupPending && File.Exists(reportPath) &&
                              CerebralJson.Read<WorkerWorkflowEffects.ToolData>(passedData).Verification?.ReportSha256 == reportHash,
                            "failed staging deletion reports pending cleanup without erasing a completed receipt");
                    }
                    var privateStage = Path.GetDirectoryName(stage.ResultsRoot)!;
                    Check(Path.GetFileName(privateStage).Length == 32 &&
                          Path.GetFileName(Path.GetDirectoryName(privateStage)) == "CerebralOwnerVerification",
                        "fixture cleanup is confined to the verified private child");
                    Directory.Delete(privateStage, recursive: true);
                }
            }
            File.WriteAllText(stagedFiles[0].OriginalPath, "tamper");
            var staleRefused = false;
            try { using var _ = OwnerVerificationStage.Prepare(stagedAdmission); }
            catch (InvalidDataException) { staleRefused = true; }
            Check(staleRefused, "changed pinned checker byte refuses before any child execution");
            var secondCheckerRoot = Path.Combine(stageInputRoot, "second-checker");
            var secondTargetRoot = Path.Combine(stageInputRoot, "second-target");
            var secondEntry = Path.Combine(secondCheckerRoot, "build", "SmallProbe.dll");
            var secondTarget = Path.Combine(secondTargetRoot, "output", "AnotherPlugin.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(secondEntry)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondTarget)!);
            var secondEntryBytes = System.Text.Encoding.UTF8.GetBytes("second-checker-byte-fixture");
            File.WriteAllBytes(secondEntry, secondEntryBytes);
            File.WriteAllText(secondTarget, "second-target-byte-fixture");
            var secondContract = new OwnerVerificationAdmission.Contract("another.descriptor/1", "another.closure/1", "3.4.0",
                "another.summary/1", "another.report/1", "probeMain", ["pluginImage"], ["one selected assertion"]);
            var secondAdmission = admission with
            {
                RecipeId = "different-programming-project", CheckerRoot = secondCheckerRoot,
                TargetRoot = secondTargetRoot, WorkingDirectory = secondCheckerRoot,
                ModelArguments = [secondEntry], CheckerContract = secondContract,
                Files =
                [
                    new("probeMain", OwnerVerificationAdmission.Scope.Checker, "SmallProbe.dll", secondEntry,
                        Convert.ToHexString(SHA256.HashData(secondEntryBytes)).ToLowerInvariant(), secondEntryBytes.Length),
                    new("pluginImage", OwnerVerificationAdmission.Scope.Target, "AnotherPlugin.dll", secondTarget)
                ]
            };
            secondAdmission.Validate();
            using (var secondStage = OwnerVerificationStage.Prepare(secondAdmission))
            {
                secondStage.RequireUnchanged();
                using var secondDescriptor = JsonDocument.Parse(File.ReadAllBytes(secondStage.DescriptorPath));
                Check(secondStage.Pins.Count == 2 &&
                      secondDescriptor.RootElement.GetProperty("schema").GetString() == secondContract.DescriptorSchema &&
                      secondDescriptor.RootElement.GetProperty("checkerVersion").GetString() == secondContract.CheckerVersion &&
                      secondStage.Pins.Single(pin => pin.Role == "probeMain").StagedPath ==
                          Path.Combine(secondStage.CheckerRoot, "SmallProbe.dll"),
                    "a distinct owner recipe stages a different role count, checker edition and nested-original mapping");
                var secondImage = secondStage.Pins.Single(pin => pin.Role == "probeMain");
                var secondLoaded = secondStage.Pins.Single(pin => pin.Role == "pluginImage");
                var secondReport = new
                {
                    schema = secondContract.ReportSchema, checkerVersion = secondContract.CheckerVersion,
                    tier = "deterministic fixture", descriptorSha256 = secondStage.DescriptorSha256,
                    checkerClosureSha256 = secondStage.ClosureDigest("checker"),
                    targetClosureSha256 = secondStage.ClosureDigest("target"),
                    stagedPins = secondStage.Pins.Select(pin => new { Role = pin.Role, Path = pin.RelativePath,
                        Sha256 = pin.Sha256, Bytes = pin.Bytes }).ToArray(),
                    checkerImage = new { Role = secondImage.Role, Sha256 = secondImage.Sha256,
                        Bytes = secondImage.Bytes, AssemblyIdentity = "fixture", ModuleVersionId = "fixture" },
                    actualLoadedBytes = new[] { new { Role = secondLoaded.Role, Sha256 = secondLoaded.Sha256,
                        Bytes = secondLoaded.Bytes, AssemblyIdentity = "fixture", ModuleVersionId = "fixture" } },
                    stagedClosureUnchanged = true,
                    checks = new[] { new { Name = "one selected assertion", Passed = true,
                        expected = "expected", actual = "actual", exception = (string?)null,
                        actualTruncated = false, exceptionTruncated = (bool?)null } },
                    totalCount = 1, passed = true, passedCount = 1, failedCount = 0,
                    failureDiagnostic = "", installed = false, mainRendered = false, windowShown = false,
                    observedUtc = DateTimeOffset.UtcNow, scope = "fixture"
                };
                var secondReportBytes = JsonSerializer.SerializeToUtf8Bytes(secondReport);
                var secondReportPath = Path.Combine(secondStage.ResultsRoot, Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllBytes(secondReportPath, secondReportBytes);
                var secondReportSha = Convert.ToHexString(SHA256.HashData(secondReportBytes)).ToLowerInvariant();
                var secondSummary = JsonSerializer.Serialize(new
                {
                    schema = secondContract.SummarySchema, checkerVersion = secondContract.CheckerVersion,
                    status = "passed", complete = true, totalCount = 1, passed = true,
                    passedCount = 1, failedCount = 0, failureDiagnostic = "",
                    descriptorSha256 = secondStage.DescriptorSha256,
                    checkerClosureSha256 = secondStage.ClosureDigest("checker"),
                    targetClosureSha256 = secondStage.ClosureDigest("target"),
                    stagedClosureUnchanged = true,
                    report = new { path = secondReportPath, sha256 = secondReportSha, bytes = secondReportBytes.Length }
                });
                var secondAttested = WorkerWorkflowEffects.ValidateCheckerReport(secondStage,
                    secondAdmission, 0, secondSummary);
                Check(secondAttested.PassedCount == 1 && secondAttested.Sha256 == secondReportSha,
                    "different one-check report joins the same generic host receipt path");
                var secondOutput = CerebralJson.Element(new WorkerWorkflowEffects.ToolData("process.run",
                    "Passed 1/1", false, new(ToolExecutionOutcome.Succeeded, "cerebral.owner-verification"),
                    Verification: new(1, secondAdmission.RecipeSha256, secondStage.InputSha256, "second-effect",
                        true, true, 0,
                        [new(secondLoaded.Role, secondLoaded.OriginalPath, secondLoaded.Sha256, secondLoaded.Bytes)],
                        secondAttested.Sha256), VerificationReport: secondAttested.Report));
                var schema = new WorkerWorkflowEffects(null!, null!, null!, null!).Commands
                    .Single(command => command.Descriptor.Id == WorkerWorkflowEffects.ToolCommand).Descriptor.OutputSchema;
                WorkflowSchema.Validate(secondOutput, schema);
                Check(true, "closed ToolCommand schema accepts the distinct owner-attested report without a Worker fixture branch");
            }
        }
        finally { Directory.Delete(stageInputRoot, recursive: true); }
        return checks;
    }

    private static WorkflowCommandResult RunOutputThroughRuntime(WorkflowCommandDescriptor descriptor,
        WorkflowCommandResult result, string label)
    {
        var root = Path.Combine(Path.GetTempPath(), "verification-output-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "engine.db");
            var store = new WorkflowStore(new EventJournal(database));
            var module = new OutputModule(descriptor, result);
            var runtime = new WorkflowRuntime(store, new WorkflowRegistry([module]), (_, _) => true);
            var submission = new WorkflowSubmission("output-" + label, "output-chat-" + label,
                "verification-fixture", OutputDefinition.Id, OutputDefinition.Version,
                "Inspect retained tool evidence.", null, WorkflowContract.EmptyObject,
                WorkflowContract.EmptyObject, StepLimit: 4);
            var accepted = store.Submit(submission);
            _ = runtime.RunAsync(accepted.Id).GetAwaiter().GetResult();
            return new WorkflowStore(new EventJournal(database)).GetEffectResult(accepted.Id,
                module.Handler.EffectId ?? throw new InvalidOperationException("Runtime never dispatched the fixture tool."));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class OutputModule(WorkflowCommandDescriptor descriptor, WorkflowCommandResult result)
        : IWorkflowModule
    {
        public string Id => WorkerWorkflowEffects.ModuleId;
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new OutputDefinition()];
        public OutputHandler Handler { get; } = new(descriptor, result);
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [Handler];
    }

    private sealed class OutputDefinition : IWorkflowDefinition
    {
        internal const string Id = "cerebral.host:owner-output-fixture";
        internal const string Version = "1.0.0";
        public WorkflowDefinitionDescriptor Descriptor { get; } =
            new(Id, Version, "Retain one bounded tool result.", WorkflowContract.EmptyObject, []);
        public WorkflowStep Advance(WorkflowRun snapshot) => snapshot.Steps == 0
            ? new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(WorkerWorkflowEffects.ToolCommand, 1,
                CerebralJson.Element(new { name = "process.run", arguments = new { } })))
            : new(WorkflowContract.EmptyObject, new CompleteWorkflow(CerebralJson.Element(new { done = true })));
    }

    private sealed class OutputHandler(WorkflowCommandDescriptor descriptor, WorkflowCommandResult result)
        : IWorkflowCommandHandler
    {
        public string? EffectId { get; private set; }
        public WorkflowCommandDescriptor Descriptor => descriptor;
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token)
        {
            EffectId = context.EffectId;
            return Task.FromResult(result);
        }
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token) =>
            Task.FromResult(result);
    }

    private sealed class NearMatchCountingTools : IToolDispatcher
    {
        public int Executions, Reconciliations;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([new("process.run", "run",
                CerebralJson.Element(new { type = "object" }), "process.execute")]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            Executions++;
            throw new InvalidOperationException("A reserved checker request reached ordinary execution.");
        }
        public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context,
            ToolExecutionEvidence? previous, CancellationToken token)
        {
            Reconciliations++;
            throw new InvalidOperationException("The near-match test did not request reconciliation.");
        }
    }
}
