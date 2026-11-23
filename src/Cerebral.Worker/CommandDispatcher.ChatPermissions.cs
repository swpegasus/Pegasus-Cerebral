using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private ConversationProperties PermissionService(string? installationId = null) => new(store,
        workflows ?? throw new InvalidOperationException("Native chat permissions are unavailable on this Worker."),
        events, executionTools, installationId ?? configuration.Get().InstallationId);

    private object ConversationPermissionsGet(JsonElement args)
    {
        Fields(args, "id");
        return PermissionService().GetPermissions(Text(args, "id"));
    }

    private async Task<object> ConversationPermissionsSaveAsync(JsonElement args, CancellationToken token)
    {
        Fields(args, "id", "expectedRevision", "expectedAuthorityFingerprint",
            "expectedCatalogGeneration", "changes");
        var id = Text(args, "id");
        var revision = args.GetProperty("expectedRevision");
        if (!revision.TryGetInt64(out var expectedRevision) || expectedRevision < 0 ||
            args.GetProperty("changes").ValueKind != JsonValueKind.Array)
            throw new ArgumentException("An exact settings revision and permission change array are required.");
        var changes = args.GetProperty("changes").EnumerateArray().Select(change =>
        {
            Fields(change, "kind", "id", "decision", "descriptorFingerprint");
            return new ChatToolPermissions.Patch(Text(change, "kind"), Text(change, "id"),
                Text(change, "decision"), Optional(change, "descriptorFingerprint"));
        }).ToArray();
        var installationId = configuration.Get().InstallationId;
        var catalog = ChatRegisteredToolCatalog.Bind(await tools.ListRegisteredAsync(token)
            .ConfigureAwait(false), servicePlugins);
        if (configuration.Get().InstallationId != installationId)
            throw new InvalidOperationException("The registered Worker identity changed during permission review.");
        token.ThrowIfCancellationRequested();
        return PermissionService(installationId).SavePermissions(id, expectedRevision,
            Text(args, "expectedAuthorityFingerprint"), Text(args, "expectedCatalogGeneration"),
            changes, catalog);
    }

    private async Task<object> ConversationPermissionsCatalogAsync(JsonElement args, CancellationToken token)
    {
        Fields(args, "id", "offset", "limit", "expectedGeneration", "expectedSettingsRevision",
            "expectedProfileRevision");
        var id = Text(args, "id");
        var offset = args.TryGetProperty("offset", out var offsetValue) && offsetValue.TryGetInt32(out var start)
            ? start : 0;
        var limit = args.TryGetProperty("limit", out var limitValue) && limitValue.TryGetInt32(out var count)
            ? count : 50;
        var generation = Optional(args, "expectedGeneration");
        long? settingsRevision = args.TryGetProperty("expectedSettingsRevision", out var settings) &&
            settings.ValueKind != JsonValueKind.Null && settings.TryGetInt64(out var expectedSettings)
                ? expectedSettings : null;
        int? profileRevision = args.TryGetProperty("expectedProfileRevision", out var profile) &&
            profile.ValueKind != JsonValueKind.Null && profile.TryGetInt32(out var expectedProfile)
                ? expectedProfile : null;
        var installationId = configuration.Get().InstallationId;
        var catalog = ChatRegisteredToolCatalog.Bind(await tools.ListRegisteredAsync(token)
            .ConfigureAwait(false), servicePlugins);
        if (configuration.Get().InstallationId != installationId)
            throw new InvalidOperationException("The registered Worker identity changed during permission review.");
        token.ThrowIfCancellationRequested();
        return PermissionService(installationId).Catalog(id, catalog, offset, limit, generation,
            settingsRevision, profileRevision);
    }

    private static void Fields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Chat permissions require an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new ArgumentException("Chat permissions contain a duplicate or unsupported field.");
    }
}
