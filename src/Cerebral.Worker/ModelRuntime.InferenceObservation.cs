namespace Cerebral.Worker;

public sealed partial class ModelRuntime
{
    internal void InitializeInferenceObservation() => Accounting.Observation.RuntimeStarted(ownerProcessGenerationId, Interlocked.Read(ref ownerRuntimeEpoch));

    private void CaptureInferenceAllocator(string modelId, Instance instance)
    {
        try
        {
            // Retire and publish under the same existing owner gate. Pool completion can notify
            // after removal but before Child.StopAndConfirmAsync marks Instance.Stopped.
            // Exact object identity also rejects callbacks from an older replaced instance.
            lock (ownerSnapshotGate)
            {
                if (ownerRuntimeDisposed || !instances.TryGetValue(modelId, out var current) ||
                    !ReferenceEquals(current, instance)) return;
                if (instance.Stopped)
                { Accounting.Observation.Stopped(instance.Id, ownerProcessGenerationId, ownerRuntimeEpoch); return; }
                Accounting.Observation.Allocator(modelId, instance.Id, ownerProcessGenerationId,
                    ownerRuntimeEpoch, instance.Slots.Capacity,
                    instance.Slots.AvailableCount, instance.Slots.Quarantined.Count);
            }
        }
        catch (Exception error) when (InferenceObservationRegistry.Ordinary(error)) { }
    }
    private void CaptureInferenceLaunch(Instance instance, OwnerRuntimeSuccessfulLaunch launch)
    {
        try
        {
            CaptureInferenceAllocator(launch.ModelId, instance);
            string? binding = null;
            if (launch.Binding is not null)
                try { binding = OwnerModelSnapshotCodec.EncodeBinding(launch.Binding).Sha256; }
                catch (InvalidDataException) { }
            Accounting.Observation.Launched(launch.ModelId, instance.Id, ownerProcessGenerationId,
                Interlocked.Read(ref ownerRuntimeEpoch), launch.ParallelRequests,
                launch.Context.MeasuredContextTokens, binding);
        }
        catch (Exception error) when (InferenceObservationRegistry.Ordinary(error)) { }
    }
}
