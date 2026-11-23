using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private sealed record InstructionTarget(string Label, string? ProfileId = null, string? Role = null)
    {
        public string Key => ProfileId is null ? "role:" + Role : "profile:" + ProfileId;
    }
    private sealed record InstructionBinding(InstructionTarget Target, InstructionDraft Draft);
    private readonly ComboBox instructionTarget = new() { DisplayMemberPath = "Label", MinWidth = 300 };
    private readonly TextBox instructionEditor = Ui.Json();
    private readonly TextBlock instructionLocation = Ui.Text("Select a shared, Cerebral, helper, compactor, or profile prompt.", 12, Ui.Muted);
    private readonly TextBlock instructionSaveState = Ui.Text("Changes save automatically after a short pause.", 12, Ui.Muted);
    private readonly Dictionary<string, InstructionBinding> instructionDrafts = new(StringComparer.Ordinal);
    private readonly DispatcherTimer instructionSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private CancellationTokenSource instructionConnection = new();
    private long instructionGeneration;
    private long instructionLoadVersion;
    private InstructionBinding? activeInstructions;
    private bool hydratingInstructions;
    private bool closingInstructions;
    private bool allowInstructionClose;
    private Func<CommandRequest, CancellationToken, Task<CommandResult>>? instructionCommandOverride;
    private Action<string, string>? instructionShowSavedOverride;
    private Func<MessageBoxResult>? instructionConflictChoiceOverride;

    private UIElement InstructionsPage()
    {
        AutomationProperties.SetAutomationId(instructionTarget, "InstructionTarget");
        AutomationProperties.SetAutomationId(instructionEditor, "InstructionEditor");
        AutomationProperties.SetAutomationId(instructionLocation, "InstructionLocation");
        AutomationProperties.SetAutomationId(instructionSaveState, "InstructionSaveState");
        instructionEditor.IsReadOnly = true;
        instructionEditor.MaxLength = 65_536;
        instructionTarget.SelectionChanged += async (_, e) =>
        {
            if (!hydratingInstructions && ReferenceEquals(e.OriginalSource, instructionTarget))
                await InstructionUiActionAsync(() => ActivateInstructionsAsync(false));
        };
        instructionEditor.TextChanged += (_, _) =>
        {
            if (hydratingInstructions || activeInstructions is null) return;
            activeInstructions.Draft.Edit(instructionEditor.Text);
            UpdateInstructionState(); ScheduleInstructionSave();
        };
        instructionSaveTimer.Tick += async (_, _) =>
        {
            instructionSaveTimer.Stop(); await InstructionUiActionAsync(FlushInstructionDraftsAsync);
        };
        Closing += async (_, e) =>
        {
            if (allowInstructionClose || !instructionDrafts.Values.Any(x => x.Draft.NeedsAttention)) return;
            e.Cancel = true;
            if (closingInstructions) return;
            closingInstructions = true; instructionSaveTimer.Stop();
            await Task.Yield(); // Let the cancelled Closing event finish before a later Close call.
            try
            {
                try { await FlushInstructionDraftsAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
                catch (Exception error) { instructionSaveState.Text = "Prompt drafts are not saved. " + error.Message; }
                var pending = instructionDrafts.Values.Where(x => x.Draft.NeedsAttention).ToArray();
                if (pending.Length > 0 && MessageBox.Show(this,
                    $"{pending.Length} prompt draft(s) are not confirmed saved. Close without saving them?\n\nChoose No to keep editing or use Retained drafts to copy them.",
                    "Unsaved prompts", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                allowInstructionClose = true; Close();
            }
            finally { closingInstructions = false; }
        };
        Closed += (_, _) => { instructionSaveTimer.Stop(); instructionConnection.Cancel(); instructionConnection.Dispose(); };
        var save = Ui.Button("Save now", () => Run(SaveInstructions), true);
        AutomationProperties.SetAutomationId(save, "SaveInstructions");
        var load = Ui.Button("Review saved version", () => Run(LoadInstructions));
        AutomationProperties.SetAutomationId(load, "LoadInstructions");
        instructionLocation.TextWrapping = TextWrapping.Wrap;
        instructionSaveState.TextWrapping = TextWrapping.Wrap;
        instructionLocation.Margin = new Thickness(0, 0, 0, 4);
        var state = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        state.Children.Add(instructionLocation);
        state.Children.Add(instructionSaveState);
        var content = new DockPanel();
        DockPanel.SetDock(state, Dock.Top);
        content.Children.Add(state);
        content.Children.Add(instructionEditor);
        return Ui.Page("Agent instructions", "Prompts save to SQLite after a short pause. Changing sources preserves pending drafts. Accepted workflows keep their original snapshot; saved grants still control access.",
            content, instructionTarget, save, load, Ui.Button("Retained drafts…", ShowInstructionDrafts),
            Ui.Button("Refresh sources", () => Run(RefreshInstructionTargets)));
    }

    private async Task InstructionUiActionAsync(Func<Task> action)
    {
        var generation = instructionGeneration;
        try { await action(); }
        catch (OperationCanceledException) when (generation != instructionGeneration || instructionConnection.IsCancellationRequested || Lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation != instructionGeneration) return;
            instructionSaveState.Text = error.Message; instructionSaveState.Foreground = Ui.ErrorBorder;
        }
    }

    private Func<CommandRequest, CancellationToken, Task<CommandResult>> InstructionDispatch()
    {
        if (instructionCommandOverride is not null) return instructionCommandOverride;
        var client = Client;
        return (request, token) => client.CommandAsync(request.Operation, request.Arguments, request.ProfileId, token);
    }

    private static async Task<T> InstructionRead<T>(Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch,
        string operation, object arguments, CancellationToken token)
    {
        var result = await dispatch(new(operation, CerebralJson.Element(arguments)), token);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "The prompt operation was rejected.");
        return CerebralJson.Read<T>(result.Data);
    }

    private static async Task<InstructionSnapshot> ReadInstructionAsync(InstructionTarget target,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch, CancellationToken token)
    {
        if (target.ProfileId is null)
        {
            var value = await InstructionRead<SystemInstructionDocument>(dispatch, "instructions.get", new { role = target.Role }, token);
            return new(value.Content, value.Sha256, value.Path);
        }
        var profile = await InstructionRead<AgentInstructionDocument>(dispatch, "agents.instructions.get", new { profileId = target.ProfileId }, token);
        if (profile.ProfileId != target.ProfileId) throw new InvalidDataException("The service returned a different prompt profile.");
        return new(profile.Content, profile.Revision.ToString(CultureInfo.InvariantCulture), "SQLite agent prompt · " + target.Label);
    }

    private async Task RefreshInstructionTargets()
    {
        var generation = instructionGeneration;
        var selected = (instructionTarget.SelectedItem as InstructionTarget)?.Key;
        var profiles = await InstructionRead<List<AgentProfile>>(InstructionDispatch(), "agents.list", new { }, instructionConnection.Token);
        if (generation != instructionGeneration) return;
        var targets = new List<InstructionTarget>
        {
            new("Shared prompt · everyone", Role: "system"), new("Cerebral prompt · lead agent", Role: "lead"),
            new("Helper prompt · helper agents", Role: "helper"), new("Compactor prompt · context compaction", Role: "compactor")
        };
        targets.AddRange(profiles.Select(x => new InstructionTarget(x.Name + " · " + x.Role, x.Id)));
        hydratingInstructions = true;
        try { instructionTarget.ItemsSource = targets; instructionTarget.SelectedItem = targets.FirstOrDefault(x => x.Key == selected) ?? targets[0]; }
        finally { hydratingInstructions = false; }
        await ActivateInstructionsAsync(true);
    }

    private async Task ActivateInstructionsAsync(bool refreshClean)
    {
        var target = instructionTarget.SelectedItem as InstructionTarget;
        if (target is null) return;
        var generation = instructionGeneration; var version = ++instructionLoadVersion;
        var key = generation.ToString(CultureInfo.InvariantCulture) + ":" + target.Key;
        activeInstructions = null; instructionEditor.IsReadOnly = true;
        if (instructionDrafts.TryGetValue(key, out var binding) && (!refreshClean || binding.Draft.NeedsAttention))
        {
            PresentInstructions(binding); return;
        }
        var snapshot = await ReadInstructionAsync(target, InstructionDispatch(), instructionConnection.Token);
        if (generation != instructionGeneration || version != instructionLoadVersion ||
            (instructionTarget.SelectedItem as InstructionTarget)?.Key != target.Key) return;
        if (binding is null)
        {
            foreach (var clean in instructionDrafts.Where(x => !x.Value.Draft.NeedsAttention && x.Key != key).Select(x => x.Key).ToArray()) instructionDrafts.Remove(clean);
            if (instructionDrafts.Count >= 64) throw new InvalidOperationException("There are 64 retained prompt drafts. Save or resolve existing drafts before opening another source.");
            binding = new(target, new InstructionDraft(key, target.Label, generation, snapshot)); instructionDrafts.Add(key, binding);
        }
        else binding.Draft.Hydrate(snapshot);
        PresentInstructions(binding);
    }

    private void PresentInstructions(InstructionBinding binding)
    {
        activeInstructions = binding; hydratingInstructions = true;
        try { instructionEditor.Text = binding.Draft.Content; instructionEditor.IsReadOnly = binding.Draft.Generation != instructionGeneration; }
        finally { hydratingInstructions = false; }
        instructionLocation.Text = binding.Draft.Baseline.Source + " · maximum 32 KiB UTF-8";
        UpdateInstructionState(); ScheduleInstructionSave();
    }

    private void UpdateInstructionState()
    {
        var draft = activeInstructions?.Draft;
        var retained = instructionDrafts.Values.Count(x => x.Draft.NeedsAttention && x.Draft != draft);
        instructionSaveState.Text = (draft?.Error ?? (draft?.SavePending == true ? "Saving…" : draft?.Dirty == true ? "Unsaved changes · saving after a short pause…" : "Saved · new workflow runs use this prompt.")) +
            (retained == 0 ? "" : $" · {retained} other retained draft(s)");
        instructionSaveState.Foreground = draft?.Error is null ? Ui.Muted : Ui.ErrorBorder;
    }

    private void ScheduleInstructionSave()
    {
        instructionSaveTimer.Stop();
        if (!closingInstructions && instructionDrafts.Values.Any(x => x.Draft.Generation == instructionGeneration && x.Draft.CanAutosave)) instructionSaveTimer.Start();
    }

    private async Task SaveInstructions()
    {
        instructionSaveTimer.Stop();
        if (activeInstructions?.Draft.Blocked == true)
        {
            SetStatus("This draft needs attention. Review the saved version before another save; your text remains in the editor.", true); return;
        }
        await FlushInstructionDraftsAsync();
    }

    private async Task FlushInstructionDraftsAsync()
    {
        var generation = instructionGeneration;
        foreach (var binding in instructionDrafts.Values.Where(x => x.Draft.Generation == generation).ToArray())
        {
            if (generation != instructionGeneration) break;
            if (binding.Draft.SavePending) await binding.Draft.Saving!;
            if (generation != instructionGeneration) return;
            if (!binding.Draft.CanAutosave) continue;
            var dispatch = InstructionDispatch();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token, instructionConnection.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var save = binding.Draft.SaveAsync(async (content, expected, token) =>
            {
                if (binding.Target.ProfileId is null)
                {
                    var value = await InstructionRead<SystemInstructionDocument>(dispatch, "instructions.save",
                        new { content, expectedSha256 = expected, role = binding.Target.Role }, token);
                    return new(value.Content, value.Sha256, value.Path);
                }
                var profile = await InstructionRead<AgentInstructionDocument>(dispatch, "agents.instructions.save",
                    new { profileId = binding.Target.ProfileId, content, expectedRevision = int.Parse(expected, CultureInfo.InvariantCulture) }, token);
                if (profile.ProfileId != binding.Target.ProfileId) throw new InvalidDataException("The service returned a different prompt profile.");
                return new(profile.Content, profile.Revision.ToString(CultureInfo.InvariantCulture), "SQLite agent prompt · " + binding.Target.Label);
            }, () => generation == instructionGeneration && !instructionConnection.IsCancellationRequested, deadline.Token);
            UpdateInstructionState(); await save;
            if (generation != instructionGeneration) return;
            UpdateInstructionState();
            if (activeInstructions == binding) instructionLocation.Text = binding.Draft.Baseline.Source + " · maximum 32 KiB UTF-8";
            if (binding.Draft.Uncertain) break;
        }
        ScheduleInstructionSave();
    }

    private async Task LoadInstructions()
    {
        instructionSaveTimer.Stop();
        var binding = activeInstructions ?? throw new InvalidOperationException("Select and load a prompt first.");
        if (binding.Draft.SavePending) await binding.Draft.Saving!;
        if (binding.Draft.Generation != instructionGeneration || activeInstructions != binding) return;
        var generation = instructionGeneration;
        var current = await ReadInstructionAsync(binding.Target, InstructionDispatch(), instructionConnection.Token);
        if (generation != instructionGeneration || activeInstructions != binding) return;
        if (!binding.Draft.NeedsAttention) binding.Draft.UseSaved(current);
        else if (!binding.Draft.Review(current))
        {
            if (instructionShowSavedOverride is { } showSaved) showSaved(binding.Target.Label, current.Content);
            else Ui.ShowText(this, "Saved prompt · " + binding.Target.Label, current.Content);
            if (generation != instructionGeneration || activeInstructions != binding) return;
            var choice = instructionConflictChoiceOverride?.Invoke() ?? MessageBox.Show(this, "Your draft differs from a newer saved prompt.\n\nYes: keep your draft and save it against this reviewed revision.\nNo: use the saved version and discard this draft.\nCancel: keep the draft without saving.",
                "Resolve prompt changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (generation != instructionGeneration || activeInstructions != binding) return;
            if (choice == MessageBoxResult.Yes) binding.Draft.KeepDraftAgainst(current);
            else if (choice == MessageBoxResult.No) binding.Draft.UseSaved(current);
        }
        PresentInstructions(binding);
    }

    private void ShowInstructionDrafts()
    {
        var drafts = instructionDrafts.Values.Where(x => x.Draft.NeedsAttention).ToArray();
        Ui.ShowText(this, "Retained prompt drafts", drafts.Length == 0 ? "All loaded prompts are saved." : string.Join("\n\n", drafts.Select(x =>
            $"{x.Target.Label} · connection generation {x.Draft.Generation}\n{x.Draft.Error}\n\n{x.Draft.Content}")));
    }

    private void InstructionsConnectionChanged()
    {
        instructionSaveTimer.Stop(); instructionGeneration++; instructionLoadVersion++;
        foreach (var draft in instructionDrafts.Values) draft.Draft.InvalidateConnection();
        instructionConnection.Cancel(); instructionConnection.Dispose(); instructionConnection = new();
        activeInstructions = null; instructionEditor.IsReadOnly = true;
        instructionSaveState.Text = "Connection changed. Unsaved drafts remain available in Retained drafts and will not be sent to this connection.";
    }
}
