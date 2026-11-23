using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop;

/// <summary>Deterministic plugin-lease admission checks. No service commands or inference.</summary>
public static class DesktopExplicitWorkflowChecks
{
    public static async Task<int> VerifyAsync()
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("A WPF dispatcher is required.");
        dispatcher.VerifyAccess();
        var checks = 0; var submitted = 0; DesktopWorkflowSubmission? received = null;
        var binding = new DesktopConversationBinding("generation", "chat");
        var draft = new DesktopDraftSnapshot(binding, 0, "UNRELATED DRAFT");
        TaskCompletionSource<DesktopOperationResult>? delayed = null;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        static Task<DesktopOperationResult> Done() => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        var adapter = new DesktopPluginHostAdapter(dispatcher, () => new(binding, "Chat", null, null, false), () => draft,
            (_, _) => throw new InvalidOperationException("Explicit workflow must not modify the composer."),
            (_, _) => throw new InvalidOperationException("Explicit workflow must not use ordinary Chat."),
            (_, _) => Done(), (_, _) => Done(), _ => { },
            submitWorkflow: (_, input, token) =>
            {
                submitted++; received = input;
                return delayed is null ? Done() : delayed.Task.WaitAsync(token);
            });
        using var scoped = adapter.CreateScope("pegasus.books", "Books", Environment.CurrentDirectory);
        IDesktopNativeWorkflowHost host = scoped;
        DesktopWorkflowSubmission Input(JsonElement value) => new(binding, "request", "VISIBLE", "PRIVATE")
            { Target = new("pegasus.books:chapter.write", "1.1.0", value, 128) };
        using var valid = JsonDocument.Parse("""{"bookId":"fixture","chapterId":1}""");
        var submission = Input(valid.RootElement);
        await host.SubmitWorkflowAsync(submission);
        Check(received?.Target is { DefinitionId: "pegasus.books:chapter.write", DefinitionVersion: "1.1.0", StepLimit: 128 } &&
            received.VisibleText == "VISIBLE" && received.Context == "PRIVATE" && draft.Text == "UNRELATED DRAFT",
            "The explicit target, visible request and private context remain separate without composer mutation.");
        var oldConstructor = new DesktopWorkflowSubmission(binding, "legacy", "VISIBLE", "PRIVATE");
        await host.SubmitWorkflowAsync(oldConstructor);
        Check(received?.Target is null, "The unchanged four-argument constructor preserves legacy private Chat planning.");

        var before = submitted;
        foreach (var invalid in new[]
        {
            submission with { Target = submission.Target! with { DefinitionId = "pegasus.books.evil:chapter.write" } },
            submission with { Target = submission.Target! with { DefinitionId = "another.plugin:chapter.write" } },
            submission with { Target = submission.Target! with { DefinitionId = "pegasus.books:" } },
            submission with { Target = submission.Target! with { DefinitionVersion = "latest" } },
            submission with { Target = submission.Target! with { StepLimit = 0 } },
            submission with { Target = submission.Target! with { StepLimit = 257 } },
            submission with { Target = submission.Target! with { Input = JsonSerializer.SerializeToElement(new[] { 1 }) } },
            submission with { Target = submission.Target! with { Input = JsonSerializer.SerializeToElement(new { value = new string('x', 131072) }) } },
            submission with { Context = "unsafe\0text" }
        })
            Check((await host.SubmitWorkflowAsync(invalid)).State == DesktopOperationState.Rejected,
                "Invalid namespace, version, limit, object, byte bound or text must be rejected before dispatch.");
        using (var duplicates = JsonDocument.Parse("""{"nested":{"value":1,"value":2}}"""))
            Check((await host.SubmitWorkflowAsync(Input(duplicates.RootElement))).State == DesktopOperationState.Rejected,
                "Duplicate JSON properties cannot cross desktop admission.");
        Check(submitted == before, "Invalid inputs never reach the submission callback.");

        delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var document = JsonDocument.Parse("""{"bookId":"immutable","chapterId":7}""");
        var pending = host.SubmitWorkflowAsync(Input(document.RootElement));
        document.Dispose();
        Check(received!.Target!.Input.GetProperty("bookId").GetString() == "immutable",
            "Admitted JSON is independent of the plugin's disposable document before an asynchronous transport completes.");
        delayed.SetResult(new(DesktopOperationState.Uncertain, "Fixture lost acknowledgement.", "request"));
        Check((await pending).State == DesktopOperationState.Uncertain,
            "Uncertainty reaches the plugin unchanged instead of becoming automatic retry or Chat fallback.");
        delayed = null;
        binding = new("replacement", "other-chat");
        before = submitted;
        Check((await host.SubmitWorkflowAsync(submission)).State == DesktopOperationState.Rejected && submitted == before,
            "A stale conversation or connection binding is rejected without dispatch.");

        scoped.Dispose();
        var disposed = false;
        try { await host.SubmitWorkflowAsync(submission); } catch (ObjectDisposedException) { disposed = true; }
        Check(disposed && submitted == before, "An unloaded plugin lease cannot submit work.");
        checks += await WorkflowStartChecks.VerifyAsync();
        checks += await WorkflowSteeringChecks.VerifyAsync();
        if (Environment.GetEnvironmentVariable("PEGASUS_WORK_GUIDANCE_PREVIEW") is { Length: > 0 } preview)
            await WorkflowSteeringChecks.RenderPreviewAsync(preview);
        Console.WriteLine($"Explicit desktop workflow admission: {checks} deterministic checks passed.");
        return checks;
    }
}
