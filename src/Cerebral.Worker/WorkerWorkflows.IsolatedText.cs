using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflows
{
    // Called only under the scheduler's existing resource -> StateStore admission gates.
    internal WorkflowRun SubmitIsolatedText(IsolatedTextGenerationRequest request, bool cancellationOnly = false)
    {
        request.Validate(DateTimeOffset.UtcNow, retained: cancellationOnly || Store.TryGet(request.RequestId) is not null);
        if (Store.TryGet(request.RequestId) is { } prior)
        {
            if (IsolatedTextGenerationAdmission.RequireSubmission(prior.Submission) != request)
                throw new WorkflowConflictException("Isolated request ID was already used for different input.");
            return prior;
        }
        var definition = Registry.GetDefinition(IsolatedTextGenerationContract.DefinitionId, IsolatedTextGenerationContract.DefinitionVersion);
        var bindings = packages.Capture(definition.Descriptor.Id);
        System.Text.Json.JsonElement settings;
        if (cancellationOnly)
            settings = CerebralJson.Element(new { capabilities = Array.Empty<string>(), packageBindings = bindings });
        else
        {
            if (Engine is null) throw new InvalidOperationException("Isolated generation requires the installed workflow engine.");
            var profile = legacy.Get<AgentProfile>("agent", request.ProfileId) ?? throw new ArgumentException("Suggestion profile is unavailable.");
            var model = legacy.Get<ModelRecord>("model", profile.ModelId) ?? throw new ArgumentException("Suggestion model is unavailable. Load its configured model explicitly.");
            settings = CerebralJson.Element(IsolatedTextGenerationAdmission.Snapshot(request, profile, model, bindings,
                WorkerExecutionSnapshot.RoutingFingerprint(configuration.Get())));
        }
        var submission = new WorkflowSubmission(request.RequestId, "isolated-" + request.RequestId, request.ProfileId,
            definition.Descriptor.Id, definition.Descriptor.Version, "Generate one advisory next-message suggestion.", null,
            CerebralJson.Element(request), settings, StepLimit: 2, Caller: new(EventActorKind.Service, "cerebral.isolated-text"))
        {
            IsolatedText = new(1, request.ProfileRevision), ActiveTimeLimitSeconds = 30, PolicyStepLimit = 2
        };
        return Store.Submit(submission);
    }

    internal WorkerExecutionSnapshot.Snapshot RequireIsolatedCurrent(WorkflowSubmission submission)
    {
        var request = IsolatedTextGenerationAdmission.RequireSubmission(submission);
        if (request.ExpiresAtUnixMilliseconds <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new OperationCanceledException("The advisory request expired.");
        var current = legacy.Get<AgentProfile>("agent", request.ProfileId) ?? throw new UnauthorizedAccessException("Suggestion profile is unavailable.");
        IsolatedTextGenerationAdmission.RequireProfile(request, current);
        var snapshot = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot);
        var model = legacy.Get<ModelRecord>("model", current.ModelId);
        if (snapshot.Profile.Id != current.Id || snapshot.Profile.Revision != current.Revision || snapshot.Profile.ModelId != current.ModelId ||
            snapshot.Model is null || model is null || !WorkerExecutionSnapshot.SameModel(snapshot.Model, model))
            throw new UnauthorizedAccessException("The exact isolated profile/model changed. Select its current revision explicitly.");
        _ = IsolatedTextGenerationAdmission.ModelRequest(request, snapshot);
        return snapshot;
    }
}
