using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    /// <summary>Deterministic native form/controller checks; synthetic services, no network or saved settings.</summary>
    public async Task<int> CheckRegistrationEditsAsync()
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run registration checks from an active WPF dispatcher callback.");
        // FieldsDialog requires a native owner. Creating its handle does not show/load this
        // window, so the normal Loaded-triggered service connection is never started.
        _ = new WindowInteropHelper(this).EnsureHandle();
        if (IsVisible || IsLoaded) throw new InvalidOperationException("Registration checks require an unshown owner window.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var priorDispatch = registrationCommandOverride;
        var priorDialog = registrationDialogOverride;
        var priorSettings = settings;
        var priorSettingsEpoch = settingsConnectionEpoch;
        var checks = 0;
        var dialogs = new List<FieldsDialog>();
        var pending = new List<TaskCompletionSource<CommandResult>>();
        var outstanding = new List<Task>();
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        static HostSettings Copy(HostSettings value) => CerebralJson.Read<HostSettings>(CerebralJson.Element(value));
        static HostSettings Fixture(int revision = 10) => new()
        {
            Name = "Fixture service A", Revision = revision, AccessKey = "synthetic-host-secret",
            Workers = [new() { Id = "worker", Name = "Fresh worker", Url = "http://127.0.0.1:7011", AccessKey = "synthetic-worker-secret", RequiresConfiguration = true }],
            Executables = [new() { Id = "tool", Path = @"C:\Fixture\tool.exe", PrefixArguments = ["fresh-argument"], RequiresConfiguration = true }],
            McpServers = [new() { Id = "mcp", Executable = @"C:\Fixture\mcp.exe", Arguments = ["fresh-mcp-argument"], ViaUserConnector = false, Enabled = true, RequiresConfiguration = true }]
        };
        Task Edit(int kind) => kind switch
        {
            0 => EditWorker(new() { Id = "worker", AccessKey = "stale-secret", Name = "Stale worker" }),
            1 => EditExecutable(new() { Id = "tool", Path = @"C:\Stale\tool.exe", PrefixArguments = ["stale-argument"] }),
            _ => EditMcp(new() { Id = "mcp", Executable = @"C:\Stale\mcp.exe", Arguments = ["stale-mcp-argument"] })
        };
        void Modal(Func<FieldsDialog, bool?> callback) => registrationDialogOverride = form => { dialogs.Add(form); return callback(form); };
        TaskCompletionSource<CommandResult> NewPending()
        {
            var value = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(value); return value;
        }
        async Task Reject(Task action, string label)
        {
            var rejected = false;
            try { await action.WaitAsync(deadline.Token); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, label);
        }
        try
        {
            for (var kind = 0; kind < 3; kind++)
            {
                var backend = Fixture(); var calls = new List<CommandRequest>();
                HostSettings? saved = null;
                registrationCommandOverride = (request, token) =>
                {
                    token.ThrowIfCancellationRequested(); calls.Add(request);
                    Check(request.ProfileId is null, "Registration editing must remain on the owner route.");
                    Check(request.Arguments.ValueKind == JsonValueKind.Object,
                        "Registration commands must use the object argument envelope required by the service.");
                    if (request.Operation == "settings.get") return Task.FromResult(CommandResult.Ok(backend));
                    Check(request.Operation == "settings.save", "Registration editing must not make unbound follow-up commands.");
                    var candidate = CerebralJson.Read<HostSettings>(request.Arguments);
                    if (candidate.Revision != backend.Revision) return Task.FromResult(CommandResult.Fail("Fixture revision conflict."));
                    saved = Copy(candidate); backend = Copy(candidate); backend.Revision++;
                    return Task.FromResult(CommandResult.Ok(backend));
                };
                Modal(form =>
                {
                    Check(kind switch
                    {
                        0 => form.Value("name") == "Fresh worker" && form.SecretValue("key") == "synthetic-worker-secret",
                        1 => form.Value("path") == @"C:\Fixture\tool.exe" && form.Lines("arguments").SequenceEqual(["fresh-argument"]),
                        _ => form.Value("executable") == @"C:\Fixture\mcp.exe" && form.Lines("args").SequenceEqual(["fresh-mcp-argument"])
                    }, "A stale grid row must not supply credentials or arguments to the new dialog.");
                    if (kind == 0) form.SetValue("name", "Reviewed worker");
                    if (kind == 1) form.SetValue("arguments", "reviewed-argument");
                    if (kind == 2) form.SetValue("args", "reviewed-mcp-argument");
                    return true;
                });
                await Edit(kind).WaitAsync(deadline.Token);
                Check(calls.Count == 2 && saved?.Revision == 10 && settings.Revision == 11,
                    "Each accepted form must save exactly its captured settings revision and adopt the result.");
                Check(saved!.AccessKey == "synthetic-host-secret" && (kind switch
                {
                    0 => saved.Workers[0].Name == "Reviewed worker" && saved.Workers[0].AccessKey == "synthetic-worker-secret" && !saved.Workers[0].RequiresConfiguration,
                    1 => saved.Executables[0].PrefixArguments.SequenceEqual(["reviewed-argument"]) && !saved.Executables[0].RequiresConfiguration,
                    _ => saved.McpServers[0].Arguments.SequenceEqual(["reviewed-mcp-argument"]) && !saved.McpServers[0].RequiresConfiguration
                }), "A confirmed form must activate the reviewed registration while retaining unrelated local values.");

                var savesBefore = calls.Count(x => x.Operation == "settings.save");
                var replacementCalls = 0;
                Modal(_ =>
                {
                    ConnectionChanged();
                    registrationCommandOverride = (_, _) => { replacementCalls++; throw new InvalidOperationException("Unexpected new-service command."); };
                    AdoptRegistrationSettings(new() { Name = "Fixture service B", Revision = 1 }, queueConnectionEpoch);
                    return true;
                });
                await Reject(Edit(kind), "A connection switch inside each modal must invalidate the edit.");
                Check(calls.Count(x => x.Operation == "settings.save") == savesBefore && replacementCalls == 0 && settings.Name == "Fixture service B" && settings.Workers.Count == 0,
                    "A stale form must not send either service a save or copy old credentials into the current settings.");
            }

            for (var kind = 0; kind < 3; kind++)
            {
                var backend = Fixture(); var savedRevision = -1; var saves = 0;
                registrationCommandOverride = (request, _) =>
                {
                    if (request.Operation == "settings.get") return Task.FromResult(CommandResult.Ok(backend));
                    saves++; savedRevision = CerebralJson.Read<HostSettings>(request.Arguments).Revision;
                    return Task.FromResult(CommandResult.Fail("Fixture revision conflict."));
                };
                Modal(_ => { backend.Revision++; backend.Name = "Concurrent edit"; AdoptRegistrationSettings(Copy(backend), queueConnectionEpoch); return true; });
                await Reject(Edit(kind), "Concurrent configuration edits must produce an explicit revision conflict.");
                Check(saves == 1 && savedRevision == 10 && settings.Revision == 11 && settings.Name == "Concurrent edit",
                    "Concurrent refreshes must not replace the form snapshot or promote its CAS revision.");
            }

            var renameBackend = Fixture();
            renameBackend.Workers[0].PeerToken = "synthetic-peer-token";
            renameBackend.Workers[0].SshHost = "fixture-ssh";
            var beforeRename = CerebralJson.Element(renameBackend).GetRawText();
            var renameSaves = 0;
            registrationCommandOverride = (request, _) =>
            {
                Check(request.ProfileId is null, "Renaming must stay on the coordinator owner route.");
                if (request.Operation == "settings.get") return Task.FromResult(CommandResult.Ok(renameBackend));
                Check(request.Operation == "settings.save", "Renaming must not configure or reprovision a remote worker.");
                var candidate = CerebralJson.Read<HostSettings>(request.Arguments);
                Check(candidate.Revision == renameBackend.Revision, "Renaming must retain the captured settings revision.");
                renameSaves++;
                renameBackend = Copy(candidate);
                renameBackend.Revision++;
                return Task.FromResult(CommandResult.Ok(renameBackend));
            };
            Modal(form =>
            {
                Check(form.Value("name") == "Fresh worker" && form.Value("id") == "worker",
                    "Renaming a stale grid row must use the fresh registered name and identity.");
                form.SetValue("name", "  GB10 studio  ");
                form.SetValue("id", "attempted-new-id");
                return true;
            });
            await RenameWorker(new() { Id = "worker", Name = "Stale name" }).WaitAsync(deadline.Token);
            Check(renameSaves == 1 && renameBackend.Workers[0].Name == "GB10 studio" &&
                  renameBackend.Workers[0].Id == "worker" && renameBackend.Workers[0].RequiresConfiguration,
                "Renaming must trim the label, keep the routing ID, and preserve an inactive restored connection.");
            var renamedCopy = Copy(renameBackend);
            renamedCopy.Revision = 10;
            renamedCopy.Workers[0].Name = "Fresh worker";
            Check(CerebralJson.Element(renamedCopy).GetRawText() == beforeRename,
                "A rename must leave credentials, endpoint, SSH settings, activation and all unrelated settings unchanged.");
            Modal(_ => false);
            await RenameWorker(new() { Id = "worker" }).WaitAsync(deadline.Token);
            Check(renameSaves == 1, "Canceling a rename must not save.");
            Modal(form => { form.SetValue("name", "  "); return true; });
            var emptyNameRejected = false;
            try { await RenameWorker(new() { Id = "worker" }).WaitAsync(deadline.Token); }
            catch (ArgumentException) { emptyNameRejected = true; }
            Check(emptyNameRejected && renameSaves == 1, "An empty worker display name must fail before saving.");
            Modal(_ => { ConnectionChanged(); return true; });
            await Reject(RenameWorker(new() { Id = "worker" }), "Switching connections while renaming must invalidate the captured edit.");
            Check(renameSaves == 1, "An invalidated rename must never save to either connection.");

            var missingSaves = 0; var missingDialogs = 0;
            registrationCommandOverride = (request, _) =>
            {
                if (request.Operation == "settings.get") return Task.FromResult(CommandResult.Ok(new HostSettings()));
                missingSaves++; return Task.FromResult(CommandResult.Ok(new HostSettings()));
            };
            Modal(_ => { missingDialogs++; return true; });
            for (var kind = 0; kind < 3; kind++) await Reject(Edit(kind), "A removed registration must not be recreated from a stale selected object.");
            Check(missingSaves == 0 && missingDialogs == 0, "A missing registration must stop before opening a populated form.");

            var slowSave = NewPending(); var oldSaves = 0; var newCalls = 0;
            registrationCommandOverride = (request, token) =>
            {
                if (request.Operation == "settings.get") return Task.FromResult(CommandResult.Ok(Fixture()));
                oldSaves++; return slowSave.Task.WaitAsync(token);
            };
            Modal(_ => true);
            var saving = Edit(0); outstanding.Add(saving);
            Check(oldSaves == 1 && !saving.IsCompleted, "The fixture must hold a real form save in flight.");
            ConnectionChanged();
            registrationCommandOverride = (_, _) => { newCalls++; throw new InvalidOperationException("Unexpected new-service command."); };
            AdoptRegistrationSettings(new() { Name = "Current service", Revision = 2 }, queueConnectionEpoch);
            slowSave.SetResult(CommandResult.Ok(Fixture(11)));
            await Reject(saving, "A late save acknowledgement must not attach to the new connection.");
            Check(oldSaves == 1 && newCalls == 0 && settings.Name == "Current service" && settings.Workers.Count == 0,
                "A save acknowledged after switching must not retry or overwrite the new service display.");

            var slowRead = NewPending();
            registrationCommandOverride = (_, token) => slowRead.Task.WaitAsync(token);
            var refreshing = RefreshSettings(); outstanding.Add(refreshing);
            ConnectionChanged(); AdoptRegistrationSettings(new() { Name = "New refresh service", Revision = 1 }, queueConnectionEpoch);
            slowRead.SetResult(CommandResult.Ok(Fixture(99)));
            await Reject(refreshing, "A late settings read must be rejected after switching services.");
            Check(settings.Name == "New refresh service" && settings.Revision == 1, "RefreshSettings must not publish a previous service's reply.");

            var olderRead = NewPending();
            registrationCommandOverride = (_, token) => olderRead.Task.WaitAsync(token);
            var olderRefresh = RefreshSettings(); outstanding.Add(olderRefresh);
            registrationCommandOverride = (_, _) => Task.FromResult(CommandResult.Ok(new HostSettings { Name = "Latest revision", Revision = 20 }));
            await RefreshSettings().WaitAsync(deadline.Token);
            olderRead.SetResult(CommandResult.Ok(Fixture(10)));
            await olderRefresh.WaitAsync(deadline.Token);
            Check(settings.Name == "Latest revision" && settings.Revision == 20, "A slower refresh on the same service must not downgrade a newer revision.");

            var unsafeSaves = 0;
            registrationCommandOverride = (request, _) =>
            {
                if (request.Operation == "settings.get") return Task.FromResult(CommandResult.Ok(Fixture()));
                unsafeSaves++; return Task.FromResult(CommandResult.Ok(Fixture(11)));
            };
            Modal(form => { form.SetValue("url", "http://user:synthetic@localhost:7011"); return true; });
            var unsafeRejected = false;
            try { await Edit(0).WaitAsync(deadline.Token); } catch (ArgumentException) { unsafeRejected = true; }
            Check(unsafeRejected && unsafeSaves == 0, "Restored worker activation must validate the raw endpoint before saving.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, registrationEditChecks = checks,
                evidence = "deterministic native WPF forms with synthetic command delegates", serviceCommandsExecuted = false, networkUsed = false, windowShown = false }));
            return checks;
        }
        finally
        {
            foreach (var completion in pending) completion.TrySetCanceled();
            foreach (var task in outstanding)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(1)); } catch { /* Observe only fixture tasks; assertions propagate above. */ }
            }
            foreach (var dialog in dialogs) dialog.Close();
            registrationCommandOverride = priorDispatch; registrationDialogOverride = priorDialog;
            settingsConnectionEpoch = -1;
            AdoptRegistrationSettings(priorSettings, queueConnectionEpoch);
            settingsConnectionEpoch = priorSettingsEpoch == queueConnectionEpoch ? priorSettingsEpoch : -1;
        }
    }
}
