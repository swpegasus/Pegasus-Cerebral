using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private async Task<int> CheckAgentProfileDraftAsync(CancellationToken token)
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        static AgentProfile Copy(AgentProfile value) => CerebralJson.Read<AgentProfile>(CerebralJson.Element(value));
        static string Snapshot(AgentProfile value) => JsonSerializer.Serialize(value, CerebralJson.Options);
        static IEnumerable<DependencyObject> Children(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            { yield return child; foreach (var nested in Children(child)) yield return nested; }
        }
        static Button SaveButton(AgentProfileEditor editor) => Children(editor.Form).OfType<Button>().Single(button => Equals(button.Content, "Save"));
        static CheckBox Images(AgentProfileEditor editor) => Children(editor.Form).OfType<CheckBox>().Single(check => Equals(check.Content, "Understand attached images"));
        static AgentProfile Original() => new()
        {
            Id = "draft-profile", Revision = 8, Name = "Original", Role = "helper",
            ModelId = "model", ModelWorkerId = "local", WorkerId = "local", FollowsDefaultModel = false,
            FollowsModelBudgets = false, ContextTokens = 6144, ResponseTokens = 1024,
            AllowedRoots = [@"C:\Fixture"], AllowedTools = ["file.read"], Capabilities = ["files.read"],
            PluginSettings = new(StringComparer.Ordinal)
            {
                ["fixture.other"] = CerebralJson.Element(new { retained = "unchanged" }),
                ["pegasus.browser"] = CerebralJson.Element(new { queueDepth = 3 }),
                ["cerebral.idle-compaction"] = CerebralJson.Element(new { schemaVersion = 1, enabled = false, triggerPercent = 70, retained = "unchanged" })
            }
        };
        var model = new ModelRecord { Id = "model", Name = "Fixture model", ContextTokens = 8192, RecommendedContextTokens = 4096 };
        var configuration = new HostSettings { ProjectsDirectory = @"C:\Fixture", DefaultModelId = model.Id, DefaultModelWorkerId = "local" };
        long epoch = 7;
        AgentProfileEditor Create(AgentProfile original,
            Func<CommandRequest, CancellationToken, Task<CommandResult>> save,
            Action<AgentProfile>? browser = null, bool missingModel = false, bool localPipe = true, bool advanced = false) =>
            new(this, original, configuration, [], missingModel ? [] : [model],
                missingModel ? "Fixture model library unavailable" : null,
                (_, _) => missingModel ? Task.FromException<List<ModelRecord>>(new IOException("Fixture offline")) : Task.FromResult(new List<ModelRecord> { model }),
                save, () => epoch, epoch, localPipe, token, advanced, browser ?? (_ => { }));
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();

        var original = Original(); var originalBody = Snapshot(original); var calls = 0; var browserCalls = 0;
        using (var editor = Create(original, (_, _) => { calls++; throw new InvalidOperationException("Cancel must not dispatch"); },
                   draft =>
                   {
                       browserCalls++;
                       if (browserCalls == 1) draft.PluginSettings["pegasus.browser"] = CerebralJson.Element(new { queueDepth = 6 });
                       // Second nested invocation is cancelled by the fixture and makes no change.
                   }, advanced: true))
        {
            Check(editor.EditorTab("advanced").IsSelected, "Advanced context action must select the same parent editor.");
            editor.Form.SetValue("name", "Changed name"); editor.Form.SetValue("instructions", "Changed guidance");
            editor.Form.SetValue("tools", "file.read\nfile.write"); editor.EditBrowserDraft(); editor.EditBrowserDraft();
            var candidate = editor.BuildCandidate();
            Check(candidate.Name == "Changed name" && candidate.Instructions == "Changed guidance" && candidate.AllowedTools.SequenceEqual(["file.read", "file.write"]),
                "Basic and Advanced values must form one candidate.");
            Check(candidate.PluginSettings["pegasus.browser"].GetProperty("queueDepth").GetInt32() == 6,
                "Nested acceptance changes only the parent draft and a later nested cancel preserves it.");
            Check(JsonSerializer.Serialize(candidate.PluginSettings["fixture.other"], CerebralJson.Options) == JsonSerializer.Serialize(original.PluginSettings["fixture.other"], CerebralJson.Options) &&
                  JsonSerializer.Serialize(candidate.PluginSettings["cerebral.idle-compaction"], CerebralJson.Options) == JsonSerializer.Serialize(original.PluginSettings["cerebral.idle-compaction"], CerebralJson.Options),
                "Unchanged optional module settings, including unexposed fields, must be retained exactly.");
            var idle = Children(editor.Form).OfType<CheckBox>().Single(check => Equals(check.Content, "Prepare context after final completion while I compose"));
            idle.IsChecked = true;
            var changedIdle = editor.BuildCandidate().PluginSettings["cerebral.idle-compaction"];
            Check(changedIdle.GetProperty("enabled").GetBoolean() && changedIdle.GetProperty("retained").GetString() == "unchanged",
                "Editing an idle adapter field retains unrelated fields in its opaque object.");
            idle.IsChecked = false;
            var layout = (FrameworkElement)editor.Form.Content;
            layout.Measure(new Size(680, 670)); layout.Arrange(new Rect(0, 0, 680, 670)); layout.UpdateLayout();
            Check(SaveButton(editor).ActualHeight > 0 && editor.EditorTab("advanced").ActualWidth > 0,
                "The combined Advanced form keeps a measured tab and fixed Save footer.");
            var captureIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--agent-draft-captures");
            if (captureIndex >= 0)
            {
                var destination = Path.GetFullPath(Environment.GetCommandLineArgs().ElementAtOrDefault(captureIndex + 1)
                    ?? throw new ArgumentException("Agent draft captures require an existing task-owned directory."));
                if (!Directory.Exists(destination)) throw new DirectoryNotFoundException(destination);
                foreach (var tab in new[] { "general", "advanced" })
                {
                    editor.EditorTab(tab).IsSelected = true; layout.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(680, 670, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(layout);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = new FileStream(Path.Combine(destination, "agent-" + tab + ".png"), FileMode.CreateNew, FileAccess.Write);
                    encoder.Save(output);
                }
            }
            var cancel = Children(editor.Form).OfType<Button>().Single(button => Equals(button.Content, "Discard unsaved draft"));
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(calls == 0 && Snapshot(original) == originalBody, "Native parent discard must neither dispatch nor mutate the original.");
        }

        calls = 0; AgentProfile? received = null;
        using (var editor = Create(original, (request, _) =>
        {
            calls++; Check(request.Operation == "agents.save", "One parent Save uses the existing admitted command.");
            received = CerebralJson.Read<AgentProfile>(request.Arguments);
            var saved = Copy(received); saved.Revision++; saved.ContextTokens = 4096; saved.ResponseTokens = 768;
            return Task.FromResult(new CommandResult(true, CerebralJson.Element(saved)));
        }))
        {
            editor.Form.SetValue("name", "Saved combined"); editor.Form.SetValue("seconds", "invalid");
            SaveButton(editor).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(calls == 0 && editor.Form.InlineStatusIsError && editor.Form.Raw("name") == "Saved combined",
                "Native Save validation must retain a repairable draft before dispatch.");
            editor.Form.SetValue("seconds", "900"); editor.Form.SetValue("instructions", "Saved guidance");
            Check(await editor.SaveAsync(), "A repaired combined draft must save through one callback.");
            Check(calls == 1 && received?.Revision == 8 && received.Id == original.Id && received.Instructions == "Saved guidance" && received.MaxSeconds == 900,
                "Save preserves exact profile identity/revision and submits Advanced fields once.");
            Check(editor.Acknowledged is { Revision: 9, ContextTokens: 4096, ResponseTokens: 768 } &&
                  editor.Draft.ContextTokens == 4096 && editor.Submitted?.ContextTokens == 6144,
                "Only the authoritative acknowledged body supplies the saved normalized state.");
            var refreshes = 0;
            await RefreshAcknowledgedAgentAsync(editor.Acknowledged!, queueConnectionEpoch,
                () => { refreshes++; throw new IOException("Fixture list refresh failed"); });
            Check(refreshes == 1 && editor.Acknowledged!.Revision == 9 && calls == 1,
                "List-refresh failure after acknowledgement must retain known Save and never repeat it.");
            editor.Form.DiscardDraftAndClose();
        }

        foreach (var genericFailure in new[] { false, true })
        {
            calls = 0;
            using var editor = Create(original, (_, _) =>
            {
                calls++; // Represents a fixture commit followed by a lost reply/publication failure.
                if (genericFailure) return Task.FromResult(CommandResult.Fail("Fixture publication failed after commit"));
                throw new LocalWorkerCommandUncertainException("agents.save", new IOException("Fixture lost reply"));
            });
            editor.Form.SetValue("instructions", "Retained unknown draft");
            Check(!await editor.SaveAsync() && editor.SaveUncertain && editor.Submitted?.Instructions == "Retained unknown draft" &&
                  Snapshot(editor.Baseline) == originalBody, "Unknown outcomes preserve submitted candidate and baseline.");
            editor.Form.SetSelection("modelMode", AgentModelBindingEditor.FollowDefault);
            SaveButton(editor).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!await editor.SaveAsync() && calls == 1 && !SaveButton(editor).IsEnabled &&
                  editor.Form.InlineStatusText.Contains("unknown", StringComparison.OrdinalIgnoreCase),
                "Selection changes and repeated native Save events cannot replay an unknown mutation.");
            editor.Form.DiscardDraftAndClose();
        }

        calls = 0;
        using (var editor = Create(original, (_, _) =>
        {
            calls++;
            if (calls == 1) throw new TimeoutException("Fixture local identity timeout; no command sent");
            var saved = Copy(original); saved.Revision++;
            return Task.FromResult(new CommandResult(true, CerebralJson.Element(saved)));
        }))
        {
            bool rejected = false;
            try { await editor.SaveAsync(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && !editor.SaveUncertain && editor.FieldsAreEnabled,
                "A proven local pre-dispatch timeout retains a repairable draft, not terminal uncertainty.");
            Check(await editor.SaveAsync() && calls == 2, "Explicit retry is permitted after a known pre-dispatch failure.");
            editor.Form.DiscardDraftAndClose();
        }

        calls = 0;
        using (var editor = Create(original, (_, _) => { calls++; throw new InvalidOperationException("Stale owner must not receive Save"); }))
        {
            epoch++;
            bool blocked = false;
            try { await editor.SaveAsync(); } catch (InvalidOperationException) { blocked = true; }
            Check(blocked && calls == 0 && Snapshot(editor.Baseline) == originalBody,
                "A changed connection blocks dispatch without discarding the baseline/draft.");
            editor.Form.DiscardDraftAndClose();
        }

        foreach (var followsDefault in new[] { false, true })
        {
            var savedProfile = Copy(original); savedProfile.FollowsDefaultModel = followsDefault; calls = 0;
            using var editor = Create(savedProfile, (request, _) =>
            {
                calls++; var saved = CerebralJson.Read<AgentProfile>(request.Arguments); saved.Revision++;
                return Task.FromResult(new CommandResult(true, CerebralJson.Element(saved)));
            }, missingModel: true);
            await editor.PendingModelRead.WaitAsync(token);
            editor.Form.SetValue("instructions", "Unchanged binding configuration edit");
            var candidate = editor.BuildCandidate();
            Check(candidate.ModelId == savedProfile.ModelId && candidate.ModelWorkerId == savedProfile.ModelWorkerId &&
                  candidate.ContextTokens == savedProfile.ContextTokens, "Missing metadata must retain exact saved binding and automatic limits.");
            Images(editor).IsChecked = true;
            bool blocked = false;
            try { editor.BuildCandidate(); } catch (InvalidOperationException) { blocked = true; }
            Check(blocked && calls == 0, "Missing metadata cannot authorize changed model requirements.");
            Images(editor).IsChecked = false;
            Check(await editor.SaveAsync() && calls == 1, "Unchanged configuration-only edit still calls ordinary backend admission.");
            editor.Form.DiscardDraftAndClose();
        }

        var pending = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        calls = 0;
        using (var editor = Create(original, (_, _) => { calls++; return pending.Task; }))
        {
            var saving = editor.SaveAsync();
            Check(!editor.FieldsAreEnabled && !Images(editor).IsEnabled, "Fields and nested actions must freeze while the submitted candidate is awaiting acknowledgement.");
            epoch++; var saved = Copy(original); saved.Revision++;
            pending.SetResult(new(true, CerebralJson.Element(saved)));
            Check(await saving && editor.Acknowledged?.Revision == 9 && !editor.OriginalConnectionIsCurrent,
                "An acknowledged save on the original connection remains known after UI owner selection changes.");
            var refreshes = 0;
            await RefreshAcknowledgedAgentAsync(saved, queueConnectionEpoch - 1, () => { refreshes++; return Task.CompletedTask; });
            Check(refreshes == 0 && calls == 1, "Original-owner acknowledgement must not refresh a newly selected service.");
            editor.Form.DiscardDraftAndClose();
        }

        calls = 0;
        using (var editor = Create(original, (_, _) =>
        {
            calls++; var wrong = Copy(original); wrong.Id = "other-profile"; wrong.Revision++;
            return Task.FromResult(new CommandResult(true, CerebralJson.Element(wrong)));
        }))
        {
            Check(!await editor.SaveAsync() && editor.SaveUncertain && editor.Acknowledged is null &&
                  !await editor.SaveAsync() && calls == 1, "Wrong-profile acknowledgement must remain unknown without resend.");
            editor.Form.DiscardDraftAndClose();
        }
        var priorUiEpoch = queueConnectionEpoch; var priorAgentRows = agents.ItemsSource;
        try
        {
            foreach (var failure in new[] { false, true })
            {
                var originalEpoch = queueConnectionEpoch;
                var listReply = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var readCalls = 0;
                Task<CommandResult> ReadOriginalOwner(CommandRequest request, CancellationToken cancellation)
                {
                    readCalls++;
                    Check(request.Operation == "agents.list", "Acknowledged refresh uses a read on the captured owner dispatch.");
                    return listReply.Task;
                }
                var saved = Copy(original); saved.Revision++;
                var refreshing = RefreshAcknowledgedAgentAsync(saved, originalEpoch,
                    () => RefreshAgentRowsOnConnectionAsync(originalEpoch, ReadOriginalOwner, token));
                queueConnectionEpoch++;
                var currentOwnerRows = new List<AgentProfile> { new() { Id = "new-owner", Name = "New owner row" } };
                agents.ItemsSource = currentOwnerRows;
                SetStatus("Fixture current-owner status");
                if (failure) listReply.SetException(new IOException("Fixture original-owner refresh failed"));
                else listReply.SetResult(new(true, CerebralJson.Element(new List<AgentProfile> { saved })));
                await refreshing.WaitAsync(token);
                Check(readCalls == 1 && ReferenceEquals(agents.ItemsSource, currentOwnerRows),
                    "A delayed original-owner list must never replace rows after the UI epoch changes.");
                Check(Children(this).OfType<TextBlock>().Any(text => text.Text == "Fixture current-owner status"),
                    "Delayed refresh success or failure must not overwrite current-owner status.");
            }
        }
        finally { queueConnectionEpoch = priorUiEpoch; agents.ItemsSource = priorAgentRows; }

        var canonicalProfile = Copy(original); canonicalProfile.RequiredModelCapabilities = [" IMAGE-INPUT ", "TEXT-GENERATION"];
        using (var editor = Create(canonicalProfile, (_, _) => throw new InvalidOperationException("Cancelled configuration must not dispatch")))
        {
            Check(Images(editor).IsChecked == true &&
                  editor.BuildCandidate().RequiredModelCapabilities.SequenceEqual(canonicalProfile.RequiredModelCapabilities, StringComparer.Ordinal),
                "Valid canonical capability variants remain coherent and unchanged during unrelated editing.");
            editor.Form.DiscardDraftAndClose();
        }
        var remote = Copy(original); remote.ModelWorkerId = "saved-remote";
        calls = 0;
        using (var editor = Create(remote, (_, _) =>
        {
            calls++; return Task.FromResult(CommandResult.Fail("Fixture remote owner unreachable"));
        }, missingModel: true))
        {
            await editor.PendingModelRead.WaitAsync(token);
            editor.Form.SetValue("instructions", "Remote configuration draft");
            Check(editor.BuildCandidate().ModelWorkerId == remote.ModelWorkerId,
                "An unavailable saved remote binding must remain exact before normal backend validation.");
            Check(!await editor.SaveAsync() && calls == 1 && editor.SaveUncertain &&
                  editor.Submitted?.Instructions == "Remote configuration draft",
                "Remote admission failure retains the submitted draft and never fabricates local approval.");
            editor.Form.DiscardDraftAndClose();
        }
        Check(Snapshot(original) == originalBody, "All fixture paths preserve the owner-supplied original body.");
        return checks;
    }

    internal (Task Completion, Action<bool> Release) BeginDeferredAgentAcknowledgementForChecks(Func<Task> resumeSetup)
    {
        Dispatcher.VerifyAccess();
        if (!IsConnected || ExistingWorkerAttachment is null)
            throw new InvalidOperationException("Deferred acknowledgement requires the connected owned Configuration fixture.");
        var owner = Client;
        var connection = queueConnectionEpoch;
        var reply = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new AgentProfile { Id = "fixture-agent", Name = "Previous-owner saved profile", Revision = 17,
            Role = "lead", ModelId = "fixture-model" };
        Task<CommandResult> Read(CommandRequest request, CancellationToken _) => request.Operation == "agents.list"
            ? reply.Task : Task.FromException<CommandResult>(new InvalidOperationException("Unexpected acknowledgement read."));
        var completion = CompleteAcknowledgedAgentAsync(saved, connection, owner,
            () => RefreshAgentRowsOnConnectionAsync(connection, Read, Lifetime.Token, owner), resumeSetup);
        void Release(bool fail)
        {
            if (fail) reply.TrySetException(new IOException("Previous-owner agent list failed."));
            else reply.TrySetResult(CommandResult.Ok(new[] { saved }));
        }
        return (completion, Release);
    }
}

