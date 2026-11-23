using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Cerebral.Tools;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    private async Task<WorkflowCommandResult> OwnerVerificationToolAsync(
        WorkflowCommandContext context, ToolArguments args, ToolExecutionContext execution,
        OwnerVerificationAdmission admission, bool reconcile, CancellationToken token)
    {
        // Reconciliation never starts another checker, even when the first child outcome is unknown.
        if (reconcile)
            return new(WorkflowEffectState.Unknown,
                context.PreviousEffect?.Result?.Clone(),
                "The admitted checker outcome is uncertain. Inspect the original effect; no checker was replayed.");
        ToolDispatcher dispatcher;
        OwnerVerificationStage stage;
        try
        {
            if (tools is not ToolDispatcher selected || context.RunId != context.Submission.RequestId ||
                execution.FullAccess ||
                admission.RequestId != context.Submission.RequestId ||
                admission.ConversationId != context.Submission.ConversationId)
                throw new UnauthorizedAccessException("The exact owner checker host or run identity is unavailable.");
            dispatcher = selected;
            try { RequireOwnerVerificationOuterWorkingDirectory(args, execution); }
            catch (Exception error) when (IsOrdinary(error) && !token.IsCancellationRequested)
            {
                return NoProcessCheckerRefusal(context, admission,
                    "The workflow working directory is not an existing writable directory in this conversation's current path scope. Select a granted project directory; no checker was started.");
            }
            var registration = configuration.Get().Executables.SingleOrDefault(item => item.Id == admission.ExecutableId);
            if (registration is null || registration.Path != admission.ExecutablePath ||
                registration.PrefixArguments.Count != 0)
                throw new UnauthorizedAccessException("The owner checker registration changed.");
            PortableRegistrationPolicy.RequireReady(registration);
            _ = ScopedFilePolicy.PathInScope(admission.WorkingDirectory, execution, ScopedPathAccess.Write, true);
            foreach (var file in admission.Files)
                _ = ScopedFilePolicy.PathInScope(file.OriginalPath, execution, ScopedPathAccess.Read, true);
            stage = OwnerVerificationStage.Prepare(admission);
        }
        catch (Exception error) when (IsOrdinary(error) && !token.IsCancellationRequested)
        {
            return NoProcessCheckerRefusal(context, admission,
                "Checker input, grant or registered host changed before dispatch. Inspect the selected files and owner recipe; no checker was started.");
        }
        using var retainedStage = stage;
        IReadOnlyList<OwnerVerificationBudget.Attempt> priorAttempts;
        try { priorAttempts = PriorVerificationAttempts(context, admission, token); }
        catch (Exception error) when ((error is InvalidDataException or KeyNotFoundException or JsonException) &&
            !token.IsCancellationRequested)
        {
            return CheckerHistoryRefusal(context, admission);
        }
        // An unconfirmed child outcome blocks the whole admitted checker lane, including an
        // A-to-B input change, until the exact original effect is reconciled.
        var unresolved = priorAttempts.FirstOrDefault(attempt => !attempt.Complete ||
            attempt.State is WorkflowEffectState.Unknown or WorkflowEffectState.Dispatched or WorkflowEffectState.Prepared);
        var decision = unresolved is not null
            ? new OwnerVerificationBudget.Decision(OwnerVerificationBudget.DecisionKind.ReconcilePrior,
                unresolved.EffectId)
            : OwnerVerificationBudget.Evaluate(admission.RecipeSha256, stage.InputSha256,
                priorAttempts, admission.MaximumFailuresPerInput);
        if (decision.Kind != OwnerVerificationBudget.DecisionKind.Admit)
        {
            var explanation = decision.Kind switch
            {
                OwnerVerificationBudget.DecisionKind.ReuseSuccess =>
                    "This exact input already passed the admitted checker. Continue to verify and present using the saved check receipt; change the tested input and rebuild before another check.",
                OwnerVerificationBudget.DecisionKind.CorrectInputs =>
                    "Two completed checks failed for these exact inputs. Read the saved failure, correct a tested input and rebuild before checking again.",
                _ => "A prior checker effect has no complete host receipt. Inspect that exact saved effect and its diagnostic; no new check is admitted."
            };
            var prior = new OwnerVerificationReceipt(1, admission.RecipeSha256, stage.InputSha256,
                context.EffectId, false, true, null, TargetInputs(stage), null);
            var data = Element(new ToolData("process.run", explanation, false,
                new(ToolExecutionOutcome.Rejected, "cerebral.owner-verification", State: "not-dispatched"),
                Verification: prior));
            return new(decision.Kind == OwnerVerificationBudget.DecisionKind.ReconcilePrior
                ? WorkflowEffectState.Unknown : WorkflowEffectState.Failed, data,
                explanation + (decision.EffectId is null ? "" : " Saved effect: " + decision.EffectId + "."));
        }

        try { stage.RequireUnchanged(); }
        catch (Exception error) when (IsOrdinary(error) && !token.IsCancellationRequested)
        {
            return NoProcessCheckerRefusal(context, admission,
                "The staged or original checker closure changed before dispatch. Refresh the exact owner recipe; no checker was started.");
        }
        var checkerImage = stage.Pins.Single(pin => pin.Role == admission.CheckerContract.EntryRole).StagedPath;
        var phase = CheckerUncertaintyPhase.HostInvocation;
        int? confirmedChildExitCode = null;
        var hostTimedOut = false;
        try
        {
            Observe(context, "tool.dispatched", outcome: "owner-verification");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(OwnerVerificationAdmission.CheckerTimeoutSeconds));
            (int ExitCode, string Output, string Error, bool Truncated) run;
            try
            {
                run = await dispatcher.ExecuteOwnerVerificationHostAsync(admission.ExecutableId,
                    admission.ExecutablePath, checkerImage, stage.CheckerRoot, stage.TargetRoot,
                    stage.ResultsRoot, stage.DescriptorPath, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
            {
                hostTimedOut = true;
                throw;
            }
            // A returned host result confirms the child exit, even if its summary cannot be admitted.
            confirmedChildExitCode = run.ExitCode;
            phase = CheckerUncertaintyPhase.PostRunClosure;
            stage.RequireUnchanged();
            phase = CheckerUncertaintyPhase.OutputBound;
            if (run.Truncated)
                throw new InvalidDataException("The checker output was truncated.");
            phase = CheckerUncertaintyPhase.ReportValidation;
            var report = ValidateCheckerReport(stage, admission, run.ExitCode, run.Output);
            phase = CheckerUncertaintyPhase.ReceiptBound;
            // Cleanup is after the child and report join. A failed private-directory deletion
            // cannot erase a confirmed checker result or turn its saved receipt into Unknown.
            retainedStage.Dispose();
            var success = run.ExitCode == 0;
            var receipt = new OwnerVerificationReceipt(1, admission.RecipeSha256, stage.InputSha256,
                context.EffectId, true, true, run.ExitCode, TargetInputs(stage), report.Sha256);
            var content = success
                ? "Admitted checker passed " + admission.CheckerContract.AssertionNames.Count + "/" +
                    admission.CheckerContract.AssertionNames.Count + " on the current pinned inputs. The host retained the exact input and report hashes. Continue to verify and present."
                : "Admitted checker failed " + report.PassedCount + "/" +
                    admission.CheckerContract.AssertionNames.Count + " on the current pinned inputs. " + report.Diagnostic;
            if (retainedStage.CleanupPending)
                content += " Temporary checker staging cleanup is pending.";
            var evidence = new ToolExecutionEvidence(success ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed,
                "cerebral.owner-verification", State: "completed", ExitCode: run.ExitCode);
            Observe(context, "tool.receipt", outcome: success ? "verified" : "failed-check");
            var data = Element(new ToolData("process.run", content, false, evidence,
                Verification: receipt, VerificationReport: report.Report));
            if (Size(data) > WorkflowContract.MaximumStateBytes)
                throw new InvalidDataException("The complete checker receipt exceeds the durable result bound.");
            return new(success ? WorkflowEffectState.Succeeded : WorkflowEffectState.Failed,
                data,
                success ? null : content);
        }
        catch (Exception error) when (IsOrdinary(error))
        {
            // Exception messages can contain private paths or checker output. Retain only a fixed
            // phase/type code and a child exit that the host actually observed.
            var code = CheckerUncertaintyCode(phase, error, hostTimedOut, confirmedChildExitCode);
            var message = "The checker may have started, but its complete host attestation is unavailable (" +
                code + "). Inspect the original saved effect and diagnostic code; no automatic retry is allowed.";
            var uncertain = new OwnerVerificationReceipt(1, admission.RecipeSha256, stage.InputSha256,
                context.EffectId, true, false, confirmedChildExitCode, TargetInputs(stage));
            return new(WorkflowEffectState.Unknown,
                Element(new ToolData("process.run", message, false,
                    new(ToolExecutionOutcome.Unknown, "cerebral.owner-verification", State: "uncertain"),
                    Verification: uncertain)), message);
        }
    }

    internal enum CheckerUncertaintyPhase
    {
        HostInvocation,
        PostRunClosure,
        OutputBound,
        ReportValidation,
        ReceiptBound
    }

    internal static string CheckerUncertaintyCode(CheckerUncertaintyPhase phase, Exception error,
        bool hostTimedOut, int? confirmedChildExitCode)
    {
        if (phase == CheckerUncertaintyPhase.HostInvocation && hostTimedOut)
            return "host-timeout";
        if (phase == CheckerUncertaintyPhase.ReportValidation && confirmedChildExitCode is not (0 or 1))
            return "checker-unsupported-exit";
        var stage = phase switch
        {
            CheckerUncertaintyPhase.HostInvocation => "host-invocation",
            CheckerUncertaintyPhase.PostRunClosure => "postrun-closure",
            CheckerUncertaintyPhase.OutputBound => "output-bound",
            CheckerUncertaintyPhase.ReportValidation => "report-validation",
            CheckerUncertaintyPhase.ReceiptBound => "receipt-bound",
            _ => "host-unknown"
        };
        var kind = error switch
        {
            OperationCanceledException => "cancelled",
            UnauthorizedAccessException => "access",
            IOException => "io",
            InvalidDataException => "invalid-data",
            JsonException => "json",
            InvalidOperationException => "invalid-operation",
            _ => "other"
        };
        return stage + "-" + kind;
    }

    // The outer workflow context is an ordinary scoped path, not the checker launch location.
    // Only the admitted private stage chooses the child working directory below.
    internal static void RequireOwnerVerificationOuterWorkingDirectory(ToolArguments args,
        ToolExecutionContext execution)
    {
        if (args.WorkingDirectory is null) return;
        if (!Path.IsPathFullyQualified(args.WorkingDirectory))
            throw new UnauthorizedAccessException("Owner verification requires an absolute workflow working directory.");
        var directory = ScopedFilePolicy.PathInScope(args.WorkingDirectory, execution,
            ScopedPathAccess.Write, mustExist: true);
        if (!Directory.Exists(directory))
            throw new UnauthorizedAccessException("Owner verification requires a directory in the workflow path scope.");
    }

    private static WorkflowCommandResult NoProcessCheckerRefusal(WorkflowCommandContext context,
        OwnerVerificationAdmission admission, string message)
    {
        var refusal = new OwnerVerificationReceipt(1, admission.RecipeSha256, new string('0', 64),
            context.EffectId, false, true, null, []);
        return new(WorkflowEffectState.Failed,
            Element(new ToolData("process.run", message, false,
                new(ToolExecutionOutcome.Rejected, "cerebral.owner-verification", State: "not-dispatched"),
                Verification: refusal)), message);
    }

    internal static WorkflowCommandResult MalformedOwnerVerificationProcessRun(
        WorkflowCommandContext context, OwnerVerificationAdmission admission) =>
        NoProcessCheckerRefusal(context, admission,
            "This process.run names the frozen owner checker but does not match its admitted invocation. " +
            "Use the exact executable, arguments, working directory, timeout and expected exit: " +
            JsonSerializer.Serialize(new { executableId = admission.ExecutableId,
                arguments = admission.ModelArguments, workingDirectory = admission.WorkingDirectory,
                timeoutSeconds = OwnerVerificationAdmission.CheckerTimeoutSeconds, expectedExitCode = 0 },
                CerebralJson.Options) + ". No checker process was started.");

    internal static WorkflowCommandResult CheckerHistoryRefusal(WorkflowCommandContext context,
        OwnerVerificationAdmission admission) => NoProcessCheckerRefusal(context, admission,
        "The saved checker intent history is incomplete or unavailable. Inspect this run's retained tool calls and the owner recipe; no checker was started or retried.");

    // EventJournal starts at sequence one. A fresh accepted conversation can have a zero
    // history fence, so natural exhaustion is its only proof of reaching that origin.
    internal static bool CompleteHistoryAtZeroOrigin(long acceptedSequence, JournalPreviousPage page,
        bool foundCurrent) => acceptedSequence == 0 && foundCurrent &&
        page.NextBeforeSequence is null && page.RetentionGap is null;

    private IReadOnlyList<OwnerVerificationBudget.Attempt> PriorVerificationAttempts(
        WorkflowCommandContext context, OwnerVerificationAdmission admission, CancellationToken token)
    {
        var journal = new EventJournal(Path.Combine(store.DirectoryPath, "engine.db"));
        return PriorVerificationAttempts(journal, new WorkflowStore(journal), context, admission, token);
    }

    internal static IReadOnlyList<OwnerVerificationBudget.Attempt> PriorVerificationAttempts(
        EventJournal journal, WorkflowStore receipts, WorkflowCommandContext context,
        OwnerVerificationAdmission admission, CancellationToken token)
    {
        var run = receipts.Get(context.Submission.RequestId);
        if (run.Id != context.RunId || run.Submission.ConversationId != admission.ConversationId ||
            run.Submission.SettingsSnapshot.GetRawText() != context.Submission.SettingsSnapshot.GetRawText())
            throw new InvalidDataException("The retained owner verification run changed.");
        var attempts = new List<OwnerVerificationBudget.Attempt>();
        long? before = null;
        long? snapshotSequence = null;
        var foundCurrent = false;
        var reachedStart = false;
        for (var pageIndex = 0; pageIndex < 32; pageIndex++)
        {
            token.ThrowIfCancellationRequested();
            var page = journal.ReadPreviousPage(admission.ConversationId, before,
                EventJournal.MaximumPageEvents, snapshotSequence, token);
            snapshotSequence ??= page.SnapshotSequence;
            foreach (var item in page.Events.Reverse())
            {
                if (item.Sequence <= run.Submission.ConversationSequence)
                {
                    reachedStart = true;
                    break;
                }
                if (item.Type != CerebralEventTypes.ToolCall || item.TurnId != run.Id) continue;
                var (call, version) = WorkerToolContextProjectionEvidence.ReadCall(item);
                if (call.ToolName != ToolCommand || version != 1) continue;
                WorkerToolContextProjectionEvidence.RequireOwner(item, run, ToolCommand);
                var tool = Read<ToolArguments>(call.Arguments);
                if (tool.Name != "process.run" || !admission.MatchesProcessRun(tool.Arguments)) continue;
                if (call.CallId == context.EffectId)
                {
                    if (foundCurrent) throw new InvalidDataException("Duplicate checker intent in the same run.");
                    foundCurrent = true;
                    continue;
                }
                if (!foundCurrent)
                    throw new InvalidDataException("A later checker intent exists before the current effect.");
                var saved = receipts.GetEffectResult(run.Id, call.CallId);
                var data = saved.Data?.Deserialize<ToolData>(CerebralJson.Options);
                var attested = data?.Verification;
                if (attested is null || attested.SchemaVersion != 1 ||
                    attested.EffectId != call.CallId || attested.RecipeSha256 != admission.RecipeSha256 ||
                    attested.Inputs is null)
                    throw new InvalidDataException("A prior checker intent lacks a complete same-run host receipt.");
                if (!attested.ProcessDispatched && attested.Inputs.Count == 0 &&
                    attested.InputSha256 == new string('0', 64) && attested.Complete &&
                    saved.State == WorkflowEffectState.Failed &&
                    data!.Evidence is { Outcome: ToolExecutionOutcome.Rejected, State: "not-dispatched" })
                    continue; // A host-authored no-process preflight cannot spend the input allowance.
                if (attested.InputSha256 != OwnerVerificationBudget.InputIdentity(attested.RecipeSha256,
                        attested.Inputs.Select(pin => new OwnerVerificationBudget.InputPin(pin.Role,
                            pin.Path, pin.Sha256, pin.Bytes)).ToArray()))
                    throw new InvalidDataException("A prior checker input identity differs from its pinned host receipt.");
                attempts.Add(new(attested.RecipeSha256, attested.InputSha256, call.CallId,
                    saved.State, attested.ProcessDispatched, attested.Complete));
            }
            if (reachedStart) break;
            if (page.NextBeforeSequence is null)
            {
                reachedStart = CompleteHistoryAtZeroOrigin(run.Submission.ConversationSequence, page, foundCurrent);
                break;
            }
            if (before is { } previous && page.NextBeforeSequence >= previous)
                throw new InvalidDataException("The checker journal page did not advance.");
            before = page.NextBeforeSequence;
        }
        if (!foundCurrent || !reachedStart)
            throw new InvalidDataException("The bounded same-run checker intent history is incomplete.");
        attempts.Reverse();
        return attempts;
    }

    private static IReadOnlyList<VerificationInputPin> TargetInputs(OwnerVerificationStage stage) =>
        stage.Pins.Where(pin => pin.Scope == "target")
            .Select(pin => new VerificationInputPin(pin.Role, pin.OriginalPath, pin.Sha256, pin.Bytes))
            .OrderBy(pin => pin.Role, StringComparer.Ordinal).ToArray();

    internal sealed record ValidatedReport(string Sha256, int PassedCount, string Diagnostic,
        JsonElement Report);

    internal static ValidatedReport ValidateCheckerReport(OwnerVerificationStage stage,
        OwnerVerificationAdmission admission, int exitCode, string stdout)
    {
        if (exitCode is not (0 or 1) || stdout.Length is < 1 or > 4096)
            throw new InvalidDataException("The checker did not return a complete supported result.");
        using var summaryDocument = JsonDocument.Parse(stdout);
        var summary = summaryDocument.RootElement;
        var passed = exitCode == 0;
        var contract = admission.CheckerContract;
        var expectedCount = contract.AssertionNames.Count;
        if (summary.GetProperty("schema").GetString() != contract.SummarySchema ||
            summary.GetProperty("checkerVersion").GetString() != contract.CheckerVersion ||
            summary.GetProperty("status").GetString() != (passed ? "passed" : "failed") ||
            !summary.GetProperty("complete").GetBoolean() ||
            summary.GetProperty("totalCount").GetInt32() != expectedCount ||
            summary.GetProperty("passed").GetBoolean() != passed ||
            !summary.GetProperty("stagedClosureUnchanged").GetBoolean())
            throw new InvalidDataException("The checker summary does not match its completed exit.");
        var passedCount = summary.GetProperty("passedCount").GetInt32();
        var failedCount = summary.GetProperty("failedCount").GetInt32();
        if (passedCount < 0 || passedCount > expectedCount || failedCount != expectedCount - passedCount ||
            passed != (passedCount == expectedCount))
            throw new InvalidDataException("The checker assertion counts are inconsistent.");
        var reportRef = summary.GetProperty("report");
        var reportPath = reportRef.GetProperty("path").GetString() ?? "";
        var reportSha = reportRef.GetProperty("sha256").GetString() ?? "";
        var reportBytes = reportRef.GetProperty("bytes").GetInt64();
        var body = stage.ReadSingleReport(reportPath, reportSha, reportBytes);
        using var reportDocument = JsonDocument.Parse(body);
        var report = reportDocument.RootElement;
        try { WorkflowSchema.Validate(report, CerebralJson.Element(OwnerVerificationReportSchemaNode())); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { throw new InvalidDataException("The checker report violates the closed host receipt shape.", error); }
        var descriptorSha = stage.DescriptorSha256;
        var checkerClosure = stage.ClosureDigest("checker");
        var targetClosure = stage.ClosureDigest("target");
        if (report.GetProperty("schema").GetString() != contract.ReportSchema ||
            report.GetProperty("checkerVersion").GetString() != contract.CheckerVersion ||
            report.GetProperty("descriptorSha256").GetString() != descriptorSha ||
            summary.GetProperty("descriptorSha256").GetString() != descriptorSha ||
            report.GetProperty("checkerClosureSha256").GetString() != checkerClosure ||
            summary.GetProperty("checkerClosureSha256").GetString() != checkerClosure ||
            report.GetProperty("targetClosureSha256").GetString() != targetClosure ||
            summary.GetProperty("targetClosureSha256").GetString() != targetClosure ||
            report.GetProperty("totalCount").GetInt32() != expectedCount ||
            report.GetProperty("passed").GetBoolean() != passed ||
            report.GetProperty("passedCount").GetInt32() != passedCount ||
            report.GetProperty("failedCount").GetInt32() != failedCount ||
            !report.GetProperty("stagedClosureUnchanged").GetBoolean() ||
            report.GetProperty("checks").GetArrayLength() != expectedCount ||
            report.GetProperty("checks").EnumerateArray().Count(check => check.GetProperty("Passed").GetBoolean()) != passedCount ||
            report.GetProperty("failureDiagnostic").GetString() != summary.GetProperty("failureDiagnostic").GetString() ||
            report.GetProperty("installed").GetBoolean() ||
            report.GetProperty("mainRendered").GetBoolean() ||
            report.GetProperty("windowShown").GetBoolean())
            throw new InvalidDataException("The checker report does not match its host-attested summary.");
        var checks = report.GetProperty("checks").EnumerateArray().ToArray();
        for (var index = 0; index < checks.Length; index++)
            if (checks[index].GetProperty("Name").GetString() != contract.AssertionNames[index])
                throw new InvalidDataException("The checker assertion names or order differ from the owner recipe.");
        var stagedPins = report.GetProperty("stagedPins");
        if (stagedPins.ValueKind != JsonValueKind.Array || stagedPins.GetArrayLength() != stage.Pins.Count)
            throw new InvalidDataException("The checker report omitted staged file facts.");
        foreach (var pin in stage.Pins)
        {
            var facts = stagedPins.EnumerateArray().Where(item => item.GetProperty("Role").GetString() == pin.Role).ToArray();
            if (facts.Length != 1 || facts[0].GetProperty("Path").GetString() != pin.RelativePath ||
                facts[0].GetProperty("Sha256").GetString() != pin.Sha256 ||
                facts[0].GetProperty("Bytes").GetInt64() != pin.Bytes)
                throw new InvalidDataException("The checker report staged closure differs from the host handles.");
        }
        var loaded = report.GetProperty("actualLoadedBytes");
        if (loaded.ValueKind != JsonValueKind.Array || loaded.GetArrayLength() != contract.LoadedRoles.Count)
            throw new InvalidDataException("The checker did not report the complete expected loaded assembly set.");
        foreach (var role in contract.LoadedRoles)
        {
            var pin = stage.Pins.Single(item => item.Role == role);
            var facts = loaded.EnumerateArray().Where(item => item.GetProperty("Role").GetString() == role).ToArray();
            if (facts.Length != 1 || facts[0].GetProperty("Sha256").GetString() != pin.Sha256 ||
                facts[0].GetProperty("Bytes").GetInt64() != pin.Bytes)
                throw new InvalidDataException("The actual loaded plugin bytes differ from the staged closure.");
        }
        var checker = stage.Pins.Single(item => item.Role == contract.EntryRole);
        var image = report.GetProperty("checkerImage");
        if (image.GetProperty("Role").GetString() != contract.EntryRole ||
            image.GetProperty("Sha256").GetString() != checker.Sha256 ||
            image.GetProperty("Bytes").GetInt64() != checker.Bytes)
            throw new InvalidDataException("The executing checker image differs from the pinned stage.");
        var diagnostic = summary.GetProperty("failureDiagnostic").GetString() ?? "";
        if (diagnostic.Length > 480) throw new InvalidDataException("The checker diagnostic exceeds its bound.");
        return new(reportSha, passedCount, diagnostic, report.Clone());
    }
}
