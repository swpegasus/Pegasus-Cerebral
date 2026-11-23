using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

/// <summary>
/// Ephemeral owner UI state for one exact review snapshot. Consent is never inferred from a request ID alone:
/// a reconnect, conversation generation, request status, reviewed package or manifest change creates a fresh state.
/// </summary>
internal sealed record InterfaceReviewStateIdentity(
    string RequestId,
    string SessionId,
    string JobId,
    string ProfileId,
    string Source,
    string Kind,
    string Status,
    string Path,
    string? PackageFingerprint,
    string? GeneratedSourceRoot,
    DateTimeOffset CreatedAt,
    string? ClaimId,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? CompletedAt,
    bool StopRequested,
    bool RepairScheduled,
    string? Error,
    InterfaceFailureStage? FailureStage,
    string? PluginTestRequestId,
    string? PluginTestStatus,
    string? PluginTestMode,
    string BindingGeneration,
    string? BindingSessionId,
    string ConnectionIdentity,
    int ConnectionVersion,
    string Action,
    string AuthorizationSha256,
    string RequestSha256,
    string ReviewSha256)
{
    public static InterfaceReviewStateIdentity Create(InterfaceRequest request, DesktopConversationBinding binding,
        string connectionIdentity, int connectionVersion, string action, DesktopPluginManifest? manifest,
        DesktopPluginInfo? installedPlugin, Exception? packageReviewError)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        var authorizationSha = AuthorizationHash(request);
        var requestSha = Hash(request);
        var reviewSha = Hash(new
        {
            Manifest = manifest,
            InstalledPlugin = installedPlugin,
            PackageReviewErrorType = packageReviewError?.GetType().FullName,
            PackageReviewErrorMessage = packageReviewError?.Message
        });
        return new(request.Id, request.SessionId, request.JobId, request.ProfileId, request.Source, request.Kind,
            request.Status, request.Path, request.PackageFingerprint, request.GeneratedSourceRoot, request.CreatedAt,
            request.ClaimId, request.ClaimedAt, request.CompletedAt, request.StopRequested, request.RepairScheduled,
            request.Error, request.FailureStage, request.PluginTests?.RequestId, request.PluginTests?.Status,
            request.PluginTests?.Mode, binding.Generation, binding.SessionId, connectionIdentity, connectionVersion,
            action, authorizationSha, requestSha, reviewSha);
    }

    public bool MatchesOwner(DesktopConversationBinding binding, string connectionIdentity, int connectionVersion,
        string? conversationId) =>
        conversationId == SessionId && binding.Generation == BindingGeneration && binding.SessionId == BindingSessionId &&
        connectionIdentity == ConnectionIdentity && connectionVersion == ConnectionVersion;

    public bool MatchesClaimedRequest(InterfaceRequest request) => request.Status == "claimed" &&
        AuthorizationHash(request) == AuthorizationSha256;

    private static string AuthorizationHash(InterfaceRequest request) => Hash(new
    {
        request.Id,
        request.SessionId,
        request.JobId,
        request.ProfileId,
        request.Source,
        request.Kind,
        request.Path,
        request.PackageFingerprint,
        request.GeneratedSourceRoot,
        request.CreatedAt,
        request.CompletedAt,
        request.ProcessId,
        request.ProcessStartedAt,
        request.ProcessStatus,
        request.ExitCode,
        request.Output,
        request.StopRequested,
        request.RepairScheduled,
        request.Error,
        request.FailureStage,
        request.Result,
        request.Resource,
        request.Plugin,
        request.PluginTests
    });

    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options)));
}

internal sealed class InterfaceReviewRefreshGate
{
    private long generation;

    public long Begin() => Interlocked.Increment(ref generation);

    public bool IsCurrent(long value) => Volatile.Read(ref generation) == value;
}

internal sealed class InterfaceReviewCardState(InterfaceReviewStateIdentity identity, bool isExpanded)
{
    public InterfaceReviewStateIdentity Identity { get; } = identity;
    public bool IsExpanded { get; set; } = isExpanded;
    public bool ControlTestsAllowed { get; set; }
}

internal sealed class InterfaceReviewStateStore
{
    private readonly Dictionary<string, InterfaceReviewCardState> states = new(StringComparer.Ordinal);

    public InterfaceReviewCardState? Current(string requestId) =>
        states.TryGetValue(requestId, out var state) ? state : null;

    public InterfaceReviewCardState Resolve(InterfaceReviewStateIdentity identity, bool defaultExpanded)
    {
        if (states.TryGetValue(identity.RequestId, out var existing) && existing.Identity == identity) return existing;
        var state = new InterfaceReviewCardState(identity, defaultExpanded);
        states[identity.RequestId] = state;
        return state;
    }

    public void RetainOnly(IEnumerable<string> requestIds)
    {
        var retained = requestIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in states.Keys.Where(id => !retained.Contains(id)).ToArray()) states.Remove(id);
    }

    public void Clear() => states.Clear();
}

internal static class InterfaceReviewStateAdapter
{
    public static void Bind(Expander expander, CheckBox? controlTests, InterfaceReviewCardState state)
    {
        ArgumentNullException.ThrowIfNull(expander);
        ArgumentNullException.ThrowIfNull(state);
        expander.IsExpanded = state.IsExpanded;
        expander.Expanded += (_, _) => state.IsExpanded = true;
        expander.Collapsed += (_, _) => state.IsExpanded = false;
        if (controlTests is null) return;
        controlTests.IsChecked = state.ControlTestsAllowed;
        controlTests.Checked += (_, _) => state.ControlTestsAllowed = true;
        controlTests.Unchecked += (_, _) => state.ControlTestsAllowed = false;
    }

    public static void BindConsentAction(CheckBox controlTests, Button action)
    {
        ArgumentNullException.ThrowIfNull(controlTests);
        ArgumentNullException.ThrowIfNull(action);
        action.IsEnabled = controlTests.IsChecked == true;
        controlTests.Checked += (_, _) => action.IsEnabled = true;
        controlTests.Unchecked += (_, _) => action.IsEnabled = false;
    }
}
