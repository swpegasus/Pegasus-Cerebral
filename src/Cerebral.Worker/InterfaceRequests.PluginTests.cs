using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class InterfaceRequests
{
    internal const string PluginTestPrefix = PluginControlReferences.TestRequestPrefix;
    internal const string RepairBeforeInstallMode = InterfacePluginTestModes.RepairBeforeInstall;
    internal const int MaximumPluginTestRounds = 3;
    private string? lastPluginTestId;

    // Consent and the follow-up intent share the same durable interface record. A lost event,
    // service restart or duplicate owner completion cannot create another test run.
    private InterfacePluginTestContinuation? CreatePluginTestContinuation(InterfaceRequest request)
    {
        if (request.Source != "workflow" || request.Kind != "plugin" || request.Status != "completed" ||
            request.Plugin is not { SchemaVersion: 1, ControlTestsAllowed: true } plugin ||
            !plugin.Mounts.Any(mount => mount.Slot == "sidebar"))
            return null;
        return CreatePluginContinuation(request, mode: null);
    }

    private InterfacePluginTestContinuation CreatePluginContinuation(InterfaceRequest request, string? mode)
    {
        var original = request.JobId;
        var round = 1;
        if (request.JobId.StartsWith(PluginTestPrefix, StringComparison.Ordinal))
        {
            var parentId = request.JobId[PluginTestPrefix.Length..];
            var parent = Guid.TryParseExact(parentId, "N", out _) ? store.Get<InterfaceRequest>(RecordKind, parentId) : null;
            if (parent?.PluginTests is not { } prior || prior.RequestId != request.JobId ||
                prior.RunId != request.JobId || parent.SessionId != request.SessionId ||
                parent.ProfileId != request.ProfileId)
                return new(PluginTestPrefix + request.Id, original, round, "blocked",
                    Message: "The service test lineage could not be verified. Start an explicit owner request.",
                    Mode: mode);
            original = prior.OriginRunId;
            round = prior.Round + 1;
        }
        return new(PluginTestPrefix + request.Id, original, round,
            round <= MaximumPluginTestRounds ? "pending" : "blocked",
            Message: round <= MaximumPluginTestRounds ? null :
                "The three approved test/repair rounds are exhausted. Review the observed failures before starting another request.",
            Mode: mode);
    }

    /// <summary>Scheduler admission only; model execution remains in the ordinary bounded workflow queue.</summary>
    internal async Task ProcessPluginTestsAsync(WorkerWorkflows workflows, CancellationToken cancellationToken,
        RemoteWorkerManagement? remoteModels = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batch = store.ListPluginTestContinuations(lastPluginTestId, 16);
        foreach (var candidate in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastPluginTestId = candidate.Id;
            var request = Get(candidate.Id);
            if (request.PluginTests is not { } tests) continue;
            InterfacePluginTestContinuation next;
            if (tests.Status == "admitted")
            {
                var run = tests.RunId is null ? null : workflows.Store.TryGet(tests.RunId);
                if (run is null) next = tests with { Status = "blocked", Message = "The admitted test run is missing." };
                else if (run.Status == Cerebral.Contracts.Workflows.WorkflowStatus.Completed)
                    next = tests with { Status = "finished", Message = tests.Mode == RepairBeforeInstallMode
                        ? "The repair run finished. Any replacement remains pending separate owner review; completion is not installation or a functional pass."
                        : "The test run finished. Inspect its actual observations; completion is not a functional pass." };
                else if (run.Status == Cerebral.Contracts.Workflows.WorkflowStatus.Cancelled || run.CancelRequested)
                    next = tests with { Status = "cancelled", Message = "The test run was stopped. It will not restart automatically." };
                else if (run.Status == Cerebral.Contracts.Workflows.WorkflowStatus.Failed)
                    next = tests with { Status = "failed", Message = Limit(run.Error ?? "The test run failed; inspect its retained evidence.") };
                else continue;
            }
            else if (tests.Status == "pending")
            {
                try
                {
                    var run = tests.Mode switch
                    {
                        null => await workflows.SubmitPluginTestsAsync(request, cancellationToken, remoteModels).ConfigureAwait(false),
                        RepairBeforeInstallMode => await workflows.SubmitPluginRepairAsync(request, cancellationToken, remoteModels).ConfigureAwait(false),
                        _ => throw new InvalidDataException("The retained plugin continuation mode is unsupported.")
                    };
                    next = tests with { Status = "admitted", RunId = run.Id, Message = null };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (PluginTestsDeferredException error)
                { next = tests with { Message = Limit(error.Message) }; }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                    UnauthorizedAccessException or InvalidDataException or IOException or KeyNotFoundException)
                { next = tests with { Status = "blocked", Message = Limit(error.Message) }; }
            }
            else continue;
            if (next == tests) continue;
            // Engine admission owns its own short transaction. Do not hold this store's write
            // transaction across it: independently reopened stores would otherwise deadlock.
            var changed = store.Locked(() =>
            {
                var current = Get(candidate.Id);
                if (current.PluginTests != tests) return null;
                current.PluginTests = next;
                store.Put(RecordKind, current.Id, current);
                return current;
            });
            if (changed is not null) Publish(changed);
        }
    }
}

internal sealed class PluginTestsDeferredException(string message) : InvalidOperationException(message);
