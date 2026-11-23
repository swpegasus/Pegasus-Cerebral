using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker;

internal interface IOwnerWorkflowSubmissionDispatcher
{
    Task<CommandResult> ExecuteOwnerVerificationPipeAsync(NamedPipeServerStream pipe,
        InstalledWorkerBinding? installedBinding, LocalUserOwnerBinding? localBinding,
        CommandRequest request, CancellationToken token);
}

public sealed partial class CommandDispatcher : IOwnerWorkflowSubmissionDispatcher
{
    internal static bool HasOwnerVerificationField(CommandRequest request) =>
        request.Operation == "jobs.workflow.submit" && request.Arguments.ValueKind == JsonValueKind.Object &&
        request.Arguments.EnumerateObject().Any(property =>
            string.Equals(property.Name, "ownerVerification", StringComparison.OrdinalIgnoreCase));

    async Task<CommandResult> IOwnerWorkflowSubmissionDispatcher.ExecuteOwnerVerificationPipeAsync(
        NamedPipeServerStream pipe, InstalledWorkerBinding? installedBinding,
        LocalUserOwnerBinding? localBinding, CommandRequest request, CancellationToken token)
    {
        try
        {
            if (SourceRecoveryBlocksOwnerSnapshot) return CommandResult.Fail("source_recovery_held");
            // This edition executes the checker in the current Worker token. A service-hosted
            // Worker must not run that checker as System until a user execution host is admitted.
            if (installedBinding is not null || localBinding is null)
                throw new UnauthorizedAccessException("owner_verification_user_session_required");
            if (!OperatingSystem.IsWindows())
                throw new UnauthorizedAccessException("owner_verification_windows_required");
            localBinding.RequireCurrent(store.DirectoryPath);
            var caller = InstalledWorkerWindows.ReadAuthenticatedCaller(pipe);
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            if (!caller.Authenticated || caller.NetworkLogon || caller.Anonymous || caller.SessionId <= 0 ||
                identity.IsSystem || caller.UserSid != localBinding.OwnerSid ||
                caller.SessionId != localBinding.SessionId || process.SessionId != caller.SessionId)
                throw new UnauthorizedAccessException("owner_verification_caller_denied");
            if (request.Operation != "jobs.workflow.submit" || request.ProfileId is not null)
                throw new UnauthorizedAccessException("owner_verification_context_denied");
            var (input, admission) = ReadOwnerVerificationRequest(request.Arguments);
            var host = workflows ?? throw new InvalidOperationException("Workflow engine is unavailable.");
            return CommandResult.Ok(await host.SubmitOwnerVerificationAsync(input, admission, token)
                .ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return CommandResult.Fail("owner_verification_cancelled"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return CommandResult.Fail(error.Message); }
    }

    internal static (SubmitWorkflowRequest Input, OwnerVerificationAdmission Admission)
        ReadOwnerVerificationRequest(JsonElement arguments)
    {
            if (arguments.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Owner verification submit arguments must be an object.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "requestId", "conversationId", "profileId", "definitionId", "definitionVersion",
                "userText", "assignmentText", "input", "stepLimit", "acceptedContent",
                "ownerProjectId", "localGeneration", "ownerVerification"
            };
            if (arguments.EnumerateObject().Any(property =>
                    !names.Add(property.Name) || !allowed.Contains(property.Name)) ||
                !arguments.TryGetProperty("ownerVerification", out var raw))
                throw new InvalidDataException("Owner verification submit arguments are duplicated or noncanonical.");
            var admission = OwnerVerificationAdmission.Read(raw);
            var input = WorkerWorkflows.Read<SubmitWorkflowRequest>(arguments);
            if (input.OwnerVerification is null)
                throw new InvalidDataException("Owner verification submission is missing its recipe.");
            return (input, admission);
    }
}
