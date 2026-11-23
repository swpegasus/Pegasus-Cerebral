using System.IO;
using System.Text;

namespace Cerebral.Integrator;

internal sealed record InstructionSnapshot(string Content, string Token, string Source);

/// <summary>One editor source, bound to one service generation. A failed mutation never retries itself.</summary>
internal sealed class InstructionDraft(string key, string label, long generation, InstructionSnapshot snapshot)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private bool invalidContent;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public long Generation { get; } = generation;
    public string Content { get; private set; } = snapshot.Content;
    public InstructionSnapshot Baseline { get; private set; } = snapshot;
    public bool Dirty => Content != Baseline.Content;
    public bool Blocked { get; private set; }
    public bool Uncertain { get; private set; }
    public string? Error { get; private set; }
    public Task? Saving { get; private set; }
    public bool SavePending => Saving is { IsCompleted: false };
    public bool NeedsAttention => Dirty || Uncertain || SavePending;
    public bool CanAutosave => Dirty && !Blocked && !SavePending;

    public void Edit(string content)
    {
        Content = content;
        if (invalidContent) { invalidContent = false; Blocked = false; Error = null; }
    }

    public static void Validate(string content)
    {
        if (content.Contains('\0')) throw new ArgumentException("Prompts cannot contain NUL characters.");
        try
        {
            if (Utf8.GetByteCount(content) > 32 * 1024)
                throw new ArgumentException("The prompt exceeds 32 KiB of UTF-8 text. Shorten it before saving.");
        }
        catch (EncoderFallbackException) { throw new ArgumentException("The prompt contains an incomplete Unicode character. Correct it before saving."); }
    }

    public void InvalidateConnection()
    {
        if (!NeedsAttention) return;
        Blocked = true;
        if (SavePending) Uncertain = true;
        Error = "The connection changed. This draft is retained for its original service; it will not be sent to the new connection.";
    }

    public void Hydrate(InstructionSnapshot current)
    {
        if (NeedsAttention) return;
        Baseline = current; Content = current.Content; Blocked = false; Error = null;
    }

    /// <summary>A user-requested read resolves uncertain saves, but does not rebase conflicting text.</summary>
    public bool Review(InstructionSnapshot current)
    {
        if (SavePending) throw new InvalidOperationException("Wait for the current prompt save before reviewing it.");
        invalidContent = false;
        if (current.Content == Content)
        {
            Baseline = current; Blocked = false; Uncertain = false; Error = null; return true;
        }
        if (current.Token == Baseline.Token)
        {
            Baseline = current; Blocked = false; Uncertain = false; Error = null; return true;
        }
        Blocked = true; Uncertain = false;
        Error = "The saved prompt changed elsewhere. Your draft is retained. Compare it with the saved version, then explicitly use that version or keep your draft against its new revision.";
        return false;
    }

    public void UseSaved(InstructionSnapshot current)
    {
        if (SavePending) throw new InvalidOperationException("Wait for the current prompt save.");
        invalidContent = false; Baseline = current; Content = current.Content; Blocked = false; Uncertain = false; Error = null;
    }

    public void KeepDraftAgainst(InstructionSnapshot reviewed)
    {
        if (SavePending) throw new InvalidOperationException("Wait for the current prompt save.");
        invalidContent = false; Baseline = reviewed; Blocked = false; Uncertain = false; Error = null;
    }

    public Task SaveAsync(Func<string, string, CancellationToken, Task<InstructionSnapshot>> save,
        Func<bool> stillCurrent, CancellationToken token)
    {
        if (SavePending) return Saving!;
        if (!CanAutosave) return Task.CompletedTask;
        var content = Content; var expected = Baseline.Token;
        try { Validate(content); }
        catch (ArgumentException error) { invalidContent = true; Blocked = true; Error = error.Message; return Task.CompletedTask; }
        // Publish the task before entering user code; synchronous fake adapters have the same state as real transports.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Saving = completion.Task;
        _ = SaveCoreAsync();
        return completion.Task;

        async Task SaveCoreAsync()
        {
            try
            {
                if (!stillCurrent()) { InvalidateConnection(); return; }
                var saved = await save(content, expected, token);
                if (!stillCurrent()) { InvalidateConnection(); return; }
                if (saved.Content != content || string.IsNullOrEmpty(saved.Token))
                    throw new InvalidDataException("The service returned a different prompt save receipt. Inspect the saved version before retrying.");
                Baseline = saved; Blocked = false; Uncertain = false; Error = null;
                // Content may already contain the next edit. Only the submitted text becomes the baseline.
            }
            catch (Exception error)
            {
                Blocked = true;
                Uncertain = error is IOException or System.Net.Http.HttpRequestException or OperationCanceledException or TimeoutException;
                Error = (Uncertain ? "Save was not confirmed. Review the saved version before retrying. " : "Not saved. ") + error.Message;
            }
            finally { completion.TrySetResult(); }
        }
    }
}
