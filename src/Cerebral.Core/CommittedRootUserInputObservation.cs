using Cerebral.Contracts.Workflows;

namespace Cerebral.Core;

/// <summary>Body-free coordinates for host-owned package, enable and profile-scope checks.</summary>
public sealed record CommittedRootUserInputSource(
    string RequestId,
    string ConversationId,
    string ProfileId,
    string DefinitionId,
    string DefinitionVersion,
    string? ProjectId,
    string EventId,
    long EventSequence);

/// <summary>
/// Exposed only after the host has resolved the current package, explicit opt-in and
/// source-profile scope. It carries the accepted UserText, never accepted attachments,
/// history, assignment text, tool output or plugin settings.
/// </summary>
public sealed record CommittedRootUserInputObservation(
    CommittedRootUserInputSource Source,
    string UserText);

/// <summary>
/// Optional service-plugin side interface. The existing IServicePlugin and
/// IServicePluginHost ABI remains unchanged. The host owns asynchronous delivery and
/// keeps the loaded instance alive until this callback actually completes.
/// </summary>
public interface IServiceCommittedRootUserInputObserver
{
    ValueTask ObserveCommittedRootUserInputAsync(
        CommittedRootUserInputObservation observation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional host facet obtained by an observer plugin from its existing IServicePluginHost.
/// A newly loaded instance starts disabled with no source profiles. The plugin must
/// explicitly register or replace its subscription after Initialize/settings admission;
/// Core does not persist it or infer it from package-owned settings. The host copies
/// and validates this metadata before atomic publication. Invalid input makes no
/// partial change. Initialization failure clears the unpublished Host. Retirement
/// refuses new acquisitions while active callback leases remain owned until their
/// tasks actually finish. No observer delivery occurs if this facet is absent.
/// </summary>
public interface IServiceCommittedRootUserInputHost
{
    /// <summary>
    /// Enable only for the listed exact, ordinal source profile IDs. No wildcard or
    /// all-profiles value exists. Each ID uses WorkflowStore.Identifier grammar:
    /// 1..128 UTF-16 characters without whitespace, control characters or NUL.
    /// Capture no more than 1025 IDs before rejecting an oversized caller collection;
    /// never trust Count as a bound for an unbounded copy. Reject nulls, duplicates,
    /// enabled with an empty list, more than 1024 IDs, or
    /// aggregate serialized UTF-8 metadata above
    /// SettingsPreferenceContract.MaximumPluginValuesUtf8Bytes. Disabled with an
    /// empty list is valid. Copy and validate outside host lifetime gates, then
    /// recheck retirement/disposal under the same gate used for publication and
    /// callback acquisition. Acquisition is the linearization point: disabling
    /// prevents new leases but never retires an already admitted callback early.
    /// </summary>
    void SetCommittedRootUserInputSubscription(bool enabled, IReadOnlyList<string> sourceProfileIds);
}
