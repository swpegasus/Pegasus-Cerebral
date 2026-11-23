using System.Text.Json;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Core;

// Private optional facet candidate; existing IServicePlugin/Host ABI is unchanged.

/// <summary>The package chooses its own bounded prompt text and opaque input.
/// UserText must satisfy the existing WorkflowStore nonblank submission bound;
/// the host does not substitute title-specific wording or accepted attachments.</summary>
public sealed record SourceBoundBackgroundIntent(
    string DefinitionId, string DefinitionVersion, string WorkRequestId,
    string WorkConversationId, string ProfileId, long ExpectedProfileRevision,
    long ExpectedSettingsRevision, string UserText,
    JsonElement PackageInput, int StepLimit);

public enum SourceBoundBackgroundAdmissionState
{
    Admitted, Existing, Missing, Uncertain
}

public sealed record SourceBoundBackgroundAdmission(
    string WorkRequestId, SourceBoundBackgroundAdmissionState State,
    WorkflowStatus? WorkflowStatus, long? RunRevision);

/// <summary>Admit returns after the exact existing WorkflowStore commit, not after
/// generation or rename. Inspect reads one retained RID; it never retries work.</summary>
public interface IServiceSourceBoundBackgroundHost
{
    /// <summary>Replace the exact source-profile subscription only if the
    /// package's current central settings revision equals the supplied value.
    /// Callback acquisition rechecks that same revision before UserText delivery.
    /// The older generic observation facet remains unchanged.</summary>
    void SetSourceBoundSubscription(bool enabled,
        IReadOnlyList<string> sourceProfileIds, long expectedSettingsRevision);
    ValueTask<SourceBoundBackgroundAdmission> AdmitAsync(
        CommittedRootUserInputObservation source, SourceBoundBackgroundIntent intent,
        CancellationToken cancellationToken);
    ValueTask<SourceBoundBackgroundAdmission> InspectAsync(
        string workRequestId, CancellationToken cancellationToken);
}

/// <summary>After a committed central settings change, the exact loaded package
/// rereads its existing IServicePluginSettingsHost.Settings and republishes its
/// optional input subscription. No settings-policy Admit side effects are needed.</summary>
public interface IServiceOwnSettingsRefresh
{
    ValueTask RefreshOwnSettingsAsync(CancellationToken cancellationToken);
}
