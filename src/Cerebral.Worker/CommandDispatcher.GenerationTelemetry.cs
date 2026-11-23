using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private static readonly WorkerWorkflows.OperationDescriptor GenerationTelemetryOperation = new(
        "metrics.generation", 1,
        "Read bounded current numeric provider lanes and recent completed samples. Rates use actual token/timing counters; absent telemetry remains unavailable. No prompts or model text.",
        CerebralJson.Element(new { type = "object", properties = new { }, additionalProperties = false }), true);

    private GenerationTelemetrySnapshot ReadGenerationTelemetry(JsonElement arguments)
    {
        if (arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null) ||
            arguments.ValueKind == JsonValueKind.Object && arguments.EnumerateObject().Any())
            throw new ArgumentException("Generation metrics accepts no arguments.");
        return runtime.GenerationTelemetrySnapshot();
    }
}
