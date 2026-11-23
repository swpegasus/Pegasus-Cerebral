using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Contracts;

/// <summary>Version and transport bounds for the canonical event envelope.</summary>
public static class CerebralEventContract
{
    public const string Id = "pegasus.cerebral.events";
    public const int Version = 1;
    public const int MaximumSerializedUtf8Bytes = 2 * 1024 * 1024;
}

public static class CerebralEventTypes
{
    public const string Message = "message";
    public const string ToolCall = "tool.call";
    public const string ToolResult = "tool.result";
    public const string ServiceUpdate = "service.update";
    public const string WorkflowTransition = "workflow.transition";
    public const string ContextCompactionRequested = "context.compaction.requested";
}

[JsonConverter(typeof(JsonStringEnumConverter<EventActorKind>))]
public enum EventActorKind
{
    User,
    Agent,
    Tool,
    Service,
    Plugin,
    System
}

[JsonConverter(typeof(JsonStringEnumConverter<EventSourceKind>))]
public enum EventSourceKind
{
    User,
    Agent,
    Tool,
    Service,
    Plugin,
    System,
    Imported
}

public sealed record EventActor(EventActorKind Kind, string? Id = null, string? DisplayName = null);

/// <summary>
/// Identifies the component that produced an event. Conversation and turn identity remain on the event itself;
/// this record carries optional plugin, agent and job coordinates without embedding process-local objects.
/// </summary>
public sealed record EventProvenance(
    EventSourceKind Source,
    string? SourceId = null,
    string? PluginId = null,
    string? PluginVersion = null,
    string? AgentId = null,
    string? JobId = null);

/// <summary>Structured payload for a tool request. Arguments remain JSON and are validated by the owning tool.</summary>
public sealed record ToolCallData(string CallId, string ToolName, JsonElement Arguments);

/// <summary>Structured terminal payload for a tool request. Event content carries any human/model-facing result.</summary>
public sealed record ToolResultData(
    string CallId,
    string ToolName,
    bool Success,
    bool Truncated = false,
    string? Error = null,
    long? DurationMilliseconds = null);

/// <summary>
/// Canonical append-only conversation event. Sequence zero represents a validated event before journal append;
/// the journal assigns a positive sequence with a record <c>with</c> expression.
/// </summary>
public sealed record CerebralEvent(
    string Id,
    string ConversationId,
    string TurnId,
    long Sequence,
    string Type,
    EventActor Actor,
    IReadOnlyList<ContentPart> Content,
    EventProjectionPolicy Projection,
    EventProvenance Provenance,
    DateTimeOffset CreatedAt,
    JsonElement? Data = null,
    string? ParentEventId = null,
    string? CorrelationId = null,
    bool ExcludedFromContext = false)
{
    public int SchemaVersion { get; init; } = CerebralEventContract.Version;
    public const int MaximumContentParts = 64;
    public const int MaximumDataCharacters = 1_048_576;

    /// <summary>Validates the bounded transport contract. Domain payload schemas are validated by their owner.</summary>
    public void Validate()
    {
        if (SchemaVersion != CerebralEventContract.Version)
            throw new ArgumentException($"Unsupported event schema version {SchemaVersion}.", nameof(SchemaVersion));
        ContractValidation.Identifier(Id, nameof(Id), 128);
        ContractValidation.Identifier(ConversationId, nameof(ConversationId), 128);
        ContractValidation.Identifier(TurnId, nameof(TurnId), 128);
        if (Sequence < 0) throw new ArgumentOutOfRangeException(nameof(Sequence), "Sequence cannot be negative.");
        ContractValidation.DottedName(Type, nameof(Type), 128);
        ArgumentNullException.ThrowIfNull(Actor);
        ArgumentNullException.ThrowIfNull(Provenance);
        ArgumentNullException.ThrowIfNull(Projection);
        ArgumentNullException.ThrowIfNull(Content);
        if (!Enum.IsDefined(Projection.Model) || !Enum.IsDefined(Projection.Human) || !Enum.IsDefined(Projection.Retention))
            throw new ArgumentException("Projection contains an unsupported enum value.", nameof(Projection));
        if (CreatedAt == default) throw new ArgumentException("CreatedAt is required.", nameof(CreatedAt));
        if (Content.Count > MaximumContentParts)
            throw new ArgumentException($"An event may contain at most {MaximumContentParts} content parts.", nameof(Content));
        for (var index = 0; index < Content.Count; index++)
        {
            if (Content[index] is null) throw new ArgumentException($"Content part {index} is null.", nameof(Content));
            Content[index].Validate(index);
        }
        ValidateActor(Actor);
        ValidateProvenance(Provenance);
        ContractValidation.OptionalIdentifier(ParentEventId, nameof(ParentEventId), 128);
        ContractValidation.OptionalIdentifier(CorrelationId, nameof(CorrelationId), 128);
        if (Data is { ValueKind: JsonValueKind.Undefined })
            throw new ArgumentException("Data cannot contain an undefined JSON value.", nameof(Data));
        if (Data is { } data && data.GetRawText().Length > MaximumDataCharacters)
            throw new ArgumentException($"Data may contain at most {MaximumDataCharacters} serialized characters.", nameof(Data));
        if (JsonSerializer.SerializeToUtf8Bytes(this).Length > CerebralEventContract.MaximumSerializedUtf8Bytes)
            throw new ArgumentException($"A serialized event may contain at most {CerebralEventContract.MaximumSerializedUtf8Bytes} UTF-8 bytes.");
    }

    private static void ValidateActor(EventActor actor)
    {
        if (!Enum.IsDefined(actor.Kind)) throw new ArgumentException("Actor.Kind is unsupported.", nameof(actor));
        ContractValidation.OptionalText(actor.Id, "Actor.Id", 256);
        ContractValidation.OptionalText(actor.DisplayName, "Actor.DisplayName", 256);
    }

    private static void ValidateProvenance(EventProvenance provenance)
    {
        if (!Enum.IsDefined(provenance.Source)) throw new ArgumentException("Provenance.Source is unsupported.", nameof(provenance));
        ContractValidation.OptionalText(provenance.SourceId, "Provenance.SourceId", 256);
        ContractValidation.OptionalIdentifier(provenance.PluginId, "Provenance.PluginId", 128);
        ContractValidation.OptionalText(provenance.PluginVersion, "Provenance.PluginVersion", 64);
        ContractValidation.OptionalIdentifier(provenance.AgentId, "Provenance.AgentId", 128);
        ContractValidation.OptionalIdentifier(provenance.JobId, "Provenance.JobId", 128);
    }
}
