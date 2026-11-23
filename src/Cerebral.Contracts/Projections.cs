using System.Text.Json.Serialization;

namespace Cerebral.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ModelInclusion>))]
public enum ModelInclusion
{
    Required,
    Recent,
    Evidence,
    Summarizable,
    OnDemand,
    Never
}

[JsonConverter(typeof(JsonStringEnumConverter<HumanVisibility>))]
public enum HumanVisibility
{
    Transcript,
    Activity,
    Sidebar,
    Hidden
}

[JsonConverter(typeof(JsonStringEnumConverter<RetentionPolicy>))]
public enum RetentionPolicy
{
    Durable,
    Rolling,
    Ephemeral
}

public sealed record EventProjectionPolicy(
    ModelInclusion Model = ModelInclusion.Recent,
    HumanVisibility Human = HumanVisibility.Transcript,
    RetentionPolicy Retention = RetentionPolicy.Durable);

/// <summary>Pure projection predicates. Storage and budget selection remain responsibilities of the caller.</summary>
public static class EventProjection
{
    public static bool IncludeInModel(CerebralEvent item, bool includeOnDemand = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(item.Projection);
        if (item.ExcludedFromContext || item.Projection.Model == ModelInclusion.Never) return false;
        return item.Projection.Model != ModelInclusion.OnDemand || includeOnDemand;
    }

    public static bool ShowInTranscript(CerebralEvent item) => Visibility(item) == HumanVisibility.Transcript;
    public static bool ShowInActivity(CerebralEvent item) => Visibility(item) == HumanVisibility.Activity;
    public static bool ShowInSidebar(CerebralEvent item) => Visibility(item) == HumanVisibility.Sidebar;
    public static bool IsVisibleToHuman(CerebralEvent item) => Visibility(item) != HumanVisibility.Hidden;

    private static HumanVisibility Visibility(CerebralEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(item.Projection);
        return item.Projection.Human;
    }
}
