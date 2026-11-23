using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using Cerebral.Desktop.Plugins;
using Cerebral.PluginLifetime;

namespace Cerebral.PromptReviewPlugin;

public sealed class PromptReviewPlugin : IDesktopPlugin, IDesktopInputModePlugin, IDesktopAgentTemplatePlugin
{
    private IDesktopPluginHost? host;
    private IDesktopServiceHost? service;
    private bool disposed, hostUnavailable;
    private readonly CancellationTokenSource lifetime = new();
    private readonly PluginCleanup cleanup = new();
    private bool CallbacksRetired => disposed || hostUnavailable;
    private readonly Dictionary<CancellationTokenSource, Action> reviews = [];
    private sealed record ModelTarget(string ModelId, string WorkerId);

    public IReadOnlyList<DesktopPanel> Panels => [];
    public IReadOnlyList<DesktopAction> ComposerActions => [];
    public IReadOnlyList<DesktopInputMode> InputModes =>
    [
        new DesktopInputMode("understanding", "Refine my prompt", ReviewAsync,
            "Review and refine this request before Cerebral receives it.")
        {
            QuickToggle = true
        }
    ];
    public IReadOnlyList<DesktopAgentTemplate> AgentTemplates =>
    [
        new("prompt-clarifier", "Prompt clarifier", "helper", "Restate a user request accurately before Cerebral acts.",
            "Return only a compact executable request for Cerebral. Preserve the user's intended outcome, deliverables, exact paths and names, user-authored constraints, corrections, prohibitions, and completion evidence. Keep requests to calculate, answer, create, or act intact. Reviewer guidance is private process context and must never appear as a constraint in the returned request. Use reversible defaults for routine omissions and mention only an ambiguity that prevents the requested result.")
        { RequiredCapabilities = [], RequiredTools = [], MaxSteps = 1, MaxSeconds = 120 }
    ];

    public void Initialize(IDesktopPluginHost value)
    {
        host = value;
        service = value as IDesktopServiceHost ?? throw new InvalidOperationException("Prompt Review requires structured service commands.");
    }
    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;

    private async Task<DesktopInputInterception> ReviewAsync(DesktopInputRequest input, CancellationToken cancellationToken)
    {
        if (CallbacksRetired || host is null || service is null) return new(DesktopInputInterceptionState.Cancel, "Prompt Review is unavailable.");
        if (input.IsSteering) return new(DesktopInputInterceptionState.Pass);
        var callerCancellation = cancellationToken;
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        cancellationToken = invocation.Token;
        try
        {
            var draft = host.GetDraft();
            ReportHostStatus("Interpreting this request with the prompt clarifier…");
            var interpretation = await InterpretAsync(input.Submission.Text, null, null, [], cancellationToken);
            CheckCurrent(input, cancellationToken);
            if (host.GetDraft() != draft) throw new InvalidOperationException("The composer draft changed during refinement. Current text was preserved.");
            return new(DesktopInputInterceptionState.Hold,
                "Refine the draft, then return your approved wording to the composer.",
                Guidance(input, draft, interpretation, callerCancellation));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(DesktopInputInterceptionState.Cancel, "Prompt refinement was cancelled.");
        }
        catch (Exception ex)
        {
            if (ex is ObjectDisposedException) RetireHostCallbacks();
            return new(DesktopInputInterceptionState.Cancel, "The request could not be interpreted: " + ex.Message);
        }
    }

    private FrameworkElement Guidance(DesktopInputRequest input, DesktopDraftSnapshot originalDraft, PromptReviewReply initial, CancellationToken cancellationToken)
    {
        var review = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = review.Token;
        var root = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var viewport = new PromptReviewViewportConstraint(root);
        var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var bodyScroll = new ScrollViewer
        {
            Content = body, MinHeight = 0, CanContentScroll = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        AutomationProperties.SetAutomationId(bodyScroll, "PromptReview.BodyScroll");
        root.Children.Add(bodyScroll);
        AutomationProperties.SetAutomationId(root, "PromptReview.Card");
        body.Children.Add(Label("Review this request", 18, true));
        body.Children.Add(Label("Work with the clarifier here. Nothing enters the main chat until you return to the composer and send."));
        var exchanges = new PromptReviewConversation();
        exchanges.Add("Clarifier", initial.Message);
        var original = new Expander { Header = "Original request · unchanged", IsExpanded = false, Margin = new Thickness(0, 7, 0, 7) };
        original.SetResourceReference(Control.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        original.Content = Box(input.Submission.Text, true, "PromptReview.Original");
        AutomationProperties.SetAutomationId(original, "PromptReview.OriginalSection");
        body.Children.Add(original);

        body.Children.Add(Label("Editable interpretation", 15, true));
        var interpretation = Box(initial.Prompt, false, "PromptReview.Interpretation");
        var lastClarifierPrompt = initial.Prompt;
        interpretation.Margin = new Thickness(0, 7, 0, 7);
        body.Children.Add(interpretation);

        var difference = new Expander { Header = "What changed · exact text comparison", IsExpanded = false, Margin = new Thickness(0, 0, 0, 7) };
        difference.SetResourceReference(Control.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        AutomationProperties.SetAutomationId(difference, "PromptReview.Differences");
        var differenceBody = new StackPanel();
        var differenceStatus = Label("");
        var removedLabel = Label("Original wording removed or changed");
        var removed = Box("", true, "PromptReview.Removed");
        var addedLabel = Label("Interpretation wording added or changed");
        var added = Box("", true, "PromptReview.Added");
        differenceBody.Children.Add(differenceStatus);
        differenceBody.Children.Add(removedLabel); differenceBody.Children.Add(removed);
        differenceBody.Children.Add(addedLabel); differenceBody.Children.Add(added);
        differenceBody.Children.Add(Label("This comparison shows the smallest text range containing edits. Check the interpretation for intent, names, quantities and constraints."));
        difference.Content = differenceBody; body.Children.Add(difference);
        void UpdateDifference()
        {
            var changes = ChangedText(input.Submission.Text, interpretation.Text);
            differenceStatus.Text = changes.Changed
                ? $"Changed wording: {changes.Removed.Length:N0} original characters and {changes.Added.Length:N0} interpretation characters."
                : "No text changes. The interpretation keeps the original wording.";
            removed.Text = changes.Removed; added.Text = changes.Added;
            removedLabel.Visibility = removed.Visibility = changes.Removed.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            addedLabel.Visibility = added.Visibility = changes.Added.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        interpretation.TextChanged += (_, _) => UpdateDifference(); UpdateDifference();

        body.Children.Add(Label("Refinement conversation", 15, true));
        var exchangeView = Box(exchanges.DisplayText(), true, "PromptReview.Conversation");
        exchangeView.MaxHeight = 180;
        body.Children.Add(exchangeView);
        body.Children.Add(Label("Tell the clarifier what to change, or answer its question", 15, true));
        var feedback = Box("", false, "PromptReview.Correction");
        feedback.ToolTip = "Use Refine to include this note. Clear an unused note before applying the draft; direct edits can be used as written.";
        body.Children.Add(feedback);
        body.Children.Add(Label("Refine includes your note or current direct edits. Clear an unused note before applying. Apply replaces only the captured draft; send from the composer when ready."));
        var status = Label("");
        AutomationProperties.SetAutomationId(status, "PromptReview.Status");
        body.Children.Add(status);
        void Report(string text, bool error = false)
        {
            status.Text = text;
            status.SetResourceReference(TextBlock.ForegroundProperty, error ? DesktopThemeResourceKeys.ErrorTextBrush : DesktopThemeResourceKeys.TextBrush);
            ReportHostStatus(text);
        }

        var actions = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        AutomationProperties.SetAutomationId(actions, "PromptReview.Actions");
        Grid.SetRow(actions, 1);
        var buttons = new List<Button>();
        var busy = false;
        var available = true;
        Button? useDraft = null;
        void RefreshButtons()
        {
            var enabled = available && !busy && !token.IsCancellationRequested && !CallbacksRetired;
            foreach (var item in buttons) item.IsEnabled = enabled;
            if (useDraft is null) return;
            var pendingNote = !string.IsNullOrWhiteSpace(feedback.Text);
            useDraft.IsEnabled = enabled && !pendingNote && !string.IsNullOrWhiteSpace(interpretation.Text);
            useDraft.ToolTip = pendingNote
                ? "Use Refine to include this note, or clear it before applying the draft."
                : "Replace the captured composer draft with this proposal. This does not send it.";
        }
        void CancelReview()
        {
            available = false;
            if (reviews.ContainsKey(review)) cleanup.Attempt(review.Cancel);
        }
        Button ActionButton(string id, string label, Func<Button, Task> action, bool preserveReviewOnError = false)
        {
            var button = new Button { Content = label, Padding = new Thickness(10, 5, 10, 5), MinWidth = 92, Margin = new Thickness(0, 0, 7, 4) };
            AutomationProperties.SetAutomationId(button, id);
            buttons.Add(button);
            button.Click += async (_, _) =>
            {
                if (busy || !available) return;
                busy = true; RefreshButtons();
                interpretation.IsReadOnly = true; feedback.IsReadOnly = true;
                try { CheckCurrent(input, token); await action(button); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Cancellation can release this card while a newer review is mounted.
                    // Only the still-owned card may dismiss the host's current guidance.
                    var ownsGuidance = reviews.ContainsKey(review);
                    CancelReview();
                    if (ownsGuidance) { Report("Prompt review was cancelled."); DismissGuidance(); }
                }
                catch (Exception error)
                {
                    var ownsGuidance = reviews.ContainsKey(review);
                    var currentOwner = host;
                    // A validation/model failure must not discard the user's edited proposal or note.
                    // Stale, cancelled and uncertain draft-application failures retain the old close/inspect path.
                    if (error is ObjectDisposedException) RetireHostCallbacks();
                    if (ownsGuidance && preserveReviewOnError && available && !CallbacksRetired && !token.IsCancellationRequested &&
                        currentOwner is not null && TryReadHost(() => currentOwner.GetConversation().Binding == input.Conversation.Binding &&
                            currentOwner.GetDraft() == originalDraft, out var stillCurrent) && stillCurrent)
                        Report(error.Message, true);
                    else
                    {
                        CancelReview();
                        if (ownsGuidance) { Report(error.Message, true); DismissGuidance(); }
                    }
                }
                finally
                {
                    busy = false; interpretation.IsReadOnly = false; feedback.IsReadOnly = false;
                    RefreshButtons();
                }
            };
            actions.Children.Add(button); return button;
        }
        _ = ActionButton("PromptReview.Refine", "Refine", async button =>
        {
            var correction = feedback.Text.Trim();
            var edited = interpretation.Text;
            if (correction.Length == 0 && (edited == lastClarifierPrompt || string.IsNullOrWhiteSpace(edited)))
                throw new InvalidOperationException("Edit the proposed prompt or add a refinement note first.");
            if (host!.GetDraft() != originalDraft)
                throw new InvalidOperationException("The composer draft changed during refinement. Current text was preserved.");
            button.Content = "Refining…";
            try
            {
                var refined = await InterpretAsync(input.Submission.Text, edited, correction.Length == 0 ? null : correction, exchanges.Messages, token);
                CheckCurrent(input, token);
                if (host!.GetDraft() != originalDraft)
                    throw new InvalidOperationException("The composer draft changed during refinement. Current text was preserved.");
                lastClarifierPrompt = refined.Prompt;
                interpretation.Text = refined.Prompt;
                if (correction.Length != 0) exchanges.Add("You", correction);
                exchanges.Add("Clarifier", refined.Message);
                exchangeView.Text = exchanges.DisplayText();
                exchangeView.ScrollToEnd();
                feedback.Clear();
                Report(refined.Message);
            }
            finally { button.Content = "Refine"; }
        }, preserveReviewOnError: true);
        useDraft = ActionButton("PromptReview.UseDraft", "Apply to prompt box", async _ =>
        {
            // Keep the guard even when a caller raises Click directly on a disabled button.
            if (!string.IsNullOrWhiteSpace(feedback.Text))
            { Report("Use Refine to include your note, or clear it before applying the draft.", true); return; }
            if (string.IsNullOrWhiteSpace(interpretation.Text))
            { Report("Enter a proposed prompt before applying it.", true); return; }
            await ReplaceDraftAsync(input, originalDraft, interpretation.Text, token);
            available = false;
        });
        feedback.TextChanged += (_, _) => RefreshButtons();
        interpretation.TextChanged += (_, _) => RefreshButtons();
        RefreshButtons();
        var cancel = new Button { Content = "Cancel review", Padding = new Thickness(10, 5, 10, 5), MinWidth = 92 };
        AutomationProperties.SetAutomationId(cancel, "PromptReview.Cancel");
        cancel.Click += (_, _) => { if (!available) return; CancelReview(); DismissGuidance(); };
        actions.Children.Add(cancel); root.Children.Add(actions);
        var currentHost = host!;
        CancellationTokenRegistration cancelled = default;
        EventHandler<DesktopConversationChangedEventArgs> changed = (_, _) =>
        {
            if (CallbacksRetired || !TryReadHost(() => currentHost.GetConversation().Binding, out var binding) || binding == input.Conversation.Binding) return;
            CancelReview(); foreach (var button in buttons) button.IsEnabled = false;
            Report("The conversation changed. Review the current draft again.", true);
        };
        currentHost.ConversationChanged += changed;
        void Release()
        {
            if (!reviews.Remove(review)) return;
            available = false;
            cleanup.Attempt(() => { _ = cancelled.Unregister(); });
            cleanup.Attempt(viewport.Dispose);
            cleanup.Attempt(() => currentHost.ConversationChanged -= changed);
            cleanup.Attempt(review.Cancel);
            cleanup.Attempt(review.Dispose);
        }
        reviews.Add(review, Release);
        cancelled = token.Register(() =>
        {
            if (root.Dispatcher.CheckAccess()) Release();
            else if (!root.Dispatcher.HasShutdownStarted) root.Dispatcher.BeginInvoke(Release);
        });
        root.Unloaded += (_, _) => Release();
        return root;
    }

    private void CheckCurrent(DesktopInputRequest input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CallbacksRetired || host is null || !TryReadHost(() => host.GetConversation().Binding, out var binding) || input.Conversation.Binding != binding)
            throw new InvalidOperationException("The conversation changed. Review the current draft again.");
    }

    private async Task ReplaceDraftAsync(DesktopInputRequest input, DesktopDraftSnapshot expected, string text, CancellationToken cancellationToken)
    {
        CheckCurrent(input, cancellationToken);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("The refined prompt is empty.");
        var replacement = text.Trim();
        // The hidden original draft may have changed through another plugin. Never overwrite it silently.
        if (host!.GetDraft() != expected) throw new InvalidOperationException("The composer draft changed during refinement. Return to it and review the current text.");
        var result = await host.ReplaceDraftAsync(new(expected.Binding, expected.Revision, replacement), cancellationToken);
        if (result.State != DesktopOperationState.Completed)
            throw new InvalidOperationException(result.Message ?? "The composer replacement could not be confirmed. Inspect the draft before retrying.");
        CheckCurrent(input, cancellationToken);
        var confirmed = host.GetDraft();
        if (confirmed.Binding != expected.Binding || confirmed.Text != replacement || confirmed.Revision != expected.Revision + (replacement == expected.Text ? 0 : 1))
            throw new InvalidOperationException("The draft changed before replacement could be verified. Inspect it before sending.");
        DismissGuidance(); ReportHostStatus("Refined prompt returned to the composer. Send when ready.");
    }

    private async Task<ModelTarget> ResolveTargetAsync(CancellationToken cancellationToken)
    {
        var agents = await service!.ExecuteServiceCommandAsync("agents.list", new { }, cancellationToken);
        if (!agents.Success) throw new InvalidOperationException(agents.Error ?? "Agent profiles are unavailable.");
        var profiles = agents.Data.ValueKind == JsonValueKind.Array ? agents.Data.EnumerateArray().ToArray() : [];
        var selected = profiles.FirstOrDefault(ProfileIsClarifier);
        if (selected.ValueKind == JsonValueKind.Undefined || !selected.TryGetProperty("modelId", out var model) || string.IsNullOrWhiteSpace(model.GetString()))
            throw new InvalidOperationException("No configured prompt-clarifier agent is available. Configure its model and worker in Models and agents before enabling prompt review.");
        var workerId = "local";
        if (selected.TryGetProperty("modelWorkerId", out var worker))
        {
            if (worker.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(worker.GetString()))
                throw new InvalidDataException("The prompt-clarifier model worker is invalid. Correct its agent profile before reviewing a request.");
            workerId = worker.GetString()!;
        }
        return new(model.GetString()!, workerId);
    }

    private async Task<PromptReviewReply> InterpretAsync(string original, string? current, string? feedback, IReadOnlyList<PromptReviewExchange> exchanges, CancellationToken cancellationToken)
    {
        // Placement may change while the quick toggle stays enabled; do not retain an old local GPU target.
        var target = await ResolveTargetAsync(cancellationToken);
        var system = "You are a request-understanding gate. Transform the user's text into only a concise, executable request for the main Cerebral agent. Preserve the intended outcome, deliverables, exact names and paths, user-authored constraints, corrections, prohibitions, and completion evidence. Preserve the request to calculate, answer, create, or act; this review step itself performs none of those actions. Never copy or paraphrase reviewer-only limits about transformation, task execution, or tool use into the output. Do not add a Constraints or Ambiguities section unless those constraints or ambiguities came from the user. When the supplied data includes an explicit current status, scheduled value, or current target, treat it as authoritative over an older inferred pattern unless the user specifically asks to challenge or reconcile it. Use reasonable reversible defaults for routine omissions and mention only an ambiguity that materially prevents the requested result. Add no policy or invented requirement.";
        PromptReviewConversation.RequireBoundedInput(original, current, feedback);
        system += " Return exactly the supplied JSON shape: prompt is the current complete executable draft; message is a short explanation or a necessary clarification question addressed to the user. If a clarification is needed, keep the known intent in prompt and ask in message; never insert the question or refinement conversation into prompt. The original request and the current edited draft are authoritative; earlier discussion is supporting context.";
        var user = current is null ? "Original request:\n" + original : "Original request:\n" + original + "\n\nCurrent interpretation:\n" + current + "\n\nUser refinement:\n" + feedback;
        var messages = new List<object> { new { role = "system", content = system } };
        if (exchanges.Count > 0) messages.Add(new { role = "user", content = "Earlier refinement discussion (private to this draft):\\n" + PromptReviewConversation.ContextText(exchanges) });
        messages.Add(new { role = "user", content = user });
        var arguments = new Dictionary<string, object?>
        {
            ["modelId"] = target.ModelId,
            ["messages"] = messages,
            ["maxTokens"] = 1024, ["temperature"] = 0.1, ["json"] = true,
            ["responseSchema"] = PromptReviewReply.Schema, ["reasoningMode"] = "off", ["reasoningTokens"] = 128
        };
        if (target.WorkerId != "local") arguments["workerId"] = target.WorkerId;
        var result = await service!.ExecuteServiceCommandAsync(target.WorkerId == "local" ? "inference.generate" : "workers.inference.generate", arguments, cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "Interpretation failed.");
        if (!result.Data.TryGetProperty("text", out var text) || string.IsNullOrWhiteSpace(text.GetString())) throw new InvalidDataException("The prompt-review model returned no interpretation.");
        var reply = PromptReviewReply.Parse(text.GetString()!);
        return reply with { Prompt = SanitizeInterpretation(original, reply.Prompt, current, feedback) };
    }

    internal static string SanitizeInterpretation(string original, string candidate, string? current = null, string? feedback = null)
    {
        // The edited draft and latest user correction supersede the original during refinement.
        // Private reviewer instructions and earlier clarifier discussion are never authority here.
        var source = current ?? original ?? "";
        var sourceLower = (source + "\n" + feedback).ToLowerInvariant();
        var explicitCurrent = ContainsAny(sourceLower, "current status", "current value", "next scheduled", "scheduled reset", "current target");
        var kept = new List<string>();
        foreach (var line in (candidate ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var normalized = line.Trim().TrimStart('-', '*', '•').Trim();
            var lower = normalized.ToLowerInvariant();
            if (!sourceLower.Contains("do not solve", StringComparison.Ordinal) &&
                (lower.Contains("do not solve", StringComparison.Ordinal) || lower.Contains("not solve the", StringComparison.Ordinal) ||
                 lower.Contains("do not provide the specific", StringComparison.Ordinal))) continue;
            if (!ContainsAny(sourceLower, "do not call tools", "do not use tools", "without tools") &&
                ContainsAny(lower, "do not call tools", "do not use tools", "without calling tools")) continue;
            if (!sourceLower.Contains("preserve exact", StringComparison.Ordinal) && lower.Contains("preserve exact names and paths", StringComparison.Ordinal)) continue;
            if (explicitCurrent && lower.Contains("historical", StringComparison.Ordinal) && lower.Contains("discrep", StringComparison.Ordinal) &&
                ContainsAny(lower, "clarif", "cannot be determined", "materially changes")) continue;
            kept.Add(line.TrimEnd());
        }
        for (var index = kept.Count - 1; index >= 0; index--)
        {
            if (!kept[index].Trim().Equals("Ambiguities:", StringComparison.OrdinalIgnoreCase) &&
                !kept[index].Trim().Equals("Constraints:", StringComparison.OrdinalIgnoreCase)) continue;
            var hasContent = kept.Skip(index + 1).TakeWhile(value => !string.IsNullOrWhiteSpace(value))
                .Any(value => !value.Trim().EndsWith(':'));
            if (!hasContent) kept.RemoveAt(index);
        }
        var result = string.Join('\n', kept).Trim();
        if (explicitCurrent)
        {
            // Restoring older facts after a correction can undo the user's change, including a
            // correction that does not repeat the old field label. Only the initial request or
            // explicit facts in the latest correction may be restored; never replay prior facts.
            var factSource = current is null ? source : feedback ?? "";
            var missingCurrentFacts = factSource.Split(['\n', '.', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => ContainsAny(value.ToLowerInvariant(), "current status", "current value", "next scheduled", "scheduled reset", "current target"))
                .Where(value => !result.Contains(value, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (missingCurrentFacts.Count > 0)
                result += "\n\nAuthoritative supplied data:\n" + string.Join('\n', missingCurrentFacts.Select(value => "- " + value));
            result += "\n\nUse the explicitly supplied current status or scheduled value as authoritative; treat an older historical pattern only as a secondary cross-check unless the user asks to reconcile it.";
        }
        if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("The prompt-review model returned only reviewer instructions instead of an executable request.");
        return result;
    }

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.Ordinal));

    private static bool ProfileIsClarifier(JsonElement profile) => Property(profile, "id").Contains("clarif", StringComparison.OrdinalIgnoreCase) || Property(profile, "name").Contains("clarif", StringComparison.OrdinalIgnoreCase) || Property(profile, "purpose").Contains("restate", StringComparison.OrdinalIgnoreCase);
    private static string Property(JsonElement profile, string name) => profile.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static TextBlock Label(string text, double size = 13, bool strong = false)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 3, 0, 3) };
        label.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        return label;
    }

    private static (bool Changed, string Removed, string Added) ChangedText(string original, string interpretation)
    {
        if (original == interpretation) return (false, "", "");
        var prefix = 0;
        while (prefix < original.Length && prefix < interpretation.Length && original[prefix] == interpretation[prefix]) prefix++;
        if (prefix > 0 && char.IsHighSurrogate(original[prefix - 1])) prefix--;
        var suffix = 0;
        while (suffix < original.Length - prefix && suffix < interpretation.Length - prefix &&
               original[original.Length - suffix - 1] == interpretation[interpretation.Length - suffix - 1]) suffix++;
        if (suffix > 0 && char.IsLowSurrogate(original[original.Length - suffix])) suffix--;
        return (true, original.Substring(prefix, original.Length - prefix - suffix), interpretation.Substring(prefix, interpretation.Length - prefix - suffix));
    }

    private static TextBox Box(string text, bool readOnly, string id)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = readOnly, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 58, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetAutomationId(box, id);
        if (!readOnly) SpellCheck.SetIsEnabled(box, true);
        return box;
    }
    private bool TryReadHost<T>(Func<T> read, out T value)
    {
        value = default!;
        if (CallbacksRetired) return false;
        if (PluginHostAccess.TryRead(read, out value)) return true;
        RetireHostCallbacks(); return false;
    }
    private void ReportHostStatus(string message)
    {
        if (!CallbacksRetired && !PluginHostAccess.TryNotify(() => host?.ReportStatus(message))) RetireHostCallbacks();
    }
    private void DismissGuidance()
    {
        if (!CallbacksRetired && !PluginHostAccess.TryNotify(() => host?.DismissInputGuidance())) RetireHostCallbacks();
    }
    private void RetireHostCallbacks()
    {
        hostUnavailable = true;
        cleanup.Attempt(() => lifetime.Cancel());
        foreach (var release in reviews.Values.ToArray()) cleanup.Attempt(release);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        RetireHostCallbacks();
        cleanup.Attempt(lifetime.Dispose);
        host = null; service = null;
        cleanup.Complete();
    }
}

/// <summary>Deterministic boundary checks used by the desktop plugin harness; no model or service is invoked.</summary>
public static class PromptReviewPluginDiagnostics
{
    public static string RunDeterministicChecks()
    {
        const string original = "Determine the next weekly usage limit reset date. Current Status: 0% left. Next Scheduled Reset: Sep 20, 2026, 1:04 PM. Historical Pattern: approximately every 7 days.";
        const string leaked = """
            Request: Determine the next weekly usage limit reset date.

            Constraints:
            - Do not solve the calculation or provide the specific date/time.
            - Do not call tools.
            - Preserve exact names and paths of the data points provided.

            Ambiguities:
            - The discrepancy between the historical pattern and the stated reset materially changes the outcome and requires clarification.
            """;
        var cleaned = PromptReviewPlugin.SanitizeInterpretation(original, leaked);
        var explicitConstraint = PromptReviewPlugin.SanitizeInterpretation("Rewrite this request but do not call tools.",
            "Rewrite this request.\n- Do not call tools.");
        var success = !cleaned.Contains("Do not solve", StringComparison.OrdinalIgnoreCase) &&
            !cleaned.Contains("Do not call tools", StringComparison.OrdinalIgnoreCase) &&
            !cleaned.Contains("requires clarification", StringComparison.OrdinalIgnoreCase) &&
            cleaned.Contains("Sep 20, 2026, 1:04 PM", StringComparison.Ordinal) &&
            cleaned.Contains("explicitly supplied current status", StringComparison.Ordinal) &&
            explicitConstraint.Contains("Do not call tools", StringComparison.OrdinalIgnoreCase);
        if (!success) throw new InvalidOperationException("Prompt Review leaked reviewer-only constraints or removed an explicit user constraint.");
        return JsonSerializer.Serialize(new { success, checks = 6, modelInvoked = false });
    }
}
