using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static void RunInventoryIdentityAdmissionChecks(Action<bool, string> check)
    {
        const string run = "identity-summary-run";
        const string earlierId = "earlier.match";
        var schema = CerebralJson.Element(new
        {
            type = "object", maxProperties = 1,
            properties = new { root = new { type = "string" }, continuation = new { type = "string" } },
            additionalProperties = false
        });
        JsonObject Terminal(int count = 51, string job = run)
        {
            var identities = Enumerable.Range(0, count).Select(index => new
            {
                root = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "P", index.ToString()),
                id = index == 0 ? earlierId : "p" + index, name = "N" + index,
                components = new[] { new { component = "desktop", id = index == 0 ? earlierId : "p" + index, name = "N" + index } }
            }).ToArray();
            return JsonNode.Parse(JsonSerializer.Serialize(new
            {
                projects = Array.Empty<object>(), count = 0, limited = true, scaffoldReady = false,
                discoverySummaryOnly = true, identitySummaryComplete = true, identityScaffoldReady = true,
                inventoryJobId = job, inventoryComplete = true, inventoryId = new string('a', 32), inventoryPage = 2,
                totalProjectsFound = count, totalDirectoriesScanned = 4000,
                continuation = (string?)null, identitySummary = identities,
                next = "Inspect the complete identity summary. Existing sources require exact-root inspection."
            }))!.AsObject();
        }
        Fixture Prepared()
        {
            var fixture = new Fixture(warm: false, requestId: run);
            fixture.History([]); fixture.Catalog(forcedNames: ["plugin.projects", "plugin.scaffold"]);
            if (fixture.Is("catalog")) fixture.Catalog(exact: true);
            fixture.Select("plugin.projects", schema);
            return fixture;
        }
        Fixture Admit(JsonObject body, string provider = "cerebral.plugin-sdk", bool truncated = false, string? requestId = null, object? arguments = null)
        {
            var fixture = Prepared();
            fixture.Model(new { action = "tool", name = "plugin.projects", arguments = arguments ?? new { } });
            fixture.Tool(true, body.ToJsonString(), provider: provider, truncated: truncated, requestId: requestId);
            if (fixture.Is("catalog")) fixture.Catalog(exact: true);
            return fixture;
        }
        static bool Ready(Fixture fixture) => fixture.Step.State.GetProperty("scaffoldReadiness").GetProperty("ready").GetBoolean();

        var complete = Admit(Terminal());
        check(complete.Step.State.GetProperty("admissionRecoveryRunId").GetString() == run &&
            complete.Step.State.GetProperty("readRecovery").GetProperty("runId").GetString() == run,
            "normal submission request and workflow run share the exact job identity consumed by terminal inventory admission");
        check(complete.Is("generate") && Ready(complete) &&
            complete.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1 &&
            complete.Step.State.GetProperty("pluginBuildCandidates").GetArrayLength() == 0,
            "a complete job-bound compact identity catalog above fifty rows prepares scaffold availability without inventing an inspected build");
        var reference = complete.Context.Contributions.Single(source => source.Category == "sdk-reference-result");
        check(reference.Requirement == ContextRequirement.Required && reference.Complete &&
            reference.Messages.SelectMany(message => message.Content).OfType<TextContentPart>().Any(part => part.Text.Contains(earlierId, StringComparison.Ordinal)) &&
            complete.Step.State.GetProperty("selected").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "plugin.scaffold"),
            "the complete aggregate remains one required reference with every earlier identity visible before scaffold schema preparation");

        foreach (var failure in new[] { "job", "count", "partial", "token", "page", "summary", "omitted", "duplicate", "component", "projection" })
        {
            var body = Terminal();
            if (failure == "job") body["inventoryJobId"] = "foreign-job";
            if (failure == "count") body["totalProjectsFound"] = 52;
            if (failure == "partial") body["inventoryComplete"] = false;
            if (failure == "token") body["continuation"] = new string('b', 32);
            if (failure == "page") body["inventoryPage"] = 0;
            if (failure == "summary") body["identitySummaryComplete"] = false;
            if (failure == "omitted")
            {
                body.Remove("identitySummaryComplete");
                body["limited"] = false; body["scaffoldReady"] = true;
            }
            if (failure == "duplicate") body["identitySummary"]!.AsArray()[1]!["root"] = body["identitySummary"]!.AsArray()[0]!["root"]!.GetValue<string>();
            if (failure == "component") body["identitySummary"]!.AsArray()[0]!["components"]!.AsArray()[0]!["id"] = null;
            if (failure == "projection") body["identitySummary"]!.AsArray()[0]!["name"] = new string('p', 250);
            if (failure == "projection")
                foreach (var row in body["identitySummary"]!.AsArray()) row!["name"] = new string('p', 250);
            var rejected = Admit(body);
            check(rejected.Is("generate") && !Ready(rejected),
                "incomplete or contradictory identity coverage (" + failure + ") cannot offer new scaffold admission");
            if (failure is "projection" or "component")
                check(rejected.Step.State.GetProperty("scaffoldReadiness").GetProperty("reason").GetString()!
                    .Contains(failure == "projection" ? "model projection" : "metadata", StringComparison.Ordinal),
                    "recovery distinguishes clipped model projection from represented identity metadata gaps");
        }
        check(!Ready(Admit(Terminal(), provider: "other-provider")) &&
            !Ready(Admit(Terminal(), truncated: true)) &&
            !Ready(Admit(Terminal(), requestId: "different-effect")),
            "foreign provenance, clipped raw bodies and mismatched request identity cannot grant compact-summary readiness");
        check(!Ready(Admit(Terminal(), arguments: new { continuation = "malformed" })),
            "a new-envelope result cannot use malformed broad arguments to bypass strict validation through the legacy branch");
        foreach (var field in new[] { "count", "inventoryPage", "totalProjectsFound" })
            foreach (var kind in new[] { "string", "boolean", "object" })
            {
                var malformed = Terminal();
                if (kind == "string") malformed[field] = "2";
                if (kind == "boolean") malformed[field] = true;
                if (kind == "object") malformed[field] = new JsonObject();
                var recovered = Admit(malformed);
                check(recovered.Is("generate") && !Ready(recovered) &&
                    recovered.Step.State.GetProperty("toolReceipts").GetArrayLength() == 1 &&
                    recovered.Step.State.GetProperty("toolReceipts")[0].GetProperty("state").GetString() == "Succeeded",
                    "malformed " + field + " " + kind + " denies readiness while preserving the actual result receipt and correction opportunity");
            }

        var paging = Prepared();
        var first = Terminal(1);
        first["identitySummary"] = null; first["identitySummaryComplete"] = false;
        first["identityScaffoldReady"] = false; first["inventoryComplete"] = false;
        first["continuation"] = new string('b', 32); first["inventoryPage"] = 1;
        paging.Model(new { action = "tool", name = "plugin.projects", arguments = new { } });
        paging.Tool(true, first.ToJsonString(), provider: "cerebral.plugin-sdk");
        check(!Ready(paging), "a real partial page supplies continuation without establishing global absence");
        paging.Model(new { action = "tool", name = "plugin.projects", arguments = new { continuation = new string('b', 32) } });
        paging.Tool(true, Terminal().ToJsonString(), provider: "cerebral.plugin-sdk");
        check(paging.Is("catalog") && Ready(paging),
            "a terminal continuation can prepare the scaffold schema after complete aggregate proof");
        paging.Catalog(exact: true);
        check(paging.Context.Contributions.Single(source => source.Category == "sdk-reference-result" && source.Requirement == ContextRequirement.Required)
            .Messages.SelectMany(message => message.Content).OfType<TextContentPart>().Any(part => part.Text.Contains(earlierId, StringComparison.Ordinal)),
            "an identity from an earlier page remains visible in the final full reference even when detailed final rows are empty");
    }
}
