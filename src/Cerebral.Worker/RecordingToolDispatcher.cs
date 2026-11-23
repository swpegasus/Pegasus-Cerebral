using Cerebral.Core;

namespace Cerebral.Worker;

public sealed class RecordingToolDispatcher(IToolDispatcher inner, ActivityJournal journal) : IToolDispatcher
{
    public bool FullAccessActive => inner.FullAccessActive;
    public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken cancellationToken) =>
        inner.ListAsync(profile, cancellationToken);

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        ActivityEntry? entry = null;
        try { entry = journal.Start(call, context); }
        catch (Exception ex) { journal.ReportFailure(ex, context.SessionId, context.JobId); }
        using var progressStop = new CancellationTokenSource();
        var progress = entry is null ? Task.CompletedTask : ReportProgressAsync(entry, progressStop.Token);
        var mutation = IsCortexMutation(call);
        var dispatched = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            dispatched = true;
            var result = await inner.ExecuteAsync(call, context, cancellationToken);
            await Finish(result.Success ? "completed" : mutation && !CortexOperationPolicy.HasNoCommitReceipt(result) ? "uncertain" : "failed",
                result.Content, result.Truncated);
            return result;
        }
        catch (OperationCanceledException)
        {
            await Finish(mutation && dispatched ? "uncertain" : "cancelled",
                mutation && dispatched ? "Cortex mutation was interrupted without a completion receipt. Inspect the target before retrying."
                    : "Tool cancelled or timed out. Inspect any partial effects before retrying.");
            throw;
        }
        catch (Exception ex)
        {
            await Finish(mutation && dispatched ? "uncertain" : "failed", ex.GetType().Name + ": " + ex.Message);
            throw;
        }

        async Task Finish(string state, string content, bool truncated = false)
        {
            progressStop.Cancel();
            try { await progress; } catch (OperationCanceledException) { }
            if (entry is null) return;
            try { journal.Finish(entry, state, content, truncated); }
            catch (Exception ex) { journal.ReportFailure(ex, entry.SessionId, context.JobId); }
        }

        async Task ReportProgressAsync(ActivityEntry activity, CancellationToken token)
        {
            var started = DateTimeOffset.UtcNow;
            while (true)
            {
                await Task.Delay(journal.ProgressInterval, token);
                try
                {
                    if (!journal.ReportProgress(activity, DateTimeOffset.UtcNow - started)) return;
                }
                catch (Exception ex) { journal.ReportFailure(ex, activity.SessionId, context.JobId); }
            }
        }
    }

    private static bool IsCortexMutation(ToolCall call)
    {
        if (!call.Name.Split('.').Last().StartsWith("cortex_", StringComparison.Ordinal)) return false;
        try { return CortexOperationPolicy.IsMutation(call.Name, call.Arguments); }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException) { return true; }
    }
}
