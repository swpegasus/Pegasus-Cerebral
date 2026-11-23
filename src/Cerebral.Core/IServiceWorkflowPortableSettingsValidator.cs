using System.Text.Json;

namespace Cerebral.Core;

/// <summary>The first workflow-only, pure semantic validation contract for approved service-plugin settings.</summary>
public static class ServiceWorkflowPortableSettingsValidatorContract
{
    public const int Version = 1;
}

/// <summary>
/// Optional service-plugin facet for validating an already projected, host-approved nonsecret settings object.
/// The Worker supplies a clone and owns structural validation, package approval, and error redaction.
/// </summary>
/// <remarks>
/// Implementations must be bounded and pure: no filesystem, network, secret-store, lifecycle, UI, or workflow mutation.
/// Returning normally admits the projected values; rejection throws without granting any authority or returning values.
/// </remarks>
public interface IServiceWorkflowPortableSettingsValidator
{
    int WorkflowPortableSettingsValidatorVersion { get; }
    void ValidateWorkflowPortableSettings(JsonElement approvedProjectedValues);
}
