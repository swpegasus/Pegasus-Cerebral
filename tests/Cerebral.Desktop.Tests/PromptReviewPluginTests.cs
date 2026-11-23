using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

internal static partial class PromptReviewPluginTests
{
    public static int Run(string packageDirectory)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { return RunOnDispatcher(packageDirectory); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static int RunOnDispatcher(string packageDirectory)
    {
        Ui.ApplyTheme(Application.Current);
        var package = Path.GetFullPath(packageDirectory);
        var type = LoadReviewType(package);
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        using (var fixture = new Fixture(type, package, "Review this locally.", "Local interpretation."))
        {
            fixture.PrepareAsync().GetAwaiter().GetResult(); fixture.Review();
            Check(!fixture.Operations.Any(x => x.Operation is "models.load" or "workers.models.load") &&
                  fixture.Operations.Single(x => x.Operation == "inference.generate").Arguments.GetProperty("modelId").GetString() == "fixture-model" &&
                  !fixture.Operations.Any(x => x.Operation.StartsWith("workers.", StringComparison.Ordinal)),
                "An explicit local clarifier must generate locally without model loading.");
        }
        using (var fixture = new Fixture(type, package, "Review this remotely.", "Remote interpretation.") { ModelWorkerId = "gb10" })
        {
            fixture.PrepareAsync().GetAwaiter().GetResult(); fixture.Review();
            var generated = fixture.Operations.Single(x => x.Operation == "workers.inference.generate").Arguments;
            Check(!fixture.Operations.Any(x => x.Operation is "models.load" or "workers.models.load") &&
                  generated.GetProperty("workerId").GetString() == "gb10" && generated.GetProperty("modelId").GetString() == "fixture-model" &&
                  !fixture.Operations.Any(x => x.Operation is "models.load" or "inference.generate"),
                "A remote clarifier must generate only through the configured worker tuple without model loading.");
            fixture.FailGeneration = true;
            var failed = fixture.ReviewAsync().GetAwaiter().GetResult();
            Check(failed.State == DesktopInputInterceptionState.Cancel && fixture.Submissions.Count == 0 &&
                  !fixture.Operations.Any(x => x.Operation is "models.load" or "inference.generate"),
                "Failed remote prompt review must not silently fall back to local generation or submit agent work.");
        }
        using (var fixture = new Fixture(type, package, "Placement can change.", "Current placement interpretation."))
        {
            fixture.PrepareAsync().GetAwaiter().GetResult();
            Check(fixture.Operations.Count == 0, "Selecting the quick toggle must perform no background preparation.");
            fixture.ModelWorkerId = "gb10"; fixture.Review();
            Check(fixture.Operations.Last().Operation == "workers.inference.generate" &&
                  !fixture.Operations.Any(x => x.Operation == "inference.generate"),
                "A quick toggle must resolve current placement rather than reuse a stale prepared local GPU target.");
        }
        using (var fixture = new Fixture(type, package, "Use my configured clarifier.", "Unused.") { IncludeClarifier = false })
        {
            var rejected = fixture.ReviewAsync().GetAwaiter().GetResult();
            Check(rejected.State == DesktopInputInterceptionState.Cancel && fixture.Operations.All(x => x.Operation == "agents.list"),
                "A missing clarifier must report configuration guidance without selecting a lead model or touching either GPU.");
        }

        using (var fixture = new Fixture(type, package, "Disable the three note plugins.", "Disable the selected note plugins; preserve their files."))
        {
            var card = fixture.Review();
            var editor = Find<TextBox>(card, "PromptReview.Interpretation");
            var correction = Find<TextBox>(card, "PromptReview.Correction");
            var original = Find<TextBox>(card, "PromptReview.Original");
            Check(original.IsReadOnly && original.Text == fixture.Original && !editor.IsReadOnly && !correction.IsReadOnly,
                "The card must distinguish unchanged original text from editable interpretation and correction.");
            Check(new[] { original, editor, correction }.All(box => new[] { Control.BackgroundProperty, Control.ForegroundProperty,
                  TextBox.CaretBrushProperty, TextBox.SelectionBrushProperty, TextBox.SelectionTextBrushProperty, Control.BorderBrushProperty }
                  .All(property => box.ReadLocalValue(property) == DependencyProperty.UnsetValue)),
                "Review text fields must inherit the shared TextBox theme instead of setting manual colors.");
            Check(!Find<Expander>(card, "PromptReview.Differences").IsExpanded &&
                  Find<TextBox>(card, "PromptReview.Removed").Text.Contains("three", StringComparison.Ordinal) &&
                  Find<TextBox>(card, "PromptReview.Added").Text.Contains("preserve", StringComparison.Ordinal),
                "The collapsed comparison must contain the actual changed wording for inspection.");
            editor.Text = fixture.Original;
            Check(Find<TextBox>(card, "PromptReview.Removed").Visibility == Visibility.Collapsed &&
                  Find<TextBox>(card, "PromptReview.Added").Visibility == Visibility.Collapsed,
                "The live difference view must recognize unchanged text without inventing changes.");
            editor.Text = "Disable selected plugins. Keep all data.";
            Click(card, "PromptReview.UseDraft");
            Check(fixture.Submissions.Count == 0 && fixture.Dismissed == 1 && fixture.Replacements.Single().ExpectedRevision == 4 && fixture.Draft.Text == editor.Text,
                "Apply must replace the captured draft once and return to the composer without submitting.");
        }
        using (var fixture = new Fixture(type, package, "Original request café.\nSecond line.", "Changed wording."))
        {
            Click(fixture.Review(), "PromptReview.Cancel");
            Check(fixture.Submissions.Count == 0 && fixture.Replacements.Count == 0 && fixture.Draft.Text == fixture.Original && fixture.Dismissed == 1,
                "Cancel must preserve the original Unicode and multiline request without submitting.");
        }
        using (var fixture = new Fixture(type, package, "Keep my draft.", "Editable replacement."))
        {
            Click(fixture.Review(), "PromptReview.UseDraft");
            Check(fixture.Replacements.Single().ExpectedRevision == 4 && fixture.Draft.Text == "Editable replacement." && fixture.Submissions.Count == 0,
                "Apply must use the captured exact draft revision and must not submit agent work.");
        }
        using (var fixture = new Fixture(type, package, "Keep my draft.", "Replacement."))
        {
            var card = fixture.Review(); fixture.AdvanceOnNextDraftRead = true;
            Click(card, "PromptReview.UseDraft");
            Check(fixture.Replacements.Count == 0 && fixture.Draft.Text == "Newer user draft" &&
                  Find<TextBlock>(card, "PromptReview.Status").Text.Contains("draft changed", StringComparison.OrdinalIgnoreCase),
                "A draft revision race must preserve the newer user draft and report the conflict.");
        }
        using (var fixture = new Fixture(type, package, "Do not delete files.", "Disable plugins."))
        {
            var card = fixture.Review();
            fixture.Binding = new("different-generation", "another-chat"); fixture.Adapter.PublishConversationChanged(true);
            Click(card, "PromptReview.UseDraft");
            Check(fixture.Submissions.Count == 0 && fixture.Replacements.Count == 0 &&
                  Find<TextBlock>(card, "PromptReview.Status").Text.Contains("conversation changed", StringComparison.OrdinalIgnoreCase),
                "The exact binding guard must prevent stale submission even before the host's deferred conversation notification runs.");
            PumpUntil(() => !Find<Button>(card, "PromptReview.UseDraft").IsEnabled);
            Check(fixture.Submissions.Count == 0 && fixture.Replacements.Count == 0 &&
                  !Find<Button>(card, "PromptReview.UseDraft").IsEnabled &&
                  Find<TextBlock>(card, "PromptReview.Status").Text.Contains("conversation changed", StringComparison.OrdinalIgnoreCase),
                "Changing the conversation must invalidate the old card without submitting its request.");
        }
        using (var fixture = new Fixture(type, package, "Keep this request here.", "Original interpretation."))
        {
            fixture.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var review = fixture.ReviewAsync();
            fixture.Binding = new("next-generation", "next-chat");
            fixture.Pending.SetResult(Reply("A late interpretation from the previous chat."));
            PumpUntil(() => review.IsCompleted);
            Check(review.GetAwaiter().GetResult() is { State: DesktopInputInterceptionState.Cancel, Guidance: null } && fixture.Submissions.Count == 0,
                "An initial interpretation arriving after a conversation change must not create a stale review card or submission.");
        }
        using (var fixture = new Fixture(type, package, "Disable them.", "Disable selected plugins."))
        {
            var card = fixture.Review(); var editor = Find<TextBox>(card, "PromptReview.Interpretation");
            Find<TextBox>(card, "PromptReview.Correction").Text = "Preserve all plugin data.";
            fixture.Interpretation = "Disable selected plugins; preserve all plugin data.";
            Click(card, "PromptReview.Refine");
            Check(editor.Text == fixture.Interpretation && Find<TextBox>(card, "PromptReview.Correction").Text.Length == 0 &&
                  Find<TextBox>(card, "PromptReview.Added").Text.Contains("data", StringComparison.Ordinal) && fixture.Submissions.Count == 0,
                "Refine must update the editable request and its visible difference without submitting it.");
            Check(fixture.GeneratedInputs.Last().Contains("Preserve all plugin data.", StringComparison.Ordinal),
                "The refinement adapter must include the actual user correction in its bounded interpretation request.");
        }
        using (var fixture = new Fixture(type, package, "Keep the original goal.", "Initial interpretation."))
        {
            var card = fixture.Review(); var editor = Find<TextBox>(card, "PromptReview.Interpretation");
            Find<TextBox>(card, "PromptReview.Correction").Text = "A correction.";
            fixture.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Click(card, "PromptReview.Refine");
            Check(editor.IsReadOnly && !Find<Button>(card, "PromptReview.UseDraft").IsEnabled && Find<Button>(card, "PromptReview.Cancel").IsEnabled,
                "An in-flight refinement must prevent competing sends or text replacement while cancellation remains available.");
            Click(card, "PromptReview.Cancel");
            Check(fixture.GenerationToken.IsCancellationRequested && fixture.Dismissed == 1 && fixture.Submissions.Count == 0,
                "Cancel review must cancel the actual refinement token and create no agent submission.");
            fixture.Pending.SetResult(Reply("Late result that must not overwrite the card."));
            PumpUntil(() => Find<Button>(card, "PromptReview.Refine").Content?.ToString() == "Refine");
            Check(editor.Text == "Initial interpretation." && fixture.Submissions.Count == 0,
                "A provider result arriving after cancellation must not update the interpretation or submit work.");
        }
        using (var fixture = new Fixture(type, package, "A😀Z", "A😃Z"))
        {
            var card = fixture.Review();
            Check(Find<TextBox>(card, "PromptReview.Removed").Text == "😀" && Find<TextBox>(card, "PromptReview.Added").Text == "😃",
                "An exact difference must preserve complete surrogate pairs in changed Unicode text.");
            Find<Expander>(card, "PromptReview.Differences").IsExpanded = true;
            var editor = Find<TextBox>(card, "PromptReview.Interpretation");
            editor.Text = string.Join('\n', Enumerable.Range(0, 80).Select(index => "Interpretation line " + index));
            var scroll = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            scroll.Measure(new Size(460, 280)); scroll.Arrange(new Rect(0, 0, 460, 280)); scroll.UpdateLayout();
            SettleLayout(scroll, new Size(460, 280));
            var bodyScroll = Find<ScrollViewer>(card, "PromptReview.BodyScroll");
            var apply = Find<Button>(card, "PromptReview.UseDraft");
            var before = apply.TransformToAncestor(scroll).TransformBounds(new Rect(apply.RenderSize));
            Check(bodyScroll.ScrollableHeight > 0 && before.Top >= 0 && before.Bottom <= scroll.ViewportHeight + 1,
                "Long review content must scroll within a viewport-bound card while Apply remains visible.");
            bodyScroll.ScrollToEnd(); SettleLayout(scroll, new Size(460, 280));
            var after = apply.TransformToAncestor(scroll).TransformBounds(new Rect(apply.RenderSize));
            Check(bodyScroll.VerticalOffset > 0 && Math.Abs(before.Top - after.Top) < 1 && scroll.ScrollableHeight < 1,
                "Scrolling review content must keep the same footer position without outer-card overflow.");
            scroll.Content = null;
        }
        return checks;
    }

    private static Type LoadReviewType(string package)
    {
        var manifest = JsonSerializer.Deserialize<DesktopPluginManifest>(File.ReadAllText(Path.Combine(package, "desktop-plugin.json")), CerebralJson.Options)
            ?? throw new InvalidDataException("Prompt Review manifest is missing.");
        return Assembly.LoadFrom(Path.Combine(package, manifest.Assembly)).GetType(manifest.EntryType, true)!;
    }

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Walk(root).OfType<T>().Single(node => AutomationProperties.GetAutomationId(node) == id);

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var node in Walk(child)) yield return node;
    }
    private static void Click(DependencyObject root, string id) => Find<Button>(root, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(3)) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        if (!done()) throw new TimeoutException("Prompt Review fixture did not complete its cancelled refinement.");
    }
    private static DesktopServiceCommandResult Result(object value) => new(true, JsonSerializer.SerializeToElement(value, CerebralJson.Options));

    private static DesktopServiceCommandResult Reply(string prompt, string message = "Fixture clarification.") => Result(new { text = JsonSerializer.Serialize(new { prompt, message }) });

    private sealed class Fixture : IDisposable
    {
        public DesktopConversationBinding Binding = new("prompt-review-fixture", "fixture-chat");
        public DesktopDraftSnapshot Draft;
        public readonly string Original;
        public string Interpretation;
        public string ReplyMessage = "Fixture clarification.";
        public string ModelWorkerId = "local";
        public bool IncludeClarifier = true;
        public bool FailGeneration;
        public readonly List<DesktopSubmission> Submissions = [];
        public readonly List<DesktopDraftUpdate> Replacements = [];
        public readonly List<string> GeneratedInputs = [];
        public readonly List<(string Operation, JsonElement Arguments)> Operations = [];
        public int Dismissed;
        public bool AdvanceOnNextDraftRead;
        public CancellationToken GenerationToken;
        public TaskCompletionSource<DesktopServiceCommandResult>? Pending;
        public DesktopPluginHostAdapter Adapter { get; }
        private readonly IDesktopPlugin plugin;
        private readonly IDesktopPluginHost scope;

        public Fixture(Type type, string package, string original, string interpretation)
        {
            Original = original; Interpretation = interpretation; Draft = new(Binding, 4, original);
            Adapter = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher, Conversation, ReadDraft,
                (update, token) => { token.ThrowIfCancellationRequested(); Replacements.Add(update); Draft = new(update.Binding, Draft.Revision + 1, update.Text); return Completed(); },
                (submission, token) => { token.ThrowIfCancellationRequested(); Submissions.Add(submission); return Completed(); },
                (_, _) => throw new InvalidOperationException("The review fixture must not steer."),
                (_, _) => throw new InvalidOperationException("The review fixture must not stop agent work."),
                _ => { }, pluginDataRoot: Path.Combine(Path.GetTempPath(), "PromptReviewFixture-" + Guid.NewGuid().ToString("N")),
                serviceCommand: Service, dismissInputGuidance: () => Dismissed++);
            scope = (IDesktopPluginHost)typeof(DesktopPluginHostAdapter).GetMethod("CreateScope", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Adapter, ["pegasus.prompt-review", "Prompt Review", package])!;
            plugin = (IDesktopPlugin)Activator.CreateInstance(type)!; plugin.Initialize(scope);
        }
        private DesktopConversationSnapshot Conversation() => new(Binding, "Fixture", null, null, false);
        private DesktopDraftSnapshot ReadDraft()
        {
            var result = Draft;
            if (AdvanceOnNextDraftRead) { AdvanceOnNextDraftRead = false; Draft = new(Binding, Draft.Revision + 1, "Newer user draft"); }
            return result;
        }
        private Task<DesktopServiceCommandResult> Service(string operation, object arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Operations.Add((operation, JsonSerializer.SerializeToElement(arguments, CerebralJson.Options)));
            if (operation == "agents.list") return Task.FromResult(Result(new[] { new { id = IncludeClarifier ? "clarifier" : "lead", name = IncludeClarifier ? "Prompt clarifier" : "Lead", modelId = "fixture-model", modelWorkerId = ModelWorkerId, role = IncludeClarifier ? "helper" : "lead" } }));
            if (operation is not ("inference.generate" or "workers.inference.generate")) throw new InvalidOperationException("Unexpected review fixture operation: " + operation);
            GenerationToken = token; GeneratedInputs.Add(JsonSerializer.Serialize(arguments, CerebralJson.Options));
            if (FailGeneration) return Task.FromResult(new DesktopServiceCommandResult(false, default, "Fixture remote generation failed."));
            return Pending?.Task ?? Task.FromResult(Reply(Interpretation, ReplyMessage));
        }
        public Task<string?> PrepareAsync() => ((IDesktopInputModePlugin)plugin).InputModes.Single().PrepareAsync?.Invoke(CancellationToken.None) ?? Task.FromResult<string?>(null);
        public FrameworkElement Review()
        {
            var result = ReviewAsync().GetAwaiter().GetResult();
            if (result.State != DesktopInputInterceptionState.Hold || result.Guidance is null)
                throw new InvalidOperationException("Prompt Review fixture did not return its review card.");
            return result.Guidance;
        }
        public Task<DesktopInputInterception> ReviewAsync() => ((IDesktopInputModePlugin)plugin).InputModes.Single()
            .InterceptAsync(new(Conversation(), new(Binding, "fixture-request", Original), false), CancellationToken.None);
        private static Task<DesktopOperationResult> Completed() => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        public void Dispose() { plugin.Dispose(); ((IDisposable)scope).Dispose(); }
    }
}
