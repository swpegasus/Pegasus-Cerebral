using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private object ReadInferenceObservation(JsonElement arguments, CancellationToken token) =>
        ReadCapturedInferenceObservationResponse(runtime.Accounting.Observation, arguments, token);

    internal static object ReadCapturedInferenceObservationResponse(
        InferenceObservationRegistry capture, JsonElement arguments, CancellationToken token)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Inference observation requires an empty object or schemaVersion 2 or 3.");
        var properties = arguments.EnumerateObject();
        if (!properties.MoveNext())
            return ReadCapturedInferenceObservation(capture, arguments, token);
        var selector = properties.Current;
        if (!selector.NameEquals("schemaVersion") || selector.Value.ValueKind != JsonValueKind.Number ||
            !selector.Value.TryGetInt32(out var version) || version is not (2 or 3) || properties.MoveNext())
            throw new ArgumentException("Inference observation requires an empty object or schemaVersion 2 or 3.");
        token.ThrowIfCancellationRequested();
        return version == 2 ? capture.SnapshotV2() : capture.SnapshotV3();
    }

    // Default callers retain the exact v1 DTO and validation contract.
    internal static InferenceObservationSnapshot ReadCapturedInferenceObservation(
        InferenceObservationRegistry capture, JsonElement arguments, CancellationToken token)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
            throw new ArgumentException("Inference observation requires an empty argument object.");
        token.ThrowIfCancellationRequested();
        return capture.Snapshot();
    }

    private static readonly WorkerWorkflows.OperationDescriptor InferenceObservationOperation = new(
        "metrics.inference.status", 1,
        "Bounded captured local-owner inference ownership, exact lease identities, accepted execution/profile counts and independently aged allocator/launch observations. Empty arguments retain schema 1; schemaVersion 2 opts into bounded supplemental links; schemaVersion 3 adds optional captured native cache diagnostics around the unchanged schema 2 base. Prefix comparison describes prepared inputs, independently of provider cache outcomes or unobserved native retention. Remote aggregation, context totals, provider activity and connection registration fence are explicitly unavailable. No read-side store/provider/lifecycle/configuration/discovery callbacks.",
        CerebralJson.Element(new
        {
            type = "object",
            properties = new { schemaVersion = new { type = "integer", @enum = new[] { 2, 3 } } },
            additionalProperties = false
        }), true);
}
