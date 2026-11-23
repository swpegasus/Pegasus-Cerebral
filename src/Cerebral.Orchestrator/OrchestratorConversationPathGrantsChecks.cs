using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>Deterministic WPF/owner-command fixtures only: no windows, Worker, paths or saved grants are modified.</summary>
    public async Task<int> CheckConversationPathGrantsAsync()
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run path editor checks on the active WPF dispatcher.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        const string target = "path-editor-target";
        const string root = @"C:\Fixture\Conversation";
        const string external = @"C:\Fixture\Reference";
        var initial = new ConversationPathSnapshot(target, "fixture-lead", "fixture-project", "bound", root, 0, null,
            [], ConversationPathSnapshot.Effective(root, []));
        static ConversationPathSnapshot Receipt(ConversationPathSnapshot prior, IReadOnlyList<ConversationPathGrant> rules,
            long revision) => prior with { Revision = revision, UpdatedAt = revision == 0 ? null : DateTimeOffset.UnixEpoch.AddSeconds(revision),
                Grants = rules, EffectiveGrants = ConversationPathSnapshot.Effective(prior.Root, rules, prior.SharedProjectsRoot) };
        ConversationPathGrantsEditor Editor(ConversationPathSnapshot snapshot,
            Func<string, CancellationToken, Task<JsonElement>>? get = null,
            Func<string, long, IReadOnlyList<ConversationPathGrant>, CancellationToken, Task<CommandResult>>? save = null,
            Func<bool>? current = null) => new(snapshot.Id, snapshot.ProfileId, snapshot.ProjectId, snapshot.RootState, snapshot.Root,
                current ?? (() => true), get ?? ((_, _) => Task.FromResult(CerebralJson.Element(snapshot))),
                save ?? ((_, _, _, _) => throw new InvalidOperationException("Unexpected fixture save.")), deadline.Token);
        void Reject(ConversationPathSnapshot snapshot, string label)
        {
            try { ConversationPathSnapshot.Read(CerebralJson.Element(snapshot), target); }
            catch (InvalidDataException) { checks++; return; }
            throw new InvalidOperationException(label);
        }
        try
        {
            Reject(initial with { Id = "foreign" }, "A foreign chat snapshot must reject.");
            Reject(initial with { Root = null }, "A bound snapshot requires its locked root.");
            Reject(initial with { RootState = "unbound" }, "An unbound snapshot must not carry a root.");
            Reject(initial with { Revision = 1 }, "A saved revision requires its timestamp.");
            Reject(initial with { EffectiveGrants = [] }, "The returned effective rules must retain the original RW root.");
            Reject(initial with { Grants = [new(root + "\\", "read")] }, "A root alias cannot enter the external draft.");
            Reject(initial with { Grants = [new(external, "READ")] }, "Access modes are exact lowercase wire values.");
            Reject(initial with { Grants = [new(external, "read"), new(external + "\\", "write")] },
                "Canonical duplicate aliases cannot enter the external rules.");
            var duplicateFields = """{"id":"path-editor-target","id":"path-editor-target","profileId":"fixture-lead","projectId":"fixture-project","rootState":"bound","root":"C:\\Fixture\\Conversation","revision":0,"updatedAt":null,"grants":[],"effectiveGrants":[{"path":"C:\\Fixture\\Conversation","access":"write"}]}""";
            using var duplicateDocument = JsonDocument.Parse(duplicateFields);
            try { ConversationPathSnapshot.Read(duplicateDocument.RootElement, target); throw new InvalidOperationException("Duplicate snapshot fields must reject."); }
            catch (InvalidDataException) { checks++; }

            const string projects = @"C:\Fixture\Projects";
            var sharedRoot = projects + @"\Source\chat-a";
            var shared = initial with { Root = sharedRoot, SharedProjectsRoot = projects,
                EffectiveGrants = ConversationPathSnapshot.Effective(sharedRoot, [], projects) };
            Check(ConversationPathSnapshot.Read(CerebralJson.Element(shared), target).SharedProjectsRoot == projects &&
                shared.EffectiveGrants.Single() == new ConversationPathGrant(projects, "write"),
                "The owner projection exposes shared Projects access separately from the chat's starting directory.");
            var restricted = Receipt(shared, [new(projects, "read")], 1);
            Check(ConversationPathSnapshot.Read(CerebralJson.Element(restricted), target).EffectiveGrants.Single().Access == "read",
                "The desktop accepts a read-only shared root without inventing a writable starting-folder exception.");
            Reject(restricted with { EffectiveGrants = [new(projects, "read"), new(sharedRoot, "write")] },
                "The desktop rejects a receipt that widens the starting folder beneath a read-only Core rule.");
            var ownFolderRule = Receipt(shared, [new(sharedRoot, "read")], 1);
            Check(ConversationPathSnapshot.Read(CerebralJson.Element(ownFolderRule), target).Grants.Single().Path == sharedRoot,
                "Chat properties may restrict the starting subfolder while the saved working-directory identity stays fixed.");
            Reject(shared with { SharedProjectsRoot = projects + "\\" },
                "Shared-root aliases must be canonical in an owner response.");
            using (var editor = Editor(shared, save: (_, _, rules, _) =>
                Task.FromResult(CommandResult.Ok(Receipt(shared, rules, 1)))))
            {
                await editor.LoadAsync();
                Check(editor.EffectiveSummary.Text.Contains(projects, StringComparison.Ordinal),
                    "Properties visibly lists the shared Core root even without initial-preparation metadata.");
                editor.AddDraft(sharedRoot, "read"); await editor.SaveAsync();
                Check(editor.Accepted?.Grants.Single().Access == "read" && editor.RootField.Text == sharedRoot,
                    "An owner-confirmed starting-folder restriction is saved without rebinding that folder.");
            }
            using (var editor = Editor(shared, save: (_, _, rules, _) =>
                Task.FromResult(CommandResult.Ok(Receipt(shared, rules, 1) with { SharedProjectsRoot = @"C:\Fixture\OtherProjects",
                    EffectiveGrants = ConversationPathSnapshot.Effective(sharedRoot, rules, @"C:\Fixture\OtherProjects") }))))
            {
                await editor.LoadAsync(); editor.AddDraft(external); await editor.SaveAsync();
                Check(editor.Accepted is null && !editor.SaveButton.IsEnabled,
                    "A shared-root change during Save cannot be adopted from an unrelated receipt.");
            }

            var reads = 0; var saves = new List<(string Id, long Revision, IReadOnlyList<ConversationPathGrant> Rules)>();
            var held = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var editor = Editor(initial, (id, _) =>
            {
                reads++; Check(id == target, "The lazy read retains the exact captured chat ID.");
                return Task.FromResult(CerebralJson.Element(initial));
            }, (id, revision, rules, token) =>
            {
                saves.Add((id, revision, rules.ToArray())); return held.Task.WaitAsync(token);
            }))
            {
                Check(reads == 0 && !editor.AddButton.IsEnabled, "Construction does not eagerly read or permit unloaded edits.");
                await editor.LoadAsync(); await editor.LoadAsync();
                Check(reads == 1 && editor.RootField.IsReadOnly && editor.RootField.Text == root,
                    "Lazy mounting reads once and displays the effective folder separately from permission-rule drafts.");
                Check(AutomationProperties.GetName(editor.RootField).Contains("edit in General", StringComparison.Ordinal),
                    "The folder display explains where its separate revision-checked edit is available.");
                editor.AddDraft(external + "\\", "read");
                var row = editor.DraftRows.Single();
                Check((row.Access.SelectedItem as ComboBoxItem)?.Tag as string == "read" &&
                    AutomationProperties.GetName(row.PathInput) == "External path 1",
                    "The mode picker has explicit read/write wire tags and accessible row labels.");
                editor.Measure(new Size(410, 500)); editor.Arrange(new Rect(0, 0, 410, 500)); editor.UpdateLayout();
                Check(editor.IsMeasureValid && editor.IsArrangeValid && editor.SaveButton.IsEnabled,
                    "The draft lays out at narrow width without showing a window.");
                var pending = editor.SaveAsync(); var duplicate = editor.SaveAsync();
                Check(ReferenceEquals(pending, duplicate) && saves.Count == 1 &&
                    saves[0].Id == target && saves[0].Revision == 0 &&
                    saves[0].Rules.Single() == new ConversationPathGrant(external, "read") && !editor.CancelButton.IsEnabled,
                    "Save sends canonical external rules and exact CAS identity once; an in-flight draft cannot be reset.");
                held.SetResult(CommandResult.Ok(Receipt(initial, saves[0].Rules, 1)));
                await pending;
                Check(editor.Accepted?.Revision == 1 && editor.Accepted.Grants.Single().Access == "read" &&
                    editor.RootField.Text == root && !editor.SaveButton.IsEnabled,
                    "Only the exact confirmed rules/revision are adopted and the root remains immutable.");
                editor.DraftRows.Single().Access.SelectedIndex = 1;
                editor.AddDraft(@"C:\Fixture\Extra", "write");
                editor.RemoveDraft(editor.DraftRows[0]);
                Check(AutomationProperties.GetName(editor.DraftRows.Single().PathInput) == "External path 1",
                    "Removing rows keeps accessible row labels unique and contiguous.");
                editor.CancelDraft();
                Check(editor.DraftRows.Single().PathInput.Text == external &&
                    (editor.DraftRows.Single().Access.SelectedItem as ComboBoxItem)?.Tag as string == "read" && saves.Count == 1,
                    "Cancel restores the last confirmed draft without another owner command.");
            }

            var adoptionSnapshot = initial with { RootState = "unbound", Root = null, EffectiveGrants = [] };
            var adoptions = 0;
            using (var editor = Editor(adoptionSnapshot, save: (id, revision, rules, _) =>
            {
                adoptions++;
                Check(id == target && revision == 0 && rules.Count == 0, "Empty adoption retains the exact unbound owner and reviewed revision.");
                return Task.FromResult(CommandResult.Ok(Receipt(adoptionSnapshot, [], 1)));
            }))
            {
                await editor.LoadAsync();
                Check(editor.SaveButton.IsEnabled && editor.Feedback.Text.Contains("empty list denies all", StringComparison.Ordinal),
                    "An unbound legacy chat exposes explicit empty deny-all adoption without an automatic save.");
                editor.CancelDraft();
                Check(adoptions == 0 && editor.SaveButton.IsEnabled, "Cancelling a draft retains profile authority until explicit adoption.");
                await editor.SaveAsync();
                Check(adoptions == 1 && editor.Accepted?.Revision == 1 && !editor.SaveButton.IsEnabled,
                    "Confirmed empty adoption is saved once and is no longer an adoption action.");
            }

            var invalidCalls = 0;
            using (var editor = Editor(initial, save: (_, _, _, _) =>
                { invalidCalls++; return Task.FromResult(CommandResult.Ok(initial)); }))
            {
                await editor.LoadAsync();
                editor.AddDraft("relative/path"); await editor.SaveAsync();
                Check(invalidCalls == 0 && editor.Feedback.Text.Contains("absolute", StringComparison.Ordinal),
                    "Relative paths are rejected locally without dispatch.");
                editor.CancelDraft(); editor.AddDraft(root + "\\", "read"); await editor.SaveAsync();
                Check(invalidCalls == 0 && editor.Feedback.Text.Contains("working root", StringComparison.Ordinal),
                    "The original root cannot be replaced or restricted by an equal external alias.");
                editor.CancelDraft(); editor.AddDraft(external, "read"); editor.AddDraft(external.ToUpperInvariant() + "\\", "write");
                await editor.SaveAsync();
                Check(invalidCalls == 0 && editor.Feedback.Text.Contains("only once", StringComparison.Ordinal),
                    "Case/trailing aliases cannot dispatch duplicate Windows rules.");
                editor.CancelDraft();
                for (var index = 0; index < 33; index++) editor.AddDraft($@"C:\Fixture\Entry{index}");
                Check(editor.DraftRows.Count == 32 && !editor.AddButton.IsEnabled, "Draft rows stay bounded to 32 external entries.");
                editor.CancelDraft(); editor.AddDraft(root + @"\Private", "read"); editor.AddDraft(root + @"\Private\Output", "write");
                Check(editor.SaveButton.IsEnabled, "Nested most-specific read/write overrides remain representable without modifying the root.");
            }

            var noOpRules = new[] { new ConversationPathGrant(external, "read") };
            var saved = Receipt(initial, noOpRules, 7); var noOpCalls = 0;
            using (var editor = Editor(saved, save: (_, revision, rules, _) =>
            {
                noOpCalls++; Check(revision == 7 && ConversationPathSnapshot.SameRules(rules, noOpRules),
                    "A canonical no-op keeps the reviewed CAS revision and rule set.");
                return Task.FromResult(CommandResult.Ok(saved));
            }))
            {
                await editor.LoadAsync(); editor.DraftRows.Single().PathInput.Text = external.ToUpperInvariant() + "\\";
                await editor.SaveAsync();
                Check(noOpCalls == 1 && editor.Accepted?.Revision == 7 && editor.Accepted.UpdatedAt == saved.UpdatedAt,
                    "A confirmed canonical no-op retains exact revision and timestamp.");
            }

            foreach (var bad in new[]
            {
                Receipt(initial, noOpRules, 2),
                Receipt(initial, noOpRules, 1) with { ProfileId = "foreign-profile" },
                Receipt(initial, noOpRules, 1) with { ProjectId = "foreign-project" },
                Receipt(initial, noOpRules, 1) with { Root = @"C:\Fixture\Foreign",
                    EffectiveGrants = ConversationPathSnapshot.Effective(@"C:\Fixture\Foreign", noOpRules) },
                Receipt(initial, [new(external, "write")], 1),
                Receipt(initial, noOpRules, 1) with { EffectiveGrants = [new(root, "write"), new(external, "write")] }
            })
            {
                var calls = 0;
                using var editor = Editor(initial, save: (_, _, _, _) =>
                    { calls++; return Task.FromResult(CommandResult.Ok(bad)); });
                await editor.LoadAsync(); editor.AddDraft(external); await editor.SaveAsync(); await editor.SaveAsync();
                Check(calls == 1 && editor.Accepted is null && !editor.SaveButton.IsEnabled &&
                    editor.Feedback.Text.Contains("unconfirmed", StringComparison.Ordinal),
                    "A mismatched identity, revision, mode or effective-rule receipt cannot be adopted or replayed.");
            }

            foreach (var rejected in new[] { false, true })
            {
                var calls = 0;
                using var editor = Editor(initial, save: (_, _, _, _) =>
                {
                    calls++;
                    return rejected ? Task.FromResult(CommandResult.Fail(new string('x', 10000))) :
                        Task.FromException<CommandResult>(new IOException("Fixture private transport detail"));
                });
                await editor.LoadAsync(); editor.AddDraft(external); await editor.SaveAsync(); await editor.SaveAsync();
                Check(calls == 1 && editor.Accepted is null && !editor.SaveButton.IsEnabled &&
                    editor.Feedback.Text.Length < 256 && !editor.Feedback.Text.Contains("private transport", StringComparison.Ordinal) &&
                    editor.Feedback.Text.Contains(rejected ? "rejected" : "unconfirmed", StringComparison.Ordinal),
                    "Explicit rejection and lost receipts have bounded diagnostics and require a fresh editor before another save.");
            }

            var binding = true; var staleCalls = 0;
            using (var editor = Editor(initial, save: (_, _, _, _) =>
                { staleCalls++; return Task.FromResult(CommandResult.Ok(initial)); }, current: () => binding))
            {
                await editor.LoadAsync(); editor.AddDraft(external); binding = false; editor.RefreshBinding();
                await editor.SaveAsync(); binding = true; editor.RefreshBinding(); await editor.SaveAsync();
                Check(staleCalls == 0 && editor.Accepted is null && !editor.SaveButton.IsEnabled,
                    "Binding invalidation is sticky and prevents dispatch after a connection returns.");
            }

            binding = true; CancellationToken pendingToken = default;
            var pendingReply = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var editor = Editor(initial, save: (_, _, _, token) =>
                { pendingToken = token; return pendingReply.Task.WaitAsync(token); }, current: () => binding))
            {
                await editor.LoadAsync(); editor.AddDraft(external); var pending = editor.SaveAsync();
                binding = false; editor.RefreshBinding(); await pending;
                Check(pendingToken.IsCancellationRequested && editor.Accepted is null &&
                    editor.Feedback.Text.Contains("unconfirmed", StringComparison.Ordinal),
                    "A binding change cancels an in-flight transport and prevents stale permission adoption.");
            }

            var loadReply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken loadToken = default;
            var loadingEditor = Editor(initial, (_, token) => { loadToken = token; return loadReply.Task.WaitAsync(token); });
            var load = loadingEditor.LoadAsync(); loadingEditor.Dispose(); loadingEditor.Dispose(); await load;
            Check(loadToken.IsCancellationRequested && loadingEditor.Accepted is null && !loadingEditor.AddButton.IsEnabled,
                "Closing cancels and settles a lazy load without leaving enabled controls.");
            var saveReply = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken saveToken = default;
            var savingEditor = Editor(initial, save: (_, _, _, token) => { saveToken = token; return saveReply.Task.WaitAsync(token); });
            await savingEditor.LoadAsync(); savingEditor.AddDraft(external); var saving = savingEditor.SaveAsync();
            savingEditor.Dispose(); await savingEditor.PendingSave;
            Check(saveToken.IsCancellationRequested && saving.IsCompleted && savingEditor.Accepted is null &&
                savingEditor.Feedback.Text.Contains("unconfirmed", StringComparison.Ordinal),
                "Closing cancels and awaits the exact save task while retaining an unknown dispatched outcome.");

            var unbound = initial with { RootState = "unbound", Root = null, EffectiveGrants = [] };
            using (var editor = Editor(unbound))
            {
                await editor.LoadAsync();
                Check(editor.RootField.Text.Contains("Unbound", StringComparison.Ordinal) &&
                    AutomationProperties.GetName(editor.RootField).Contains("unbound", StringComparison.Ordinal),
                    "An unbound legacy chat never invents a writable root from current navigation.");
            }
            using (var editor = Editor(initial, (_, _) => Task.FromResult(CerebralJson.Element(initial with { ProjectId = "foreign" }))))
            {
                await editor.LoadAsync();
                Check(!editor.AddButton.IsEnabled && editor.Accepted is null,
                    "A foreign lazy-read identity cannot enable draft permission changes.");
            }
            Console.WriteLine($"CONVERSATION PATH EDITOR: PASS · {checks} deterministic WPF/owner-command checks");
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            Console.Error.WriteLine("CONVERSATION PATH EDITOR: FAIL · " + error);
            return 1;
        }
    }
}
