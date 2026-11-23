using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    public static int HistoricalOperationsPreserveGuards(ContextContribution historical, long boundary)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Historical operation policy: " + message); checks++; }
        var reducible = new ContextContribution("ordinary-earlier-source", "conversation", "conversation", boundary,
            ContextRequirement.Reducible, [new("user", [new TextContentPart("Earlier context")])], BeforeCurrentInput: true);
        var fixture = new Fixture(history: [historical, reducible], input: new { requiredSuccessfulTools = new[] { "file.write" } });
        Check(fixture.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0 && fixture.Context.Contributions.Any(source => source.Id == historical.Id),
            "historical confirmed outcomes remain context and do not populate the current receipt ledger");
        fixture.Model(new { action = "final", text = "Historical write means this request is complete." });
        Check(fixture.Is("generate") && fixture.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0,
            "a historical successful write cannot satisfy the current required-success completion guard");
        fixture.RejectContext(ContextProjectionStatus.NeedsCompaction);
        Check(fixture.Is("compact") && ContextValidation.GetCoveredManifest(fixture.Context).All(source => source.Id != historical.Id),
            "the required historical summary remains outside compaction coverage");
        fixture.CompactSuccess();
        Check(fixture.Is("generate") && JsonSerializer.Serialize(fixture.Context.Contributions.Single(source => source.Id == historical.Id)) == JsonSerializer.Serialize(historical) &&
            fixture.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0,
            "compaction preserves the exact required evidence and leaves current completion state untouched");
        return checks;
    }
}
