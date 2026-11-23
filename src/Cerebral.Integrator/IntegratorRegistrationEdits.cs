using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private sealed record RegistrationEditSession(long Connection,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> Dispatch, HostSettings Settings);

    private Func<CommandRequest, CancellationToken, Task<CommandResult>>? registrationCommandOverride;
    private Func<FieldsDialog, bool?>? registrationDialogOverride;
    private long settingsConnectionEpoch = -1;

    private Func<CommandRequest, CancellationToken, Task<CommandResult>> RegistrationDispatch()
    {
        if (registrationCommandOverride is not null) return registrationCommandOverride;
        var capturedClient = Client;
        return (request, token) => capturedClient.CommandAsync(request.Operation, request.Arguments, request.ProfileId, token);
    }

    private async Task<RegistrationEditSession> BeginRegistrationEditAsync()
    {
        Dispatcher.VerifyAccess();
        var connection = queueConnectionEpoch;
        var dispatch = RegistrationDispatch();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await dispatch(new("settings.get", CerebralJson.Element(new { })), timeout.Token);
        if (connection != queueConnectionEpoch) throw new InvalidOperationException("The connection changed. Reopen settings on the current service.");
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "Could not read the service settings.");
        // Deserialize a private snapshot: other refreshes and modal callbacks must not replace the
        // values or revision whose credentials and command arguments the owner is reviewing.
        return new(connection, dispatch, CerebralJson.Read<HostSettings>(result.Data));
    }

    private void RequireRegistrationConnection(RegistrationEditSession session)
    {
        Dispatcher.VerifyAccess();
        if (session.Connection != queueConnectionEpoch)
            throw new InvalidOperationException("The connection changed. Reopen this registration on the current service before saving.");
    }

    private bool ShowRegistrationDialog(RegistrationEditSession session, FieldsDialog form)
    {
        RequireRegistrationConnection(session);
        var accepted = registrationDialogOverride is { } show ? show(form) : form.ShowDialog();
        // ShowDialog pumps the dispatcher, including connection changes and settings refreshes.
        RequireRegistrationConnection(session);
        return accepted == true;
    }

    private async Task CommitRegistrationEditAsync(RegistrationEditSession session)
    {
        _ = await SaveHostSettingsSnapshotAsync(session, session.Settings, CancellationToken.None);
        SetStatus("Configuration saved. Listener changes take effect after a service restart.");
    }

    private async Task<HostSettings> SaveHostSettingsSnapshotAsync(RegistrationEditSession session,
        HostSettings candidate, CancellationToken cancellationToken)
    {
        RequireRegistrationConnection(session);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await session.Dispatch(new("settings.save", CerebralJson.Element(candidate)), timeout.Token);
        if (session.Connection != queueConnectionEpoch)
            throw new SettingsRevisionConflictException("The connection changed while this draft was saving. It was not applied to the current service; close and reopen these settings.");
        if (!result.Success)
        {
            var error = result.Error ?? "Could not save configuration.";
            if (error.StartsWith("Service settings changed while this copy was open.", StringComparison.Ordinal) ||
                error.Contains("revision conflict", StringComparison.OrdinalIgnoreCase))
                throw new SettingsRevisionConflictException(error);
            if (IsSettingsValidationError(error)) throw new SettingsDraftInvalidException(error);
            throw new InvalidOperationException(error);
        }
        var saved = CerebralJson.Read<HostSettings>(result.Data);
        AdoptRegistrationSettings(saved, session.Connection);
        return saved;
    }

    private static bool IsSettingsValidationError(string error) =>
        error.StartsWith("Invalid queue,", StringComparison.Ordinal) ||
        error.StartsWith("History limits must", StringComparison.Ordinal) ||
        error.StartsWith("Logical processor policy", StringComparison.Ordinal) ||
        error.StartsWith("Question timeout must", StringComparison.Ordinal) ||
        error.Contains(" directory must be ", StringComparison.Ordinal) ||
        error.Contains(" files must remain outside ", StringComparison.Ordinal);

    private void AdoptRegistrationSettings(HostSettings saved, long connection)
    {
        Dispatcher.VerifyAccess();
        if (connection != queueConnectionEpoch ||
            (settingsConnectionEpoch == connection && settings.Revision > saved.Revision)) return;
        settings = saved;
        settingsConnectionEpoch = connection;
        workers.ItemsSource = settings.Workers;
        RefreshModelWorkerChoices();
        if (agents.ItemsSource is IEnumerable<AgentRow> rows)
            agents.ItemsSource = rows.Select(x => x.Record).ToArray().Select(CreateAgentRow).ToList();
        executables.ItemsSource = settings.Executables;
        mcp.ItemsSource = settings.McpServers.Where(item => item.CortexNative is null).ToArray();
        background.ItemsSource = settings.BackgroundTasks;
        backgroundState.Text = settings.BackgroundTasksEnabled ? "Background work enabled" : "Background work disabled";
        RefreshSettingsSummary();
    }
}
