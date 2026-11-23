using Cerebral.Engine.Persistence;

namespace Cerebral.Worker;

internal sealed record RetentionSweepResult(int EngineConversationsVisited, int ActivitySessionsVisited,
    long EngineEventsRemoved, long ActivityEntriesRemoved, int PressureCount, int FailureCount);

/// <summary>
/// Applies rolling diagnostic retention outside request execution. Each pass is bounded and resumes from stable
/// identity cursors; accepted messages, workflow/domain state and model-bearing event content are never candidates.
/// </summary>
internal sealed class RetentionCoordinator : BackgroundService
{
    private readonly EventJournal journal;
    private readonly StateStore store;
    private readonly Configuration configuration;
    private readonly EventHub events;
    private readonly TimeProvider clock;
    private readonly TimeSpan interval;
    private readonly int pageSize;
    private readonly int maximumPagesPerSweep;
    private readonly SemaphoreSlim sweepGate = new(1, 1);
    private string? conversationCursor;
    private string? activityCursor;
    private SourceRecoveryFence? sourceRecoveryFence;

    internal void AttachSourceRecoveryFence(SourceRecoveryFence fence)
    {
        if (sourceRecoveryFence is not null) throw new InvalidOperationException("source_recovery_retention_already_bound");
        sourceRecoveryFence = fence;
    }

    public RetentionCoordinator(EventJournal journal, StateStore store, Configuration configuration, EventHub events,
        TimeProvider? clock = null, TimeSpan? interval = null, int pageSize = 64, int maximumPagesPerSweep = 8)
    {
        if (pageSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (maximumPagesPerSweep is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maximumPagesPerSweep));
        this.journal = journal;
        this.store = store;
        this.configuration = configuration;
        this.events = events;
        this.clock = clock ?? TimeProvider.System;
        this.interval = interval ?? TimeSpan.FromMinutes(15);
        if (this.interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        this.pageSize = pageSize;
        this.maximumPagesPerSweep = maximumPagesPerSweep;
    }

    internal async Task<RetentionSweepResult> RunSweepAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (sourceRecoveryFence is { } fence) await fence.WaitForReleaseAsync(cancellationToken).ConfigureAwait(false);
        await sweepGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = configuration.Get();
            var limits = new JournalRetentionLimits(settings.EngineEventHistoryLimit,
                checked((long)settings.EngineEventHistoryMegabytes * 1024 * 1024), settings.EngineEventHistoryDays);
            var engineVisited = 0; var activityVisited = 0;
            long engineRemoved = 0; long activityRemoved = 0;
            var pressure = 0; var failures = 0;
            string? firstFailureArea = null; string? firstPressureConversation = null;
            try { store.TrimPromptCacheDiagnostics(now); }
            catch (Exception) { failures++; firstFailureArea = "prompt-cache-prune"; }

            for (var page = 0; page < maximumPagesPerSweep; page++)
            {
                IReadOnlyList<string> conversations;
                try { conversations = journal.ReadConversationIds(conversationCursor, pageSize, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { failures++; firstFailureArea ??= "engine-enumeration"; break; }
                if (conversations.Count == 0) { conversationCursor = null; break; }
                foreach (var conversationId in conversations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engineVisited++;
                    try
                    {
                        var result = journal.PruneRolling(conversationId, limits, now, cancellationToken);
                        engineRemoved = checked(engineRemoved + result.RemovedEvents);
                        if (!result.QuotaSatisfied)
                        {
                            pressure++;
                            firstPressureConversation ??= conversationId;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { failures++; firstFailureArea ??= "engine-prune"; }
                }
                conversationCursor = conversations[^1];
                if (conversations.Count < pageSize) { conversationCursor = null; break; }
            }

            for (var page = 0; page < maximumPagesPerSweep; page++)
            {
                IReadOnlyList<string> sessions;
                try { sessions = store.ReadActivitySessionIds(activityCursor, pageSize, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { failures++; firstFailureArea ??= "activity-enumeration"; break; }
                if (sessions.Count == 0) { activityCursor = null; break; }
                foreach (var sessionId in sessions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    activityVisited++;
                    try
                    {
                        activityRemoved = checked(activityRemoved + store.TrimActivity(sessionId,
                            settings.ActivityHistoryLimit, settings.ActivityHistoryDays, trimByAge: true,
                            cancellationToken, now));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { failures++; firstFailureArea ??= "activity-prune"; }
                }
                activityCursor = sessions[^1];
                if (sessions.Count < pageSize) { activityCursor = null; break; }
            }

            if (failures > 0 || pressure > 0)
            {
                try
                {
                    events.Publish("retention.warning", null, new
                    {
                        failures,
                        pressure,
                        firstFailureArea,
                        firstPressureConversation,
                        message = failures > 0
                            ? "Rolling activity cleanup could not complete for every bounded item; it will retry on a later sweep."
                            : "Rolling Engine activity remains above its configured limit because required or active evidence is pinned."
                    });
                }
                catch (Exception) { }
            }
            return new(engineVisited, activityVisited, engineRemoved, activityRemoved, pressure, failures);
        }
        finally { sweepGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunSweepAsync(clock.GetUtcNow(), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                try
                {
                    events.Publish("retention.warning", null, new
                    {
                        failures = 1,
                        pressure = 0,
                        firstFailureArea = "coordinator",
                        message = "Rolling activity cleanup did not complete; it will retry on a later sweep."
                    });
                }
                catch (Exception) { }
            }

            try { await Task.Delay(interval, clock, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
