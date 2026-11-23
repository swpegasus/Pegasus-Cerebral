using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>
/// Actual ephemeral HTTP and same-user pipe paths through real dispatcher/store/configuration/runtime
/// composition, using synthetic task-owned settings/model metadata. No provider/model is launched,
/// no installed transport or service enrollment is authenticated, and no remote owner is contacted.
/// </summary>
public static class OwnerModelSnapshotPathTests
{
    private const string WorkerId = "123456781234123412341234567890ab";
    private const string AssignedControllerId = "223456781234123412341234567890ab";
    private const string OtherId = "323456781234123412341234567890ab";
    private const string ModelId = "snapshot-model";
    private static readonly JsonSerializerOptions CompactJson = new(CerebralJson.Options) { WriteIndented = false };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Task<int> RunAsync(string fixtureRoot) => RunCoreAsync(fixtureRoot, minimal: false);

    /// <summary>First supported-path checkpoint: genuine successful HTTP and pipe calls plus complete state/counter comparisons.</summary>
    public static Task<int> RunMinimalAsync(string fixtureRoot) => RunCoreAsync(fixtureRoot, minimal: true);

    private static async Task<int> RunCoreAsync(string fixtureRoot, bool minimal)
    {
        var parent = RequireFixtureParent(fixtureRoot);
        var root = Path.Combine(parent, "owner-snapshot-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            checks++;
        }
        try
        {
            await VerifySourcePathsAsync(Path.Combine(root, "source"), Check, minimal);
            if (!minimal)
            {
                await VerifyStandaloneHttpAsync(Path.Combine(root, "standalone"), Check);
                await VerifyUnavailableConfigurationAsync(Path.Combine(root, "missing"), "missing", Check);
                await VerifyUnavailableConfigurationAsync(Path.Combine(root, "legacy"), "legacy", Check);
            }
            Console.WriteLine($"Owner snapshot paths: {checks} actual ephemeral HTTP/same-user pipe and SQLite assertions passed with synthetic settings/model metadata. Supported-path no-normalization/no-write assertions are distinct from reported legacy middleware effects. No provider/model launch, real remote owner, installed enrollment, LAN request or deployment was exercised.");
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("owner-snapshot-path-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact task-owned path fixture.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    /// <summary>Exact-route purity regression: aliases must stop before legacy configuration or owner admission.</summary>
    public static async Task<int> RunAliasesAsync(string fixtureRoot)
    {
        var parent = RequireFixtureParent(fixtureRoot);
        var root = Path.Combine(parent, "owner-snapshot-aliases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            checks++;
        }
        try
        {
            foreach (var seed in new[] { "source", "missing", "legacy" })
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await using var host = new Fixture(Path.Combine(root, seed), seed);
                await host.StartAsync(pipe: false, deadline.Token);
                foreach (var path in new[]
                {
                    "/API/COMMAND/MODELS.BINDING.SNAPSHOT",
                    OwnerModelSnapshotService.HttpPath + "/",
                    "/API/Command/Models.Binding.Snapshot/",
                    OwnerModelSnapshotService.HttpPath + "///",
                    "/API/COMMAND/MODELS.BINDING.SNAPSHOT///"
                })
                {
                    var before = host.Capture();
                    var admissions = host.Commands.OwnerSnapshotVerifiedAdmissions;
                    var result = await host.HttpAsync(path, Envelope(Request(host.ControllerId)), host.OwnerKey, deadline.Token);
                    Check(result.Status == HttpStatusCode.BadRequest && result.Result?.Success == false &&
                        result.Result.Error == "snapshot_route_noncanonical",
                        seed + " noncanonical route must return the exact early400 classification, not fall through to generic/legacy dispatch.");
                    host.VerifyNoEffects(before, expectModelRead: false, Check, seed + " route-alias denial");
                    Check(host.Commands.OwnerSnapshotVerifiedAdmissions == admissions,
                        seed + " route-alias refusal must never issue verified owner admission.");
                }
            }
            Console.WriteLine($"Owner snapshot route aliases: {checks} actual ephemeral Kestrel/SQLite assertions passed for canonical case/trailing-slash aliases under synthetic valid, missing and legacy owner state. No owner admission, model reads, provider launch, installed service or remote owner was exercised.");
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("owner-snapshot-aliases-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact task-owned alias fixture.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    /// <summary>Separate catalog checkpoint after minimal transports pass; all retained inputs are synthetic fixture rows.</summary>
    public static async Task<int> RunCatalogAsync(string fixtureRoot)
    {
        var parent = RequireFixtureParent(fixtureRoot);
        var root = Path.Combine(parent, "owner-snapshot-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            checks++;
        }
        try
        {
            await VerifyCatalogSelectionAndMalformedAsync(Path.Combine(root, "catalog"), Check);
            await VerifyCatalogBoundsAsync(Path.Combine(root, "bounds"), Check);
            await VerifyFingerprintOnlyAsync(Path.Combine(root, "fingerprint"), Check);
            Console.WriteLine($"Owner snapshot catalogs: {checks} actual HTTP/same-user pipe and SQLite assertions passed with synthetic retained registrations, independent fingerprint constants and zero-effect comparisons. No provider/model launch, installed enrollment, remote owner or deployment was exercised.");
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("owner-snapshot-catalog-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact task-owned catalog fixture.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private static async Task VerifyCatalogSelectionAndMalformedAsync(string root, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var host = new Fixture(root, "source");
        await host.StartAsync(pipe: true, deadline.Token);
        var selected = Request(host.ControllerId);
        var original = ReadModelBody(host, ModelId);
        var compact = JsonNode.Parse(original)!.ToJsonString(CompactJson);
        var baseline = await CatalogBothAsync(host, selected, check, deadline.Token, (data, transport) =>
            VerifySelectedResponse(CommandResult.Ok(data), host.ControllerId, "source_local", check), "complete alias/external catalog");

        var inventoryArgs = JsonNode.Parse(selected.Arguments.GetRawText())!.AsObject();
        inventoryArgs.Remove("modelId");
        await CatalogBothAsync(host, selected with { Arguments = CerebralJson.Element(inventoryArgs) }, check, deadline.Token,
            (data, transport) =>
            {
                check(data.GetProperty("catalog").GetProperty("complete").GetBoolean() &&
                    data.GetProperty("registration").ValueKind == JsonValueKind.Null &&
                    !data.GetProperty("admission").GetBoolean(),
                    transport + " inventory without modelId must report complete IDs without inventing a selected binding or admission.");
            }, "inventory selection absence");
        var missingArgs = JsonNode.Parse(selected.Arguments.GetRawText())!.AsObject();
        missingArgs["modelId"] = "snapshot-missing";
        await CatalogBothAsync(host, selected with { Arguments = CerebralJson.Element(missingArgs) }, check, deadline.Token,
            (data, transport) =>
            {
                var registration = data.GetProperty("registration");
                var binding = registration.GetProperty("binding");
                check(data.GetProperty("catalog").GetProperty("complete").GetBoolean() &&
                    registration.GetProperty("id").GetString() == "snapshot-missing" &&
                    registration.GetProperty("state").GetString() == "not_found" &&
                    binding.GetProperty("bindingMode").GetString() == "unavailable" &&
                    !binding.GetProperty("rawBindingAvailable").GetBoolean() &&
                    binding.GetProperty("rawBinding").ValueKind == JsonValueKind.Null &&
                    !data.GetProperty("admission").GetBoolean(),
                    transport + " complete catalog must distinguish a genuinely absent selection from an invalid observed registration.");
            }, "complete catalog not-found selection");

        var explicitSchema = ChangeModel(compact, value => value["schemaVersion"] = 1);
        host.SetRaw("model", ModelId, explicitSchema);
        await CatalogBothAsync(host, selected, check, deadline.Token, (data, transport) =>
        {
            check(data.GetProperty("catalog").GetProperty("complete").GetBoolean() &&
                data.GetProperty("registration").GetProperty("state").GetString() == "unloaded",
                transport + " declared explicit persisted projection schemaVersion1 must remain supported.");
        }, "declared persisted schema");
        host.SetRaw("model", ModelId, original);

        var malformed = new (string Label, object Body, bool CastBytesAsText)[]
        {
            ("malformed JSON body", "{broken", false),
            ("null JSON body", "null", false),
            ("body-coordinate mismatch", ChangeModel(compact, value => value["id"] = "snapshot-foreign"), false),
            ("absent required path", ChangeModel(compact, value => value.Remove("path")), false),
            ("absent nullable template field", ChangeModel(compact, value => value.Remove("chatTemplate")), false),
            ("case-alias path field", ChangeModel(compact, value => value["Path"] = "synthetic-case-alias"), false),
            ("unsupported stored schema", ChangeModel(compact, value => value["schemaVersion"] = 999), false),
            ("null required path", ChangeModel(compact, value => value["path"] = null), false),
            ("null stop array", ChangeModel(compact, value => value["stopSequences"] = null), false),
            ("unpaired escaped template", compact.Replace("\"chatTemplate\":null", "\"chatTemplate\":\"\\uD800\"", StringComparison.Ordinal), false),
            ("duplicate exact body field", "{\"id\":\"snapshot-model\"," + compact[1..], false),
            ("wrong SQLite body storage type", StrictUtf8.GetBytes("{}"), false),
            ("invalid original stored UTF8", new byte[] { 0xc3, 0x28 }, true)
        };
        foreach (var value in malformed)
        {
            host.SetRaw("model", ModelId, value.Body, value.CastBytesAsText);
            await CatalogBothAsync(host, selected, check, deadline.Token, (data, transport) =>
            {
                var catalog = data.GetProperty("catalog");
                var registration = data.GetProperty("registration");
                var binding = registration.GetProperty("binding");
                check(!catalog.GetProperty("complete").GetBoolean() &&
                    catalog.GetProperty("fence").ValueKind == JsonValueKind.Null &&
                    catalog.GetProperty("diagnostics").GetArrayLength() > 0 &&
                    !catalog.GetProperty("ids").EnumerateArray().Any(id => id.GetString() == ModelId),
                    transport + " " + value.Label + " must make the full catalog incomplete without a complete fence or invented valid ID.");
                check(registration.GetProperty("id").GetString() == ModelId &&
                    registration.GetProperty("state").GetString() == "invalid" &&
                    registration.GetProperty("registrationFence").ValueKind == JsonValueKind.Null &&
                    binding.GetProperty("bindingMode").GetString() == "unavailable" &&
                    !binding.GetProperty("rawBindingAvailable").GetBoolean() &&
                    binding.GetProperty("rawBinding").ValueKind == JsonValueKind.Null &&
                    !data.GetProperty("admission").GetBoolean(),
                    transport + " " + value.Label + " must preserve observed invalid selection identity and never relabel it not_found.");
            }, value.Label);
            host.SetRaw("model", ModelId, original);
        }

        // Reformat the same complete retained JSON value. The semantic binding stays equal, while
        // catalog/registration fences conservatively follow the complete original stored body bytes.
        host.SetRaw("model", ModelId, compact);
        var reformatted = await CatalogBothAsync(host, selected, check, deadline.Token, (data, transport) =>
        {
            check(data.GetProperty("catalog").GetProperty("complete").GetBoolean() &&
                data.GetProperty("registration").GetProperty("binding").GetProperty("sha256").GetString() ==
                    baseline.GetProperty("registration").GetProperty("binding").GetProperty("sha256").GetString() &&
                data.GetProperty("catalog").GetProperty("fence").GetString() != baseline.GetProperty("catalog").GetProperty("fence").GetString() &&
                data.GetProperty("registration").GetProperty("registrationFence").GetString() !=
                    baseline.GetProperty("registration").GetProperty("registrationFence").GetString(),
                transport + " raw body reformat must change conservative fences while preserving semantic binding fingerprint.");
        }, "raw-body reformat");
        check(reformatted.GetProperty("registration").GetProperty("binding").GetProperty("rawBinding").GetProperty("path").GetString() ==
            baseline.GetProperty("registration").GetProperty("binding").GetProperty("rawBinding").GetProperty("path").GetString(),
            "Raw-body fence changes must not normalize or replace the configured remote path.");
    }

    private static async Task VerifyCatalogBoundsAsync(string root, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var host = new Fixture(root, "source");
        await host.StartAsync(pipe: true, deadline.Token);
        var selected = Request(host.ControllerId);
        var extraIds = Enumerable.Range(0, 254).Select(index => "overflow-" + index.ToString("D3", CultureInfo.InvariantCulture)).ToArray();
        foreach (var id in extraIds) host.Store.Put("model", id, new ModelRecord
        {
            Id = id, Name = "Synthetic bounded row", Path = "/synthetic/" + id + ".gguf",
            State = "unloaded", MaximumContextTokens = 4096
        });
        await CatalogBothAsync(host, selected, check, deadline.Token, (data, transport) =>
        {
            var catalog = data.GetProperty("catalog");
            check(!catalog.GetProperty("complete").GetBoolean() && catalog.GetProperty("fence").ValueKind == JsonValueKind.Null &&
                catalog.GetProperty("ids").GetArrayLength() <= 256 &&
                catalog.GetProperty("diagnostics").EnumerateArray().Any(value => value.GetString() == "catalog_row_bound") &&
                data.GetProperty("registration").GetProperty("state").GetString() != "not_found" &&
                !data.GetProperty("admission").GetBoolean(),
                transport + " 257th-row detector must refuse completeness/fence/admission without claiming an existing selection absent.");
        }, "257-row detector", expectedRowReads: 257);
        foreach (var id in extraIds) host.Store.Delete("model", id);

        var alias = ReadModelBody(host, "snapshot-alias");
        var external = ReadModelBody(host, "snapshot-external");
        var paddedAlias = ChangeModel(alias, value => value["description"] = new string('a', 4300000));
        var paddedExternal = ChangeModel(external, value => value["description"] = new string('b', 4300000));
        host.SetRaw("model", "snapshot-alias", paddedAlias);
        host.SetRaw("model", "snapshot-external", paddedExternal);
        await CatalogBothAsync(host, selected, check, deadline.Token, (data, transport) =>
        {
            var catalog = data.GetProperty("catalog");
            check(!catalog.GetProperty("complete").GetBoolean() && catalog.GetProperty("fence").ValueKind == JsonValueKind.Null &&
                catalog.GetProperty("diagnostics").EnumerateArray().Any(value => value.GetString() == "catalog_byte_bound") &&
                data.GetProperty("registration").GetProperty("state").GetString() == "unobserved" &&
                data.GetProperty("registration").GetProperty("binding").GetProperty("bindingMode").GetString() == "unavailable" &&
                !data.GetProperty("admission").GetBoolean(),
                transport + " aggregate8MiB bound must leave the unseen selected coordinate unobserved and prevent any complete fence/admission.");
        }, "aggregate retained-body bound", expectedRowReads: 2);
    }

    private static async Task VerifyFingerprintOnlyAsync(string root, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var host = new Fixture(root, "source");
        await host.StartAsync(pipe: true, deadline.Token);
        host.Store.Put("model", ModelId, new ModelRecord
        {
            Id = ModelId, Name = "Synthetic full large binding", Path = "/models/a.gguf", State = "unloaded",
            ChatTemplate = new string('t', 65536), StopSequences = Enumerable.Repeat(new string('s', 4096), 128).ToList(),
            MaximumContextTokens = 4096, ParallelRequests = 1, GpuLayers = -1, VisionProjectorPath = null
        });
        // Fixed independently using ECMAScript JSON/Node SHA-256, without invoking production codec:
        // snapshot-model,/models/a.gguf,65536 t,128 ordered4096 s,4096,1,-1,null.
        const string completeFingerprint = "bd69f2af37c5698dc14c607ba06320e74c29787f9e856e161307799c28ae8520";
        await CatalogBothAsync(host, Request(host.ControllerId), check, deadline.Token, (data, transport) =>
        {
            var catalog = data.GetProperty("catalog");
            var registration = data.GetProperty("registration");
            var binding = registration.GetProperty("binding");
            check(catalog.GetProperty("complete").GetBoolean() && Digest(catalog.GetProperty("fence").GetString()) &&
                Digest(registration.GetProperty("registrationFence").GetString()) &&
                registration.GetProperty("state").GetString() == "unloaded" &&
                binding.GetProperty("bindingMode").GetString() == "fingerprint_only" &&
                !binding.GetProperty("rawBindingAvailable").GetBoolean() &&
                binding.GetProperty("rawBinding").ValueKind == JsonValueKind.Null &&
                binding.GetProperty("sha256").GetString() == completeFingerprint &&
                binding.GetProperty("canonicalJsonUtf8Bytes").GetInt32() == 590382 &&
                binding.GetProperty("fingerprintPreimageBytes").GetInt32() == 590420 &&
                !data.GetProperty("admission").GetBoolean(),
                transport + " full valid >256KiB binding must retain its complete independent hash/counts in fingerprint_only mode without a raw DTO or generation admission.");
            check(data.GetProperty("runtime").GetProperty("instanceState").GetString() == "absent" &&
                data.GetProperty("runtime").GetProperty("launchBinding").ValueKind == JsonValueKind.Null &&
                data.GetProperty("runtime").GetProperty("slots").GetProperty("state").GetString() == "unobserved",
                transport + " fingerprint-only persisted inspection must not invent loaded launch/slot evidence.");
        }, "complete fingerprint-only binding");
    }

    private static async Task<JsonElement> CatalogBothAsync(Fixture host, CommandRequest request,
        Action<bool, string> check, CancellationToken token, Action<JsonElement, string> inspect, string label,
        int? expectedRowReads = null)
    {
        JsonElement observed = default;
        foreach (var transport in new[] { "HTTP", "pipe" })
        {
            var before = host.Capture();
            CommandResult result;
            if (transport == "HTTP")
            {
                var response = await host.HttpAsync(OwnerModelSnapshotService.HttpPath, Envelope(request), host.OwnerKey, token);
                check(response.Status == HttpStatusCode.OK && response.Result?.Success == true,
                    label + " actual authenticated HTTP observation must return its explicit complete/incomplete inspection result.");
                result = response.Result!;
            }
            else
            {
                using var client = new CerebralClient(host.PipeEndpoint!);
                result = await client.CommandAsync(request.Operation, request.Arguments, cancellationToken: token);
                check(result.Success, label + " actual same-user pipe observation must return its explicit complete/incomplete result.");
            }
            inspect(result.Data, transport);
            host.VerifyNoEffects(before, expectModelRead: true, check, label + " " + transport);
            var after = host.Capture();
            if (expectedRowReads is { } count)
                check(after.ModelRowsRead - before.ModelRowsRead == count,
                    label + " " + transport + " must visit exactly the bounded detector/remaining-budget coordinates.");
            if (observed.ValueKind == JsonValueKind.Undefined) observed = result.Data.Clone();
            else
            {
                check(JsonElement.DeepEquals(result.Data.GetProperty("catalog"), observed.GetProperty("catalog")) &&
                    JsonElement.DeepEquals(result.Data.GetProperty("registration"), observed.GetProperty("registration")),
                    label + " genuine HTTP and pipe paths must agree on the same retained catalog/registration observation.");
            }
        }
        return observed;
    }

    private static string ChangeModel(string original, Action<JsonObject> change)
    {
        var model = JsonNode.Parse(original)!.AsObject();
        change(model);
        return model.ToJsonString(CompactJson);
    }

    private static string ReadModelBody(Fixture host, string id)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(host.Store.DirectoryPath, "cerebral.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM records WHERE kind='model' AND id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string ?? throw new InvalidOperationException("Synthetic model body coordinate was not found.");
    }

    private static async Task VerifySourcePathsAsync(string root, Action<bool, string> check, bool minimal)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var host = new Fixture(root, "source");
        await host.StartAsync(pipe: true, deadline.Token);
        check(host.Address is not null && host.PipeEndpoint is not null,
            "The source fixture must start actual ephemeral Kestrel and CurrentUserOnly pipe transports.");

        var request = Request(host.ControllerId);
        var before = host.Capture();
        var http = await host.HttpAsync(OwnerModelSnapshotService.HttpPath, Envelope(request), host.OwnerKey, deadline.Token);
        check(http.Status == HttpStatusCode.OK && http.Result?.Success == true, "Authenticated loopback owner HTTP snapshot must succeed through the actual route.");
        VerifySelectedResponse(http.Result!, host.ControllerId, "source_local", check);
        host.VerifyNoEffects(before, expectModelRead: true, check, "successful source owner HTTP path");

        before = host.Capture();
        var noProfile = JsonNode.Parse(Envelope(request))!.AsObject();
        noProfile.Remove("profileId");
        var optionalProfile = await host.HttpAsync(OwnerModelSnapshotService.HttpPath, StrictUtf8.GetBytes(noProfile.ToJsonString()),
            host.OwnerKey, deadline.Token);
        check(optionalProfile.Status == HttpStatusCode.OK && optionalProfile.Result?.Success == true,
            "The frozen nullable optional profileId may be absent without changing the original owner envelope contract.");
        host.VerifyNoEffects(before, expectModelRead: true, check, "absent optional profileId");

        before = host.Capture();
        using (var client = new CerebralClient(host.PipeEndpoint!))
        {
            var result = await client.CommandAsync(request.Operation, request.Arguments, cancellationToken: deadline.Token);
            check(result.Success, "Actual same-user pipe admission must reach the supported snapshot path.");
            VerifySelectedResponse(result, host.ControllerId, "source_local", check);
        }
        host.VerifyNoEffects(before, expectModelRead: true, check, "successful same-user pipe path");
        if (minimal) return;

        foreach (var credential in new string?[] { null, new string('x', 48) })
        {
            before = host.Capture();
            var denied = await host.HttpAsync(OwnerModelSnapshotService.HttpPath, Envelope(request), credential, deadline.Token);
            check(denied.Status == HttpStatusCode.Unauthorized, "Missing/wrong owner key must be rejected even on actual loopback HTTP.");
            host.VerifyNoEffects(before, expectModelRead: false, check, "pre-auth loopback credential denial");
        }
        before = host.Capture();
        var origin = await host.HttpAsync(OwnerModelSnapshotService.HttpPath, Envelope(request), host.OwnerKey,
            deadline.Token, origin: "https://synthetic-origin.invalid");
        check(origin.Status == HttpStatusCode.Forbidden, "A browser Origin must remain forbidden before owner/model observation.");
        host.VerifyNoEffects(before, expectModelRead: false, check, "Origin denial");

        foreach (var deniedRequest in new[]
        {
            Request(host.ControllerId, workerId: OtherId),
            Request(OtherId),
            request with { ProfileId = "snapshot-admin" }
        })
        {
            before = host.Capture();
            var outcome = await host.HttpAsync(OwnerModelSnapshotService.HttpPath, Envelope(deniedRequest), host.OwnerKey, deadline.Token);
            check(outcome.Denied, "Wrong owner/controller identity and actor-bearing requests must be denied.");
            host.VerifyNoEffects(before, expectModelRead: false, check, "identity/actor HTTP denial");
            before = host.Capture();
            using var client = new CerebralClient(host.PipeEndpoint!);
            var pipeResult = await client.CommandAsync(deniedRequest.Operation, deniedRequest.Arguments,
                deniedRequest.ProfileId, deadline.Token);
            check(!pipeResult.Success, "The actual pipe path must enforce the same owner/controller and actor denial.");
            host.VerifyNoEffects(before, expectModelRead: false, check, "identity/actor pipe denial");
        }

        var envelope = StrictUtf8.GetString(Envelope(request));
        var strictInputs = new (string Label, byte[] Bytes)[]
        {
            ("duplicate envelope field", StrictUtf8.GetBytes(envelope.Replace("\"operation\":", "\"operation\":\"models.binding.snapshot\",\"operation\":", StringComparison.Ordinal))),
            ("case alias envelope field", StrictUtf8.GetBytes(envelope.Replace("\"operation\":", "\"Operation\":", StringComparison.Ordinal))),
            ("unknown envelope field", StrictUtf8.GetBytes(envelope[..^1] + ",\"unexpected\":true}")),
            ("duplicate argument", StrictUtf8.GetBytes(envelope.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal))),
            ("case alias argument", StrictUtf8.GetBytes(envelope.Replace("\"schemaVersion\":", "\"SchemaVersion\":", StringComparison.Ordinal))),
            ("unknown argument", StrictUtf8.GetBytes(envelope.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"unexpected\":true", StringComparison.Ordinal))),
            ("unsupported schema", StrictUtf8.GetBytes(envelope.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal))),
            ("escaped unpaired surrogate", StrictUtf8.GetBytes(envelope.Replace("\"modelId\":\"snapshot-model\"", "\"modelId\":\"\\uD800\"", StringComparison.Ordinal))),
            ("invalid original UTF8", [0x7b, 0x22, 0xc3, 0x28, 0x22, 0x3a, 0x31, 0x7d]),
            ("oversized original envelope", StrictUtf8.GetBytes(envelope + new string(' ', 5000)))
        };
        foreach (var input in strictInputs)
        {
            before = host.Capture();
            var denied = await host.HttpAsync(OwnerModelSnapshotService.HttpPath, input.Bytes, host.OwnerKey, deadline.Token);
            check(denied.Denied, input.Label + " must be rejected on the actual original-body HTTP path.");
            host.VerifyNoEffects(before, expectModelRead: false, check, input.Label + " HTTP denial");
        }

        // Raw framing preserves the original invalid/oversized bytes, rather than using a client
        // serializer that would reject or repair them before the real pipe server sees them.
        foreach (var commandBytes in new[]
        {
            strictInputs[0].Bytes,
            strictInputs[7].Bytes,
            strictInputs[9].Bytes
        })
        {
            before = host.Capture();
            var denied = await host.RawPipeAsync(commandBytes, deadline.Token);
            check(denied, "Malformed or oversized original pipe command must fail or close before model reads/normalization.");
            host.VerifyNoEffects(before, expectModelRead: false, check, "strict original-frame pipe denial");
        }
        before = host.Capture();
        var direct = await host.Commands.ExecuteAsync(request, deadline.Token);
        check(!direct.Success, "A direct generic dispatcher call must not manufacture verified owner transport proof.");
        host.VerifyNoEffects(before, expectModelRead: false, check, "missing verified owner context");

        // Verify existing route boundaries separately. These deliberately traverse legacy
        // Configuration.Get and can normalize the seeded migration bait; no zero-effects claim follows.
        await VerifyLegacyRouteBoundariesAsync(host, request, check, deadline.Token);
    }

    private static async Task VerifyStandaloneHttpAsync(string root, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var host = new Fixture(root, "standalone");
        await host.StartAsync(pipe: false, deadline.Token);
        var before = host.Capture();
        var success = await host.HttpAsync(OwnerModelSnapshotService.HttpPath, Envelope(Request(host.ControllerId)),
            host.OwnerKey, deadline.Token);
        check(success.Status == HttpStatusCode.OK && success.Result?.Success == true,
            "Actual owner HTTP path must admit the synthetic assigned standalone runtime/settings role.");
        VerifySelectedResponse(success.Result!, host.ControllerId, "assigned_standalone", check);
        host.VerifyNoEffects(before, expectModelRead: true, check, "assigned standalone HTTP success");
        before = host.Capture();
        var wrongController = await host.HttpAsync(OwnerModelSnapshotService.HttpPath, Envelope(Request(OtherId)),
            host.OwnerKey, deadline.Token);
        check(wrongController.Denied, "Assigned standalone controller mismatch must be rejected before catalog observation.");
        host.VerifyNoEffects(before, expectModelRead: false, check, "assigned standalone controller denial");
    }

    private static async Task VerifyUnavailableConfigurationAsync(string root, string seed, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var host = new Fixture(root, seed);
        await host.StartAsync(pipe: true, deadline.Token);
        var request = Request(host.ControllerId);
        var before = host.Capture();
        var response = await host.HttpAsync(OwnerModelSnapshotService.HttpPath, Envelope(request), host.OwnerKey, deadline.Token);
        check(response.Denied, seed + " owner configuration must be unavailable without initialization/repair.");
        host.VerifyNoEffects(before, expectModelRead: false, check, seed + " HTTP configuration denial");
        before = host.Capture();
        using var client = new CerebralClient(host.PipeEndpoint!);
        var result = await client.CommandAsync(request.Operation, request.Arguments, cancellationToken: deadline.Token);
        check(!result.Success, seed + " pipe owner state must fail before normalization or model reads.");
        host.VerifyNoEffects(before, expectModelRead: false, check, seed + " pipe configuration denial");
    }

    private static async Task VerifyLegacyRouteBoundariesAsync(Fixture host, CommandRequest request,
        Action<bool, string> check, CancellationToken token)
    {
        var before = host.Capture();
        var generic = await host.HttpAsync("/api/command", Envelope(request), host.OwnerKey, token);
        var after = host.Capture();
        check(generic.Result?.Success == false && after.ModelRowsRead == before.ModelRowsRead &&
            after.GetCalls > before.GetCalls && after.LegacyRequests > before.LegacyRequests,
            "Generic HTTP command route must deny snapshot owner proof; its actual legacy Get effects remain separate.");
        Console.WriteLine($"Legacy generic route fixture: Configuration.Get delta={after.GetCalls - before.GetCalls}; SQLite logical state changed={after.LogicalRowsDigest != before.LogicalRowsDigest}; model-row reads delta={after.ModelRowsRead - before.ModelRowsRead}. These are legacy-path effects, not supported snapshot zero-effect evidence.");

        var grant = host.Peers.Create(CerebralJson.Element(new
        {
            name = "Synthetic snapshot boundary peer", profileIds = new[] { "snapshot-admin" }, maxConcurrentJobs = 1
        }));
        before = host.Capture();
        var peer = await host.HttpAsync("/api/peer/command", Envelope(request), null, token, peerToken: grant.Token);
        after = host.Capture();
        check(peer.Result?.Success == false && after.ModelRowsRead == before.ModelRowsRead &&
            after.GetCalls > before.GetCalls,
            "A genuinely authenticated fixture peer route must not reach the owner snapshot handler.");
        before = host.Capture();
        var external = await host.HttpAsync("/api/external/command", Envelope(request with { ProfileId = "snapshot-admin" }),
            host.OwnerKey, token);
        after = host.Capture();
        check(external.Result?.Success == false && after.ModelRowsRead == before.ModelRowsRead &&
            after.GetCalls > before.GetCalls,
            "Enabled external route with owner key and saved admin profile must not create snapshot owner proof.");
    }

    private static void VerifySelectedResponse(CommandResult result, string controllerId, string role, Action<bool, string> check)
    {
        var data = result.Data;
        check(data.GetProperty("schemaVersion").GetInt32() == 1 &&
            data.GetProperty("operation").GetString() == OwnerModelSnapshotService.Operation &&
            data.GetProperty("httpRoute").GetString() == OwnerModelSnapshotService.HttpPath &&
            data.GetProperty("workerInstallationId").GetString() == WorkerId &&
            data.GetProperty("controllerInstallationId").GetString() == controllerId &&
            data.GetProperty("role").GetString() == role && !data.GetProperty("admission").GetBoolean(),
            "Snapshot must report exact owner/route/role comparisons and never grant generation admission.");
        var catalog = data.GetProperty("catalog");
        check(catalog.GetProperty("complete").GetBoolean() &&
            catalog.GetProperty("ids").EnumerateArray().Select(value => value.GetString())
                .Order(StringComparer.Ordinal).SequenceEqual(new[] { ModelId, "snapshot-alias", "snapshot-external" }.Order(StringComparer.Ordinal)) &&
            Digest(catalog.GetProperty("fence").GetString()),
            "Complete catalog must preserve separately registered same-path aliases/external metadata and a complete fence.");
        var registration = data.GetProperty("registration");
        var binding = registration.GetProperty("binding");
        check(registration.GetProperty("id").GetString() == ModelId &&
            registration.GetProperty("state").GetString() == "unloaded" &&
            Digest(registration.GetProperty("registrationFence").GetString()) &&
            binding.GetProperty("bindingMode").GetString() == "full" && binding.GetProperty("rawBindingAvailable").GetBoolean() &&
            binding.GetProperty("rawBinding").GetProperty("id").GetString() == ModelId &&
            Digest(binding.GetProperty("sha256").GetString()) &&
            binding.GetProperty("fingerprintPreimageBytes").GetInt32() == binding.GetProperty("canonicalJsonUtf8Bytes").GetInt32() + 38,
            "Selected persisted binding must be complete, exact and separately fenced/count-qualified.");
        var runtime = data.GetProperty("runtime");
        check(runtime.GetProperty("instanceState").GetString() == "absent" &&
            runtime.GetProperty("instanceId").ValueKind == JsonValueKind.Null &&
            runtime.GetProperty("launchBinding").ValueKind == JsonValueKind.Null &&
            runtime.GetProperty("launchBindingState").GetString() == "unavailable" &&
            runtime.GetProperty("provider").ValueKind == JsonValueKind.Null &&
            runtime.GetProperty("effectiveContext").ValueKind == JsonValueKind.Null &&
            runtime.GetProperty("configuredParallelRequests").ValueKind == JsonValueKind.Null,
            "Absent actual runtime instance must leave launch/provider/context facts unobserved rather than inferred from registration.");
        var slots = runtime.GetProperty("slots");
        check(slots.GetProperty("state").GetString() == "unobserved" &&
            new[] { "busyCount", "availableCount", "quarantinedCount" }.All(name => slots.GetProperty(name).ValueKind == JsonValueKind.Null) &&
            runtime.GetProperty("capabilities").GetProperty("state").GetString() == "unobserved" &&
            runtime.GetProperty("capabilities").GetProperty("entries").GetArrayLength() == 0 &&
            runtime.GetProperty("componentPins").GetProperty("state").GetString() == "unobserved",
            "Unobserved readiness/count/component facets must not appear as idle, zero or ready.");
    }

    private static bool Digest(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static CommandRequest Request(string controllerId, string workerId = WorkerId) => new(
        OwnerModelSnapshotService.Operation, CerebralJson.Element(new
        {
            schemaVersion = 1, expectedWorkerInstallationId = workerId,
            expectedControllerInstallationId = controllerId, modelId = ModelId
        }));

    private static byte[] Envelope(CommandRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, CompactJson);

    private static string RequireFixtureParent(string value)
    {
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("Path fixtures require an explicit absolute task-owned root.");
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        var allowed = Path.GetFullPath(@"C:\Development\Pegasus-Cerebral\.tmp\core-10000000-0000-4000-8000-000000000001\snapshot-candidate");
        if (!parent.Equals(allowed, StringComparison.OrdinalIgnoreCase) &&
            !parent.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path fixtures must remain beneath this task's snapshot-candidate directory.");
        for (var directory = new DirectoryInfo(parent); directory is not null && directory.FullName.Length >= allowed.Length; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Path fixtures refuse reparse-point roots.");
        Directory.CreateDirectory(parent);
        return parent;
    }

    internal sealed class HttpOutcome(HttpStatusCode status, CommandResult? result)
    {
        public HttpStatusCode Status { get; } = status;
        public CommandResult? Result { get; } = result;
        public bool Denied => Result?.Success == false || Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.ServiceUnavailable;
    }

    internal sealed record StateEvidence(string LogicalRowsDigest, string DirectoriesDigest, long GetCalls,
        long ModelRowsRead, long RuntimeSettingsCalls, long ToolSettingsCalls, int AgentCalls,
        int EventCount, int RuntimeInstances, long LegacyRequests);

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly bool standalone;
        private readonly NoExecutor executor = new();
        private readonly (Guid Id, ChannelReader<ServiceEvent> Reader) observer;
        private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        private WebApplication? app;
        private LocalWorkerPipeServer? pipe;
        private long runtimeSettingsCalls;
        private long toolSettingsCalls;
        private long legacyRequests;
        private int eventCount;
        public string OwnerKey { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        public string ControllerId => standalone ? AssignedControllerId : WorkerId;
        public string? Address { get; private set; }
        public LocalWorkerEndpoint? PipeEndpoint { get; private set; }
        public StateStore Store { get; }
        public Configuration Configuration { get; }
        public EventHub Events { get; } = new();
        public ModelRuntime Runtime { get; }
        public ToolDispatcher Tools { get; }
        public JobScheduler Scheduler { get; }
        public PeerAccessControl Peers { get; }
        public CommandDispatcher Commands { get; }
        internal Action<HttpContext>? BeforeOwnerRoute { get; set; }

        public Fixture(string root, string seed)
        {
            this.root = Path.GetFullPath(root);
            standalone = seed == "standalone";
            Directory.CreateDirectory(this.root);
            Store = new StateStore(Path.Combine(this.root, "data"));
            Configuration = new(Store);
            var settings = new HostSettings
            {
                Revision = 7, InstallationId = WorkerId, Name = "Synthetic owner snapshot fixture",
                Coordinator = !standalone, StandaloneWorker = standalone,
                ManagementControllerId = standalone ? AssignedControllerId : null,
                ManagementControllerName = standalone ? "Synthetic assigned controller" : null,
                AccessKey = OwnerKey, AllowLan = false, ExternalCommandsEnabled = true,
                DefaultModelId = ModelId, ModelDirectory = Path.Combine(this.root, "models"),
                ProjectsDirectory = Path.Combine(this.root, "projects"), SharedDirectory = Path.Combine(this.root, "shared"),
                ToolsDirectory = Path.Combine(this.root, "tools")
            };
            if (seed != "missing") Store.Put("settings", "host", settings);
            if (seed == "legacy")
            {
                var legacy = JsonSerializer.SerializeToNode(settings, CompactJson)!.AsObject();
                legacy.Remove("installationId"); legacy.Remove("managementControllerId");
                SetRaw("settings", "host", legacy.ToJsonString());
            }
            Store.Put("agent", "snapshot-admin", new AgentProfile
            {
                Id = "snapshot-admin", Name = "Orchestrator", Role = "lead", Revision = 9,
                ModelId = ModelId, Capabilities = ["administration"]
            });
            Store.Put("skill", "snapshot-skill", new { id = "snapshot-skill", revision = 4, content = "Synthetic retained skill metadata." });
            foreach (var model in new[]
            {
                new ModelRecord { Id = ModelId, Name = "Synthetic registration", Path = @"X:\Synthetic\Models\fixture.gguf", State = "unloaded", MaximumContextTokens = 4096 },
                new ModelRecord { Id = "snapshot-alias", Name = "Synthetic same-path alias", Path = @"X:\Synthetic\Models\fixture.gguf", State = "unloaded", MaximumContextTokens = 4096 },
                new ModelRecord { Id = "snapshot-external", Name = "Synthetic remote-OS path", Path = "/srv/synthetic/models/fixture.gguf", State = "unloaded", MaximumContextTokens = 4096 }
            }) Store.Put("model", model.Id, model);
            observer = Events.Subscribe();
            Runtime = new(Store, () => { Interlocked.Increment(ref runtimeSettingsCalls); return Configuration.Get(); }, Events);
            Tools = new(() => { Interlocked.Increment(ref toolSettingsCalls); return Configuration.Get(); });
            Scheduler = new(Store, Configuration, executor, Runtime, Events, NullLogger<JobScheduler>.Instance);
            Peers = new(Store, Configuration, Scheduler);
            Commands = new(Store, Configuration, Runtime, Scheduler, Tools, Events,
                localRun: new LocalRunControl(!standalone), peerAccess: Peers);
        }

        public async Task StartAsync(bool pipe, CancellationToken token)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = root, Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            app = builder.Build();
            if (BeforeOwnerRoute is { } beforeOwnerRoute)
                app.Use(async (context, next) => { beforeOwnerRoute(context); await next(context); });
            OwnerModelSnapshotHttp.UseOwnerModelSnapshotRoute(app, Commands);
            // Exact legacy authentication behavior from Program's HTTP composition. This is kept
            // after the real owned route so its mutating Get can be counted independently.
            app.Use(async (context, next) =>
            {
                Interlocked.Increment(ref legacyRequests);
                var current = Configuration.Get();
                var remote = context.Connection.RemoteIpAddress;
                var local = remote is not null && IPAddress.IsLoopback(remote);
                var peerRoute = context.Request.Path.StartsWithSegments("/api/peer");
                if (context.Request.Headers.ContainsKey("Origin")) { context.Response.StatusCode = 403; return; }
                if (!local && !current.AllowLan) { context.Response.StatusCode = 403; return; }
                if (peerRoute)
                {
                    if (!Peers.TryAuthenticate(context.Request.Headers[PeerAccessControl.HeaderName].ToString(), out var grant))
                    { context.Response.StatusCode = 401; return; }
                    context.Items[typeof(PeerAccessGrant)] = grant!;
                    await next(context); return;
                }
                if (!local || context.Request.Path.StartsWithSegments("/api/external"))
                {
                    var supplied = context.Request.Headers["X-Cerebral-Key"].ToString();
                    if (string.IsNullOrEmpty(current.AccessKey) ||
                        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(current.AccessKey)))
                    { context.Response.StatusCode = 401; return; }
                }
                await next(context);
            });
            app.MapPost("/api/command", async (CommandRequest request, CancellationToken ct) =>
                await Commands.ExecuteAsync(request, ct));
            app.MapPost("/api/peer/command", async Task<IResult> (HttpContext context, CommandRequest request, CancellationToken ct) =>
            {
                if (context.Items[typeof(PeerAccessGrant)] is not PeerAccessGrant grant) return Results.Unauthorized();
                return Results.Json(await Peers.ExecuteAsync(grant, request, ct), CerebralJson.Options);
            });
            app.MapPost("/api/external/command", async (CommandRequest request, CancellationToken ct) =>
            {
                if (!Configuration.Get().ExternalCommandsEnabled) return CommandResult.Fail("Optional fixture access is disabled.");
                if (string.IsNullOrEmpty(request.ProfileId)) return CommandResult.Fail("Optional access requires an explicit saved agent profile.");
                return await Commands.ExecuteAsync(request, ct);
            });
            await app.StartAsync(token);
            Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            http.BaseAddress = new Uri(Address);
            if (pipe)
            {
                PipeEndpoint = new("cerebral-owner-snapshot-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
                this.pipe = new(PipeEndpoint, Commands, Events);
                await this.pipe.StartAsync(token);
            }
        }

        public async Task<HttpOutcome> HttpAsync(string path, byte[] body, string? key,
            CancellationToken token, string? origin = null, string? peerToken = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (key is not null) request.Headers.Add("X-Cerebral-Key", key);
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (peerToken is not null) request.Headers.Add(PeerAccessControl.HeaderName, peerToken);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(bounded.Token);
            using var capture = new MemoryStream();
            var buffer = new byte[8192];
            const int maximum = 256 * 1024;
            while (true)
            {
                var remaining = maximum + 1 - (int)capture.Length;
                if (remaining <= 0) throw new InvalidOperationException("Snapshot response exceeded its complete envelope budget.");
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), bounded.Token);
                if (read == 0) break;
                capture.Write(buffer, 0, read);
                if (capture.Length > maximum) throw new InvalidOperationException("Snapshot response exceeded its complete envelope budget.");
            }
            var bytes = capture.ToArray();
            CommandResult? result = null;
            if (bytes.Length > 0 && response.Content.Headers.ContentType?.MediaType == "application/json")
                result = JsonSerializer.Deserialize<CommandResult>(bytes, CerebralJson.Options)
                    ?? throw new InvalidOperationException("Snapshot response did not contain a command result.");
            return new(response.StatusCode, result);
        }

        public async Task<bool> RawPipeAsync(byte[] commandBody, CancellationToken token)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(TimeSpan.FromSeconds(10));
            await using var client = new NamedPipeClientStream(".", PipeEndpoint!.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
            await client.ConnectAsync(bounded.Token);
            await LocalWorkerProtocol.WriteAsync(client, LocalWorkerProtocol.CreateHello(PipeEndpoint), bounded.Token);
            LocalWorkerProtocol.ValidateHello(await LocalWorkerProtocol.ReadAsync<LocalWorkerHello>(client, bounded.Token), PipeEndpoint);
            var prefix = StrictUtf8.GetBytes("{\"kind\":\"command\",\"command\":");
            var suffix = StrictUtf8.GetBytes("}");
            var payload = new byte[prefix.Length + commandBody.Length + suffix.Length];
            prefix.CopyTo(payload, 0); commandBody.CopyTo(payload, prefix.Length); suffix.CopyTo(payload, prefix.Length + commandBody.Length);
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await client.WriteAsync(header, bounded.Token); await client.WriteAsync(payload, bounded.Token); await client.FlushAsync(bounded.Token);
            try { return !(await LocalWorkerProtocol.ReadAsync<CommandResult>(client, bounded.Token)).Success; }
            catch (Exception error) when (!bounded.IsCancellationRequested && error is IOException or InvalidDataException) { return true; }
        }

        public StateEvidence Capture()
        {
            while (observer.Reader.TryRead(out _)) eventCount++;
            var directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.Ordinal).ToArray();
            return new(LogicalDigest(), Hash(StrictUtf8.GetBytes(string.Join('\n', directories))),
                Counter(Configuration, "GetInvocationCount"), Counter(Store, "OwnerSnapshotModelRowsRead"),
                Interlocked.Read(ref runtimeSettingsCalls), Interlocked.Read(ref toolSettingsCalls), executor.Calls,
                eventCount, CerebralJson.Element(Runtime.Diagnostics()).GetArrayLength(), Interlocked.Read(ref legacyRequests));
        }

        public void VerifyNoEffects(StateEvidence before, bool expectModelRead, Action<bool, string> check, string label)
        {
            var after = Capture();
            check(before.LogicalRowsDigest == after.LogicalRowsDigest,
                label + " must preserve every SQLite logical row, including host/model/agent/skill records.");
            check(before.DirectoriesDigest == after.DirectoriesDigest && before.EventCount == after.EventCount,
                label + " must create no directories or publish events.");
            check(before.GetCalls == after.GetCalls && before.RuntimeSettingsCalls == after.RuntimeSettingsCalls &&
                before.ToolSettingsCalls == after.ToolSettingsCalls && before.AgentCalls == after.AgentCalls &&
                after.RuntimeInstances == 0 && before.LegacyRequests == after.LegacyRequests,
                label + " must avoid Configuration.Get, provider/tool settings callbacks, agent execution, loaded instances and legacy middleware.");
            check(expectModelRead ? after.ModelRowsRead > before.ModelRowsRead : after.ModelRowsRead == before.ModelRowsRead,
                label + (expectModelRead ? " must perform the actual bounded catalog read." : " must fail before any actual model-row read."));
        }

        private static long Counter(object instance, string name) =>
            (long)(instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance)
                ?? throw new InvalidOperationException("Missing exact snapshot path counter " + name));

        public void SetRaw(string kind, string id, object body, bool castBytesAsText = false)
        {
            using var connection = Open(SqliteOpenMode.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = castBytesAsText
                ? "UPDATE records SET body=CAST($body AS TEXT) WHERE kind=$kind AND id=$id"
                : "UPDATE records SET body=$body WHERE kind=$kind AND id=$id";
            command.Parameters.AddWithValue("$body", body); command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Synthetic retained record coordinate was not found.");
        }

        private SqliteConnection Open(SqliteOpenMode mode)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Store.DirectoryPath, "cerebral.db"), Mode = mode, Pooling = false
            }.ToString());
            connection.Open(); return connection;
        }

        private string LogicalDigest()
        {
            using var connection = Open(SqliteOpenMode.ReadOnly);
            var tables = new List<string>();
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name COLLATE BINARY";
                using var reader = schema.ExecuteReader(); while (reader.Read()) tables.Add(reader.GetString(0));
            }
            if (tables.Count > 64) throw new InvalidOperationException("Synthetic logical-state table inventory exceeded its bound.");
            var logical = new List<string>();
            foreach (var table in tables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = table == "records"
                    ? "SELECT kind,id,typeof(body) AS body_storage_type,CAST(body AS BLOB) AS body FROM records"
                    : "SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                using var reader = command.ExecuteReader();
                var rows = new List<string>();
                while (reader.Read())
                {
                    if (rows.Count >= 2000) throw new InvalidOperationException("Synthetic logical-state row inventory exceeded its bound.");
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    for (var column = 0; column < reader.FieldCount; column++)
                    {
                        var value = reader.GetValue(column);
                        var bytes = value is DBNull ? Array.Empty<byte>() : value is byte[] blob ? blob :
                            StrictUtf8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                        var metadata = StrictUtf8.GetBytes(reader.GetName(column) + ":" + value.GetType().FullName);
                        var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, metadata.Length); digest.AppendData(length); digest.AppendData(metadata);
                        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); digest.AppendData(length); digest.AppendData(bytes);
                    }
                    rows.Add(Convert.ToHexString(digest.GetHashAndReset()));
                }
                rows.Sort(StringComparer.Ordinal);
                logical.Add(table + ":" + string.Join(',', rows));
            }
            // Only hashes leave this helper. Raw credential/configuration bodies are never logged,
            // returned, attached or retained as evidence.
            return Hash(StrictUtf8.GetBytes(string.Join('\n', logical)));
        }

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        public async ValueTask DisposeAsync()
        {
            http.Dispose();
            if (pipe is not null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await pipe.StopAsync(stop.Token);
                await pipe.DisposeAsync();
            }
            if (app is not null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await app.StopAsync(stop.Token);
                await app.DisposeAsync();
            }
            Events.Unsubscribe(observer.Id);
            Scheduler.Dispose();
            await Tools.DisposeAsync();
            await Runtime.DisposeAsync();
        }
    }

    private sealed class NoExecutor : IAgentExecutor
    {
        public int Calls { get; private set; }
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("Snapshot path fixtures must never execute an agent.");
        }
    }
}
