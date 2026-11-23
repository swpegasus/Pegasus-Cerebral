using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker;

public sealed partial class JobScheduler
{
    internal WorkflowRun SubmitIsolatedText(IsolatedTextGenerationRequest request) =>
        WithResourceAdmission(paused => store.Locked(() =>
        {
            if (workflows is null) throw new InvalidOperationException("Isolated generation is unavailable.");
            // Retained IDs stay inspectable, including cancellation-before-submit tombstones.
            if (workflows.Store.TryGet(request.RequestId) is not null) return workflows.SubmitIsolatedText(request);
            RequireIsolatedAdmission(paused, IntentionalHoldBlocksAdmission(), workflows.Engine?.IsPaused != false);
            if (store.ActiveJobCount() + workflows.Store.CountActive() >= configuration.Get().QueueCapacity)
                throw new InvalidOperationException("The work queue is full.");
            return workflows.Publish(workflows.SubmitIsolatedText(request));
        }));

    internal static void RequireIsolatedAdmission(bool resourcesPaused, bool intentionalHold, bool enginePaused)
    {
        if (resourcesPaused || intentionalHold || enginePaused)
            throw new InvalidOperationException("Suggestions are disabled while work or model resources are held or paused.");
    }

    internal WorkflowRun CancelIsolatedText(IsolatedTextGenerationRequest request)
    {
        if (workflows is null) throw new InvalidOperationException("Isolated generation is unavailable.");
        // Same immutable request, in the same durable workflow store. If cancellation overtakes
        // submission, create and cancel it before releasing scheduler admission; a late generate
        // request can only retrieve this cancelled identity, never enqueue a replacement.
        store.Locked(() =>
        {
            workflows.SubmitIsolatedText(request, cancellationOnly: true);
            return workflows.Store.Cancel(request.RequestId);
        });
        // Provider cancellation callbacks must run after releasing the StateStore monitor.
        return CancelWorkflow(request.RequestId);
    }
}
