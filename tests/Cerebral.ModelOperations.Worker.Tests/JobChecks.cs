using System.Text.Json;
using Cerebral.Contracts.ModelOperations;
using Cerebral.Worker;
using Cerebral.Worker.ModelOperations;

public static class JobChecks
{
    public static async Task<int> RunAsync(string scratch)
    {
        var passed = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
        async Task Reject(Func<Task> action)
        { try { await action(); } catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException) { passed++; return; } throw new Exception("Expected job refusal."); }
        var store = new StateStore(Path.Combine(scratch, "jobs"));
        var admission = new Admission();
        var jobs = new ModelOperationJobs(store, admission, new EventHub());
        var request = Submission("request-one", "first", "second");
        var accepted = jobs.Submit(request, "profile-one");
        Check(accepted.State == "queued" && admission.Runtime.Calls == 0, "Acceptance must not execute the model.");
        Check(jobs.Submit(request, "profile-one").Id == accepted.Id, "Exact retry must return its original batch.");
        await Reject(() => Task.FromResult(jobs.Submit(request with { Items = [request.Items[0]] }, "profile-one")));
        await Reject(() => Task.FromResult(jobs.Get(accepted.Id, "another-profile")));
        Check(jobs.GetByRequest(request.RequestId, "profile-one").Id == accepted.Id, "Read-only request lookup reconciles lost acceptance replies.");
        await Reject(() => Task.FromResult(jobs.GetByRequest(request.RequestId, "another-profile")));
        admission.Held = true;
        Check(!await jobs.RunNextAsync(default), "Held policy must not start a model operation.");
        Check(jobs.Get(accepted.Id, "profile-one").State == "queued" && admission.Runtime.Calls == 0, "Held work remains durably queued.");
        admission.Held = false;
        Check(await jobs.RunNextAsync(default), "First admitted item must run.");
        Check(jobs.Get(accepted.Id, "profile-one").Items[0].State == ModelOperationState.Succeeded, "First output commits independently.");
        admission.Runtime.FailNext = true;
        await jobs.RunNextAsync(default);
        var partial = jobs.Get(accepted.Id, "profile-one");
        Check(partial.State == "partial" && partial.Items[0].State == ModelOperationState.Succeeded && partial.Items[1].State == ModelOperationState.Failed,
            "A failed item must preserve earlier completed output.");
        Check(jobs.GetRequest(accepted.Id, "profile-one").Items.Count == 2, "Retry can read the original frozen input/settings.");

        var cancelled = jobs.Submit(Submission("cancel-request", "cancelled-item"), "profile-one");
        var calls = admission.Runtime.Calls; jobs.Cancel(cancelled.Id, "profile-one");
        Check(!await jobs.RunNextAsync(default) && admission.Runtime.Calls == calls, "Queued cancellation must have no runtime effect.");
        Check(jobs.Get(cancelled.Id, "profile-one").State == "cancelled", "Queued cancellation is confirmed without dispatch.");

        var lost = jobs.Submit(Submission("lost-request", "lost-item", "untouched-item"), "profile-one");
        admission.Runtime.ThrowNext = true;
        await jobs.RunNextAsync(default);
        Check(jobs.Get(lost.Id, "profile-one").State == "unknown" && admission.LastLease?.UnknownRetained == true,
            "A lost dispatched response must retain uncertainty/capacity.");
        calls = admission.Runtime.Calls;
        Check(!await jobs.RunNextAsync(default) && admission.Runtime.Calls == calls, "Unknown batches cannot automatically dispatch another item.");
        Check(jobs.HasActiveWork("model-fixture"), "Unknown work must block automatic idle retirement.");
        jobs.Cancel(lost.Id, "profile-one");
        Check(jobs.Get(lost.Id, "profile-one").Items[1].State == ModelOperationState.Cancelled && jobs.Get(lost.Id, "profile-one").State == "unknown",
            "Cancel may stop queued remainder without erasing an unknown item.");

        var crashed = jobs.Submit(Submission("restart-request", "restart-item"), "profile-one");
        admission.Runtime.Block = true;
        var run = jobs.RunNextAsync(default);
        await admission.Runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!await jobs.RunNextAsync(default), "A second scheduler tick cannot exceed graphics concurrency one.");
        var restarted = new ModelOperationJobs(store, admission, new EventHub()); restarted.ReconcileInterrupted();
        Check(restarted.Get(crashed.Id, "profile-one").State == "unknown", "Controller restart marks dispatched work unknown.");
        admission.Runtime.Release.TrySetResult(); await run;
        Check(jobs.Get(crashed.Id, "profile-one").State == "unknown", "Late completion cannot overwrite a reconciled unknown receipt.");
        Console.WriteLine(JsonSerializer.Serialize(new { state = "passed", jobChecks = passed,
            evidence = "real SQLite/durable batch code with deterministic runtime/admission fixtures; no model/GPU" }));
        return passed;
    }

    private static ModelOperationSubmissionRequest Submission(string id, params string[] items)
    {
        var input = new ModelOperationInput("image", new("worker-fixture", "input-job", "input-effect", "input-artifact", 1,
            "image/png", "cerebral.image.preview/1", "image", 70, new string('a', 64)));
        return new(1, id, "model-fixture", ModelOperationContract.ImageTo3D,
            items.Select(x => new ModelOperationSubmissionItem(x, [input], new() { TextureResolution = 1024, Seed = 42 })).ToArray());
    }

    private sealed class Admission : IModelOperationJobAdmission
    {
        public bool Held; public Runtime Runtime = new(); public Lease? LastLease;
        public void ValidateSubmission(ModelOperationSubmissionRequest request, string? ownerProfileId) => ModelOperationContract.Validate(request);
        public Task<IModelOperationExecutionLease> PrepareAsync(ModelOperationSubmissionRequest batch, ModelOperationSubmissionItem item,
            ModelOperationItemStatus identity, string? ownerProfileId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Held) throw new ModelOperationAdmissionException("host_hold", "Fixture admission is held.", waiting: true);
            LastLease = new(Runtime);
            return Task.FromResult<IModelOperationExecutionLease>(LastLease);
        }
    }
    private sealed class Lease(Runtime runtime) : IModelOperationExecutionLease
    {
        public ModelOperationRuntimeBinding Binding { get; } = new("worker-fixture", "runtime-fixture", "1.0", new string('b',64),
            "model-fixture", "package-fixture", "1.0", new string('c',64), "profile-fixture", new string('d',64), "instance-fixture");
        public IModelOperationRuntime Runtime => runtime;
        public bool UnknownRetained; private bool completed;
        public Task CompleteAsync(ModelOperationResult result, CancellationToken token) { completed = result.State != ModelOperationState.Unknown; UnknownRetained = !completed; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { if (!completed) UnknownRetained = true; return ValueTask.CompletedTask; }
    }
    private sealed class Runtime : IModelOperationRuntime
    {
        public int Calls; public bool FailNext, ThrowNext, Block;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelOperationRuntimeDescriptor Descriptor { get; } = new("runtime-fixture", "1.0", [], []);
        public Task<ModelOperationRuntimeState> LoadAsync(ModelOperationRuntimeBinding binding, CancellationToken token) => throw new NotSupportedException();
        public async Task<ModelOperationResult> RunAsync(ModelOperationRequest request, IProgress<ModelOperationProgress>? progress, CancellationToken token)
        {
            Calls++;
            if (ThrowNext) { ThrowNext = false; throw new IOException("Fixture lost acknowledgement after dispatch."); }
            if (Block) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); Block = false; }
            progress?.Report(new(1, request.JobId, request.EffectId, ModelOperationState.Exporting, "exporting"));
            if (FailNext) { FailNext = false; return new(1, request.RequestId, request.JobId, request.EffectId, request.Binding,
                ModelOperationState.Failed, false, [], new("fixture_failure", "Deterministic model fixture failure.")); }
            return new(1, request.RequestId, request.JobId, request.EffectId, request.Binding, ModelOperationState.Succeeded, false,
                [new("mesh", new(request.Binding.WorkerInstallationId, request.JobId, request.EffectId, "output-artifact", 1,
                    "model/gltf-binary", "cerebral.gltf.pbr/1", "mesh", 1024, new string('e',64)))]);
        }
        public Task<ModelOperationResult> ExportAsync(ModelOperationExportRequest request, IProgress<ModelOperationProgress>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task<ModelOperationCancellation> CancelAsync(ModelOperationCancelRequest request, CancellationToken token) => Task.FromResult(new ModelOperationCancellation(1,request.JobId,request.EffectId,true,true,false));
        public Task<ModelOperationRuntimeState> UnloadAsync(ModelOperationRuntimeBinding binding, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
