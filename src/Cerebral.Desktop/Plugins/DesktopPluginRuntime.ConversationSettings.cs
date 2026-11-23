using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Cerebral.Desktop.Plugins;

/// <summary>A lazy editor factory owned by one exact loaded native plugin generation.</summary>
public sealed record DesktopHostedConversationSettings(string PluginId, string Id, string Title,
    Func<DesktopConversationSettingsContext, CancellationToken, Task<IDesktopConversationSettingsEditor>> OpenAsync);

public sealed partial class DesktopPluginRuntime
{
    private Dispatcher SettingsDispatcher => dispatcher;

    private void ReportSettingsFailure(string id, string message)
    {
        try { Report(id, message); }
        catch (Exception error) when (SettingsFailure(error))
        { /* Observational diagnostics must not prevent cancellation or the remaining editor/plugin cleanup. */ }
    }

    private static bool SettingsFailure(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static bool SettingsIdentifier(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    private static IReadOnlyList<DesktopConversationSettingsContribution> ValidateConversationSettings(IReadOnlyList<DesktopConversationSettingsContribution> settings)
    {
        var count = settings.Count;
        if (count is < 0 or > DesktopConversationSettingsContract.MaximumContributionsPerPlugin)
            throw new InvalidDataException("A native plugin can contribute at most eight conversation-settings editors.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var frozen = new DesktopConversationSettingsContribution[count];
        for (var index = 0; index < frozen.Length; index++)
        {
            var item = settings[index];
            if (item is null || item.ContractVersion != DesktopConversationSettingsContract.Version ||
                !SettingsIdentifier(item.Id, DesktopConversationSettingsContract.MaximumContributionIdChars) ||
                !seen.Add(item.Id) || !SettingsIdentifier(item.Title, DesktopConversationSettingsContract.MaximumTitleChars) ||
                item.OpenAsync is null)
                throw new InvalidDataException("Conversation settings require the exact supported version, unique bounded IDs, titles, and lazy editor factories.");
            frozen[index] = item;
        }
        return frozen;
    }

    private void RequireSettingsOwner(LoadedPlugin owner)
    {
        dispatcher.VerifyAccess();
        if (disposed || host.Surface != DesktopPluginSurface.Orchestrator ||
            !loaded.TryGetValue(owner.Id, out var current) || !ReferenceEquals(current, owner) ||
            owner.Lifetime.IsCancellationRequested)
            throw new InvalidOperationException("The contributing native plugin instance is no longer available.");
    }

    private static void ValidateSettingsContext(DesktopConversationSettingsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!SettingsIdentifier(context.ConversationId, 128) || !SettingsIdentifier(context.ProfileId, 128) ||
            context.Title is null || context.Title.Length > 1_024 || context.Title.Contains('\0') ||
            context.ProjectId is not null && !SettingsIdentifier(context.ProjectId, 128) ||
            context.WorkingDirectory is not null &&
                (string.IsNullOrWhiteSpace(context.WorkingDirectory) || context.WorkingDirectory.Length > 32_768 || context.WorkingDirectory.Contains('\0')))
            throw new ArgumentException("Conversation settings require exact, bounded saved-chat metadata.", nameof(context));
    }

    private async Task<IDesktopConversationSettingsEditor> OpenConversationSettingsAsync(LoadedPlugin owner,
        DesktopConversationSettingsContribution contribution, DesktopConversationSettingsContext context, CancellationToken token)
    {
        RequireSettingsOwner(owner);
        ValidateSettingsContext(context);
        token.ThrowIfCancellationRequested();
        context.AvailabilityToken.ThrowIfCancellationRequested();
        using var invocation = BeginInvocation(owner);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.SettingsOpens.Add(settled.Task);
        var availability = CancellationTokenSource.CreateLinkedTokenSource(context.AvailabilityToken, owner.Lifetime.Token);
        IDesktopConversationSettingsEditor? editor = null;
        var cleanupEditor = true;
        var availabilityTransferred = false;
        var published = false;
        HostedConversationSettingsEditor? guarded = null;
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(token, availability.Token);
            editor = await contribution.OpenAsync(context with { AvailabilityToken = availability.Token }, opening.Token) ??
                throw new InvalidDataException("The plugin conversation-settings factory returned no editor.");
            if (owner.SettingsEditorIdentities.TryGetValue(editor, out _))
            {
                // A broken factory must not dispose another live session when it reuses that session's editor.
                cleanupEditor = false;
                throw new InvalidDataException("A conversation-settings factory must return a fresh editor for each opening.");
            }
            opening.Token.ThrowIfCancellationRequested();
            RequireSettingsOwner(owner);
            var view = editor.View ?? throw new InvalidDataException("The plugin settings editor returned no WPF view.");
            view.Dispatcher.VerifyAccess();
            if (!ReferenceEquals(view, editor.View))
                throw new InvalidDataException("A conversation-settings editor must retain one stable WPF view.");
            RequireUnparented(view);
            if (owner.SettingsViewIdentities.TryGetValue(view, out _))
                throw new InvalidDataException("A conversation-settings factory must return a fresh WPF view for each opening.");
            RequireSettingsOwner(owner);
            opening.Token.ThrowIfCancellationRequested();
            owner.SettingsEditorIdentities.Add(editor, new object());
            owner.SettingsViewIdentities.Add(view, new object());
            guarded = new HostedConversationSettingsEditor(this, owner, editor, view, availability);
            owner.SettingsEditors.Add(guarded);
            availabilityTransferred = true;
            editor = null; // The guarded session now owns editor cleanup and the availability source.
            guarded.ObserveAvailability();
            RequireSettingsOwner(owner);
            opening.Token.ThrowIfCancellationRequested();
            published = true;
            return guarded;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || availability.IsCancellationRequested) { throw; }
        catch (Exception error) when (SettingsFailure(error))
        {
            ReportSettingsFailure(owner.Id, "Conversation-settings opening failed: " + BoundError(Error(error)));
            throw;
        }
        finally
        {
            if (availabilityTransferred && !published) _ = guarded!.Retire();
            if (!availabilityTransferred)
            {
                try { availability.Cancel(); }
                catch (Exception error) when (SettingsFailure(error)) { ReportSettingsFailure(owner.Id, "Settings opening cancellation failed: " + BoundError(Error(error))); }
                if (editor is not null && cleanupEditor) CleanupSettingsEditor(owner, editor);
                availability.Dispose();
            }
            owner.SettingsOpens.Remove(settled.Task);
            settled.TrySetResult();
        }
    }

    private void CleanupSettingsEditor(LoadedPlugin owner, IDesktopConversationSettingsEditor editor)
    {
        try { editor.Cancel(); }
        catch (Exception error) when (SettingsFailure(error)) { ReportSettingsFailure(owner.Id, "Settings discard failed: " + BoundError(Error(error))); }
        try { editor.Dispose(); }
        catch (Exception error) when (SettingsFailure(error))
        {
            ReportSettingsFailure(owner.Id, "Settings editor cleanup failed: " + BoundError(Error(error)));
            owner.SettingsCleanupError ??= error;
            owner.FailedSettingsEditors.Add(editor); // Failed native resources remain rooted with their retired owner.
            if (loaded.TryGetValue(owner.Id, out var current) && ReferenceEquals(current, owner)) Disable(owner.Id);
        }
    }

    private Task RetireConversationSettings(LoadedPlugin owner)
    {
        // Every operation is registered before invoking plugin code. A removed owner cannot add another operation.
        var waits = owner.SettingsOpens.Concat(owner.SettingsEditors.ToArray().Select(item => item.Retire())).ToArray();
        return waits.Length == 0 ? Task.CompletedTask : Task.WhenAll(waits);
    }

    private void FailRetirement(LoadedPlugin item, Exception error)
    {
        item.RetirementError = BoundError("Native cleanup failed; restart this host process before replacing this package. " + Error(error));
        ReportSettingsFailure(item.Id, item.RetirementError);
        item.Retirement.TrySetResult(item.RetirementError);
        RaiseChanged();
        // Keep the exact owner and catalog reader rooted until actual process exit. Unknown native
        // resources must not become a successful cleanup receipt when this runtime is discarded.
    }

    private void FinishRelease(LoadedPlugin item)
    {
        if (item.CleanupStarted || item.RetirementError is not null) return;
        item.CleanupStarted = true;
        try
        {
            item.Plugin.Dispose();
            item.Lifetime.Dispose();
            item.Context.Unload(); // A request, not a guarantee of thread termination or WPF collection.
            item.RetirementObservation?.Dispose();
            item.RetirementObservation = null;
        }
        catch (Exception error) when (SettingsFailure(error)) { FailRetirement(item, error); return; }
        if (retiring.TryGetValue(item.Id, out var current) && ReferenceEquals(current, item)) retiring.Remove(item.Id);
        UnregisterRetirement(item);
        item.Retirement.TrySetResult(null);
        if (!disposed && item.ReloadAfterRetirement) SchedulePublicationRefresh();
    }

    private async Task FinishReleaseAfterSettingsAsync(LoadedPlugin item, Task cleanup)
    {
        try
        {
            // A callback/cleanup reader excludes publication across the whole retirement boundary.
            // Busy publication is observed without spinning or invoking native cleanup inside it.
            while (item.RetirementObservation is null)
            {
                if (dispatcher.HasShutdownStarted) throw new InvalidOperationException("The host dispatcher ended before native cleanup could start.");
                item.RetirementObservation = DesktopPluginPublicationLease.TryAcquireIndependentReader(catalog.RootDirectory);
                if (item.RetirementObservation is null) await Task.Delay(TimeSpan.FromSeconds(1));
            }
            await cleanup;
            if (dispatcher.CheckAccess()) FinishRelease(item);
            else if (!dispatcher.HasShutdownStarted) await dispatcher.InvokeAsync(() => FinishRelease(item));
            else throw new InvalidOperationException("The host dispatcher ended before native cleanup settled.");
        }
        catch (Exception error) when (SettingsFailure(error))
        {
            if (dispatcher.CheckAccess()) FailRetirement(item, error);
            else if (!dispatcher.HasShutdownStarted) await dispatcher.InvokeAsync(() => FailRetirement(item, error));
        }
    }

    private sealed class HostedConversationSettingsEditor(DesktopPluginRuntime runtime, LoadedPlugin owner,
        IDesktopConversationSettingsEditor editor, FrameworkElement view, CancellationTokenSource availability) : IDesktopConversationSettingsEditor
    {
        private CancellationTokenRegistration cancellation;
        private Task<DesktopOperationResult>? pending;
        private DesktopOperationResult? uncertain;
        internal bool HasPendingSave => pending is not null;
        private bool accepted;
        private bool retired;
        private bool cleaned;
        private readonly TaskCompletionSource cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FrameworkElement View
        {
            get
            {
                runtime.SettingsDispatcher.VerifyAccess();
                RequireAvailable();
                RequireStableView();
                return view;
            }
        }

        public void ObserveAvailability() => cancellation = availability.Token.Register(() =>
        {
            if (runtime.SettingsDispatcher.CheckAccess()) _ = Retire();
            else if (!runtime.SettingsDispatcher.HasShutdownStarted)
                _ = runtime.SettingsDispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => _ = Retire()));
        });

        private void RequireAvailable()
        {
            runtime.RequireSettingsOwner(owner);
            if (retired || availability.IsCancellationRequested)
                throw new InvalidOperationException("This conversation-settings editor is no longer available.");
        }

        private void RequireStableView()
        {
            if (!ReferenceEquals(view, editor.View))
                throw new InvalidDataException("The plugin changed its conversation-settings editor view.");
        }

        public Task<DesktopOperationResult> SaveAsync(CancellationToken cancellationToken = default)
        {
            runtime.SettingsDispatcher.VerifyAccess();
            if (pending is not null) return pending;
            // An uncertain reply never authorizes another mutation from the same editor draft.
            if (uncertain is not null) return Task.FromResult(uncertain);
            if (accepted) return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected,
                "The earlier plugin settings save was accepted but has no confirmed settlement. Reopen settings and inspect the plugin's saved state before another save."));
            try
            {
                RequireAvailable();
                RequireStableView();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception error) when (SettingsFailure(error))
            {
                return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, BoundError(Error(error))));
            }
            pending = SaveCoreAsync(cancellationToken);
            return pending;
        }

        private async Task<DesktopOperationResult> SaveCoreAsync(CancellationToken token)
        {
            // Reserve the shared task before invoking plugin code, including synchronous/reentrant factories.
            await Task.Yield();
            var dispatched = false;
            try
            {
                RequireAvailable();
                RequireStableView();
                token.ThrowIfCancellationRequested();
                using var invocation = runtime.BeginInvocation(owner);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, availability.Token);
                dispatched = true;
                var result = await editor.SaveAsync(linked.Token);
                ValidateResult(result);
                RequireAvailable();
                RequireStableView();
                linked.Token.ThrowIfCancellationRequested();
                if (result.State == DesktopOperationState.Uncertain) uncertain = result;
                if (result.State == DesktopOperationState.Accepted) accepted = true;
                return result;
            }
            catch (Exception error) when (SettingsFailure(error))
            {
                runtime.ReportSettingsFailure(owner.Id, "Conversation-settings save failed: " + BoundError(Error(error)));
                var result = new DesktopOperationResult(dispatched ? DesktopOperationState.Uncertain : DesktopOperationState.Rejected,
                    dispatched ? "The dispatched plugin settings save could not be confirmed. Reopen settings and inspect the plugin's saved state before another save."
                        : BoundError(Error(error)));
                if (dispatched) uncertain = result;
                return result;
            }
            finally { pending = null; }
        }

        private static void ValidateResult(DesktopOperationResult? result)
        {
            if (result is null || !Enum.IsDefined(result.State) ||
                result.Message is not null && (result.Message.Length > DesktopConversationSettingsContract.MaximumStatusChars || result.Message.Contains('\0')) ||
                result.RequestId is not null && !SettingsIdentifier(result.RequestId, 128) ||
                result.JobId is not null && !SettingsIdentifier(result.JobId, 128))
                throw new InvalidDataException("The plugin returned a malformed or excessive settings-save receipt.");
        }

        public void Cancel()
        {
            runtime.SettingsDispatcher.VerifyAccess();
            _ = Retire();
        }

        public void Dispose()
        {
            runtime.SettingsDispatcher.VerifyAccess();
            _ = Retire();
        }

        public Task Retire()
        {
            runtime.SettingsDispatcher.VerifyAccess();
            if (retired) return cleanup.Task;
            retired = true;
            try { availability.Cancel(); }
            catch (Exception error) when (SettingsFailure(error)) { runtime.ReportSettingsFailure(owner.Id, "Settings cancellation failed: " + BoundError(Error(error))); }
            if (pending is null) FinishCleanup();
            else _ = CleanupAfterSaveAsync(pending);
            return cleanup.Task;
        }

        private async Task CleanupAfterSaveAsync(Task<DesktopOperationResult> save)
        {
            try { await save; }
            catch (Exception error) when (SettingsFailure(error)) { runtime.ReportSettingsFailure(owner.Id, "Settings save settlement failed: " + BoundError(Error(error))); }
            finally { FinishCleanup(); }
        }

        private void FinishCleanup()
        {
            if (cleaned) return;
            cleaned = true;
            cancellation.Dispose();
            try { runtime.CleanupSettingsEditor(owner, editor); }
            finally
            {
                availability.Dispose();
                owner.SettingsEditors.Remove(this);
                cleanup.TrySetResult();
            }
        }
    }
}
