using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    /// <summary>Deterministic controller/native-field checks; synthetic service, no network or saved settings.</summary>
    public async Task<int> CheckSettingsAutoSaveAsync()
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run settings autosave checks from an active WPF dispatcher callback.");
        _ = new WindowInteropHelper(this).EnsureHandle();
        if (IsVisible || IsLoaded) throw new InvalidOperationException("Settings autosave checks require an unshown owner window.");

        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        static HostSettings Copy(HostSettings value) => CerebralJson.Read<HostSettings>(CerebralJson.Element(value));
        static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!predicate()) await Task.Delay(5, deadline.Token);
        }

        var backend = new HostSettings { Revision = 10, QueueCapacity = 32, AccessKey = "synthetic-owner-access-key" };
        var saves = 0; var adopted = 0; var candidateRevisions = new List<int>();
        var statuses = new List<ExpectedRevisionAutoSaveStatus>();
        var delayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var form = new FieldsDialog(this, "Autosave fixture").Add("capacity", "Capacity", "32");
        await using var controller = new ExpectedRevisionAutoSave<HostSettings>(Copy(backend),
            baseline =>
            {
                var candidate = Copy(baseline);
                candidate.QueueCapacity = form.Number("capacity", 1, 1000);
                return candidate;
            },
            (candidate, token) =>
            {
                token.ThrowIfCancellationRequested(); saves++; candidateRevisions.Add(candidate.Revision);
                if (candidate.Revision != backend.Revision)
                    throw new SettingsRevisionConflictException("The service settings changed elsewhere. The draft remains visible and was not applied.");
                backend = Copy(candidate); backend.Revision++;
                return Task.FromResult(Copy(backend));
            },
            _ => adopted++,
            status => { statuses.Add(status); form.SetInlineStatus(status.Message, status.Phase is ExpectedRevisionAutoSavePhase.Invalid or ExpectedRevisionAutoSavePhase.Conflict or ExpectedRevisionAutoSavePhase.Failed); },
            TimeSpan.FromSeconds(30), wait: (_, token) => delayRelease.Task.WaitAsync(token));
        form.WhenAnyInputChanged(controller.Changed).ConfigureSubmitAsync("Done", controller.FlushAsync);

        form.SetValue("capacity", "not-a-number");
        Check(!await controller.FlushAsync() && saves == 0 && backend.QueueCapacity == 32 &&
              form.Value("capacity") == "not-a-number" && statuses[^1].Phase == ExpectedRevisionAutoSavePhase.Invalid && form.InlineStatusIsError,
            "invalid draft remains visible while the last valid service value stays active");

        delayRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        form.SetValue("capacity", "40");
        form.SetValue("capacity", "41");
        delayRelease.TrySetResult();
        await WaitUntilAsync(() => saves == 1 && statuses[^1].Phase == ExpectedRevisionAutoSavePhase.Saved);
        Check(backend.QueueCapacity == 41 && backend.Revision == 11 && candidateRevisions.SequenceEqual([10]) && adopted == 1,
            "rapid valid edits debounce to one exact expected-revision save and adopt its acknowledged revision");

        backend.QueueCapacity = 88; backend.Revision++;
        delayRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        form.SetValue("capacity", "42");
        delayRelease.TrySetResult();
        await WaitUntilAsync(() => statuses[^1].Phase == ExpectedRevisionAutoSavePhase.Conflict);
        Check(saves == 2 && candidateRevisions[^1] == 11 && backend.QueueCapacity == 88 && backend.Revision == 12 &&
              form.Value("capacity") == "42" && form.InlineStatusIsError,
            "a concurrent revision wins without overwriting or hiding the local draft");
        form.SetValue("capacity", "43");
        Check(!await controller.FlushAsync() && saves == 2 && backend.QueueCapacity == 88,
            "a conflicted editor stays blocked instead of retrying against an unknown newer baseline");
        form.DiscardDraftAndClose();

        var closeBackend = new HostSettings { Revision = 20, QueueCapacity = 20, AccessKey = "synthetic-owner-access-key" };
        var closeSaves = 0;
        var closeForm = new FieldsDialog(this, "Close fixture").Add("capacity", "Capacity", "20");
        var neverDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var closeController = new ExpectedRevisionAutoSave<HostSettings>(Copy(closeBackend),
            baseline => { var value = Copy(baseline); value.QueueCapacity = closeForm.Number("capacity", 1, 1000); return value; },
            (candidate, token) =>
            {
                token.ThrowIfCancellationRequested(); closeSaves++;
                closeBackend = Copy(candidate); closeBackend.Revision++;
                return Task.FromResult(Copy(closeBackend));
            }, _ => { }, status => closeForm.SetInlineStatus(status.Message, status.Phase == ExpectedRevisionAutoSavePhase.Invalid),
            TimeSpan.FromMinutes(1), wait: (_, token) => neverDelay.Task.WaitAsync(token));
        closeForm.WhenAnyInputChanged(closeController.Changed).ConfigureSubmitAsync("Done", closeController.FlushAsync);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        closeForm.Closed += (_, _) => closed.TrySetResult();
        _ = new WindowInteropHelper(closeForm).EnsureHandle();
        closeForm.SetValue("capacity", "21");
        closeForm.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(closeSaves == 1 && closeBackend.QueueCapacity == 21 && closeBackend.Revision == 21,
            "closing a valid editor flushes a change still inside the debounce interval");

        var previousDispatch = registrationCommandOverride;
        var previousDialog = registrationDialogOverride;
        var previousSettings = settings;
        var previousSettingsEpoch = settingsConnectionEpoch;
        try
        {
            var service = new HostSettings
            {
                Revision = 30, AccessKey = "synthetic-owner-access-key", QueueCapacity = 32,
                ProjectsDirectory = @"C:\Fixture\Projects", ModelDirectory = @"C:\Fixture\Models",
                SharedDirectory = @"C:\Fixture\Shared"
            };
            var commandSaves = 0; var expectedRevisions = new List<int>();
            registrationCommandOverride = (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (request.Operation == "settings.get") return Task.FromResult(CommandResult.Ok(Copy(service)));
                if (request.Operation != "settings.save") return Task.FromResult(CommandResult.Fail("Unexpected fixture command."));
                var candidate = CerebralJson.Read<HostSettings>(request.Arguments);
                commandSaves++; expectedRevisions.Add(candidate.Revision);
                if (candidate.Revision != service.Revision) return Task.FromResult(CommandResult.Fail("Service settings changed while this copy was open. Refresh and apply the intended change again."));
                if (candidate.ModelDirectory == "relative") return Task.FromResult(CommandResult.Fail("Model directory must be an absolute service-accessible path."));
                service = Copy(candidate); service.Revision++;
                return Task.FromResult(CommandResult.Ok(Copy(service)));
            };
            registrationDialogOverride = dialog =>
            {
                dialog.SetValue("capacity", "44");
                dialog.SetValue("memory", "2048");
                dialog.SetValue("processorMaximum", "3");
                dialog.SetValue("processors", "5, 1, 3");
                dialog.SetValue("workflowExpiry", "14");
                return true;
            };
            await EditQueueSettings();
            Check(commandSaves == 1 && expectedRevisions.SequenceEqual([30]) && service.QueueCapacity == 44 &&
                  service.ReservedMemoryMb == 2048 && service.MaxLogicalProcessorCount == 3 &&
                  service.WorkflowIdleExpiryDays == 14 &&
                  service.AllowedLogicalProcessors.SequenceEqual([1, 3, 5]) && settings.Revision == 31,
                "the existing Queues and limits editor uses one autosaved private snapshot and adopts the acknowledged service result");

            FieldsDialog? invalidProcessorDraft = null;
            registrationDialogOverride = dialog =>
            {
                invalidProcessorDraft = dialog;
                dialog.SetValue("processors", "1, 1");
                return true;
            };
            await EditQueueSettings();
            Check(commandSaves == 1 && invalidProcessorDraft is { InlineStatusIsError: true } processorDraft &&
                  processorDraft.Value("processors") == "1, 1" &&
                  processorDraft.InlineStatusText.Contains("duplicates", StringComparison.Ordinal),
                "the processor-list editor retains an invalid duplicate draft and explains it without sending a settings mutation");

            registrationDialogOverride = dialog =>
            {
                dialog.SetValue("projects", @"C:\Fixture\NewProjects");
                dialog.SetValue("runtime", @"C:\Fixture\llama-server.exe");
                return true;
            };
            await EditRepositorySettings();
            Check(commandSaves == 2 && expectedRevisions.SequenceEqual([30, 31]) &&
                  service.ProjectsDirectory == @"C:\Fixture\NewProjects" && service.LlamaServerPath == @"C:\Fixture\llama-server.exe" && settings.Revision == 32,
                "the existing Folders and runtime editor composes on the latest acknowledged revision without a parallel settings store");

            FieldsDialog? invalidFolderDraft = null;
            registrationDialogOverride = dialog =>
            {
                invalidFolderDraft = dialog;
                dialog.SetValue("models", "relative");
                return true;
            };
            await EditRepositorySettings();
            Check(commandSaves == 3 && service.Revision == 32 && invalidFolderDraft is { } invalidDraft &&
                  invalidDraft.Value("models") == "relative" && invalidDraft.InlineStatusIsError &&
                  invalidDraft.InlineStatusText.Contains("absolute", StringComparison.Ordinal),
                "service-side folder validation is shown inline without replacing the typed draft or acknowledged settings");

            var resourceSnapshot = Copy(service);
            registrationDialogOverride = dialog =>
            {
                dialog.SetValue("timeout", "3600");
                dialog.SetSelection("action", "assume");
                return true;
            };
            await EditQuestionSettings();
            Check(commandSaves == 4 && service.Revision == 33 && settings.Revision == 33 &&
                  service.QuestionTimeoutSeconds == 3600 && service.QuestionTimeoutAction == "assume" &&
                  service.QueueCapacity == resourceSnapshot.QueueCapacity &&
                  service.ReservedMemoryMb == resourceSnapshot.ReservedMemoryMb &&
                  service.JobHistoryLimit == resourceSnapshot.JobHistoryLimit &&
                  service.EngineEventHistoryMegabytes == resourceSnapshot.EngineEventHistoryMegabytes,
                "the focused question editor saves the current revision without changing capacity or retention settings");

            FieldsDialog? invalidQuestionDraft = null;
            registrationDialogOverride = dialog =>
            {
                invalidQuestionDraft = dialog;
                dialog.SetValue("timeout", "86401");
                return true;
            };
            await EditQuestionSettings();
            Check(commandSaves == 4 && service.Revision == 33 &&
                  invalidQuestionDraft is { InlineStatusIsError: true } questionDraft &&
                  questionDraft.Value("timeout") == "86401" && service.QuestionTimeoutSeconds == 3600,
                "an out-of-range question timeout stays visible and is not submitted");

            registrationDialogOverride = dialog =>
            {
                dialog.SetValue("history", "12");
                dialog.SetValue("days", "4");
                dialog.SetValue("activity", "200");
                dialog.SetValue("activityDays", "7");
                dialog.SetValue("engineEvents", "432");
                dialog.SetValue("engineMegabytes", "8");
                dialog.SetValue("engineDays", "9");
                dialog.SetSelection("cacheDiagnostics", "enabled");
                dialog.SetValue("cacheThreshold", "9");
                return true;
            };
            await EditRetentionSettings();
            Check(commandSaves == 5 && service.Revision == 34 && settings.Revision == 34 &&
                  service.JobHistoryLimit == 12 && service.JobHistoryDays == 4 &&
                  service.ActivityHistoryLimit == 200 && service.ActivityHistoryDays == 7 &&
                  service.EngineEventHistoryLimit == 432 && service.EngineEventHistoryMegabytes == 8 &&
                  service.EngineEventHistoryDays == 9 && service.PromptCacheDiagnosticsEnabled &&
                  service.PromptCacheDiagnosticsChangePercentagePoints == 9 &&
                  service.QueueCapacity == resourceSnapshot.QueueCapacity &&
                  service.MaxConcurrentJobs == resourceSnapshot.MaxConcurrentJobs &&
                  service.ReservedMemoryMb == resourceSnapshot.ReservedMemoryMb &&
                  service.QuestionTimeoutSeconds == 3600 && service.QuestionTimeoutAction == "assume",
                "the focused retention editor saves only history and collection fields on the latest acknowledged revision");

            FieldsDialog? invalidRetentionDraft = null;
            registrationDialogOverride = dialog =>
            {
                invalidRetentionDraft = dialog;
                dialog.SetValue("activityDays", "0");
                return true;
            };
            await EditRetentionSettings();
            Check(commandSaves == 5 && service.Revision == 34 &&
                  invalidRetentionDraft is { InlineStatusIsError: true } retentionDraft &&
                  retentionDraft.Value("activityDays") == "0" && service.ActivityHistoryDays == 7,
                "an invalid retention draft cannot silently replace the saved history settings");

            var serviceForm = CreateServiceSettingsForm(service);
            serviceForm.SetValue("name", "Renamed fixture worker");
            var ordinaryCandidate = ServiceSettingsCandidate(service, serviceForm);
            var expectedOrdinaryCandidate = Copy(service);
            expectedOrdinaryCandidate.Name = "Renamed fixture worker";
            Check(CerebralJson.Element(ordinaryCandidate).GetRawText() == CerebralJson.Element(expectedOrdinaryCandidate).GetRawText() &&
                  ordinaryCandidate.AccessKey == service.AccessKey && commandSaves == 5,
                "the actual ordinary service editor preserves credentials and every unrelated setting while preparing a name edit");
        }
        finally
        {
            registrationCommandOverride = previousDispatch;
            registrationDialogOverride = previousDialog;
            settingsConnectionEpoch = -1;
            AdoptRegistrationSettings(previousSettings, queueConnectionEpoch);
            settingsConnectionEpoch = previousSettingsEpoch == queueConnectionEpoch ? previousSettingsEpoch : -1;
        }

        Console.WriteLine(JsonSerializer.Serialize(new { success = true, settingsAutoSaveChecks = checks,
            evidence = "deterministic revision controller and native FieldsDialog events", serviceCommandsExecuted = false,
            networkUsed = false, windowShown = false }));
        return checks;
    }
}
