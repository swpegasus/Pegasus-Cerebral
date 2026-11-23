using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private InternetPolicies InternetService() => new(store, configuration,
        workflows ?? throw new InvalidOperationException("Native Internet authority is unavailable."),
        events, () => ChatRegisteredToolCatalog.Bind(tools.ListManagedInternet(), servicePlugins));
    private object InternetCapabilitiesRead(JsonElement args)
    {
        Fields(args);
        var current = InternetService().Global();
        return new InternetCapabilities(InternetAuthorityContract.Id, 1, InternetAuthorityContract.Scope,
            current.ManagedTools.Select(x => x.ToolId).ToArray(), InternetOperations.Select(x => x.Id).ToArray(),
            ["remote inference/provider HTTP", "configured MCP/provider-owned network", "paired remote Worker transports",
                "registered executable and third-party plugin sockets", "browser-native process traffic outside intercepted HTTP"],
            "AllowedDomains null means unrestricted public destinations; [] means deny all. Exact lowercase ASCII domains; no wildcards. BlockedDomains always deny.",
            "Broker bounds response body bytes as forwarded, including compressed bytes; http.fetch also bounds decoded text. This is not a total wire/OS traffic budget.");
    }
    private object InternetGlobalGet(JsonElement args) { Fields(args); return InternetService().Global(); }
    private object InternetGlobalSave(JsonElement args) => InternetService().SaveGlobal(InternetJson.Read<InternetGlobalSaveRequest>(args));
    private object InternetMainGet(JsonElement args)
    { Fields(args, "mainConversationId"); return InternetService().Main(Text(args, "mainConversationId")); }
    private object InternetMainSave(JsonElement args) => InternetService().SaveMain(InternetJson.Read<InternetMainSaveRequest>(args));
    private object InternetRequestGet(JsonElement args)
    { Fields(args, "requestId"); return InternetService().GetRequest(Text(args, "requestId")); }
    private object InternetRequestList(JsonElement args)
    {
        Fields(args, "mainConversationId", "afterRequestId", "limit");
        return InternetService().List(Text(args, "mainConversationId"), Optional(args, "afterRequestId"),
            Number(args, "limit", 20, 1, 50));
    }
    private object InternetRequestRetire(JsonElement args) => InternetService().Retire(InternetJson.Read<InternetRequestRetireRequest>(args));
    private object InternetRequestDecide(JsonElement args) => InternetService().Decide(InternetJson.Read<InternetDecisionRequest>(args));

    private static JsonElement InternetSchema(string properties, string required)
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{" + properties +
            "},\"required\":[" + required + "],\"additionalProperties\":false}");
        return document.RootElement.Clone();
    }
    private const string InternetIdSchema = "{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}";
    private const string InternetHashSchema = "{\"type\":\"string\",\"minLength\":64,\"maxLength\":64,\"pattern\":\"^[0-9a-f]{64}$\"}";
    private const string InternetRevisionSchema = "{\"type\":\"integer\",\"minimum\":0}";
    private const string InternetLimitsSchema = """
        {"type":"object","properties":{
          "requestsPerSecond":{"type":"number","minimum":0.1,"maximum":100},
          "burst":{"type":"integer","minimum":1,"maximum":100},
          "maximumConcurrentRequests":{"type":"integer","minimum":1,"maximum":32},
          "requestTimeoutSeconds":{"type":"integer","minimum":1,"maximum":120},
          "maximumResponseBytes":{"type":"integer","minimum":1024,"maximum":8388608},
          "maximumRedirects":{"type":"integer","minimum":0,"maximum":10},
          "allowedDomains":{"oneOf":[{"type":"null"},{"type":"array","maxItems":64,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":253}}]},
          "blockedDomains":{"oneOf":[{"type":"null"},{"type":"array","maxItems":64,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":253}}]}
        },"additionalProperties":false}
        """;
    private static readonly string InternetToolRulesSchema = """
        {"type":"array","maxItems":6,"items":{"type":"object","properties":{
          "toolId":{"type":"string","minLength":1,"maxLength":256},
          "descriptorFingerprint":{"type":"string","minLength":64,"maxLength":64,"pattern":"^[0-9a-f]{64}$"}},
          "required":["toolId","descriptorFingerprint"],"additionalProperties":false}}
        """;
    private static readonly string InternetToolIdsSchema = """
        {"oneOf":[{"type":"null"},{"type":"array","maxItems":6,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":256}}]}
        """;
    private static readonly IReadOnlyList<WorkerWorkflows.OperationDescriptor> InternetOperations = BuildInternetOperations();
    private static IReadOnlyList<WorkerWorkflows.OperationDescriptor> BuildInternetOperations()
    {
        var id = InternetIdSchema; var hash = InternetHashSchema; var rev = InternetRevisionSchema;
        return [
            new("internet.capabilities", 1, "Owner read: exact managed web coverage and uncovered transport boundaries.", InternetSchema("", ""), true),
            new("internet.policy.get", 1, "Owner read: global managed web ceiling and local-only tool catalog.", InternetSchema("", ""), true),
            new("internet.policy.save", 1, "Owner CAS global managed web policy; never grants a chat or bypasses Deny.",
                InternetSchema("\"expectedRevision\":" + rev + ",\"expectedCatalogGeneration\":" + hash +
                    ",\"mode\":{\"enum\":[\"off\",\"ask\",\"allow\"]},\"limits\":" + InternetLimitsSchema + ",\"allowedTools\":" + InternetToolRulesSchema,
                    "\"expectedRevision\",\"expectedCatalogGeneration\",\"mode\",\"limits\",\"allowedTools\""), true),
            new("conversations.internet.get", 1, "Owner read: exact owning Main policy, provenance and effective managed web limits.",
                InternetSchema("\"mainConversationId\":" + id, "\"mainConversationId\""), true),
            new("conversations.internet.save", 1, "Owner CAS Main consent/restrictions within the global managed web ceiling.",
                InternetSchema("\"mainConversationId\":" + id + ",\"expectedRevision\":" + rev + ",\"expectedGlobalRevision\":" + rev +
                    ",\"expectedGlobalFingerprint\":" + hash + ",\"expectedRootRevision\":" + rev + ",\"expectedCatalogGeneration\":" + hash +
                    ",\"choice\":{\"enum\":[\"inherit\",\"ask\",\"allow\",\"deny\"]},\"limits\":{\"oneOf\":[{\"type\":\"null\"}," + InternetLimitsSchema + "]},\"allowedTools\":" + InternetToolIdsSchema,
                    "\"mainConversationId\",\"expectedRevision\",\"expectedGlobalRevision\",\"expectedGlobalFingerprint\",\"expectedRootRevision\",\"expectedCatalogGeneration\",\"choice\",\"limits\",\"allowedTools\""), true),
            new("internet.requests.get", 1, "Owner read: exact no-send request and native Main question readiness.",
                InternetSchema("\"requestId\":" + hash, "\"requestId\""), true),
            new("internet.requests.list", 1, "Owner bounded Main request page; no policy or request mutation.",
                InternetSchema("\"mainConversationId\":" + id + ",\"afterRequestId\":{\"oneOf\":[{\"type\":\"null\"}," + hash + "]},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50}",
                    "\"mainConversationId\""), true),
            new("internet.requests.retire", 1, "Owner CAS archive of a terminal-source request; full evidence and retry identity are retained.",
                InternetSchema("\"requestId\":" + hash + ",\"expectedRevision\":" + rev + ",\"requestDigest\":" + hash,
                    "\"requestId\",\"expectedRevision\",\"requestDigest\""), true),
            new("internet.requests.decide", 1, "Owner exact CAS Allow/Deny and idempotent native-question resume. Retry only identical DecisionId input.",
                InternetSchema("\"requestId\":" + hash + ",\"expectedRequestRevision\":" + rev + ",\"requestDigest\":" + hash +
                    ",\"expectedGlobalRevision\":" + rev + ",\"expectedGlobalFingerprint\":" + hash + ",\"expectedMainRevision\":" + rev +
                    ",\"expectedRootRevision\":" + rev + ",\"questionSetId\":" + id + ",\"expectedQuestionRevision\":" + rev +
                    ",\"allowed\":{\"type\":\"boolean\"},\"decisionId\":" + id,
                    "\"requestId\",\"expectedRequestRevision\",\"requestDigest\",\"expectedGlobalRevision\",\"expectedGlobalFingerprint\",\"expectedMainRevision\",\"expectedRootRevision\",\"questionSetId\",\"expectedQuestionRevision\",\"allowed\",\"decisionId\""), true)
        ];
    }
}
