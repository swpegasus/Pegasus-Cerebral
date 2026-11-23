using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Durable conversation bridge to the signed-in desktop. Success means queued, never displayed.</summary>
public sealed class InterfaceToolExtension : IToolExtension
{
    public const string ToolName = "interface.present";
    public const string PluginToolName = "plugin.present";
    public const string StatusToolName = "interface.status";
    private readonly InterfaceRequests requests;
    public InterfaceToolExtension(StateStore store, EventHub events, Func<int> executionAttemptLimit)
        : this(store, events, executionAttemptLimit, null) { }

    public InterfaceToolExtension(StateStore store, EventHub events, Func<int> executionAttemptLimit,
        Func<bool>? fullAccess) =>
        requests = new(store, events, fullAccess ?? (() => false), executionAttemptLimit);

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(ToolName,
            "Queue one existing local file, folder, or executable application for the signed-in Cerebral desktop. For a native plugin package in Conversation, use plugin.present after plugin.verify with its exact built desktop-plugin.json; a JSON manifest is not an executable application. The plugin kind remains a compatibility route outside Conversation. The exact saved profile grants and path are rechecked when the desktop claims it. A successful tool receipt proves only that the request is pending; do not claim it opened, ran, installed, or rendered.",
            CerebralJson.Element(new
            {
                type = "object",
                properties = new
                {
                    kind = new { type = "string", @enum = new[] { "file", "folder", "application", "plugin" } },
                    path = new { type = "string", minLength = 1, maxLength = 32768 }
                },
                required = new[] { "kind", "path" }, additionalProperties = false
            }), "filesystem.read"),
        new(PluginToolName,
            "Queue one exact built desktop-plugin.json package for owner review in the signed-in Cerebral desktop. SDK-generated Release packages are rechecked for implemented source, fresh output and entrypoint metadata before queueing. A successful tool receipt proves only that review is pending; do not claim the plugin installed, enabled, loaded, rendered, or worked until the desktop receipt confirms it.",
            CerebralJson.Element(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", minLength = 1, maxLength = 32768 },
                    expectedPackageFingerprint = new
                    {
                        type = "string", minLength = 64, maxLength = 64, pattern = "^[a-f0-9]{64}$",
                        description = "Optional exact packageFingerprint returned by plugin.verify. Changed package bytes reject before owner review is queued."
                    }
                },
                required = new[] { "path" }, additionalProperties = false
            }), "filesystem.read") { PrerequisiteTools = ["plugin.verify"] },
        new(StatusToolName,
            "Read one retained presentation receipt owned by this root user conversation or its exact approved plugin QA continuation. requestId is the exact requestId returned by interface.present or plugin.present. The literal reviewed is available only inside the automatic owner-approved plugin QA run and resolves its already-bound presentation; it never selects a recent presentation. For plugins, this same value is plugin.controls presentationId. A plugin generation and a plugin.controls receipt are never presentation IDs. Pending/claimed means awaiting the owner; completed confirms presentation only, not controls or persistence. Read after an owner action; do not repeatedly poll. This cannot approve, install, replay or invoke anything.",
            CerebralJson.Element(new
            {
                type = "object",
                properties = new
                {
                    requestId = new
                    {
                        oneOf = new object[]
                        {
                            new { type = "string", minLength = 32, maxLength = 32 },
                            new { type = "string", @enum = new[] { InterfaceRequests.ReviewedReference } }
                        },
                        description = "Exact requestId returned by interface.present or plugin.present, or reviewed only in its bound automatic plugin QA run. For plugin controls, reuse plugin.present's requestId (or reviewed in that bound QA run) as presentationId; never use a plugin generation or plugin.controls receipt."
                    }
                },
                required = new[] { "requestId" }, additionalProperties = false
            }), "filesystem.read")
    ];

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (call.Name == StatusToolName) return Task.FromResult(ReadStatus(call.Arguments, context));
        if (call.Name is not (ToolName or PluginToolName)) throw new ArgumentException("Unknown interface tool.");
        InterfaceRequest request;
        try
        {
            if (call.Arguments.ValueKind != JsonValueKind.Object ||
                call.Arguments.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() !=
                    call.Arguments.EnumerateObject().Count() ||
                call.Arguments.EnumerateObject().Any(property => call.Name == PluginToolName
                    ? property.Name is not ("path" or "expectedPackageFingerprint")
                    : property.Name is not ("kind" or "path")))
                throw new ArgumentException("Presentation accepts only its advertised path/kind fields; expectedPackageFingerprint is available only on plugin.present.");
            var kind = call.Name == PluginToolName ? "plugin" : Text(call.Arguments, "kind");
            var path = Text(call.Arguments, "path");
            var expected = call.Name == PluginToolName && call.Arguments.TryGetProperty("expectedPackageFingerprint", out _)
                ? Text(call.Arguments, "expectedPackageFingerprint") : null;
            request = requests.CreateForWorkflow(context, kind, path, expected);
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or IOException or InvalidDataException or JsonException or InvalidOperationException)
        {
            // These failures occur before an interface request is persisted or claimed. Preserve them as
            // deterministic rejection evidence so a durable conversation can correct its package/path.
            return Task.FromResult(new ToolResult(false, error.Message)
            {
                Evidence = new(ToolExecutionOutcome.Rejected, "cerebral.interface")
            });
        }
        var result = JsonSerializer.Serialize(new
        {
            requestId = request.Id, request.Status, request.Kind,
            packageFingerprint = request.PackageFingerprint,
            message = request.Status == "pending"
                ? "Queued for the signed-in Cerebral desktop. This is not proof that the presentation completed."
                : "Recovered the retained interface request; inspect its desktop receipt before claiming an outcome."
        }, CerebralJson.Options);
        return Task.FromResult(new ToolResult(true, result)
        {
            Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.interface", RequestId: request.Id)
        });
    }

    private ToolResult ReadStatus(JsonElement arguments, ToolExecutionContext context)
    {
        try
        {
            if (arguments.ValueKind != JsonValueKind.Object ||
                arguments.EnumerateObject().Any(property => property.Name != "requestId"))
                throw new ArgumentException("Presentation status accepts only requestId.");
            var id = requests.ResolvePresentationReference(Text(arguments, "requestId"), context);
            if (!Guid.TryParseExact(id, "N", out var parsed) || parsed == Guid.Empty)
                throw new ArgumentException("Presentation status requires an exact nonempty 32-character requestId.");
            var request = requests.GetForContext(id, context);
            // Claim IDs are owner control tokens. Return only reported observations;
            // no mutable owner fields, unbounded process output or new authority.
            return new ToolResult(true, JsonSerializer.Serialize(new
            {
                requestId = request.Id,
                status = request.Status,
                kind = request.Kind,
                sourceManifest = request.Kind == "plugin" ? request.Path : null,
                generatedSourceRoot = request.Kind == "plugin" ? request.GeneratedSourceRoot : null,
                packageFingerprint = request.PackageFingerprint,
                message = request.Result,
                error = request.Error,
                failureStage = request.FailureStage,
                resource = request.Resource,
                plugin = request.Plugin,
                tests = request.PluginTests
            }, CerebralJson.Options))
            {
                Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.interface", RequestId: request.Id, State: request.Status)
            };
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException)
        {
            return new ToolResult(false, error.Message +
                " For a plugin, use plugin.present's exact requestId, or reviewed only in its bound automatic plugin QA run; a plugin generation and a plugin.controls receipt are never presentation IDs.")
            {
                Evidence = new(ToolExecutionOutcome.Rejected, "cerebral.interface", State: "rejected")
            };
        }
    }

    private static string Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new ArgumentException("Interface " + name + " must be a nonempty string.");
}
