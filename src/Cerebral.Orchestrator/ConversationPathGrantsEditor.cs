using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

/// <summary>Private owner-command projection; no desktop plugin SDK contract is added.</summary>
internal sealed record ConversationPathGrant(string Path, string Access);
internal sealed record ConversationInitialPath(string FirstRequestId, string ProfileId, int ExpectedProfileRevision,
    string? ProjectId, string? WorkingDirectory);
internal sealed record ConversationPathSnapshot(string Id, string ProfileId, string? ProjectId, string RootState,
    string? Root, long Revision, DateTimeOffset? UpdatedAt, IReadOnlyList<ConversationPathGrant> Grants,
    IReadOnlyList<ConversationPathGrant> EffectiveGrants)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConversationInitialPath? Initial { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InitialState { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SharedProjectsRoot { get; init; }

    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static ConversationPathSnapshot Read(JsonElement body, string id)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(body.GetRawText()) > 256 * 1024) throw Invalid();
            var marked = body.TryGetProperty("initial", out _) || body.TryGetProperty("initialState", out _);
            var hasShared = body.TryGetProperty("sharedProjectsRoot", out var shared);
            if (hasShared && shared.ValueKind != JsonValueKind.String) throw Invalid();
            string[] sharedField = hasShared ? ["sharedProjectsRoot"] : [];
            if (marked)
            {
                Fields(body, ["id", "profileId", "projectId", "rootState", "root", "revision", "updatedAt", "grants", "effectiveGrants", "initial", "initialState", .. sharedField]);
                var initial = body.GetProperty("initial");
                Fields(initial, "firstRequestId", "profileId", "expectedProfileRevision", "projectId", "workingDirectory");
                if (initial.GetProperty("expectedProfileRevision").ValueKind != JsonValueKind.Number ||
                    !initial.GetProperty("expectedProfileRevision").TryGetInt32(out var revision) || revision < 1 ||
                    body.GetProperty("initialState").ValueKind != JsonValueKind.String ||
                    body.GetProperty("initialState").GetString() != "accepted") throw Invalid();
            }
            else Fields(body, ["id", "profileId", "projectId", "rootState", "root", "revision", "updatedAt", "grants", "effectiveGrants", .. sharedField]);
            CheckGrantFields(body.GetProperty("grants"), 32);
            CheckGrantFields(body.GetProperty("effectiveGrants"), marked ? 128 : hasShared ? 34 : 33);
            var value = CerebralJson.Read<ConversationPathSnapshot>(body);
            Validate(value, id);
            return value;
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or OverflowException)
        { throw Invalid(); }
    }

    internal static void Validate(ConversationPathSnapshot value, string id)
    {
        if (value.Id != id || !Text(value.Id, 128) || !Text(value.ProfileId, 128) ||
            value.ProjectId is { } project && !Text(project, 128) ||
            value.RootState is not ("bound" or "unbound") ||
            (value.RootState == "bound") != (value.Root is not null) ||
            value.Revision < 0 || (value.Revision == 0) != (value.UpdatedAt is null) ||
            value.Grants is null || value.EffectiveGrants is null) throw Invalid();
        try
        {
            if (value.Root is { } root && CanonicalPath(root) != root) throw Invalid();
            if (value.SharedProjectsRoot is { } projects && CanonicalPath(projects) != projects) throw Invalid();
            var external = CanonicalRules(value.Grants, value.Root, value.SharedProjectsRoot);
            var effective = Effective(value.Root, external, value.SharedProjectsRoot);
            if (!ExactRules(value.Grants, external)) throw Invalid();
            if (value.Initial is { } initial)
            {
                // Initial preparation is immutable provenance. The effective folder may have
                // been explicitly changed or cleared later through owner Properties.
                if (value.InitialState != "accepted" || !Text(initial.FirstRequestId, 128) ||
                    initial.ProfileId != value.ProfileId || initial.ProjectId != value.ProjectId ||
                    (initial.WorkingDirectory is { } originalRoot && CanonicalPath(originalRoot) != originalRoot) ||
                    initial.ExpectedProfileRevision < 1 || value.Revision < 1 ||
                    !NarrowerEffective(value.EffectiveGrants, effective)) throw Invalid();
            }
            else if (value.InitialState is not null || !ExactRules(value.EffectiveGrants, effective)) throw Invalid();
        }
        catch (ArgumentException) { throw Invalid(); }
    }

    private static bool Inside(string path, string boundary) => PathComparer.Equals(path, boundary) ||
        path.StartsWith(System.IO.Path.EndsInDirectorySeparator(boundary) ? boundary : boundary + System.IO.Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // The response does not expose current profile roots. Prove narrowing of the requested policy,
    // never infer that the requested rules are the complete effective authority.
    private static bool NarrowerEffective(IReadOnlyList<ConversationPathGrant> returned,
        IReadOnlyList<ConversationPathGrant> requested)
    {
        if (returned.Count > 128) return false;
        var seen = new HashSet<string>(PathComparer);
        foreach (var rule in returned)
            if (rule is null || rule.Access is not ("read" or "write") ||
                CanonicalPath(rule.Path) != rule.Path || !seen.Add(rule.Path)) return false;
        if (!ExactRules(returned, returned.OrderBy(x => x.Path, PathComparer).ToArray())) return false;
        static string? AccessAt(string path, IReadOnlyList<ConversationPathGrant> rules) =>
            rules.Where(x => Inside(path, x.Path)).OrderByDescending(x => x.Path.Length).FirstOrDefault()?.Access;
        // Checking only returned entry roots would miss a dropped nested read-only exception.
        foreach (var path in returned.Select(x => x.Path).Concat(requested.Select(x => x.Path)).Distinct(PathComparer))
        {
            var effective = AccessAt(path, returned); var allowed = AccessAt(path, requested);
            if (effective is not null && (allowed is null || effective == "write" && allowed != "write")) return false;
        }
        return true;
    }

    // Structural UI checks only. The owner API independently rejects links and enforces policy.
    internal static string CanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(char.IsControl) ||
            !System.IO.Path.IsPathFullyQualified(path) || path.Split(['/', '\\']).Any(x => x == "..") ||
            OperatingSystem.IsWindows() && (path.StartsWith("\\\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0))
            throw new ArgumentException("Enter an absolute path of at most 2048 characters without traversal, device paths or control characters.");
        try { return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("Enter a valid absolute path."); }
    }

    internal static ConversationPathGrant[] CanonicalRules(IReadOnlyList<ConversationPathGrant> rules, string? root,
        string? sharedProjectsRoot = null)
    {
        if (rules.Count > 32) throw new ArgumentException("A chat can have at most 32 external path entries.");
        var seen = new HashSet<string>(PathComparer);
        return rules.Select(rule =>
        {
            if (rule is null || rule.Access is not ("read" or "write"))
                throw new ArgumentException("Choose Read only or Read and write for every path.");
            var path = CanonicalPath(rule.Path);
            if (root is not null && PathComparer.Equals(path, root) &&
                (sharedProjectsRoot is null || !Inside(root, sharedProjectsRoot)))
                throw new ArgumentException("The working root is already included. Change the folder in General instead of replacing it with a path rule.");
            if (!seen.Add(path)) throw new ArgumentException("Each canonical path can appear only once.");
            return new ConversationPathGrant(path, rule.Access);
        }).OrderBy(x => x.Path, PathComparer).ToArray();
    }

    internal static ConversationPathGrant[] Effective(string? root, IReadOnlyList<ConversationPathGrant> external,
        string? sharedProjectsRoot = null) =>
        ScopedPathAccessPolicy.IncludeChatDefaults(root, sharedProjectsRoot,
            external.Select(rule => new ScopedPathGrant(rule.Path,
                rule.Access == "write" ? ScopedPathAccess.Write : ScopedPathAccess.Read)).ToArray())
        .Select(rule => new ConversationPathGrant(rule.Path, rule.Access == ScopedPathAccess.Write ? "write" : "read"))
        .OrderBy(rule => rule.Path, PathComparer).ToArray();

    internal static bool SameRules(IReadOnlyList<ConversationPathGrant> left, IReadOnlyList<ConversationPathGrant> right) =>
        left.Count == right.Count && left.Zip(right).All(x => PathComparer.Equals(x.First.Path, x.Second.Path) && x.First.Access == x.Second.Access);
    private static bool ExactRules(IReadOnlyList<ConversationPathGrant> left, IReadOnlyList<ConversationPathGrant> right) =>
        left.Count == right.Count && left.Zip(right).All(x => x.First is not null && x.First.Path == x.Second.Path && x.First.Access == x.Second.Access);
    private static bool Text(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static void CheckGrantFields(JsonElement rules, int maximum)
    {
        if (rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() > maximum) throw Invalid();
        foreach (var rule in rules.EnumerateArray()) Fields(rule, "path", "access");
    }
    private static void Fields(JsonElement body, params string[] expected)
    {
        if (body.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in body.EnumerateObject())
            if (!names.Add(field.Name) || !expected.Contains(field.Name, StringComparer.Ordinal)) throw Invalid();
        if (names.Count != expected.Length) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("The service returned invalid or mismatched chat path permissions.");
}

/// <summary>
/// Lazy owner-bound editor. Its parent supplies a captured owner route, refreshes binding with its
/// existing guard, cancels/disposes on close, and awaits PendingLoad/PendingSave before releasing that route.
/// </summary>
internal sealed class ConversationPathGrantsEditor : UserControl, IDisposable
{
    private readonly string id, profileId, rootState;
    private readonly string? projectId, root;
    private readonly Func<bool> current;
    private readonly Func<string, CancellationToken, Task<JsonElement>> get;
    private readonly Func<string, long, IReadOnlyList<ConversationPathGrant>, CancellationToken, Task<CommandResult>> save;
    private readonly CancellationTokenSource lifetime;
    private readonly StackPanel rowsPanel = new();
    internal TextBlock EffectiveSummary { get; } = Ui.Text("", 13, Ui.Muted);
    private readonly List<PathDraftRow> rows = [];
    private ConversationPathSnapshot? saved;
    private bool loading, saving, locked, invalidBinding, disposed, replacingDraft;
    internal TextBox RootField { get; }
    internal Button EditWorkingDirectoryButton { get; }
    internal Button AddButton { get; }
    internal Button SaveButton { get; }
    internal Button CancelButton { get; }
    internal TextBlock Feedback { get; } = Ui.Text("Open Permissions to load this chat's path entries.", 13, Ui.Muted);
    internal TextBlock EntryCount { get; } = Ui.Text("", 13, Ui.Muted);
    internal IReadOnlyList<PathDraftRow> DraftRows => rows;
    internal ConversationPathSnapshot? Accepted { get; private set; }
    internal Task PendingLoad { get; private set; } = Task.CompletedTask;
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    internal ConversationPathGrantsEditor(string id, string profileId, string? projectId, string rootState,
        string? root, Func<bool> current, Func<string, CancellationToken, Task<JsonElement>> get,
        Func<string, long, IReadOnlyList<ConversationPathGrant>, CancellationToken, Task<CommandResult>> save,
        CancellationToken ownerLifetime, Action? editWorkingDirectory = null)
    {
        // Validate the independently captured Properties identity before any owner command is available.
        ConversationPathSnapshot.Validate(new(id, profileId, projectId, rootState, root, 0, null, [],
            ConversationPathSnapshot.Effective(root, [])), id);
        this.id = id; this.profileId = profileId; this.projectId = projectId; this.rootState = rootState; this.root = root;
        this.current = current; this.get = get; this.save = save;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ownerLifetime);
        Background = Ui.Surface; Foreground = Ui.Ink;
        AutomationProperties.SetAutomationId(this, "ConversationPathPermissions");
        RootField = Ui.Input(root ?? "Unbound — no saved working folder");
        RootField.IsReadOnly = true; RootField.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetName(RootField, root is null ? "Working folder — unbound" : "Working folder — edit in General");
        EditWorkingDirectoryButton = Ui.Button("Edit working folder in General", () =>
        { RefreshBinding(); if (!invalidBinding && !disposed) editWorkingDirectory?.Invoke(); });
        EditWorkingDirectoryButton.IsEnabled = editWorkingDirectory is not null;
        AutomationProperties.SetAutomationId(EditWorkingDirectoryButton, "ConversationPathsEditWorkingDirectory");
        AddButton = Ui.Button("Add path", () => AddDraft());
        SaveButton = Ui.Button("Save paths", () => { _ = SaveAsync(); }, true);
        CancelButton = Ui.Button("Cancel path changes", CancelDraft);
        AutomationProperties.SetAutomationId(AddButton, "ConversationPathsAdd");
        AutomationProperties.SetAutomationId(SaveButton, "ConversationPathsSave");
        AutomationProperties.SetAutomationId(CancelButton, "ConversationPathsCancel");
        AutomationProperties.SetAutomationId(Feedback, "ConversationPathsFeedback");
        AutomationProperties.SetLiveSetting(Feedback, AutomationLiveSetting.Polite);
        var body = new StackPanel();
        body.Children.Add(Ui.Text("Paths for this chat", 18));
        body.Children.Add(Ui.Text("Working folder · saved separately in General", 13, Ui.Muted));
        body.Children.Add(RootField);
        body.Children.Add(EditWorkingDirectoryButton);
        body.Children.Add(Ui.Text("Chat path rules", 15));
        body.Children.Add(Ui.Text("Read and write includes read. The most specific path entry controls access, so a nested Read only entry can restrict its parent.", 13, Ui.Muted));
        body.Children.Add(rowsPanel); body.Children.Add(EntryCount);
        body.Children.Add(Ui.Bar(AddButton, SaveButton, CancelButton));
        body.Children.Add(Feedback); body.Children.Add(EffectiveSummary);
        body.Children.Add(Ui.Text("These paths are enforced by Cerebral's filesystem APIs. They are not a script or process sandbox. Tool and capability permissions still apply.", 13, Ui.Muted));
        Content = body;
        RefreshBinding();
    }

    internal void RefreshBinding()
    {
        Dispatcher.VerifyAccess();
        if (disposed) return;
        if (!invalidBinding && (!current() || lifetime.IsCancellationRequested))
        {
            invalidBinding = true;
            // Cancellation can synchronously finish the transport continuation. Preserve its
            // dispatched/uncertain outcome instead of replacing it after Cancel returns.
            if (!saving && !loading) Status("The owner connection or chat binding changed. Close and reopen Properties.", true);
            lifetime.Cancel();
        }
        if (invalidBinding) EditWorkingDirectoryButton.IsEnabled = false;
        var editable = saved is not null && !loading && !saving && !locked && !invalidBinding;
        AddButton.IsEnabled = editable && rows.Count < 32;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index]; row.SetNumber(index + 1);
            row.PathInput.IsEnabled = editable; row.Access.IsEnabled = editable; row.RemoveButton.IsEnabled = editable;
        }
        var dirty = saved is not null && IsDirty();
        SaveButton.IsEnabled = editable && dirty;
        CancelButton.IsEnabled = editable && dirty;
        EntryCount.Text = $"{rows.Count} / 32 chat path rules";
    }

    internal Task LoadAsync()
    {
        Dispatcher.VerifyAccess(); RefreshBinding();
        if (disposed || invalidBinding || locked || saved is not null || loading) return PendingLoad;
        loading = true; Status("Loading path permissions…"); RefreshBinding();
        PendingLoad = LoadCoreAsync();
        return PendingLoad;
    }

    private async Task LoadCoreAsync()
    {
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            request.CancelAfter(TimeSpan.FromSeconds(30)); request.Token.ThrowIfCancellationRequested();
            var snapshot = ConversationPathSnapshot.Read(await get(id, request.Token), id);
            RefreshBinding(); request.Token.ThrowIfCancellationRequested();
            RequireIdentity(snapshot);
            if (invalidBinding || disposed) throw new OperationCanceledException();
            saved = snapshot; RefreshEffectiveSummary(); ReplaceDraft(); Status(snapshot.SharedProjectsRoot is null && snapshot.RootState == "unbound" && snapshot.Revision == 0
                ? "This older chat still uses profile paths. Save paths explicitly adopts chat permissions; an empty list denies all paths."
                : "Path entries loaded. Changes remain a draft until Save paths.");
        }
        catch (Exception error) when (Ordinary(error))
        { locked = true; Status("Path permissions could not be confirmed. Close and reopen Properties.", true); }
        finally { loading = false; RefreshBinding(); }
    }

    internal void AddDraft(string path = "", string access = "read")
    {
        Dispatcher.VerifyAccess(); RefreshBinding();
        if (saved is null || loading || saving || locked || invalidBinding || disposed || rows.Count >= 32) return;
        AppendRow(path, access); RefreshBinding();
    }

    private void AppendRow(string path, string access)
    {
        var row = new PathDraftRow(path, access, rows.Count + 1, () => DraftChanged(), value => RemoveDraft(value));
        rows.Add(row); rowsPanel.Children.Add(row.View);
    }

    internal void RemoveDraft(PathDraftRow row)
    {
        Dispatcher.VerifyAccess(); RefreshBinding();
        if (saving || locked || invalidBinding || disposed || !rows.Remove(row)) return;
        rowsPanel.Children.Remove(row.View); DraftChanged();
    }

    internal void CancelDraft()
    {
        Dispatcher.VerifyAccess(); RefreshBinding();
        if (saved is null || saving || locked || invalidBinding || disposed) return;
        ReplaceDraft(); Status(saved.SharedProjectsRoot is null && saved.RootState == "unbound" && saved.Revision == 0
            ? "Path draft discarded. Profile paths still apply until you explicitly Save paths. An empty save denies all paths."
            : "Path changes discarded. Saved permissions are unchanged."); RefreshBinding();
    }

    private void ReplaceDraft()
    {
        replacingDraft = true;
        try
        {
            rows.Clear(); rowsPanel.Children.Clear();
            foreach (var rule in saved!.Grants) AppendRow(rule.Path, rule.Access);
        }
        finally { replacingDraft = false; }
    }

    private void DraftChanged()
    {
        if (replacingDraft || disposed) return;
        Status("Unsaved path changes."); RefreshBinding();
    }

    private ConversationPathGrant[] Draft() => ConversationPathSnapshot.CanonicalRules(
        rows.Select(row => new ConversationPathGrant(row.PathInput.Text,
            (row.Access.SelectedItem as ComboBoxItem)?.Tag as string ?? "")).ToArray(), root, saved?.SharedProjectsRoot);

    private bool IsDirty()
    {
        try { return saved!.RootState == "unbound" && saved.Revision == 0 || !ConversationPathSnapshot.SameRules(saved.Grants, Draft()); }
        catch (ArgumentException) { return true; }
    }

    internal Task SaveAsync()
    {
        Dispatcher.VerifyAccess(); RefreshBinding();
        if (saving) return PendingSave;
        if (saved is null || loading || locked || invalidBinding || disposed) return PendingSave;
        ConversationPathGrant[] rules;
        try { rules = Draft(); }
        catch (ArgumentException error) { Status(error.Message, true); return Task.CompletedTask; }
        saving = true; Accepted = null; Status("Saving path permissions…"); RefreshBinding();
        PendingSave = SaveCoreAsync(rules);
        return PendingSave;
    }

    private async Task SaveCoreAsync(ConversationPathGrant[] rules)
    {
        var prior = saved!; var dispatched = false;
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            request.CancelAfter(TimeSpan.FromSeconds(30)); request.Token.ThrowIfCancellationRequested();
            RefreshBinding();
            if (invalidBinding || disposed) throw new OperationCanceledException();
            dispatched = true;
            var result = await save(id, prior.Revision, rules, request.Token);
            RefreshBinding(); request.Token.ThrowIfCancellationRequested();
            if (invalidBinding || disposed) throw new OperationCanceledException();
            if (!result.Success)
            {
                locked = true;
                Status("Path save was rejected. Close and reopen Properties to reload permissions.", true);
                return;
            }
            var confirmed = ConversationPathSnapshot.Read(result.Data, id);
            RequireIdentity(confirmed);
            var changed = prior.RootState == "unbound" && prior.Revision == 0 || !ConversationPathSnapshot.SameRules(prior.Grants, rules);
            if (confirmed.Revision != checked(prior.Revision + (changed ? 1 : 0)) ||
                !ConversationPathSnapshot.SameRules(confirmed.Grants, rules) ||
                !changed && confirmed.UpdatedAt != prior.UpdatedAt ||
                confirmed.Initial != prior.Initial || confirmed.InitialState != prior.InitialState ||
                confirmed.SharedProjectsRoot != prior.SharedProjectsRoot)
                throw new InvalidDataException("The path save receipt did not match the submitted rules and revision.");
            saved = confirmed; Accepted = confirmed; RefreshEffectiveSummary(); ReplaceDraft();
            Status("Path permissions saved for this chat.");
        }
        catch (Exception error) when (Ordinary(error))
        {
            locked |= dispatched;
            Status(dispatched
                ? "Path save is unconfirmed. It will not be retried. Close and reopen Properties to check saved permissions."
                : "Path changes were not sent. Close and reopen Properties.", true);
        }
        finally { saving = false; RefreshBinding(); }
    }

    private void RefreshEffectiveSummary()
    {
        EffectiveSummary.Text = saved is null || saved.Initial is null && saved.SharedProjectsRoot is null ? "" :
            (saved.SharedProjectsRoot is { } shared ? "Shared Projects root: " + shared + ". Chat path rules can restrict this default. " : "") +
            (saved.Initial is null ? "Effective access: " : "Effective access is limited by the current profile: ") +
            saved.EffectiveGrants.Count + " path entries. " +
            string.Join("; ", saved.EffectiveGrants.Take(8).Select(x => x.Path + " (" + (x.Access == "write" ? "read and write" : "read only") + ")")) +
            (saved.EffectiveGrants.Count > 8 ? "; additional effective entries are retained in the owner response." : "");
        AutomationProperties.SetName(EffectiveSummary, EffectiveSummary.Text);
    }

    private void RequireIdentity(ConversationPathSnapshot value)
    {
        if (value.ProfileId != profileId || value.ProjectId != projectId || value.RootState != rootState || value.Root != root)
            throw new InvalidDataException("The chat's saved permission identity changed.");
    }

    private void Status(string message, bool error = false)
    {
        Feedback.Text = message; Feedback.Foreground = error ? Ui.ErrorText : Ui.Muted;
    }
    private static bool Ordinary(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    public void Dispose()
    {
        Dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true; lifetime.Cancel(); lifetime.Dispose();
        AddButton.IsEnabled = SaveButton.IsEnabled = CancelButton.IsEnabled = EditWorkingDirectoryButton.IsEnabled = false;
        foreach (var row in rows) { row.PathInput.IsEnabled = row.Access.IsEnabled = row.RemoveButton.IsEnabled = false; }
    }

    internal sealed class PathDraftRow
    {
        internal TextBox PathInput { get; }
        internal ComboBox Access { get; } = new() { MinWidth = 155, Margin = new Thickness(0, 6, 8, 0) };
        internal Button RemoveButton { get; }
        internal Border View { get; }
        internal PathDraftRow(string path, string access, int number, Action changed, Action<PathDraftRow> remove)
        {
            PathInput = Ui.Input(path); PathInput.MaxLength = 2048;
            PathInput.ToolTip = "Absolute file or directory path";
            Access.Items.Add(new ComboBoxItem { Content = "Read only", Tag = "read" });
            Access.Items.Add(new ComboBoxItem { Content = "Read and write", Tag = "write" });
            Access.SelectedIndex = access == "write" ? 1 : 0;
            RemoveButton = Ui.Button("Remove", () => remove(this));
            SetNumber(number);
            var content = new StackPanel(); content.Children.Add(PathInput); content.Children.Add(Ui.Bar(Access, RemoveButton));
            View = new Border { Child = content, BorderBrush = Ui.Line, BorderThickness = new Thickness(1),
                Background = Ui.Surface, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8) };
            PathInput.TextChanged += (_, _) => changed();
            Access.SelectionChanged += (_, _) => changed();
        }
        internal void SetNumber(int number)
        {
            AutomationProperties.SetName(PathInput, $"External path {number}");
            AutomationProperties.SetName(Access, $"Access for external path {number}");
            AutomationProperties.SetName(RemoveButton, $"Remove external path {number}");
        }
    }
}
