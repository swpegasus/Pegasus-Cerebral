using System.Text.Json;

namespace Cerebral.MetricsPlugin;

public static class MetricsValidation
{
    public static bool Identifier(string? value, bool optional = true) => value is null ? optional :
        value.Length is > 0 and <= 200 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    public static void Preferences(MetricsPreferences value)
    {
        if (value is null || value.RetentionDays is < 1 or > 365 || value.MaximumBytes is < 1_048_576 or > 268_435_456)
            throw new ArgumentException("History retention must be 1–365 days and 1–256 MiB.");
    }

    public static void Query(MetricsQuery value)
    {
        if (value is null || value.From == default || value.To <= value.From || value.To - value.From > TimeSpan.FromDays(366) ||
            value.Limit is < 1 or > MetricsContract.MaximumQueryRows ||
            !Identifier(value.ModelId) || !Identifier(value.AgentId) || !Identifier(value.ConversationId) || !Identifier(value.ProjectId) || !Identifier(value.WorkerId) ||
            value.Origin is { } origin && !Enum.IsDefined(origin))
            throw new ArgumentException("Metrics requires a bounded time range, exact identifiers and at most 200 rows.");
    }

    // Strict legacy input shape includes field presence, not only deserialized values.
    // In particular explicit null v2 fields must not make an altered v1 row appear compatible.
    public static MetricObservation DeserializeObservation(byte[] data)
    {
        if (data.Length > MetricsContract.MaximumRecordBytes) throw new JsonException("Metrics record is oversized.");
        using var document = JsonDocument.Parse(data);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Metrics record must be an object.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw new JsonException("Metrics record has duplicate fields.");
        var row = JsonSerializer.Deserialize(data, MetricsContract.TypeInfo<MetricObservation>()) ?? throw new JsonException("Metrics record is missing.");
        if (row.SchemaVersion == 1 && (names.Contains(nameof(MetricObservation.ProviderObservedInputTokens)) ||
            names.Contains(nameof(MetricObservation.ProviderObservedOutputTokens))))
            throw new JsonException("Schema1 cannot contain schema2 progress fields.");
        return row;
    }

    public static bool Observation(MetricObservation value, DateTimeOffset now)
    {
        if (value is null || value.SchemaVersion is not (1 or 2) || value.Revision < 0 || value.ObservedAt == default ||
            value.ObservedAt > now.AddMinutes(5) || value.ObservedAt < now.AddYears(-2) ||
            !Enum.IsDefined(value.Kind) || !Enum.IsDefined(value.State) || !Enum.IsDefined(value.Origin) ||
            !Enum.IsDefined(value.Basis) || !Enum.IsDefined(value.OutputTokenScope) ||
            !Identifier(value.InstallationId, false) || !Identifier(value.SourceGeneration, false) || !Identifier(value.EntityId, false) ||
            new[] { value.WorkerId, value.ModelId, value.AgentId, value.ConversationId, value.ProjectId,
                value.RunId, value.EffectId, value.ToolName }.Any(id => !Identifier(id))) return false;
        if (value.SchemaVersion == 1 && (value.Origin == MetricOrigin.Unknown ||
            value.ProviderObservedInputTokens is not null || value.ProviderObservedOutputTokens is not null)) return false;
        if (new long?[] { value.ConfirmedInputTokens, value.ConfirmedOutputTokens,
            value.ProviderObservedInputTokens, value.ProviderObservedOutputTokens, value.PreparedInputTokens,
            value.EffectiveContextTokens, value.ResponseBudgetTokens, value.ReasoningReserveTokens,
            value.CachedTokens, value.ProcessedTokens, value.GenerationHostMilliseconds, value.WorkflowActiveMilliseconds,
            value.WorkflowWallMilliseconds, value.AllocatedSlots, value.AvailableSlots, value.QuarantinedSlots,
            value.ObservedExecutions, value.ObservedInferenceActivity }.Any(n => n is < 0 or > 9_000_000_000_000)) return false;
        if (new[] { value.ProviderPromptMilliseconds, value.ProviderOutputMilliseconds,
            value.InputTokensPerSecond, value.OutputTokensPerSecond, value.ConnectionLatencyMilliseconds }.Any(n => n is { } d && (!double.IsFinite(d) || d < 0 || d > 9_000_000_000_000))) return false;
        // Accept only fields meaningful for the declared source kind. This keeps accidental whole
        // object projections from polluting token totals or treating prepared budgets as consumption.
        if (value.Kind != MetricKind.Generation && (value.ConfirmedInputTokens is not null || value.ConfirmedOutputTokens is not null ||
            value.ProviderObservedInputTokens is not null || value.ProviderObservedOutputTokens is not null ||
            value.InputTokensPerSecond is not null || value.OutputTokensPerSecond is not null)) return false;
        if (value.Kind != MetricKind.Capacity && (value.AllocatedSlots is not null || value.AvailableSlots is not null ||
            value.QuarantinedSlots is not null || value.ObservedExecutions is not null || value.ObservedInferenceActivity is not null || value.ConnectionLatencyMilliseconds is not null)) return false;
        if (value.Kind != MetricKind.Workflow && (value.WorkflowActiveMilliseconds is not null || value.WorkflowWallMilliseconds is not null)) return false;
        if ((value.ConfirmedInputTokens is not null || value.ConfirmedOutputTokens is not null) &&
            value.Basis is not (MetricBasis.ProviderTelemetry or MetricBasis.HostReceipt)) return false;
        if ((value.InputTokensPerSecond is not null || value.OutputTokensPerSecond is not null ||
            value.ProviderObservedInputTokens is not null || value.ProviderObservedOutputTokens is not null) &&
            value.Basis != MetricBasis.ProviderTelemetry) return false;
        if (value.CachedTokens is { } cached && value.ProcessedTokens is { } processed && cached > processed) return false;
        return true;
    }
}
