using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private sealed record NativePluginOwnerIdentity(NativePluginManagementRequest Request,
        ConnectionPreferences Connection, string Endpoint, int Source, long Selection, DateTimeOffset LiveAt)
    {
        internal bool Invalidated { get; set; }
    }
    private sealed class NativePluginManagementReview(NativePluginOwnerIdentity identity, Border view,
        TextBlock state, Button approve, Button decline, string name, string version)
    {
        internal NativePluginOwnerIdentity Identity { get; } = identity;
        internal Border View { get; } = view;
        internal TextBlock State { get; } = state;
        internal Button Approve { get; } = approve;
        internal Button Decline { get; } = decline;
        internal string Name { get; } = name;
        internal string Version { get; } = version;
        internal bool Busy { get; set; }
        internal string? ClaimId { get; set; }
        internal bool RejectedBeforeClaim { get; set; }
        internal long? ObservedEffectRevision { get; set; }
    }

    private readonly StackPanel nativePluginManagementReviewsHost = new();
    private readonly Dictionary<string, NativePluginManagementReview> nativePluginManagementReviews = new(StringComparer.Ordinal);
    private readonly HashSet<string> nativePluginManagementSeen = new(StringComparer.Ordinal);
    private readonly Queue<string> nativePluginManagementSeenOrder = new();
    private DateTimeOffset nativePluginManagementLiveAt = DateTimeOffset.MaxValue;

    private void InitializeNativePluginManagement()
    {
        AutomationProperties.SetAutomationId(nativePluginManagementReviewsHost, "NativePluginManagementReviews");
        AutomationProperties.SetName(nativePluginManagementReviewsHost, "Installed plugin changes awaiting review");
        ServiceStatusChanged += (_, _) => RenderNativePluginManagementReviews();
        if (nativeChat is not null)
            nativeChat.SnapshotChanged += (_, _) => InvalidateNativePluginManagementSnapshot(nativeChat.Snapshot);
        Closed += (_, _) => ResetNativePluginManagementLiveAdmission();
    }

    private void ResetNativePluginManagementLiveAdmission()
    {
        // Only live effects arriving after this connection is ready are eligible. Hydration, a
        // restarted desktop, and navigation never search for or replay saved pending/claimed work.
        nativePluginManagementLiveAt = IsConnected && !Lifetime.IsCancellationRequested
            ? DateTimeOffset.UtcNow : DateTimeOffset.MaxValue;
        foreach (var review in nativePluginManagementReviews.Values) RevokeNativePluginManagementReview(review);
        nativePluginManagementReviews.Clear();
        nativePluginManagementSeen.Clear();
        nativePluginManagementSeenOrder.Clear();
        nativePluginManagementReviewsHost.Children.Clear();
        interfaceRequests.Children.Remove(nativePluginManagementReviewsHost);
    }

    // A terminal receipt retires its review card without changing the owner of the captured
    // read-back. Card admission, deadline, and profile validity are separate from this diagnostic.
    private bool NativePluginOwnerProjectionIsCurrent(NativePluginOwnerIdentity identity) =>
        IsConnected && ServiceIsLocal && !Lifetime.IsCancellationRequested &&
        ReferenceEquals(identity.Connection, ServiceConnection) && identity.Endpoint == ServiceConnectionIdentity &&
        identity.Source == knowledgeConnectionVersion && identity.Selection == conversationSelectionVersion &&
        identity.LiveAt == nativePluginManagementLiveAt && CurrentConversationId == identity.Request.SessionId;

    private bool NativePluginOwnerIsCurrent(NativePluginOwnerIdentity identity) =>
        !identity.Invalidated && NativePluginOwnerProjectionIsCurrent(identity) &&
        identity.Request.CreatedAt >= identity.LiveAt && identity.Request.ExpiresAt > DateTimeOffset.UtcNow &&
        TryConversationProfile() is { } profile && profile.Id == identity.Request.ProfileId &&
        profile.Revision == identity.Request.ProfileRevision && profile.Role == "lead";

    private void RequireNativePluginOwnerCurrent(NativePluginOwnerIdentity identity)
    {
        if (!NativePluginOwnerIsCurrent(identity))
            throw new InvalidOperationException("The selected conversation, connection, profile, or review deadline changed. No new plugin action was delivered.");
    }

    private static bool SameNativePluginManagementEffect(NativePluginManagementRequest left,
        NativePluginManagementRequest right) =>
        left.SchemaVersion == right.SchemaVersion && left.Id == right.Id && left.SessionId == right.SessionId &&
        left.ProfileId == right.ProfileId && left.ProfileRevision == right.ProfileRevision &&
        left.JobId == right.JobId && left.Operation == right.Operation && left.Query == right.Query &&
        left.CatalogFingerprint == right.CatalogFingerprint && left.CatalogRequestId == right.CatalogRequestId &&
        left.Selection == right.Selection &&
        left.Enabled == right.Enabled && left.CreatedAt == right.CreatedAt && left.ExpiresAt == right.ExpiresAt;

    private static void RequireSameNativePluginManagementEffect(NativePluginManagementRequest expected,
        NativePluginManagementRequest actual)
    {
        if (!SameNativePluginManagementEffect(expected, actual))
            throw new InvalidOperationException("The retained plugin-management effect differs from the reviewed request.");
    }

    private async Task NativePluginManagementEventAsync(ServiceEvent item)
    {
        if (item.Type == "workflow.updated")
        {
            var obsolete = nativePluginManagementReviews.Values.Where(review =>
                NativePluginOwnerIsCurrent(review.Identity) &&
                NativePluginManagementWorkflowInvalidates(review.Identity.Request, item)).ToArray();
            foreach (var review in obsolete)
            {
                RevokeNativePluginManagementReview(review);
                nativePluginManagementReviews.Remove(review.Identity.Request.Id);
            }
            if (obsolete.Length > 0) RenderNativePluginManagementReviews();
            return;
        }
        if (item.Type != "desktop.plugins.management.updated") return;
        NativePluginOwnerIdentity? identity = null;
        try
        {
            var pending = item.Data.Deserialize<NativePluginManagementRequest>(CerebralJson.Options);
            if (pending is null || pending.SchemaVersion != 1 || item.JobId != pending.JobId) return;
            if (pending.Status != "pending")
            {
                if (nativePluginManagementReviews.TryGetValue(pending.Id, out var prior) &&
                    SameNativePluginManagementEffect(prior.Identity.Request, pending) &&
                    (pending.Status is "completed" or "rejected" or "uncertain" or "cancelled" or "expired" || pending.Status == "claimed" && !prior.Busy))
                {
                    RevokeNativePluginManagementReview(prior);
                    nativePluginManagementReviews.Remove(pending.Id);
                    RenderNativePluginManagementReviews();
                }
                return;
            }
            if (pending.Operation is not ("list" or "set-enabled") ||
                pending.Id.Length != 32 || !Guid.TryParseExact(pending.Id, "N", out _) ||
                pending.CreatedAt < nativePluginManagementLiveAt || item.At < nativePluginManagementLiveAt ||
                pending.ExpiresAt <= DateTimeOffset.UtcNow || pending.SessionId != CurrentConversationId ||
                !ServiceIsLocal || !IsConnected || nativePluginManagementSeen.Contains(pending.Id)) return;

            identity = new NativePluginOwnerIdentity(pending, ServiceConnection, ServiceConnectionIdentity,
                knowledgeConnectionVersion, conversationSelectionVersion, nativePluginManagementLiveAt);
            RequireNativePluginOwnerCurrent(identity);
            nativePluginManagementSeen.Add(pending.Id); nativePluginManagementSeenOrder.Enqueue(pending.Id);
            while (nativePluginManagementSeenOrder.Count > 256)
                nativePluginManagementSeen.Remove(nativePluginManagementSeenOrder.Dequeue());

            if (pending.Operation == "list")
            {
                await ApplyNativePluginManagementAsync(identity);
                return;
            }

            if (pending.Selection is null || pending.Enabled is null || string.IsNullOrEmpty(pending.CatalogFingerprint))
                throw new InvalidOperationException("The installed plugin change has no exact catalog selection.");
            var snapshot = desktopPluginCatalog.GetManagementSnapshot(pending.Selection.Id, desktopPlugins?.Status);
            var selected = RequireNativePluginSelection(snapshot, pending);
            if (nativePluginManagementReviews.Count >= 8)
                throw new InvalidOperationException("The desktop already has eight live plugin reviews. Complete one before requesting another.");
            AddNativePluginManagementReview(identity, selected);
        }
        catch (Exception error)
        {
            if (identity is not null && identity.Request.Operation == "set-enabled" &&
                !nativePluginManagementReviews.ContainsKey(identity.Request.Id) && NativePluginOwnerIsCurrent(identity))
                await RejectNativePluginManagementPreconditionAsync(identity, error);
            if (identity is null || CurrentConversationId == identity.Request.SessionId)
                SetStatus("Installed plugin request: " + NativePluginManagementError(error), true);
        }
    }

    private async Task RejectNativePluginManagementPreconditionAsync(NativePluginOwnerIdentity identity, Exception failure)
    {
        try
        {
            RequireNativePluginOwnerCurrent(identity);
            using var owner = CreateIndependentClient();
            var request = identity.Request;
            var claimId = Guid.NewGuid().ToString("N");
            var claim = await owner.GetAsync<NativePluginManagementClaim>("interface.native-plugins.claim",
                new { id = request.Id, sessionId = request.SessionId, claimId }, cancellationToken: Lifetime.Token);
            if (!claim.Claimed) return;
            RequireSameNativePluginManagementEffect(request, claim.Request);
            if (claim.Request.Status != "claimed" || claim.Request.ClaimId != claimId)
                throw new InvalidOperationException("The service did not retain this exact precondition-failure claim.");
            // Claiming solely to record a precondition rejection does not approve a native action.
            // No ready preflight, setting write, plugin load, or retry occurs in this lane.
            var rejected = await owner.GetAsync<NativePluginManagementRequest>("interface.native-plugins.complete",
                new { id = request.Id, sessionId = request.SessionId, claimId, status = "rejected",
                    result = (NativePluginManagementResult?)null, error = NativePluginManagementError(failure) },
                cancellationToken: Lifetime.Token);
            RequireSameNativePluginManagementEffect(request, rejected);
            if (rejected.Status != "rejected" || rejected.ClaimId != claimId)
                throw new InvalidOperationException("The service did not retain this exact precondition rejection.");
        }
        catch (Exception retentionError)
        {
            if (CurrentConversationId == identity.Request.SessionId)
                SetStatus("Plugin precondition failure was not confirmed: " + NativePluginManagementError(retentionError) +
                    " Check the retained request status; no native action was delivered.", true);
        }
    }

    private static NativePluginCatalogEntry RequireNativePluginSelection(NativePluginCatalogSnapshot snapshot,
        NativePluginManagementRequest request)
    {
        var selection = request.Selection ?? throw new InvalidOperationException("No installed plugin was selected.");
        var selected = snapshot.Plugins.SingleOrDefault(item => item.Id == selection.Id) ??
            throw new InvalidOperationException("The selected installed plugin is no longer in the catalog.");
        if (snapshot.CatalogFingerprint != request.CatalogFingerprint || selected.Enabled != selection.ExpectedEnabled ||
            selected.Generation != selection.Generation || selected.PackageFingerprint != selection.PackageFingerprint)
            throw new InvalidOperationException("The installed plugin catalog changed after listing. Request a fresh installed-plugin list before changing it.");
        if (request.Enabled == true && selected.Error is not null)
            throw new InvalidOperationException("The selected installed plugin needs attention: " + selected.Error);
        return selected;
    }

    private void AddNativePluginManagementReview(NativePluginOwnerIdentity identity, NativePluginCatalogEntry selected)
    {
        var request = identity.Request;
        var card = new StackPanel();
        card.Children.Add(Ui.Text(request.Enabled == true ? "Enable installed plugin" : "Disable installed plugin", 16));
        card.Children.Add(Ui.Text(selected.Name + " " + selected.Version, 14));
        card.Children.Add(Ui.Text("Plugin ID: " + selected.Id, 12, Ui.Muted));
        card.Children.Add(Ui.Text("Currently " + (selected.Enabled ? "enabled" : "disabled") +
            " · " + (selected.Loaded ? "loaded" : "not loaded"), 12, Ui.Muted));
        card.Children.Add(Ui.Text(request.Enabled == true
            ? DesktopPluginCatalog.NativeTrustNotice + "\nEnabling an exclusive provider can disable its installed alternatives."
            : "Disabling stops this plugin's contributions. An enabled dependent can prevent the change.", 12, Ui.Muted));
        var state = Ui.Text("Awaiting your review. No installed setting has changed.", 12, Ui.Muted);
        card.Children.Add(state);
        var approve = Ui.Button(request.Enabled == true ? "Enable plugin" : "Disable plugin",
            () => Run(() => ApproveNativePluginManagementAsync(request.Id)), true);
        var decline = Ui.Button("Cancel change", () => Run(() => DeclineNativePluginManagementAsync(request.Id)));
        AutomationProperties.SetAutomationId(approve, "ApproveNativePluginChange-" + request.Id);
        AutomationProperties.SetAutomationId(decline, "DeclineNativePluginChange-" + request.Id);
        card.Children.Add(Ui.Bar(approve, decline));
        var view = new Border
        {
            Child = card, Padding = new Thickness(12), BorderThickness = new Thickness(1),
            BorderBrush = Ui.Accent, Background = Ui.Surface, CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 3, 0, 6)
        };
        AutomationProperties.SetAutomationId(view, "NativePluginChange-" + request.Id);
        nativePluginManagementReviews.Add(request.Id, new(identity, view, state, approve, decline, selected.Name, selected.Version));
        RenderNativePluginManagementReviews();
        _ = ExpireNativePluginManagementReviewAsync(request.Id, request.ExpiresAt);
    }

    private void RenderNativePluginManagementReviews()
    {
        if (Lifetime.IsCancellationRequested)
        {
            nativePluginManagementReviewsHost.Children.Clear();
            interfaceRequests.Children.Remove(nativePluginManagementReviewsHost);
            return;
        }
        foreach (var entry in nativePluginManagementReviews.ToArray())
            if (!entry.Value.Busy && !NativePluginOwnerIsCurrent(entry.Value.Identity))
            {
                RevokeNativePluginManagementReview(entry.Value);
                nativePluginManagementReviews.Remove(entry.Key);
            }
        var visible = nativePluginManagementReviews.Values.Where(review => NativePluginOwnerIsCurrent(review.Identity))
            .Select(review => review.View).ToArray();
        foreach (var child in nativePluginManagementReviewsHost.Children.OfType<Border>().ToArray())
            if (!visible.Contains(child)) nativePluginManagementReviewsHost.Children.Remove(child);
        foreach (var view in visible)
            if (!nativePluginManagementReviewsHost.Children.Contains(view)) nativePluginManagementReviewsHost.Children.Add(view);
        if (nativePluginManagementReviewsHost.Children.Count == 0)
            interfaceRequests.Children.Remove(nativePluginManagementReviewsHost);
        else if (!interfaceRequests.Children.Contains(nativePluginManagementReviewsHost))
            interfaceRequests.Children.Insert(0, nativePluginManagementReviewsHost);
    }

    private async Task ExpireNativePluginManagementReviewAsync(string id, DateTimeOffset expiresAt)
    {
        try
        {
            var remaining = expiresAt - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, Lifetime.Token);
            if (nativePluginManagementReviews.TryGetValue(id, out var expired))
            {
                RevokeNativePluginManagementReview(expired);
                nativePluginManagementReviews.Remove(id);
                RenderNativePluginManagementReviews();
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
    }

    private async Task ApproveNativePluginManagementAsync(string id)
    {
        if (!nativePluginManagementReviews.TryGetValue(id, out var review) || review.Busy) return;
        RequireNativePluginOwnerCurrent(review.Identity);
        // The card's exact tuple is checked again before claiming, then by the catalog under its
        // publication lease. A review of an old generation cannot approve a newer installation.
        _ = RequireNativePluginSelection(desktopPluginCatalog.GetManagementSnapshot(
            review.Identity.Request.Selection!.Id, desktopPlugins?.Status), review.Identity.Request);
        review.Busy = true; review.Approve.IsEnabled = review.Decline.IsEnabled = false;
        review.State.Text = "Applying the reviewed installed-plugin change…";
        await ApplyNativePluginManagementAsync(review.Identity, review);
    }

    private async Task DeclineNativePluginManagementAsync(string id)
    {
        if (!nativePluginManagementReviews.TryGetValue(id, out var review) || review.Busy) return;
        RequireNativePluginOwnerCurrent(review.Identity);
        review.Busy = true; review.Approve.IsEnabled = review.Decline.IsEnabled = false;
        using var owner = CreateIndependentClient();
        try
        {
            var rejected = await owner.GetAsync<NativePluginManagementRequest>("interface.native-plugins.reject",
                new { id, sessionId = review.Identity.Request.SessionId }, cancellationToken: Lifetime.Token);
            RequireSameNativePluginManagementEffect(review.Identity.Request, rejected);
            if (rejected.Status != "rejected")
                throw new InvalidOperationException("The service did not retain the owner-declined result.");
            nativePluginManagementReviews.Remove(id); RenderNativePluginManagementReviews();
            SetStatus("The installed plugin change was declined. No plugin setting was changed.");
        }
        catch (Exception error)
        {
            review.State.Text = "Decline was not confirmed: " + NativePluginManagementError(error) +
                " Check the retained request status; this card will not repeat an action.";
            SetStatus(review.State.Text, true);
        }
    }

    private async Task ApplyNativePluginManagementAsync(NativePluginOwnerIdentity identity,
        NativePluginManagementReview? review = null)
    {
        var pending = identity.Request;
        using var owner = CreateIndependentClient();
        var claimId = Guid.NewGuid().ToString("N");
        if (review is not null) review.ClaimId = claimId;
        NativePluginManagementClaim claim;
        try
        {
            RequireNativePluginOwnerCurrent(identity);
            claim = await owner.GetAsync<NativePluginManagementClaim>("interface.native-plugins.claim",
                new { id = pending.Id, sessionId = pending.SessionId, claimId }, cancellationToken: Lifetime.Token);
        }
        catch (Exception error)
        {
            if (review is not null) review.State.Text = "Claim was not confirmed. No native action was delivered. Check the retained request status.";
            SetStatus("Installed plugin claim: " + NativePluginManagementError(error), true);
            return; // An unconfirmed claim is never blindly retried.
        }
        if (!claim.Claimed)
        {
            RequireSameNativePluginManagementEffect(pending, claim.Request);
            if (review is not null)
                review.RejectedBeforeClaim = claim.Request.Status == "rejected" && claim.Request.ClaimId is null;
            nativePluginManagementReviews.Remove(pending.Id); RenderNativePluginManagementReviews();
            return;
        }

        var status = "rejected";
        string? errorMessage = null;
        NativePluginManagementResult? result = null;
        var nativeEffectStarted = false;
        try
        {
            RequireSameNativePluginManagementEffect(pending, claim.Request);
            if (claim.Request.Status != "claimed" || claim.Request.ClaimId != claimId)
                throw new InvalidOperationException("The service did not return the exact native plugin claim.");
            RequireNativePluginOwnerCurrent(identity);
            var ready = await owner.GetAsync<NativePluginManagementRequest>("interface.native-plugins.ready",
                new { id = pending.Id, sessionId = pending.SessionId, claimId }, cancellationToken: Lifetime.Token);
            RequireSameNativePluginManagementEffect(pending, ready);
            if (ready.Status != "claimed" || ready.ClaimId != claimId)
                throw new InvalidOperationException("The service did not retain the exact preflight claim.");
            RequireNativePluginOwnerCurrent(identity);
            if (pending.Operation == "list")
            {
                result = new(desktopPluginCatalog.GetManagementSnapshot(pending.Query, desktopPlugins?.Status), []);
            }
            else
            {
                var runtime = desktopPlugins ?? throw new InvalidOperationException("The native desktop plugin runtime is unavailable.");
                _ = RequireNativePluginSelection(desktopPluginCatalog.GetManagementSnapshot(pending.Selection!.Id, runtime.Status), pending);
                result = desktopPluginCatalog.SetEnabledExact(pending.CatalogFingerprint!, pending.Selection,
                    pending.Enabled!.Value, runtime.Status);
                nativeEffectStarted = result.ChangedPluginIds.Count > 0;
                // Call the runtime directly: the convenience ReloadDesktopPlugins wrapper reports and
                // swallows errors, which cannot substantiate a completed management receipt.
                nativeEffectStarted = true;
                runtime.LoadEnabled(); MountDesktopPlugins();
                var observed = ObserveNativePluginManagementResult(result, pending.Selection.Id, runtime);
                result = observed;
                RequireNativePluginOwnerCurrent(identity);
                VerifyNativePluginManagementLoadedState(result, pending, runtime);
            }
            status = "completed";
        }
        catch (Exception error)
        {
            if (error is DesktopPluginManagementMutationException)
            {
                nativeEffectStarted = true;
                // Reconcile the runtime to the settings that actually survived, without retrying
                // any catalog write. If observation failed, no changed-ID list is invented.
                try { desktopPlugins?.LoadEnabled(); MountDesktopPlugins(); } catch { }
            }
            status = nativeEffectStarted ? "uncertain" : "rejected";
            errorMessage = NativePluginManagementError(error);
            if (nativeEffectStarted && desktopPlugins is { } runtime && result is not null)
            {
                try { result = ObserveNativePluginManagementResult(result, pending.Selection!.Id, runtime); }
                catch { /* Preserve the last actual observation; an uncertain outcome is not retried. */ }
            }
        }
        try
        {
            var completed = await owner.GetAsync<NativePluginManagementRequest>("interface.native-plugins.complete",
                new { id = pending.Id, sessionId = pending.SessionId, claimId, status, result, error = errorMessage },
                cancellationToken: Lifetime.Token);
            RequireSameNativePluginManagementEffect(pending, completed);
            if (completed.Status != status || completed.ClaimId != claimId)
                throw new InvalidOperationException("The retained plugin outcome did not match this exact claim.");
            nativePluginManagementReviews.Remove(pending.Id); RenderNativePluginManagementReviews();
            if (pending.Operation == "set-enabled" && CurrentConversationId == pending.SessionId &&
                ReferenceEquals(identity.Connection, ServiceConnection))
                SetStatus(status == "completed"
                    ? "Installed plugin " + pending.Selection!.Id + " is " + (pending.Enabled == true ? "enabled and loaded." : "disabled and unloaded.")
                    : "Installed plugin change " + status + ": " + errorMessage, status != "completed");
        }
        catch (Exception completionError)
        {
            if (review is not null) review.State.Text = "The action receipt was not confirmed. Check the retained request status; the native action will not be repeated.";
            SetStatus("Installed plugin outcome was not confirmed: " + NativePluginManagementError(completionError), true);
        }
    }

    private NativePluginManagementResult ObserveNativePluginManagementResult(NativePluginManagementResult applied,
        string selectedId, DesktopPluginRuntime runtime)
    {
        var snapshot = desktopPluginCatalog.GetManagementSnapshot(runtimeStatus: runtime.Status);
        if (snapshot.CatalogFingerprint != applied.Catalog.CatalogFingerprint)
            throw new InvalidOperationException("The installed catalog changed again before the native outcome was observed.");
        var affected = applied.ChangedPluginIds.Prepend(selectedId).Distinct(StringComparer.Ordinal).ToArray();
        var byId = snapshot.Plugins.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        foreach (var id in affected.Where(id => !byId.ContainsKey(id)))
        {
            var exact = desktopPluginCatalog.GetManagementSnapshot(id, runtime.Status);
            if (exact.CatalogFingerprint != snapshot.CatalogFingerprint)
                throw new InvalidOperationException("The installed catalog changed during outcome inspection.");
            byId[id] = exact.Plugins.SingleOrDefault(entry => entry.Id == id) ??
                throw new InvalidOperationException("An affected installed plugin disappeared before its outcome could be observed.");
        }
        // Prefer the exact target/changed IDs when the general catalog exceeds its bounded page.
        var entries = affected.Select(id => byId[id]).Concat(snapshot.Plugins)
            .DistinctBy(entry => entry.Id, StringComparer.Ordinal).Take(100).ToArray();
        var total = snapshot.Plugins.Count + snapshot.Omitted;
        return new(new(snapshot.CatalogFingerprint, entries, Math.Max(0, total - entries.Length)), applied.ChangedPluginIds);
    }

    private static void VerifyNativePluginManagementLoadedState(NativePluginManagementResult result,
        NativePluginManagementRequest request, DesktopPluginRuntime runtime)
    {
        foreach (var id in result.ChangedPluginIds.Prepend(request.Selection!.Id).Distinct(StringComparer.Ordinal))
        {
            var entry = result.Catalog.Plugins.SingleOrDefault(item => item.Id == id) ??
                throw new InvalidOperationException("The affected plugin did not fit in the bounded outcome. Its runtime state is unconfirmed.");
            var shouldBeEnabled = id == request.Selection.Id && request.Enabled == true;
            if (entry.Enabled != shouldBeEnabled || entry.Loaded != shouldBeEnabled || entry.Error is not null)
                throw new InvalidOperationException("Plugin " + id + " did not reach the requested runtime state: " +
                    (entry.Error ?? (entry.Loaded ? "still loaded" : "not loaded")));
            var loaded = runtime.GetLoadedPackage(id);
            if (!shouldBeEnabled)
            {
                if (loaded is not null) throw new InvalidOperationException("The disabled plugin still has an active native package.");
                continue;
            }
            if (loaded is null || loaded.Value.Version != entry.Version ||
                NativeDesktopPluginPackage.ComputeFingerprint(Path.Combine(loaded.Value.Directory,
                    DesktopPluginCatalog.ManifestFileName)) != request.Selection.PackageFingerprint)
                throw new InvalidOperationException("The loaded native plugin does not match the exact reviewed package.");
        }
    }



    private static void RevokeNativePluginManagementReview(NativePluginManagementReview review)
    {
        // Retire the captured admission too: an awaited claim/ready handler still holds this
        // identity after the card is removed and must not regain authority from the old controls.
        review.Identity.Invalidated = true;
        review.Approve.IsEnabled = review.Decline.IsEnabled = false;
    }

    private static bool NativePluginManagementWorkflowInvalidates(NativePluginManagementRequest request, ServiceEvent item)
    {
        if (item.Type != "workflow.updated" || item.JobId != request.JobId || item.At < request.CreatedAt ||
            item.Data.ValueKind != JsonValueKind.Object ||
            !item.Data.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != request.JobId ||
            !item.Data.TryGetProperty("conversationId", out var conversation) || conversation.ValueKind != JsonValueKind.String ||
            conversation.GetString() != request.SessionId ||
            !item.Data.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<WorkflowStatus>(status.GetString(), out var value) || !Enum.IsDefined(value)) return false;
        return value != WorkflowStatus.Running;
    }

    private static bool NativePluginManagementSnapshotInvalidates(NativePluginManagementRequest request,
        WorkflowConversationRun run, long? observedRevision)
    {
        if (run.Id != request.JobId || run.ConversationId != request.SessionId || run.ProfileId != request.ProfileId ||
            observedRevision is { } revision && run.Revision < revision) return false;
        // A refreshed snapshot may still describe the preceding effect when a new request event
        // arrives. Only the exact effect or a later revision after its observed dispatch can revoke.
        if (run.EffectId != request.Id && observedRevision is null) return false;
        if (observedRevision is null && run.EffectState is null or WorkflowEffectState.Prepared)
            return run.CancelRequested || run.IsTerminal || run.Status == WorkflowStatus.Cancelling;
        return run.CancelRequested || run.Status != WorkflowStatus.Running || run.EffectId != request.Id ||
            run.EffectState != WorkflowEffectState.Dispatched;
    }

    private void InvalidateNativePluginManagementSnapshot(WorkflowConversationSnapshot? snapshot)
    {
        if (snapshot?.Run is not { } run || snapshot.ConversationId != CurrentConversationId ||
            Lifetime.IsCancellationRequested) return;
        var changed = false;
        foreach (var review in nativePluginManagementReviews.Values.ToArray())
        {
            var request = review.Identity.Request;
            if (!NativePluginOwnerIsCurrent(review.Identity) || request.SessionId != snapshot.ConversationId) continue;
            if (NativePluginManagementSnapshotInvalidates(request, run, review.ObservedEffectRevision))
            {
                RevokeNativePluginManagementReview(review);
                nativePluginManagementReviews.Remove(request.Id); changed = true;
            }
            else if (run.Id == request.JobId && run.ConversationId == request.SessionId && run.ProfileId == request.ProfileId &&
                run.EffectId == request.Id && run.EffectState == WorkflowEffectState.Dispatched &&
                run.Status == WorkflowStatus.Running && !run.CancelRequested)
                review.ObservedEffectRevision = Math.Max(review.ObservedEffectRevision ?? 0, run.Revision);
        }
        if (changed) RenderNativePluginManagementReviews();
    }

    private async Task<DesktopControlResponse> HandleNativePluginManagementControlAsync(
        DesktopControlRequest request, CancellationToken cancellationToken)
    {
        if (request.Operation == "native-plugins.inspect")
        {
            RequireNativePluginManagementControlArguments(request.Arguments, "limit");
            var limit = OptionalInt(request.Arguments, "limit", 1, 8) ?? 8;
            // Observe the existing tree only. Showing the window or hydrating a request here could
            // admit old work, change selection, or establish a new connection during an inspection.
            var reviews = nativePluginManagementReviews.Values.Where(NativePluginManagementReviewIsPresented).ToArray();
            return DesktopControlProtocol.Result(request, "completed", new
            {
                conversationId = CurrentConversationId,
                reviews = reviews.Take(limit).Select(NativePluginManagementReviewControlSnapshot).ToArray(),
                omitted = Math.Max(0, reviews.Length - limit)
            });
        }

        RequireNativePluginManagementControlArguments(request.Arguments, "id", "approved", "sessionId");
        var id = RequiredString(request.Arguments, "id", 32);
        if (!Guid.TryParseExact(id, "N", out var parsedId) || parsedId == Guid.Empty)
            throw new ArgumentException("A nonempty native plugin request ID in GUID N format is required.");
        var approved = RequiredBool(request.Arguments, "approved");
        var sessionId = request.Arguments.TryGetProperty("sessionId", out var sessionValue)
            ? JsonString(sessionValue, "sessionId", 128, allowEmpty: false) : CurrentConversationId;
        if (sessionId is null || sessionId != CurrentConversationId)
            throw new InvalidOperationException("Select the exact local conversation before reviewing its installed plugin change.");
        if (!nativePluginManagementReviews.TryGetValue(id, out var review) ||
            review.Identity.Request.SessionId != sessionId || !NativePluginManagementReviewIsPresented(review))
            throw new InvalidOperationException("The selected conversation has no current mounted installed-plugin review card.");
        if (review.Busy)
            throw new InvalidOperationException("This installed-plugin review is already working or awaiting confirmation. Inspect its retained request; do not repeat the action.");
        var control = approved ? review.Approve : review.Decline;
        if (!NativePluginManagementControlIsPresented(review, control) || !control.IsEnabled)
            throw new InvalidOperationException("The requested installed-plugin review control is unavailable or disabled.");

        var identity = review.Identity;
        RequireNativePluginOwnerCurrent(identity);
        cancellationToken.ThrowIfCancellationRequested();
        // Capture this exact owner before awaiting either handler. A later navigation or reconnect
        // must never redirect the retained read to a different Worker or conversation.
        using var owner = CreateIndependentClient();
        try
        {
            if (approved) await ApproveNativePluginManagementAsync(id);
            else await DeclineNativePluginManagementAsync(id);
        }
        catch (Exception error) when (!review.Busy)
        {
            // The shared visible handler rejected its preconditions before claiming or writing.
            return DesktopControlProtocol.Result(request, "rejected",
                new { id, sessionId, pluginId = identity.Request.Selection!.Id, desiredEnabled = identity.Request.Enabled },
                NativePluginManagementError(error));
        }

        catch (Exception)
        {
            // The handler may already have claimed or changed settings. Its retained receipt,
            // rather than an exception from UI projection, remains the source of truth below.
        }

        try
        {
            var retained = await owner.GetAsync<NativePluginManagementRequest>("interface.native-plugins.get",
                new { id, sessionId }, cancellationToken: cancellationToken);
            var state = NativePluginManagementRetainedControlState(identity.Request, retained, approved,
                review.ClaimId, review.RejectedBeforeClaim);
            var error = state == "completed" ? null : retained.Error is null
                ? "The native review outcome is not yet confirmed. Inspect this exact retained request; no action was repeated."
                : NativePluginManagementError(new InvalidOperationException(retained.Error));
            return DesktopControlProtocol.Result(request, state, new
            {
                id = retained.Id, sessionId = retained.SessionId, runId = retained.JobId,
                pluginId = retained.Selection!.Id, desiredEnabled = retained.Enabled,
                status = retained.Status,
                result = NativePluginManagementResultControlSnapshot(retained.Result),
                projectionWarning = NativePluginOwnerProjectionIsCurrent(identity) ? null
                    : "The desktop selection or connection changed. This outcome belongs to the original captured owner."
            }, error);
        }
        catch (Exception error)
        {
            // Even if the button handler finished, a lost/mismatched receipt cannot prove outcome.
            // No retry, readiness call, native write, or reconnect occurs in the read-back lane.
            return DesktopControlProtocol.Result(request, "uncertain", new
            {
                id, sessionId, runId = identity.Request.JobId,
                pluginId = identity.Request.Selection!.Id, desiredEnabled = identity.Request.Enabled
            }, "The exact retained plugin outcome could not be confirmed: " + NativePluginManagementError(error));
        }
    }

    private static void RequireNativePluginManagementControlArguments(JsonElement arguments, params string[] allowed)
    {
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            if (!names.Contains(property.Name) || !seen.Add(property.Name))
                throw new ArgumentException("Use only the exact installed-plugin control arguments, once each.");
    }

    private bool NativePluginManagementReviewIsPresented(NativePluginManagementReview review) =>
        NativePluginOwnerIsCurrent(review.Identity) &&
        nativePluginManagementReviewsHost.Children.Contains(review.View) &&
        interfaceRequests.Children.Contains(nativePluginManagementReviewsHost) &&
        Tabs.SelectedItem is TabItem { Tag: "chat" } &&
        NativePluginManagementElementIsPresented(review.View, this);

    private static bool NativePluginManagementElementIsPresented(FrameworkElement element, FrameworkElement ancestor)
    {
        // Window hiding/minimizing does not unmount a review. Validate the retained visible layout
        // up to the window without showing or activating it; collapsed tabs/ancestors still fail.
        DependencyObject? current = element;
        for (var depth = 0; current is not null && depth < 64; depth++)
        {
            if (ReferenceEquals(current, ancestor)) return ancestor is Window || ancestor.Visibility == Visibility.Visible;
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
            current = current is FrameworkElement { Parent: { } parent } ? parent
                : current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(current) : null;
        }
        return false;
    }

    private static bool NativePluginManagementControlIsPresented(NativePluginManagementReview review, Button control) =>
        control.Visibility == Visibility.Visible && NativePluginManagementElementIsPresented(control, review.View);

    private object NativePluginManagementReviewControlSnapshot(NativePluginManagementReview review) => new
    {
        id = review.Identity.Request.Id, sessionId = review.Identity.Request.SessionId,
        runId = review.Identity.Request.JobId, pluginId = review.Identity.Request.Selection!.Id,
        name = Bounded(review.Name, 256), version = Bounded(review.Version, 128),
        desiredEnabled = review.Identity.Request.Enabled,
        state = review.Busy ? "working-or-unconfirmed" : "pending",
        message = NativePluginManagementError(new InvalidOperationException(review.State.Text)),
        controls = new
        {
            approve = NativePluginManagementReviewButtonSnapshot(review, review.Approve),
            decline = NativePluginManagementReviewButtonSnapshot(review, review.Decline)
        }
    };

    private static object NativePluginManagementReviewButtonSnapshot(NativePluginManagementReview review, Button control) => new
    {
        id = AutomationProperties.GetAutomationId(control),
        name = Bounded(control.Content as string ?? "", 80),
        enabled = !review.Busy && control.IsEnabled && NativePluginManagementControlIsPresented(review, control)
    };

    private static string NativePluginManagementRetainedControlState(NativePluginManagementRequest expected,
        NativePluginManagementRequest retained, bool approved, string? claimId, bool rejectedBeforeClaim)
    {
        RequireSameNativePluginManagementEffect(expected, retained);
        if (approved)
        {
            if (claimId is null || retained.ClaimId != claimId)
            {
                if (rejectedBeforeClaim && retained.Status == "rejected" && retained.ClaimId is null) return "rejected";
                throw new InvalidOperationException("The retained result does not belong to this exact native review claim.");
            }
        }
        else if (retained.ClaimId is not null || retained.Status == "completed")
            throw new InvalidOperationException("The retained result does not belong to this unclaimed owner decline.");

        if (retained.Status == "completed")
        {
            var target = retained.Result?.Catalog.Plugins.SingleOrDefault(entry => entry.Id == expected.Selection!.Id);
            if (target is null || target.Enabled != expected.Enabled || target.Loaded != expected.Enabled ||
                target.Error is not null || target.Generation != expected.Selection!.Generation ||
                target.PackageFingerprint != expected.Selection.PackageFingerprint || retained.Error is not null)
                throw new InvalidOperationException("The retained completion does not prove the exact requested catalog/runtime state.");
            return "completed";
        }
        return retained.Status is "rejected" or "uncertain" ? retained.Status : "uncertain";
    }

    private static object? NativePluginManagementResultControlSnapshot(NativePluginManagementResult? result) =>
        result is null ? null : new
        {
            catalog = new
            {
                plugins = result.Catalog.Plugins.Take(100).Select(entry => new
                {
                    id = Bounded(entry.Id, 128), name = Bounded(entry.Name, 256), version = Bounded(entry.Version, 128),
                    entry.Enabled, entry.Loaded,
                    error = entry.Error is null ? null : NativePluginManagementError(new InvalidOperationException(entry.Error))
                }).ToArray(),
                omitted = result.Catalog.Omitted + Math.Max(0, result.Catalog.Plugins.Count - 100)
            },
            changedPluginIds = result.ChangedPluginIds
        };

    private static string NativePluginManagementError(Exception error)
    {
        // Native paths are for the Configurator's owner diagnostics, not model-facing catalog data.
        var message = System.Text.RegularExpressions.Regex.Replace(error.Message,
            @"[A-Za-z]:[\\/][^\r\n\x22<>]+", "[local path]");
        message = System.Text.RegularExpressions.Regex.Replace(message,
            @"\\\\[^\\\r\n\x22<>]+\\[^\r\n\x22<>]+", "[local path]");
        message = System.Text.RegularExpressions.Regex.Replace(message,
            @"/(?:home|var|usr|tmp|mnt|opt|Users)/[^\r\n\x22<>]+", "[local path]");
        return message[..Math.Min(message.Length, 1000)];
    }
}
