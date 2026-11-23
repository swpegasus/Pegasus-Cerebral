using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Desktop.Plugins;

/// <summary>Presentation admission only. The service remains authoritative for schema and execution grants.</summary>
internal static class DesktopWorkflowAdmission
{
    // A plugin-authored explicit workflow retains the original smaller presentation boundary.
    // Native Chat can use the wider engine budget while the profile still caps actual agent work.
    private const int MaximumExplicitPluginSteps = 256;

    internal static DesktopWorkflowSubmission Freeze(string pluginId, DesktopWorkflowSubmission submission)
    {
        if (submission.Target is not { } target) return submission;
        // The lease supplies pluginId. Trusted in-process plugins are not sandboxed by this routing guard.
        var prefix = pluginId + ":";
        if (string.IsNullOrWhiteSpace(pluginId) || target.DefinitionId is null ||
            target.DefinitionId.Length > 256 || !target.DefinitionId.StartsWith(prefix, StringComparison.Ordinal) ||
            !Regex.IsMatch(target.DefinitionId[prefix.Length..], @"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("An explicit workflow must use the submitting plugin's exact namespace and a bounded definition ID.");
        if (target.DefinitionVersion is null || target.DefinitionVersion.Length > 64 ||
            !Regex.IsMatch(target.DefinitionVersion, @"^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?(?:\+[a-zA-Z0-9.-]+)?$", RegexOptions.CultureInvariant))
            throw new ArgumentException("An explicit workflow requires an exact semantic definition version.");
        if (target.StepLimit is < 1 or > MaximumExplicitPluginSteps)
            throw new ArgumentException($"The workflow step limit must be between 1 and {MaximumExplicitPluginSteps}; profile limits still apply.");
        if (target.Input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Explicit workflow input must be a JSON object.");
        var json = target.Input.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > WorkflowContract.MaximumStateBytes)
            throw new ArgumentException("The explicit workflow input exceeds the 128 KiB limit.");
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        ValidateNames(parsed.RootElement);
        if (submission.VisibleText.Contains('\0') || submission.Context.Contains('\0'))
            throw new ArgumentException("Workflow text cannot contain NUL characters.");
        return submission with { Target = target with { Input = parsed.RootElement.Clone() } };
    }

    private static void ValidateNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Workflow input contains duplicate JSON properties.");
                ValidateNames(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateNames(item);
    }
}
