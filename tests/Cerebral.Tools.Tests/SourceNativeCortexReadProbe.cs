using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Tools;

internal static class SourceNativeCortexReadProbe
{
    private const string GuideId = "Knowledge.Cerebral.Guide.BoundedWork";
    private static readonly string[] ReadNamespaces = ["project.pegasus-cerebral", "knowledge.shared"];
    private sealed class ProbeFailure(string reason) : Exception { public string Reason { get; } = reason; }
    private sealed record Binding(McpRegistration Provider, int SettingsRevision);
    private sealed record ProfileProjection(string Id, int Revision, string WorkerId, JsonElement Capabilities, JsonElement AllowedTools);
    private sealed record ProfileCatalog(ProfileProjection Profile, IReadOnlyList<ToolDescriptor> Descriptors, JsonElement Snapshot, int CandidatesProbed);
    private sealed record NativeInspectionDiagnostic(string CurrentProcessExecutable, string CurrentProcessAccountSid,
        int CurrentProcessSession, int ConfiguredNativeSession, string InspectorState, string InspectorTransport, string InspectorMessage);

    // Use the admitted Source Worker and an existing saved profile. The helper never
    // enrolls its own executable, saves settings/grants, reads credentials or calls a model.
    // The Source Worker's normal activity journal may record these passive tools.call reads.
    public static async Task<int> RunAsync(string discoveryPath, string sourceRoot, string installationId)
    {
        var phase = "source-admission";
        var sourceAdmitted = false;
        var nativeAuthenticationConfirmed = false;
        NativeInspectionDiagnostic? inspectionDiagnostic = null;
        object? readReceiptDiagnostic = null;
        try
        {
            if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(sourceRoot) ||
                !Path.IsPathFullyQualified(discoveryPath) || !Guid.TryParseExact(installationId, "N", out var installation) || installation == Guid.Empty)
                throw new ProbeFailure("An explicit Windows Source root, discovery file and expected installation ID are required.");
            sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
            discoveryPath = Path.GetFullPath(discoveryPath);
            if (Path.GetFileName(sourceRoot) != "Source" ||
                !discoveryPath.Equals(Path.Combine(sourceRoot, "local-pipe.json"), StringComparison.OrdinalIgnoreCase))
                throw new ProbeFailure("Discovery must be the selected Source root's local-pipe.json; installed defaults are not used.");
            ScopedFilePolicy.RejectLinks(discoveryPath);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var discovery = new FileStream(discoveryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (discovery.Length is < 1 or > 16_384) throw new ProbeFailure("Source discovery exceeds its 16 KiB bound.");
            var discoveryBytes = new byte[checked((int)discovery.Length)];
            await discovery.ReadExactlyAsync(discoveryBytes, deadline.Token);
            using var document = JsonDocument.Parse(discoveryBytes, new() { MaxDepth = 8 });
            var declaration = document.RootElement;
            if (Text(declaration, "product") != LocalWorkerProtocol.Product || declaration.GetProperty("version").GetInt32() != LocalWorkerProtocol.Version)
                throw new ProbeFailure("Source discovery is not the supported Cerebral local-pipe contract.");
            var endpoint = CerebralJson.Read<LocalWorkerEndpoint>(declaration.GetProperty("endpoint"));
            endpoint.Validate();
            using var owner = new CerebralClient(endpoint);
            var health = await owner.GetAsync<HostInfo>("health", cancellationToken: deadline.Token);
            if (health.Product != LocalWorkerProtocol.Product || health.Protocol != LocalWorkerProtocol.Version || !health.Coordinator ||
                health.InstallationId != installationId || !Path.GetFullPath(health.DataDirectory).Equals(sourceRoot, StringComparison.OrdinalIgnoreCase))
                throw new ProbeFailure("The owner pipe did not confirm the selected Source installation and data root.");
            sourceAdmitted = true;

            phase = "native-binding";
            var binding = await ReadBindingAsync(owner, deadline.Token);
            var provider = binding.Provider;
            var native = provider.CortexNative!;
            native.Validate();
            var searchName = "mcp." + provider.Id + ".cortex_search";
            var readName = "mcp." + provider.Id + ".cortex_read";
            phase = "native-inspection";
            var caller = InstalledWorkerWindows.ReadCurrentProcessIdentity();
            inspectionDiagnostic = new(Bound(caller.Executable, 4096), Bound(caller.AccountSid, 184), caller.SessionId, native.SessionId,
                "not-returned", "not-returned", "The central inspector did not return a report.");
            var route = await owner.GetAsync<CortexConnectionReport>("cortex.connection.inspect", new { serverId = provider.Id }, cancellationToken: deadline.Token);
            inspectionDiagnostic = inspectionDiagnostic with { InspectorState = Bound(route.State, 64),
                InspectorTransport = Bound(route.Transport, 64), InspectorMessage = Bound(route.Message, 1000) };
            if (route.State != "ready" || route.Transport != "cortex-native-pipe" || route.McpIdentity is not null ||
                route.NativeIdentity is not { } identity || identity.Contract != CortexNativeProtocol.Contract)
                throw new ProbeFailure("The configured native binding did not confirm a ready authenticated Cortex identity; no alternate route was attempted.");
            nativeAuthenticationConfirmed = true;
            phase = "existing-profile-discovery";
            var catalog = await FindProfileAsync(owner, provider.Id, searchName, readName, deadline.Token);
            var profile = catalog.Profile;
            var descriptors = catalog.Descriptors;
            var searchBudget = Budget(descriptors.Single(value => value.Name == searchName).InputSchema, 4096);
            var readBudget = Budget(descriptors.Single(value => value.Name == readName).InputSchema, 64 * 1024);

            phase = "bounded-search";
            var searched = await owner.GetAsync<ToolResult>("tools.call", new { profileId = profile.Id, name = searchName,
                arguments = new { namespaces = ReadNamespaces, text = GuideId, limit = 4, maxUtf8Bytes = searchBudget, format = "compact" } }, cancellationToken: deadline.Token);
            var preview = Body(searched);
            if (!preview.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 4)
                throw new ProbeFailure("The native search did not return a bounded compact preview list.");
            var hits = items.EnumerateArray().Where(value => Text(value, "type") == "knowledge" && Text(value, "id") == GuideId &&
                ReadNamespaces.Contains(Text(value, "ns"), StringComparer.Ordinal)).ToArray();
            if (hits.Length != 1) throw new ProbeFailure("The repaired BoundedWork guide was not uniquely found in the two selected namespaces.");
            var space = Text(hits[0], "ns");
            long? previewRevision = hits[0].TryGetProperty("rev", out var previewRev) && previewRev.ValueKind == JsonValueKind.Number &&
                previewRev.TryGetInt64(out var previewNumber) && previewNumber > 0 ? previewNumber : null;

            phase = "exact-read";
            // Use only the current advertised schema. No speculative revision pin is added;
            // the local current validator checks the ORIGINAL Source result, including its
            // actual revision and all complete-body metadata, without regenerating that JSON.
            var readArguments = CerebralJson.Element(new { @namespace = space, recordType = "knowledge", recordId = GuideId, maxUtf8Bytes = readBudget });
            var read = await owner.GetAsync<ToolResult>("tools.call", new { profileId = profile.Id, name = readName, arguments = readArguments }, cancellationToken: deadline.Token);
            var payload = Body(read);
            ValidateOriginalRead(readArguments, read);
            if (read.Evidence?.Outcome != ToolExecutionOutcome.Succeeded || read.Evidence?.Partial == true ||
                Text(payload, "recordType") != "knowledge" || Text(payload, "recordId") != GuideId)
            {
                readReceiptDiagnostic = ReadReceiptMetadata(read, payload);
                throw new ProbeFailure("The exact-read dispatcher did not confirm a complete successful typed receipt.");
            }
            var record = payload.GetProperty("record");
            var content = record.GetProperty("content").GetString()!;
            var revision = payload.GetProperty("revision").GetInt64();
            if (Text(record, "namespace") != space || Text(record, "recordId") != GuideId || Text(record, "state") != "active" ||
                revision < 1 || record.GetProperty("revision").GetInt64() != revision)
                throw new ProbeFailure("The exact body did not confirm the selected active guide coordinate and actual revision.");
            var contentBytes = Encoding.UTF8.GetBytes(content);
            var contentHash = Convert.ToHexStringLower(SHA256.HashData(contentBytes));

            phase = "binding-recheck";
            var current = await ReadBindingAsync(owner, deadline.Token);
            if (!JsonElement.DeepEquals(CerebralJson.Element(provider), CerebralJson.Element(current.Provider)))
                throw new ProbeFailure("The Source native binding changed during acceptance; this read is not current binding evidence.");
            var currentProfile = (await ReadProfilesAsync(owner, deadline.Token)).SingleOrDefault(value => value.Id == profile.Id);
            if (currentProfile is null || !JsonElement.DeepEquals(CerebralJson.Element(profile), CerebralJson.Element(currentProfile)))
                throw new ProbeFailure("The selected existing profile's identity, revision or grants changed during acceptance.");
            var currentDescriptors = await ReadCatalogAsync(owner, profile.Id, deadline.Token);
            if (currentDescriptors is null || !JsonElement.DeepEquals(catalog.Snapshot, CatalogSnapshot(currentDescriptors)))
                throw new ProbeFailure("The selected existing profile's tool catalog changed during acceptance.");
            var currentRoute = await owner.GetAsync<CortexConnectionReport>("cortex.connection.inspect", new { serverId = provider.Id }, cancellationToken: deadline.Token);
            if (currentRoute.State != "ready" || currentRoute.Transport != "cortex-native-pipe" || currentRoute.McpIdentity is not null ||
                !JsonElement.DeepEquals(CerebralJson.Element(identity), CerebralJson.Element(currentRoute.NativeIdentity)))
                throw new ProbeFailure("The Source Worker's authenticated native Cortex identity changed during acceptance.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "cerebral.source-native-cortex-read/1", success = true,
                source = new { health.InstallationId, health.LocalRun, workerInstanceId = endpoint.InstanceId, settingsRevision = binding.SettingsRevision,
                    finalSettingsRevision = current.SettingsRevision },
                authenticatedCaller = nativeAuthenticationConfirmed ? "SourceWorker" : null, intendedCaller = "SourceWorker", sourceAdmitted,
                routeDescription = "Source owner command to existing native Cortex client", independentDirectTestProcess = false,
                profile = new { profile.Id, profile.Revision, profile.WorkerId, catalog.CandidatesProbed, catalogRechecked = true },
                route = new { serverId = provider.Id, route.Transport, identity.Contract, identity.Product, identity.Version,
                    identity.OwnerSid, identity.Session, identity.ClientId, identity.State, identity.StorageKind, identity.Network,
                    hostExecutable = native.HostExecutable, pipeName = native.PipeName ?? CortexNativeProtocol.PipeName(native.SessionId),
                    viaUserConnector = provider.ViaUserConnector, mcpIdentityPresent = false },
                search = new { namespaces = ReadNamespaces, maxUtf8Bytes = searchBudget, limit = 4, returnedHits = items.GetArrayLength() },
                read = new { @namespace = space, recordType = "knowledge", recordId = GuideId, previewRevision, revision,
                    utf8Bytes = contentBytes.Length, sha256 = contentHash, locallyValidatedComplete = true, maxUtf8Bytes = readBudget,
                    validator = "Local ToolDispatcher.CortexExactReadResults against original Source ToolResult JSON",
                    sourceSuccess = read.Success, sourceTruncated = read.Truncated,
                    sourceEvidenceOutcome = read.Evidence?.Outcome.ToString(), sourceEvidencePartial = read.Evidence?.Partial },
                modelCalls = 0, cortexRecordWrites = 0, liveSettingsWrites = 0, liveProfileWrites = 0,
                fullAccessRequested = false, sourceActivityJournalMayRecordCalls = true, credentialOutput = false
            }, CerebralJson.Options));
            return 0;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // Parser/provider errors can contain retained bodies or private settings. Emit
            // only a fixed phase/type/reason and the explicit bounded identity/sanitized
            // inspector projection, never raw exception messages or response bodies.
            Console.WriteLine(JsonSerializer.Serialize(new { schema = "cerebral.source-native-cortex-read/1", success = false, phase,
                errorCode = error.GetType().Name, reason = error is ProbeFailure failure ? failure.Reason
                    : "The bounded read-only acceptance was not confirmed; no alternate route, replay, grant or credential change was attempted.",
                nativeInspection = phase == "native-inspection" ? inspectionDiagnostic : null,
                readReceipt = readReceiptDiagnostic,
                authenticatedCaller = nativeAuthenticationConfirmed ? "SourceWorker" : null, intendedCaller = "SourceWorker", sourceAdmitted,
                routeDescription = "Source owner command to existing native Cortex client", independentDirectTestProcess = false,
                modelCalls = 0, cortexRecordWrites = 0, liveSettingsWrites = 0, liveProfileWrites = 0,
                fullAccessRequested = false, sourceActivityJournalMayRecordCalls = true, credentialOutput = false }, CerebralJson.Options));
            return 1;
        }
    }

    private static async Task<Binding> ReadBindingAsync(CerebralClient owner, CancellationToken token)
    {
        var response = await owner.CommandAsync("settings.get", cancellationToken: token);
        if (!response.Success) throw new ProbeFailure("The admitted Source owner settings read was denied; no settings body was emitted.");
        var settings = response.Data;
        var servers = settings.GetProperty("mcpServers");
        var spaces = settings.GetProperty("cortexNamespaces");
        if (servers.ValueKind != JsonValueKind.Array || servers.GetArrayLength() > 256 || spaces.ValueKind != JsonValueKind.Array || spaces.GetArrayLength() > 64 ||
            !ReadNamespaces.All(space => spaces.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == space)))
            throw new ProbeFailure("The Source provider catalog is oversized or does not permit both selected read namespaces.");
        var candidates = servers.EnumerateArray().Where(value => Flag(value, "enabled") &&
            value.TryGetProperty("cortexNative", out var native) && native.ValueKind == JsonValueKind.Object).ToArray();
        if (candidates.Length != 1) throw new ProbeFailure("The Source owner did not expose one unambiguous enabled native Cortex binding.");
        var selected = candidates[0];
        if (Flag(selected, "requiresConfiguration") || !string.IsNullOrEmpty(Text(selected, "url")) ||
            selected.GetProperty("arguments").GetArrayLength() != 0)
            throw new ProbeFailure("The selected Source native binding is unconfigured or contains an alternate endpoint.");
        return new(new McpRegistration
        {
            Id = Text(selected, "id"), Executable = Text(selected, "executable"), Enabled = true,
            ViaUserConnector = Flag(selected, "viaUserConnector"), CortexNativeRequired = Flag(selected, "cortexNativeRequired"),
            CortexNative = CerebralJson.Read<CortexNativeConnection>(selected.GetProperty("cortexNative"))
        }, settings.GetProperty("revision").GetInt32());
    }
    private static int Budget(JsonElement schema, int ceiling)
    {
        if (!schema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty("maxUtf8Bytes", out var rule) || Text(rule, "type") != "integer")
            throw new ProbeFailure("The live read schema does not advertise the supported maxUtf8Bytes argument.");
        var minimum = rule.TryGetProperty("minimum", out var min) ? min.GetInt32() : 512;
        var maximum = rule.TryGetProperty("maximum", out var max) ? max.GetInt32() : ceiling;
        var budget = Math.Min(maximum, ceiling);
        if (minimum < 1 || budget < Math.Max(512, minimum)) throw new ProbeFailure("The live response budget cannot support the bounded acceptance request.");
        return budget;
    }
    private static async Task<ProfileCatalog> FindProfileAsync(CerebralClient owner, string serverId, string searchName, string readName, CancellationToken token)
    {
        var candidates = (await ReadProfilesAsync(owner, token)).Where(value => value.WorkerId == "local" &&
            value.Capabilities.EnumerateArray().Any(capability => capability.ValueKind == JsonValueKind.String && capability.GetString() == "mcp." + serverId))
            .OrderBy(value => value.Id, StringComparer.Ordinal).Take(8).ToArray();
        var probed = 0;
        foreach (var candidate in candidates)
        {
            probed++;
            var descriptors = await ReadCatalogAsync(owner, candidate.Id, token);
            if (descriptors is null || descriptors.Count(value => value.Name == searchName) != 1 || descriptors.Count(value => value.Name == readName) != 1) continue;
            return new(candidate, descriptors, CatalogSnapshot(descriptors), probed);
        }
        throw new ProbeFailure("No existing local profile among the bounded candidate set advertises both native search/read tools; no grants were changed.");
    }
    private static async Task<IReadOnlyList<ProfileProjection>> ReadProfilesAsync(CerebralClient owner, CancellationToken token)
    {
        var response = await owner.CommandAsync("agents.list", cancellationToken: token);
        if (!response.Success || response.Data.ValueKind != JsonValueKind.Array || response.Data.GetArrayLength() > 256)
            throw new ProbeFailure("The Source owner profile inventory was denied, malformed or exceeds its bound; no profile bodies were emitted.");
        var profiles = new List<ProfileProjection>();
        foreach (var value in response.Data.EnumerateArray())
        {
            var id = Text(value, "id");
            var capabilities = value.GetProperty("capabilities"); var allowedTools = value.GetProperty("allowedTools");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl) ||
                capabilities.ValueKind != JsonValueKind.Array || capabilities.GetArrayLength() > 256 ||
                allowedTools.ValueKind != JsonValueKind.Array || allowedTools.GetArrayLength() > 4096)
                throw new ProbeFailure("An existing profile omitted a bounded identity/grant projection.");
            profiles.Add(new(id, value.GetProperty("revision").GetInt32(), Text(value, "workerId"), capabilities.Clone(), allowedTools.Clone()));
        }
        if (profiles.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Count)
            throw new ProbeFailure("The Source owner profile inventory contains ambiguous IDs.");
        return profiles;
    }
    private static async Task<IReadOnlyList<ToolDescriptor>?> ReadCatalogAsync(CerebralClient owner, string profileId, CancellationToken token)
    {
        var response = await owner.CommandAsync("tools.list", new { profileId }, cancellationToken: token);
        if (!response.Success) return null;
        if (response.Data.ValueKind != JsonValueKind.Array || response.Data.GetArrayLength() > 1024)
            throw new ProbeFailure("An existing profile's catalog is malformed or exceeds its bound.");
        var descriptors = CerebralJson.Read<List<ToolDescriptor>>(response.Data);
        if (descriptors.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != descriptors.Count)
            throw new ProbeFailure("An existing profile's catalog contains ambiguous tool names.");
        return descriptors;
    }
    private static JsonElement CatalogSnapshot(IReadOnlyList<ToolDescriptor> descriptors) =>
        CerebralJson.Element(descriptors.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray());
    private static void ValidateOriginalRead(JsonElement arguments, ToolResult original)
    {
        // Test-only fixed private seam. No SDK surface is widened and no local provider call
        // is made; invoke the production validator on the untouched owner result envelope.
        using var envelope = JsonDocument.Parse(original.Content, new() { MaxDepth = 32 });
        var validator = typeof(ToolDispatcher).GetMethod("ValidateExactCortexRead", BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, types: [typeof(JsonElement), typeof(JsonElement)], modifiers: null)
            ?? throw new ProbeFailure("The current local Tools assembly does not contain the exact-read admission validator.");
        validator.Invoke(null, [arguments, envelope.RootElement]);
    }
    private static object ReadReceiptMetadata(ToolResult original, JsonElement payload)
    {
        // Called only after the untouched envelope passed the local exact-read validator.
        // Keep nullable evidence and absent completeness flags distinct from false; omit
        // all provider diagnostic strings and retain only a hash/length of the full body.
        var record = payload.GetProperty("record");
        var body = Encoding.UTF8.GetBytes(record.GetProperty("content").GetString()!);
        var flags = new Dictionary<string, bool?>();
        foreach (var name in new[] { "complete", "truncated", "partial", "excerpt", "metadataOnly", "bodyOmitted", "bodyIncluded", "fullBodyIncluded" })
            flags[name] = payload.TryGetProperty(name, out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? flag.GetBoolean() : null;
        return new
        {
            original.Success, original.Truncated, locallyValidatedComplete = true,
            sourceEvidence = new { present = original.Evidence is not null, outcome = original.Evidence?.Outcome.ToString(), partial = original.Evidence?.Partial },
            contentType = "Object", contentTypeSource = "Parsed original ToolResult.Content JSON kind",
            topLevel = new { recordType = Text(payload, "recordType"), recordId = Text(payload, "recordId"),
                revision = payload.GetProperty("revision").GetInt64(), completeFlags = flags },
            record = new { @namespace = Text(record, "namespace"), recordId = Text(record, "recordId"),
                revision = record.GetProperty("revision").GetInt64(), state = Bound(Text(record, "state"), 64),
                bodyUtf8Bytes = body.Length, bodySha256 = Convert.ToHexStringLower(SHA256.HashData(body)) }
        };
    }
    private static JsonElement Body(ToolResult result)
    {
        if (!result.Success || result.Truncated || Encoding.UTF8.GetByteCount(result.Content) > 256 * 1024)
            throw new ProbeFailure("The native lookup failed or was incomplete; its raw body and diagnostic were not emitted.");
        using var document = JsonDocument.Parse(result.Content, new() { MaxDepth = 32 });
        return document.RootElement.GetProperty("structuredContent").Clone();
    }
    private static string Text(JsonElement value, string field) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
    private static bool Flag(JsonElement value, string field) => value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.True;
    private static string Bound(string value, int maximum) => value[..Math.Min(value.Length, maximum)];
}
