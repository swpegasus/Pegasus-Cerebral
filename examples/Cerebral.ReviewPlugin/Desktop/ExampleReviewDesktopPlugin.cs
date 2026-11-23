using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Desktop.Plugins;
using Cerebral.PluginLifetime;

namespace Cerebral.ReviewPlugin.Desktop;

/// <summary>Native companion for the durable example.review service workflow.</summary>
public sealed class ExampleReviewDesktopPlugin : IDesktopPlugin
{
    private IDesktopPluginHost? host;
    private bool disposed, hostUnavailable, submitting;
    private readonly CancellationTokenSource lifetime = new();
    private readonly PluginCleanup cleanup = new();
    private readonly List<WeakReference<TextBlock>> statusViews = [];

    public IReadOnlyList<DesktopPanel> Panels => [new("review", "Review", CreatePanel)];
    public IReadOnlyList<DesktopAction> ComposerActions =>
    [
        new("start-review", "Review draft", StartReviewAsync,
            "Starts the durable review workflow with the current draft while leaving the composer unchanged.")
    ];

    public void Initialize(IDesktopPluginHost value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        host = value;
    }

    private FrameworkElement CreatePanel()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "Measure a draft through the durable example.review workflow. Cerebral will ask for a label and presentation style, persist one SQL review record, and keep private workflow context out of the composer.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        var button = new Button
        {
            Content = "Review current draft",
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 160,
            Padding = new Thickness(12, 6, 12, 6)
        };
        AutomationProperties.SetAutomationId(button, "example-review-start");
        button.Click += async (_, _) =>
        {
            if (disposed || hostUnavailable || submitting) return;
            button.IsEnabled = false;
            try { await StartReviewAsync(CancellationToken.None); }
            finally { if (!disposed) button.IsEnabled = !hostUnavailable; }
        };
        panel.Children.Add(button);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetAutomationId(status, "example-review-status");
        var reference = new WeakReference<TextBlock>(status); statusViews.Add(reference);
        panel.Unloaded += (_, _) => statusViews.Remove(reference);
        panel.Children.Add(status);
        return panel;
    }

    private async Task StartReviewAsync(CancellationToken cancellationToken)
    {
        if (disposed || hostUnavailable || submitting) return;
        submitting = true;
        var attempted = false;
        string? requestId = null;
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            request.Token.ThrowIfCancellationRequested();
            var currentHost = host;
            if (currentHost is not IDesktopNativeWorkflowHost workflows)
            {
                ReportStatus("This Cerebral host does not support native service workflows.");
                return;
            }
            var draft = currentHost.GetDraft();
            if (string.IsNullOrWhiteSpace(draft.Text))
            {
                ReportStatus("Enter a draft before starting review.");
                return;
            }
            requestId = Guid.NewGuid().ToString("N");
            var submission = new DesktopWorkflowSubmission(draft.Binding, requestId,
                "Review this draft.", "Run the installed example.review workflow against the exact captured draft.")
            {
                Target = new DesktopWorkflowTarget("example.review:review", "1.0.0",
                    JsonSerializer.SerializeToElement(new { text = draft.Text }), StepLimit: 8)
            };
            // Cancellation or a lost reply after this boundary cannot prove that no workflow started.
            attempted = true;
            var result = await workflows.SubmitWorkflowAsync(submission, request.Token);
            ReportStatus(result.Message ?? result.State switch
            {
                DesktopOperationState.Accepted => "Review started. Answer the requested fields in the conversation.",
                DesktopOperationState.Completed => "Review completed.",
                DesktopOperationState.Uncertain => Unconfirmed(requestId),
                DesktopOperationState.Rejected => "Review submission was rejected.",
                _ => "Review submission returned " + result.State + ". Inspect the conversation before trying again."
            });
        }
        catch (ObjectDisposedException)
        {
            RetireHost();
            ReportLocal(attempted ? Unconfirmed(requestId) : "The review host is no longer available.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested)
        {
            ReportStatus(attempted ? Unconfirmed(requestId) : "Review was cancelled before submission.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ReportStatus((attempted ? Unconfirmed(requestId) : "Review could not be prepared.") + " " + Bound(error.Message));
        }
        finally { submitting = false; }
    }

    private static string Unconfirmed(string? requestId) =>
        "Review submission" + (requestId is null ? "" : " " + requestId) +
        " could not be confirmed. Inspect the conversation/workflow before trying again; it may already have started.";
    private static string Bound(string text) => text.Length <= 512 ? text : text[..512];
    private void ReportLocal(string message)
    {
        if (disposed) return;
        statusViews.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in statusViews) if (reference.TryGetTarget(out var view)) view.Text = Bound(message);
    }
    private void ReportStatus(string message)
    {
        if (disposed) return;
        ReportLocal(message);
        if (hostUnavailable || host is not { } currentHost) return;
        try { if (!PluginHostAccess.TryNotify(() => currentHost.ReportStatus(message))) RetireHost(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A notification failure must never turn an acknowledged submission into an uncertain retry.
            ReportLocal(message + " Status notification failed (" + error.GetType().Name + ").");
        }
    }
    private void RetireHost()
    {
        if (hostUnavailable) return;
        hostUnavailable = true;
        cleanup.Attempt(() => lifetime.Cancel());
    }

    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; host = null;
        cleanup.Attempt(() => lifetime.Cancel());
        cleanup.Attempt(lifetime.Dispose);
        statusViews.Clear();
        cleanup.Complete();
    }
}
