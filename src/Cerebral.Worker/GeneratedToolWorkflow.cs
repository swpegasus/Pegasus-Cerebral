using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

// Compatibility wrapper for the legacy AgentEngine; native durable consumers use Core directly.
internal static class GeneratedToolWorkflow
{
    public static void Validate(ToolGenerationRequest request) => ToolGenerationPlan.Validate(request);
    public static JsonElement WithContent(ToolGenerationRequest request, string content) => ToolGenerationPlan.WithContent(request, content);
    public static JsonElement ActivityArguments(ToolGenerationRequest request, JsonElement arguments, int characterCount) =>
        ToolGenerationPlan.ActivityArguments(request, arguments, characterCount);
    public static int AttemptTokenLimit(ToolGenerationRequest request, int attempt) => ToolGenerationPlan.AttemptTokenLimit(request, attempt);
    public static string RetryGuidance(ToolGenerationRequest request, string rejection, int attempt = 2) =>
        ToolGenerationPlan.RetryGuidance(request, rejection, attempt);
}

internal sealed record GeneratedToolDispatch(ToolResult Result, int SessionRevision);
