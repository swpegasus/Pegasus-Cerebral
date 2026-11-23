using System.Text.Json;
using Cerebral.Contracts;

namespace Cerebral.Engine.Tests;

public static class EventContractTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            count++;
            Console.WriteLine("PASS: " + message);
        }

        var image = new AttachmentReference("att-1", 1, "image/png", "map.png", 1234,
            new Dictionary<string, string> { ["purpose"] = "book-map" });
        var value = JsonSerializer.SerializeToElement(new { chapter = 3, accepted = true });
        var original = new CerebralEvent(
            "evt-1", "conversation-1", "turn-1", 0, CerebralEventTypes.Message,
            new(EventActorKind.Agent, "lead", "Cerebral"),
            [
                new TextContentPart("The route is ready.", "markdown"),
                new ImageContentPart(image, "A map", 640, 480),
                new StructuredContentPart("pegasus.books.chapter/1", value),
                new ResourceContentPart(new("pegasus.books", "book/a/chapter/3", "7", "application/json"))
            ],
            new(ModelInclusion.Evidence, HumanVisibility.Activity, RetentionPolicy.Durable),
            new(EventSourceKind.Plugin, "books.workflow", "pegasus.books", "2.0.0", "editor", "job-7"),
            DateTimeOffset.Parse("2026-09-25T12:34:56Z"),
            JsonSerializer.SerializeToElement(new ToolResultData("call-4", "books.chapter.context", true, false, DurationMilliseconds: 25), JsonSerializerOptions.Web),
            CorrelationId: "operation-9");
        original.Validate();

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(original, options);
        var restored = JsonSerializer.Deserialize<CerebralEvent>(json, options) ?? throw new Exception("FAIL: event did not deserialize");
        restored.Validate();
        Check(restored.Content.Count == 4 && restored.Content[0] is TextContentPart && restored.Content[1] is ImageContentPart &&
            restored.Content[2] is StructuredContentPart && restored.Content[3] is ResourceContentPart,
            "typed content parts survive a JSON round trip");
        Check(restored.SchemaVersion == CerebralEventContract.Version,
            "the event schema version survives a JSON round trip");
        Check(restored.Provenance.PluginId == "pegasus.books" && restored.Provenance.JobId == "job-7" && restored.TurnId == "turn-1",
            "turn, plugin, agent and job provenance survive a JSON round trip");
        Check(restored.Data is { } data && data.GetProperty("toolName").GetString() == "books.chapter.context",
            "typed tool result data remains structured JSON");

        var appended = restored with { Sequence = 42 };
        appended.Validate();
        Check(restored.Sequence == 0 && appended.Sequence == 42 && appended.Id == restored.Id,
            "the journal can assign a sequence without changing the stable event ID");
        Check(!json.Contains("base64", StringComparison.OrdinalIgnoreCase) &&
            ((ImageContentPart)restored.Content[1]).Attachment.Revision == 1,
            "attachments serialize as durable references rather than embedded binary data");

        var hidden = appended with { Projection = appended.Projection with { Human = HumanVisibility.Hidden } };
        Check(EventProjection.IncludeInModel(hidden) && !EventProjection.IsVisibleToHuman(hidden),
            "human-hidden evidence remains eligible for model context");
        var displayOnly = appended with { Projection = appended.Projection with { Model = ModelInclusion.Never, Human = HumanVisibility.Transcript } };
        Check(!EventProjection.IncludeInModel(displayOnly) && EventProjection.ShowInTranscript(displayOnly),
            "display-only content remains visible without entering model context");
        var excluded = appended with { ExcludedFromContext = true, Projection = appended.Projection with { Model = ModelInclusion.Required, Human = HumanVisibility.Transcript } };
        Check(!EventProjection.IncludeInModel(excluded) && EventProjection.ShowInTranscript(excluded),
            "explicit context exclusion overrides model priority without hiding the human transcript");
        var onDemand = appended with { Projection = appended.Projection with { Model = ModelInclusion.OnDemand } };
        Check(!EventProjection.IncludeInModel(onDemand) && EventProjection.IncludeInModel(onDemand, includeOnDemand: true),
            "on-demand context requires an explicit projection choice");

        var rejected = false;
        try { (appended with { Sequence = -1 }).Validate(); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "contract validation rejects a negative journal sequence");

        rejected = false;
        try
        {
            (appended with
            {
                Content = [
                    new TextContentPart(new string('a', 800_000)),
                    new TextContentPart(new string('b', 800_000)),
                    new TextContentPart(new string('c', 800_000))
                ]
            }).Validate();
        }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "contract validation enforces the 2 MiB aggregate serialized-event bound");

        rejected = false;
        try
        {
            var unknown = json.Replace("\"kind\":\"text\"", "\"kind\":\"unknown\"", StringComparison.Ordinal);
            _ = JsonSerializer.Deserialize<CerebralEvent>(unknown, options);
        }
        catch (JsonException) { rejected = true; }
        Check(rejected, "unknown content discriminators fail deserialization instead of being ignored");

        Console.WriteLine($"Event contract: {count} deterministic checks passed.");
    }
}
