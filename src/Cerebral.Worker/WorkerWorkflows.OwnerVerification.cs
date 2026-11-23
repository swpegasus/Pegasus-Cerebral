using System.Text.Json;
using Cerebral.Conversation;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflows
{
    // The local owner pipe admits the closed recipe before this entry. Workflow Input and
    // model text cannot supply it, and generic, child and service submissions cannot select it.
    internal Task<Cerebral.Contracts.Workflows.WorkflowRun> SubmitOwnerVerificationAsync(
        SubmitWorkflowRequest input, OwnerVerificationAdmission admission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        RequireOwnerVerificationTuple(input, admission);
        return SubmitAsyncCore(input, null, null, null, cancellationToken, ownerVerification: admission);
    }

    private static void RequireOwnerVerificationTuple(SubmitWorkflowRequest input,
        OwnerVerificationAdmission admission)
    {
        admission.Validate();
        if (input.RequestId != admission.RequestId || input.ConversationId != admission.ConversationId ||
            input.ProfileId != admission.ProfileId || input.DefinitionId != admission.DefinitionId ||
            input.DefinitionVersion != admission.DefinitionVersion ||
            input.DefinitionId != ConversationAgentHarness.Id ||
            (input.DefinitionVersion != ConversationAgentHarness.RepeatedSuccessGuardVersion &&
             input.DefinitionVersion != ConversationAgentHarness.RepeatPreparedVersion &&
             input.DefinitionVersion != ConversationAgentHarness.OwnerVerifiedPreparedVersion) ||
            input.LocalGeneration is not null)
            throw new UnauthorizedAccessException("Owner verification admission belongs to a different fresh ordinary conversation run.");
    }

    // The pre-capture RID lookup is only an early refusal. Another owner submission can
    // commit during asynchronous capture, so compare the incoming recipe under the same
    // lock that reads the retained run before RetainedSubmission reuses its snapshot.
    internal static void RequireEquivalentOwnerVerificationRetained(JsonElement? incoming,
        JsonElement retainedSnapshot)
    {
        if (incoming is null && !retainedSnapshot.TryGetProperty("ownerVerification", out _)) return;
        var retained = WorkerExecutionSnapshot.Read(retainedSnapshot).OwnerVerification;
        var submitted = incoming is { } value ? OwnerVerificationAdmission.Read(value) : null;
        if (submitted?.RecipeSha256 != retained?.RecipeSha256)
            throw new Cerebral.Contracts.Workflows.WorkflowConflictException(
                "Retained request identity has a different owner verification recipe.");
    }

    private void RequireOwnerVerificationCurrent(SubmitWorkflowRequest input,
        WorkerExecutionSnapshot.Snapshot snapshot, OwnerVerificationAdmission admission)
    {
        RequireOwnerVerificationTuple(input, admission);
        if (snapshot.FullAccess || snapshot.SourceBoundBackground is not null ||
            !snapshot.Capabilities.Contains("process.execute", StringComparer.Ordinal) ||
            !WorkerExecutionSnapshot.AllowsTool(snapshot.Profile, "process.run") ||
            snapshot.Tools.Count(tool => tool.Name == "process.run" && tool.Capability == "process.execute") != 1)
            throw new UnauthorizedAccessException("Owner verification requires an ordinary selected process tool without application Full Access.");
        var registration = configuration.Get().Executables.SingleOrDefault(item => item.Id == admission.ExecutableId);
        if (registration is null || registration.Path != admission.ExecutablePath ||
            !registration.PrefixArguments.SequenceEqual(admission.PrefixArguments, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("The owner verification executable registration changed.");
        PortableRegistrationPolicy.RequireReady(registration);
        var installedDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        if (!ScopedFilePolicy.IsWithin(admission.ExecutablePath, installedDotnet) ||
            admission.ModelArguments.Count != 1 ||
            admission.ModelArguments[0] != admission.Files.Single(file =>
                file.Role == admission.CheckerContract.EntryRole).OriginalPath ||
            admission.WorkingDirectory != admission.CheckerRoot ||
            snapshot.AllowedRoots.Count == 0 ||
            admission.Files.Any(file => !snapshot.AllowedRoots.Any(root => ScopedFilePolicy.IsWithin(file.OriginalPath, root))))
            throw new UnauthorizedAccessException("Owner verification recipe is outside the exact registered host or selected file scope.");
    }
}
