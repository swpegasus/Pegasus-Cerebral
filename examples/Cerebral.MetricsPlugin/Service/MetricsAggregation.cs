namespace Cerebral.MetricsPlugin.Service;

internal static class MetricsAggregation
{
    internal static MetricsQueryResult Project(MetricObservation[] rows, MetricsQuery query,
        MetricsCoverage coverage, DateTimeOffset now)
    {
        var generations = rows.Where(row => row.Kind == MetricKind.Generation).ToArray();
        var workflows = rows.Where(row => row.Kind == MetricKind.Workflow).ToArray();
        var tools = rows.Where(row => row.Kind == MetricKind.Tool && row.State != MetricState.Active).ToArray();
        var capacity = rows.Where(row => row.Kind == MetricKind.Capacity).ToArray();
        // A rate is a sampled provider-phase measurement. Never sum it, derive it from
        // wall time, or keep a stale active sample labeled as live.
        var rate = generations.Where(row => row.OutputTokensPerSecond is not null &&
            (row.State != MetricState.Active || now - row.ObservedAt <= TimeSpan.FromSeconds(2)))
            .OrderByDescending(row => row.ObservedAt).FirstOrDefault()?.OutputTokensPerSecond;
        int Count(MetricState state) => workflows.Count(row => row.State == state);
        var summary = new MetricsSummary(Total(generations.Select(row => row.ConfirmedInputTokens)),
            Total(generations.Select(row => row.ConfirmedOutputTokens)), generations.Length, workflows.Length,
            Count(MetricState.Completed), Count(MetricState.Failed), Count(MetricState.Cancelled),
            tools.Length, tools.Count(row => row.State == MetricState.Failed), rate,
            Peak(capacity.Select(row => row.ObservedExecutions)), Peak(capacity.Select(row => row.ObservedInferenceActivity)),
            generations.Where(row => row.ConfirmedOutputTokens is not null).Select(row => row.OutputTokenScope).Distinct().Order().ToArray(),
            generations.Where(row => row.ConfirmedInputTokens is not null || row.ConfirmedOutputTokens is not null).Select(row => row.Basis).Distinct().Order().ToArray())
        {
            ProviderObservedInputTokens = Total(generations.Select(row => row.ProviderObservedInputTokens)),
            ProviderObservedOutputTokens = Total(generations.Select(row => row.ProviderObservedOutputTokens))
        };
        var trends = rows.GroupBy(row => DateOnly.FromDateTime(row.ObservedAt.UtcDateTime)).OrderBy(group => group.Key)
            .Select(group => new MetricsTrend(group.Key,
                Total(group.Where(row => row.Kind == MetricKind.Generation).Select(row => row.ConfirmedInputTokens)),
                Total(group.Where(row => row.Kind == MetricKind.Generation).Select(row => row.ConfirmedOutputTokens)),
                group.Count(row => row.Kind == MetricKind.Workflow && row.State == MetricState.Completed),
                group.Count(row => row.Kind == MetricKind.Workflow && row.State == MetricState.Failed),
                group.Count(row => row.Kind == MetricKind.Tool && row.State != MetricState.Active))
            {
                ProviderObservedInputTokens = Total(group.Where(row => row.Kind == MetricKind.Generation).Select(row => row.ProviderObservedInputTokens)),
                ProviderObservedOutputTokens = Total(group.Where(row => row.Kind == MetricKind.Generation).Select(row => row.ProviderObservedOutputTokens))
            }).ToArray();
        var activity = rows.Where(row => row.AgentId is not null).GroupBy(row => row.AgentId!, StringComparer.Ordinal)
            .Select(group => new MetricsActivity(group.Key, group.Count(row => row.Kind == MetricKind.Generation),
                group.Count(row => row.Kind == MetricKind.Workflow && row.State == MetricState.Completed),
                group.Count(row => row.Kind == MetricKind.Workflow && row.State == MetricState.Failed),
                group.Count(row => row.Kind == MetricKind.Tool && row.State != MetricState.Active)))
            .OrderByDescending(row => row.Generations + row.ToolReceipts + row.CompletedWorkflows + row.FailedWorkflows)
            .ThenBy(row => row.AgentId, StringComparer.Ordinal).Take(20).ToArray();
        var pools = capacity.GroupBy(row => (row.InstallationId, row.WorkerId, row.ModelId, row.Origin))
            .Select(group => group.OrderByDescending(row => row.ObservedAt).First()).ToArray();
        return new("cerebral.metrics-history.query/2", now, query, coverage,
            summary, trends, activity,
            pools.Take(64).ToArray(), Math.Max(0, pools.Length - 64),
            rows.Take(query.Limit).ToArray(), Math.Max(0, rows.Length - query.Limit), rows.Length > query.Limit || pools.Length > 64);
    }
    private static int? Peak(IEnumerable<int?> values)
    { var known = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray(); return known.Length == 0 ? null : known.Max(); }
    private static MetricTotal Total(IEnumerable<long?> source)
    {
        var values = source.ToArray(); var known = values.Where(value => value.HasValue).ToArray();
        return new(known.Length == 0 ? null : checked(known.Sum(value => value!.Value)), known.Length, values.Length - known.Length);
    }
}
