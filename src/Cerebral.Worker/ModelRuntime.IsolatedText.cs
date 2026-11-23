namespace Cerebral.Worker;

public sealed partial class ModelRuntime
{
    // Match the actual successful launch, not merely a same-ID mutable model row. This shares
    // normal registration/ownership gates and never calls Load/EnsureReady/provider probes.
    internal Task<RegisteredNativeGenerationService> RegisterLoadedIsolatedTextAsync(
        WorkerExecutionSnapshot.ModelBinding accepted, CancellationToken token) =>
        RegisterLoadedNativeTextCoreAsync(accepted.Id, token, accepted);
}
