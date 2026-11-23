using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.MetricsPlugin;

// A closed generated metadata set avoids process-wide reflection member accessors
// for collectible private types. There is deliberately no reflection fallback.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata,
    UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    MaxDepth = 12)]
[JsonSerializable(typeof(MetricObservation))]
[JsonSerializable(typeof(MetricsPreferences))]
[JsonSerializable(typeof(MetricsSettings))]
[JsonSerializable(typeof(MetricsSettingsChange))]
[JsonSerializable(typeof(MetricsQuery))]
[JsonSerializable(typeof(MetricsQueryResult))]
[JsonSerializable(typeof(MetricsStatus))]
[JsonSerializable(typeof(MetricsCoverageCheckpoint))]
internal partial class MetricsJsonContext : JsonSerializerContext;
