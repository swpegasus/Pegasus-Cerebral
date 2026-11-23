using System.IO.Pipes;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker;

internal interface IOwnerModelSnapshotDispatcher
{
    Task<CommandResult> ExecuteOwnerSnapshotPipeAsync(NamedPipeServerStream pipe,
        InstalledWorkerBinding? binding, CommandRequest request, int frameBytes, CancellationToken token);
}

public sealed partial class CommandDispatcher : IOwnerModelSnapshotDispatcher
{
    private readonly Lazy<OwnerModelSnapshotService> ownerSnapshots =
        new(() => new OwnerModelSnapshotService(store, configuration, runtime, localRun));

    internal long OwnerSnapshotVerifiedAdmissions => ownerSnapshots.Value.VerifiedOwnerAdmissions;

    internal Task<CommandResult> ExecuteOwnerSnapshotHttpAsync(HttpContext context, CommandRequest request, CancellationToken token) =>
        SourceRecoveryBlocksOwnerSnapshot ? Task.FromResult(CommandResult.Fail("source_recovery_held")) :
        ownerSnapshots.Value.ExecuteHttpAsync(context, request, token);

    Task<CommandResult> IOwnerModelSnapshotDispatcher.ExecuteOwnerSnapshotPipeAsync(NamedPipeServerStream pipe,
        InstalledWorkerBinding? binding, CommandRequest request, int frameBytes, CancellationToken token) =>
        SourceRecoveryBlocksOwnerSnapshot ? Task.FromResult(CommandResult.Fail("source_recovery_held")) :
        ownerSnapshots.Value.ExecutePipeAsync(pipe, binding, request, frameBytes, token);

    private bool SourceRecoveryBlocksOwnerSnapshot => localRun?.SourceRecovery is { } recovery &&
        !recovery.RuntimeAdmissionOpen;

    private static readonly WorkerWorkflows.OperationDescriptor OwnerSnapshotOperation = new(
        OwnerModelSnapshotService.Operation, 1,
        "Optional pure owner inspection through the fixed authenticated HTTP path " + OwnerModelSnapshotService.HttpPath +
        " or verified native owner pipe. Generic, peer, external and profile dispatch cannot invoke it. Never model admission.",
        CerebralJson.Element(new {
            type = "object", additionalProperties = false,
            required = new[] { "schemaVersion", "expectedWorkerInstallationId", "expectedControllerInstallationId" },
            properties = new {
                schemaVersion = new { type = "integer", @const = 1 },
                expectedWorkerInstallationId = new { type = "string", pattern = "^[0-9a-f]{32}$" },
                expectedControllerInstallationId = new { type = "string", pattern = "^[0-9a-f]{32}$" },
                modelId = new { type = "string", minLength = 1, maxLength = 128 }
            }
        }), OwnerOnly: true);
}
