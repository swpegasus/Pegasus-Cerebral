using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

internal sealed record ConfigurationLaunchOutcome(string Status, string Code, Exception? Failure = null)
{
    internal static ConfigurationLaunchOutcome FromFailure(bool startAttempted, Exception error) =>
        new(startAttempted ? "unconfirmed" : "failed",
            startAttempted ? "configuration_launch_unconfirmed" : "configuration_launch_failed", error);

    internal DesktopControlResponse ToControlResponse(DesktopControlRequest request) =>
        DesktopControlProtocol.Result(request, Status switch
        {
            "launch-requested" => "accepted",
            "unconfirmed" => "uncertain",
            _ => "failed"
        }, new
        {
            status = Status, code = Code,
            receiverConnectionVerified = false,
            foregroundVerified = false
        }, Status == "launch-requested" ? null : Status == "unconfirmed"
            ? "The Configuration launch outcome is unconfirmed. Inspect the existing receiver before another explicit action."
            : "Configuration launch was not accepted. Review the pinned mapping, current selection and Main status.");
}

public sealed partial class OrchestratorWindow
{
    private readonly SemaphoreSlim configurationLaunchGate = new(1, 1);
    private (ConfigurationLaunchEntry Entry, ExistingWorkerDesktopOptions Target,
        ConfigurationManagedPluginOwnerBinding OwnerBinding)? configurationLaunch;
    private ConfigurationLaunchSubmission? lastConfigurationLaunch;

    internal void ConfigureConfigurationLaunch(ExistingWorkerDesktopOptions worker, string configurationState, ConfigurationLaunchEntry entry)
    {
        if (configurationLaunch is not null) throw new InvalidOperationException("Configuration launch mapping is already selected.");
        if (!ConfigurationLaunchBindingMatches(worker))
            throw new InvalidOperationException("Configuration launch must retain this interface's exact Worker binding.");
        var target = ExistingWorkerDesktopOptions.FromArguments(["--attach-existing", worker.Endpoint.PipeName,
            worker.Endpoint.InstanceId, worker.WorkerDataDirectory, configurationState, worker.InstallationId])!;
        var ownerBinding = ConfigurationManagedPluginOwnerBinding.CreateForMain(worker, target,
            desktopPluginCatalog.RootDirectory, desktopPluginDataRoot
                ?? throw new InvalidOperationException("Attached Main has no selected desktop plugin data root."),
            DesktopInstancePaths.Current);
        entry = ConfigurationLaunchEntry.Parse(entry.Path, entry.Sha256);
        if (ScopedFilePolicy.IsWithin(entry.Path, worker.WorkerDataDirectory) || ScopedFilePolicy.IsWithin(entry.Path, worker.DesktopDataDirectory) ||
            ScopedFilePolicy.IsWithin(entry.Path, target.DesktopDataDirectory))
            throw new ArgumentException("Configuration application files must be separate from Worker and interface data.");
        configurationLaunch = (entry, target, ownerBinding);
        Closed += (_, _) => lastConfigurationLaunch?.Dispose();
    }

    private new async Task OpenConnectionConfigurationAsync()
    {
        if (configurationLaunch is null)
        {
            await base.OpenConnectionConfigurationAsync();
            return;
        }
        var result = await LaunchMappedConfigurationAsync(Lifetime.Token);
        if (Lifetime.IsCancellationRequested || result.Code == "configuration_launch_busy") return;
        if (result.Status != "launch-requested")
            throw result.Failure ?? new IOException("Configuration launch was not confirmed.");
    }

    private async Task<DesktopControlResponse> OpenConfigurationFromSemanticControlAsync(DesktopControlRequest request,
        CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        if (semanticAttachInvocation.Value is null)
            throw new InvalidOperationException("Configuration semantic control requires the original expected selection.");
        RequireSemanticAttachCurrent();
        var authority = CaptureSemanticAttachAuthority()
            ?? throw new InvalidOperationException("Configuration semantic control requires a captured current owner.");
        void RequireOriginalSelection()
        {
            Dispatcher.VerifyAccess();
            // This explicit snapshot guard survives the background launch/dispatcher hop
            // without depending on an ambient invocation flowing through native delivery.
            if (!authority())
                throw new InvalidOperationException("The original Configuration launch owner or selection changed.");
        }
        var outcome = await LaunchMappedConfigurationAsync(token, RequireOriginalSelection);
        if (outcome.Status != "launch-requested" && !Lifetime.IsCancellationRequested && authority())
            SetStatus(outcome.Status == "unconfirmed"
                ? "Configuration launch outcome is unconfirmed. Inspect the existing receiver before another action."
                : "Configuration launch failed. Review its pinned mapping and the current Main binding.", true);
        return outcome.ToControlResponse(request);
    }

    private async Task<ConfigurationLaunchOutcome> LaunchMappedConfigurationAsync(CancellationToken token,
        Action? requireOriginalSelection = null)
    {
        Dispatcher.VerifyAccess();
        if (configurationLaunch is not { } mapping)
            return new("failed", "configuration_mapping_missing",
                new InvalidOperationException("This Main has no pinned Configuration launch mapping."));
        var held = false;
        var startAttempted = false;
        try
        {
            held = await configurationLaunchGate.WaitAsync(0, token);
            if (!held) return new("failed", "configuration_launch_busy");
            token.ThrowIfCancellationRequested();
            requireOriginalSelection?.Invoke();
            RequireConfigurationLaunchBinding(mapping.Target);
            SetStatus("Opening Pegasus Cerebral Configuration for this Worker…");

            var submitted = await mapping.Entry.LaunchAsync(mapping.Target, token, start =>
            {
                ConfigurationLaunchSubmission Submit()
                {
                    token.ThrowIfCancellationRequested();
                    requireOriginalSelection?.Invoke();
                    RequireConfigurationLaunchBinding(mapping.Target);
                    startAttempted = true;
                    return start();
                }
                // All file validation awaits have finished. For a semantic action the
                // final original-selection check and single start share one UI action.
                return requireOriginalSelection is null ? Submit() :
                    Dispatcher.Invoke(Submit, System.Windows.Threading.DispatcherPriority.Normal, token);
            }, mapping.OwnerBinding);
            if (Lifetime.IsCancellationRequested)
            {
                submitted.Dispose();
                return ConfigurationLaunchOutcome.FromFailure(startAttempted,
                    new OperationCanceledException("Main closed after Configuration launch submission."));
            }
            var previous = lastConfigurationLaunch;
            lastConfigurationLaunch = submitted;
            previous?.Dispose();
            requireOriginalSelection?.Invoke();
            if (submitted.Process.HasExited && submitted.Process.ExitCode != 0)
                return new("failed", "configuration_startup_failed",
                    new IOException("Configuration could not open for this Worker. Review its application startup error; no other connection was selected."));
            SetStatus("Configuration launch requested for this Worker.");
            return new("launch-requested", "configuration_launch_requested");
        }
        catch (Exception error)
        {
            // Even a process-metadata or cancelled-reply failure can follow a successful
            // native start. Retain uncertainty; never infer absence or replay the start.
            return ConfigurationLaunchOutcome.FromFailure(startAttempted, error);
        }
        finally { if (held) configurationLaunchGate.Release(); }
    }

    private bool ConfigurationLaunchBindingMatches(ExistingWorkerDesktopOptions target) =>
        ExistingWorkerAttachment is { } binding && binding.Endpoint == target.Endpoint &&
        binding.WorkerDataDirectory == target.WorkerDataDirectory && binding.InstallationId == target.InstallationId;

    private void RequireConfigurationLaunchBinding(ExistingWorkerDesktopOptions target)
    {
        if (!ConfigurationLaunchBindingMatches(target))
            throw new InvalidOperationException("The pinned Configuration launch no longer matches this Main's Worker binding.");
        if (configurationLaunch is { } mapping)
        {
            mapping.OwnerBinding.RequireForChild(target);
            var currentCatalog = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(desktopPluginCatalog.RootDirectory);
            var currentData = desktopPluginDataRoot is null ? null :
                ExistingWorkerDesktopOptions.NormalizeLocalDirectory(desktopPluginDataRoot);
            if (!mapping.OwnerBinding.CatalogRoot.Equals(currentCatalog, StringComparison.OrdinalIgnoreCase) ||
                !mapping.OwnerBinding.PluginDataRoot.Equals(currentData, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The pinned Configuration plugin owner no longer matches this Main.");
        }
    }

    private object? ConfigurationReceiverSnapshot()
    {
        Dispatcher.VerifyAccess();
        if (configurationLaunch is not { } mapping) return null;
        var observedAtUtc = DateTimeOffset.UtcNow;
        var matches = ConfigurationLaunchBindingMatches(mapping.Target);
        var submitted = lastConfigurationLaunch;
        ConfigurationLaunchProcessSnapshot? processSnapshot = null;
        if (submitted is not null)
        {
            var process = submitted.Process;
            processSnapshot = ReadConfigurationLaunchProcess(submitted.ProcessId, submitted.ProcessStartUtcTicks, matches,
                new ConfigurationProcessReader(process.Refresh, () => process.Id,
                    () => process.StartTime.ToUniversalTime().Ticks, () => process.HasExited,
                    () => process.MainWindowHandle.ToInt64(), () => process.ExitCode));
        }
        return new
        {
            observedAtUtc,
            entryPath = mapping.Entry.Path,
            entrySha256 = mapping.Entry.Sha256,
            configurationStateDirectory = mapping.Target.DesktopDataDirectory,
            workerBindingMatchesMain = matches,
            // A later launch may be an activation sender. Never rediscover or authenticate a receiver here.
            lastLaunchProcess = processSnapshot
        };
    }

    // Immutable call-scoped readers keep failure/race checks deterministic without a production override.
    internal sealed record ConfigurationProcessReader(Action Refresh, Func<int> ProcessId, Func<long> StartUtcTicks,
        Func<bool> HasExited, Func<long> WindowHandle, Func<int> ExitCode);

    internal sealed record ConfigurationLaunchProcessSnapshot(int ProcessId, string ProcessStartUtcTicks, string State,
        string? WindowHandle, int? ExitCode, string? ErrorCode);

    internal static ConfigurationLaunchProcessSnapshot ReadConfigurationLaunchProcess(int processId, long startUtcTicks,
        bool bindingMatchesMain, ConfigurationProcessReader reader)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        if (startUtcTicks <= 0) throw new ArgumentOutOfRangeException(nameof(startUtcTicks));
        ArgumentNullException.ThrowIfNull(reader);
        var ticks = startUtcTicks.ToString(CultureInfo.InvariantCulture);
        ConfigurationLaunchProcessSnapshot Unavailable(string code) => new(processId, ticks, "unavailable", null, null, code);
        if (!bindingMatchesMain) return Unavailable("binding_mismatch");
        try
        {
            reader.Refresh();
            if (reader.ProcessId() != processId || reader.StartUtcTicks() != startUtcTicks)
                return Unavailable("process_identity_mismatch");
            if (reader.HasExited())
                return new(processId, ticks, "exited", null, reader.ExitCode(), null);
            var handle = reader.WindowHandle();
            // Exit can race window metadata. Clear the sampled handle if the retained process has exited.
            if (reader.HasExited())
                return new(processId, ticks, "exited", null, reader.ExitCode(), null);
            return new(processId, ticks, "running", handle == 0 ? null : $"0x{handle:X16}", null, null);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or
            UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            // InvalidOperationException includes ObjectDisposedException. Unknown/cancellation failures propagate.
            return Unavailable("process_metadata_unavailable");
        }
    }
}
