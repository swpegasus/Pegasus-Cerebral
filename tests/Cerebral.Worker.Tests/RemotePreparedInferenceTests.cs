using System.Collections.Concurrent;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic owner-transport and SQLite fixtures; no model, network or hardware is exercised.</summary>
public static partial class RemotePreparedInferenceTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string name)
        { if (!value) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
        async Task Reject(Func<Task> action, string name)
        {
            var rejected = false;
            try { await action(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or JsonException or NotSupportedException or IOException) { rejected = true; }
            Check(rejected, name);
        }
        var binding = new RemotePreparedModelBinding("remote-model", "/srv/cerebral/models/model.gguf", "alpaca", [], 0, 1, -1);
        var request = new ModelGenerationRequest(binding.Id, [new("user", [new TextContentPart("Exact input café.")])], 8192, 512);
        var imageBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var imagePart = new ImageContentPart(new("image-id", 1, "image/png", "fixture.png", imageBytes.Length), Width: 1, Height: 1);
        var imageRequest = request with { Messages = [new("user", [new TextContentPart("Inspect this image."), imagePart])] };
        RemotePreparedImageInput Image(byte[]? data = null, int messageIndex = 0, int contentIndex = 1,
            string mediaType = "image/png", int width = 1, int height = 1) =>
            new(messageIndex, contentIndex, mediaType, width, height, data ?? imageBytes.ToArray());
        JsonElement PrepareInput(ModelGenerationRequest? value = null) => CerebralJson.Element(new RemotePrepareRequest(1, value ?? request, binding));
        JsonElement PrepareImageInput(IReadOnlyList<RemotePreparedImageInput>? images = null, ModelGenerationRequest? value = null) =>
            CerebralJson.Element(new RemotePrepareRequestV2(RemotePreparedProtocol.ImageVersion, value ?? imageRequest, binding, images ?? [Image()]));
        JsonElement Lease(string id, int version = RemotePreparedProtocol.Version) => CerebralJson.Element(new RemoteLeaseRequest(version, id));
        RemotePreparedResponse Preparation(CommandResult result) => CerebralJson.Read<RemotePreparedResponse>(result.Data);
        RemoteGeneratedResponse Generation(CommandResult result) => CerebralJson.Read<RemoteGeneratedResponse>(result.Data);
        JsonElement LegacyInput() => CerebralJson.Element(new
        {
            workerId = "gb10", modelId = binding.Id, messages = new[] { new ModelMessage("user", "Owner canary") },
            maxTokens = 128, temperature = 0.2, json = false, responseSchema = (object?)null,
            reasoningMode = "off", reasoningTokens = 1024
        });

        // The helper must preserve tokenizer evidence and consume a lease exactly once.
        {
            var model = new MeasuredAdapter();
            await using var server = new RemotePreparedInference(model, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
            var prepared = Preparation(await server.PrepareAsync(PrepareInput(), default));
            Check(prepared.ProtocolVersion == 1 && prepared.LeaseId?.Length == 32 && prepared.Metadata.InputTokens == 321 &&
                prepared.Metadata.CountBasis == "fixture exact rendered-token evidence" && model.Preparations == 1,
                "remote prepare preserves exact adapter count evidence without proxy estimates");
            var completed = Generation(await server.GenerateAsync(Lease(prepared.LeaseId!), default));
            Check(completed.Result.Finish == ModelGenerationFinish.Stop && completed.Result.Text == "Completed café." &&
                completed.Result.GeneratedTokens == 17 && completed.Result.EvaluatedTokens == 321 && completed.Metadata == prepared.Metadata,
                "generation preserves finish, visible text, usage and the exact prepared metadata");
            Check(model.Generations == 1 && model.Disposals == 1, "successful remote generation releases its actual prepared slot");
            await Reject(() => server.GenerateAsync(Lease(prepared.LeaseId!), default), "consumed lease never replays generation");
            var released = CerebralJson.Read<RemoteReleaseResponse>((await server.ReleaseAsync(Lease(prepared.LeaseId!), default)).Data);
            Check(released.Released && !released.CancellationRequested && model.Disposals == 1, "terminal release is idempotent without a second dispose");
        }
        {
            var model = new MeasuredAdapter();
            await using var server = new RemotePreparedInference(model, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                prepareMultimodal: model.PrepareMultimodalAsync);
            var prepared = Preparation(await server.PrepareAsync(PrepareImageInput(), default));
            Check(prepared.ProtocolVersion == RemotePreparedProtocol.ImageVersion && prepared.Adapter.ContentCapabilities.Contains("image") &&
                model.LastImageBytes!.SequenceEqual(imageBytes) && model.BorrowedImages!.Single().Data.All(value => value == 0),
                "version two maps exact inline image bytes and clears the server-owned transport copy");
            await Reject(() => server.GenerateAsync(Lease(prepared.LeaseId!), default),
                "a version-one lease claim cannot consume a version-two image preparation");
            var completed = Generation(await server.GenerateAsync(Lease(prepared.LeaseId!, RemotePreparedProtocol.ImageVersion), default));
            Check(completed.ProtocolVersion == RemotePreparedProtocol.ImageVersion && completed.Result.Finish == ModelGenerationFinish.Stop &&
                model.Generations == 1 && model.Disposals == 1, "version-two image lease preserves one-use generation and response version echo");
            await Reject(() => server.GenerateAsync(Lease(prepared.LeaseId!, RemotePreparedProtocol.ImageVersion), default),
                "consumed version-two image lease never replays generation");
        }
        {
            var model = new MeasuredAdapter(); var now = DateTimeOffset.UtcNow;
            await using var server = new RemotePreparedInference(model, (_, _) => Task.CompletedTask, () => now, TimeSpan.FromMinutes(1), 1);
            var first = Preparation(await server.PrepareAsync(PrepareInput(), default));
            await Reject(() => server.PrepareAsync(PrepareInput(), default), "unused prepared slots count against the bounded lease limit");
            now += TimeSpan.FromMinutes(2); await server.SweepExpiredAsync();
            Check(model.Disposals == 1 && model.Generations == 0, "unused expiry disposes preparation without dispatching inference");
            await Reject(() => server.GenerateAsync(Lease(first.LeaseId!), default), "expired lease cannot dispatch");
            var replacement = Preparation(await server.PrepareAsync(PrepareInput(), default));
            Check(replacement.LeaseId != first.LeaseId && model.Preparations == 2, "expired capacity permits a fresh unrelated preparation");
            await server.ReleaseAsync(Lease(replacement.LeaseId!), default);
        }
        {
            var model = new MeasuredAdapter { Limit = true };
            await using var server = new RemotePreparedInference(model, (_, _) => Task.CompletedTask, maxLeases: 1);
            var limited = Preparation(await server.PrepareAsync(PrepareInput(), default));
            Check(limited.ContextLimited && limited.LeaseId is null && limited.ExpiresAt is null &&
                limited.Metadata.InputTokens == 8191 && limited.Metadata.ResponseBudget == 0 && model.Generations == 0,
                "context overflow retains exact counts with no dispatchable lease");
            model.Limit = false;
            var next = Preparation(await server.PrepareAsync(PrepareInput(), default));
            Check(next.LeaseId is not null, "overflow does not retain unavailable capacity");
            await server.ReleaseAsync(Lease(next.LeaseId!), default);
        }
        {
            var changed = false; var model = new MeasuredAdapter();
            await using var server = new RemotePreparedInference(model, (_, _) =>
                changed ? throw new InvalidOperationException("Fixture binding changed.") : Task.CompletedTask);
            var prepared = Preparation(await server.PrepareAsync(PrepareInput(), default)); changed = true;
            var result = Generation(await server.GenerateAsync(Lease(prepared.LeaseId!), default));
            Check(result.Result.Finish == ModelGenerationFinish.Unknown && model.Generations == 0 && model.Disposals == 1,
                "model binding drift after preparation prevents inference and consumes the stale lease");
        }
        {
            var model = new MeasuredAdapter();
            await using var server = new RemotePreparedInference(model, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
            var prepared = Preparation(await server.PrepareAsync(PrepareInput(), default));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var result = Generation(await server.GenerateAsync(Lease(prepared.LeaseId!), cancel.Token));
            Check(result.Result.Finish == ModelGenerationFinish.Cancelled && result.Result.CancellationConfirmed && model.Generations == 0,
                "cancellation before provider dispatch is confirmed without generation");
        }
        {
            var model = new MeasuredAdapter { Block = true }; var now = DateTimeOffset.UtcNow;
            await using var server = new RemotePreparedInference(model, (_, _) => Task.CompletedTask, () => now, maxLeases: 1);
            var prepared = Preparation(await server.PrepareAsync(PrepareInput(), default));
            var pending = server.GenerateAsync(Lease(prepared.LeaseId!), default);
            await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var release = CerebralJson.Read<RemoteReleaseResponse>((await server.ReleaseAsync(Lease(prepared.LeaseId!), default)).Data);
            Check(!release.Released && release.CancellationRequested && model.LastToken.IsCancellationRequested && model.Disposals == 0,
                "release requests cancellation but never claims an active provider has stopped");
            await Reject(() => server.PrepareAsync(PrepareInput(), default), "interrupted active inference retains capacity until terminal evidence");
            await Reject(() => server.GenerateAsync(Lease(prepared.LeaseId!), default), "competing claim cannot dispatch a second active inference");
            now += TimeSpan.FromMinutes(11); await server.SweepExpiredAsync();
            Check(model.Disposals == 0, "active expiry does not dispose a provider that has not returned");
            model.Finish.TrySetResult();
            var result = Generation(await pending);
            Check(result.Result.Finish == ModelGenerationFinish.Unknown && !result.Result.CancellationConfirmed && model.Disposals == 1,
                "post-dispatch cancellation exception stays uncertain and disposes only after provider return");
            await Reject(() => server.GenerateAsync(Lease(prepared.LeaseId!), default), "uncertain consumed lease is never replayed");
        }
        {
            var model = new MeasuredAdapter();
            await using var server = new RemotePreparedInference(model, (_, _) => Task.CompletedTask);
            var versionOneShape = PrepareInput();
            Check(versionOneShape.EnumerateObject().Select(property => property.Name).SequenceEqual(["protocolVersion", "request", "expectedModel"]) &&
                !versionOneShape.TryGetProperty("images", out _), "version-one prepare serialization retains its original three-field shape");
            var json = PrepareInput().GetRawText();
            using var unknown = JsonDocument.Parse(json[..^1] + ",\"arbitraryCommand\":\"settings.save\"}");
            await Reject(() => server.PrepareAsync(unknown.RootElement, default), "closed prepare schema rejects arbitrary forwarding fields");
            using var duplicate = JsonDocument.Parse("{\"protocolVersion\":1,\"protocolVersion\":1,\"leaseId\":\"" + new string('a', 32) + "\"}");
            await Reject(() => server.ReleaseAsync(duplicate.RootElement, default), "duplicate lease fields are rejected at the boundary");
            using var duplicateCase = JsonDocument.Parse("{\"protocolVersion\":1,\"ProtocolVersion\":1,\"leaseId\":\"" + new string('a', 32) + "\"}");
            await Reject(() => server.ReleaseAsync(duplicateCase.RootElement, default), "case-variant duplicate fields cannot exploit case-insensitive binding");
            await Reject(() => server.PrepareAsync(CerebralJson.Element(new RemotePrepareRequest(3, request, binding)), default), "unsupported protocol cannot create preparations");
            await Reject(() => server.ReleaseAsync(Lease(new string('A', 32)), default), "noncanonical lease identifiers cannot dispatch or release");
            await Reject(() => server.PrepareAsync(PrepareInput(request with { ModelId = "other-model" }), default), "request and immutable model IDs must match");
            var image = new ImageContentPart(new("image-id", 1, "image/png", "fixture.png", 16), Width: 1, Height: 1);
            await Reject(() => server.PrepareAsync(PrepareInput(request with { Messages = [new("user", [image])] }), default),
                "version one rejects binary content before provider access");
            Check(model.Preparations == 0, "all malformed inputs remain before provider preparation");
        }
        {
            var model = new MeasuredAdapter();
            await using var server = new RemotePreparedInference(model, (_, _) => Task.CompletedTask,
                prepareMultimodal: model.PrepareMultimodalAsync);
            await Reject(() => server.PrepareAsync(PrepareImageInput([Image(contentIndex: 0)]), default),
                "version two rejects an inline image mapped to a text content position");
            await Reject(() => server.PrepareAsync(PrepareImageInput([Image(width: 2)]), default),
                "version two rejects dimensions that differ from the admitted image part");
            var oversized = new byte[2_400_000];
            await Reject(() => server.PrepareAsync(PrepareImageInput([Image(oversized)], imageRequest with
                { Messages = [new("user", [new TextContentPart("Inspect."), imagePart with { Attachment = imagePart.Attachment with { Length = oversized.Length } }])] }), default),
                "version two rejects an inline image whose serialized request exceeds three MiB");
            Check(model.Preparations == 0, "invalid version-two image inputs remain before provider preparation");
        }
        {
            var model = new MeasuredAdapter { Output = new string('"', 900_000) };
            await using var server = new RemotePreparedInference(model, (_, _) => Task.CompletedTask);
            var prepared = Preparation(await server.PrepareAsync(PrepareInput(), default));
            var result = Generation(await server.GenerateAsync(Lease(prepared.LeaseId!), default));
            Check(result.Result.Finish == ModelGenerationFinish.Unknown && result.Result.Text.Length == 0 &&
                result.Result.Error?.Contains("transport limit", StringComparison.Ordinal) == true && model.Disposals == 1,
                "oversized escaped terminal transport is uncertain, never an empty completed answer");
        }

        await using (var fixture = new OwnerFixture())
        {
            var local = new MeasuredAdapter();
            var adapter = fixture.Adapter();
            await using var prepared = await adapter.PrepareAsync(request with { ReasoningTokens = 128 }, default);
            var ready = fixture.OutboundObservations().Single();
            var accounting = adapter.Accounting.Snapshot().Entries.Single(entry => entry.Id == ready.AccountingEntryId);
            Check(ready.Phase == "ready" && ready.Budget is { InputTokens: 321, Response: 512, ReasoningReserve: 128 } &&
                ready.AccountingLeaseId is not null && ready.ReservationId is not null &&
                ready.InstanceId == prepared.Metadata.InstanceId && ready.NativeSlotId is null && ready.LeaseId is null &&
                ready.Terminal is null && ready.Cache is null && accounting.WorkerInstallationId == fixture.Identity &&
                accounting.LeaseId == ready.AccountingLeaseId && accounting.ReservationId == ready.ReservationId,
                "outbound capture binds validated preparation and remote transport ownership without inventing a native slot");
            var callbacks = 0; var result = await prepared.GenerateAsync(_ => callbacks++, default);
            Check(prepared.Metadata.InputTokens == 321 && prepared.Metadata.ReasoningReserveTokens == 128 &&
                fixture.RemoteModel.LastRequest?.ReasoningTokens == 128 && result.GeneratedTokens == 17,
                "owner client preserves exact prepared reasoning reservation and usage");
            Check(callbacks == 0 && local.Preparations == 0 && fixture.Calls.Count(x => x == RemotePreparedProtocol.GenerateOperation) == 1,
                "terminal remote protocol advertises no fake token stream or local fallback");
            var completed = fixture.OutboundObservations().Single();
            Check(completed.Release == "confirmed" && completed.Phase == "released" &&
                completed.Terminal is { Finish: "stop", EvaluatedTokens: 321, GeneratedTokens: 17, ProviderError: false } &&
                completed.Cache is null && completed.Budget == ready.Budget &&
                completed.AccountingEntryId == ready.AccountingEntryId && completed.AccountingLeaseId == ready.AccountingLeaseId,
                "validated outbound terminal usage survives route retirement on the exact captured row");
            await Reject(() => prepared.GenerateAsync(null, default), "client preparation cannot replay generation");
        }
        await using (var fixture = new OwnerFixture { OmitPromptCache = true })
        {
            // Supply an actual optional field before removing it from the simulated legacy wire.
            // Null telemetry now serializes as absent, so removing null would not exercise the fixture.
            fixture.RemoteModel.PromptCache = new(321, 300, 21, 14.5);
            await using var prepared = await fixture.Adapter().PrepareAsync(request, default);
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Stop && result.PromptCache is null && fixture.LegacyCacheFieldRemoved,
                "old remote terminal JSON without optional cache metrics remains compatible and never invents a cache miss");
            Check(fixture.OutboundObservations().Single() is { Release: "confirmed", Cache: null, Terminal.Finish: "stop" },
                "legacy terminal capture retains unavailable cache telemetry rather than fabricating zero reuse");
        }
        foreach (var metrics in new[]
        {
            new PromptCacheMetrics(321, 300, 21, 14.5), new PromptCacheMetrics(321, 0, 321, 0),
            new PromptCacheMetrics(321, 321, 0), new PromptCacheMetrics(321, PromptProcessingMilliseconds: 4)
        })
        {
            await using var fixture = new OwnerFixture();
            fixture.RemoteModel.PromptCache = metrics;
            await using var prepared = await fixture.Adapter().PrepareAsync(request, default);
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Stop && result.Text == "Completed café." && result.PromptCache == metrics &&
                result.PromptCache.ReuseRatio == metrics.ReuseRatio && prepared.Metadata.InputTokens == 321 &&
                prepared.Metadata.ResponseBudget == 512 && fixture.RemoteModel.Generations == 1 && fixture.RemoteModel.Disposals == 1,
                "remote serialization preserves explicit provider cache measurements without changing the full prepared budget or replaying inference");
            var captured = fixture.OutboundObservations().Single();
            Check(captured.Terminal is { Finish: "stop", GeneratedTokens: 17, EvaluatedTokens: 321 } &&
                captured.Cache is { PreparedInputTokens: 321 } cache && cache.CachedTokens == metrics.CachedTokens &&
                cache.ProcessedTokens == metrics.ProcessedPromptTokens && cache.PromptMilliseconds == metrics.PromptProcessingMilliseconds &&
                captured.Release == "confirmed",
                "outbound terminal capture preserves only the explicit provider cache measurements on its released lease row");
        }
        foreach (var metrics in new[]
        {
            new PromptCacheMetrics(320, 300, 20), new PromptCacheMetrics(321, -1), new PromptCacheMetrics(321, 322),
            new PromptCacheMetrics(321, 300, 22), new PromptCacheMetrics(321, ProcessedPromptTokens: -1),
            new PromptCacheMetrics(321, ProcessedPromptTokens: 322), new PromptCacheMetrics(321, PromptProcessingMilliseconds: -1),
            new PromptCacheMetrics(321, PromptProcessingMilliseconds: double.NaN),
            new PromptCacheMetrics(321, PromptProcessingMilliseconds: double.PositiveInfinity),
            new PromptCacheMetrics(321, PromptProcessingMilliseconds: double.NegativeInfinity),
            new PromptCacheMetrics(321, Diagnostic: new string('x', 4096))
        })
        {
            await using var fixture = new OwnerFixture();
            fixture.RemoteModel.PromptCache = metrics;
            await using var prepared = await fixture.Adapter().PrepareAsync(request, default);
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Stop && result.Error is null && result.Text == "Completed café." &&
                fixture.RemoteModel.Generations == 1 && result.PromptCache is { PreparedInputTokens: 321,
                    CachedTokens: null, ProcessedPromptTokens: null, PromptProcessingMilliseconds: null,
                    ReuseRatio: null, Diagnostic.Length: > 0 and <= 256 },
                "malformed optional remote metrics are unavailable with an authored bounded diagnostic while independently verified output remains adopted");
        }
        foreach (var time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var metrics = LlamaProtocol.ValidatePromptCache(new(321, PromptProcessingMilliseconds: time), 321);
            Check(metrics is { PreparedInputTokens: 321, PromptProcessingMilliseconds: null, Diagnostic.Length: > 0 and <= 256 },
                "shared telemetry validator rejects nonfinite provider timings without serializing fabricated measurements");
        }
        Check(LlamaProtocol.ValidatePromptCache(new(0, 0, 0, 0), 0) is
            { PreparedInputTokens: 0, CachedTokens: 0, ProcessedPromptTokens: 0, PromptProcessingMilliseconds: 0, ReuseRatio: null, Diagnostic: null },
            "valid zero-length prepared input preserves explicit zero measurements with an undefined reuse ratio");
        foreach (var metrics in new[] { new PromptCacheMetrics(320, 300, 20), new PromptCacheMetrics(321, -1),
            new PromptCacheMetrics(321, PromptProcessingMilliseconds: -1) })
        {
            await using var fixture = new OwnerFixture { TamperPromptCache = metrics };
            await using var prepared = await fixture.Adapter().PrepareAsync(request, default);
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Stop && result.Error is null && result.Text == "Completed café." &&
                result.PromptCache is { PreparedInputTokens: 321, CachedTokens: null, PromptProcessingMilliseconds: null,
                    Diagnostic.Length: > 0 and <= 256 } && fixture.RemoteModel.Generations == 1,
                "owner client independently validates optional remote telemetry even when altered after server normalization");
            Check(fixture.OutboundObservations().Single() is
                { Release: "confirmed", Terminal: { Finish: "stop", ProviderError: false, GeneratedTokens: 17 },
                    Cache: { CachedTokens: null, ProcessedTokens: null, PromptMilliseconds: null } },
                "malformed optional cache measurements never enter capture as numeric facts or poison the independently validated terminal");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.RemoteModel.Block = true;
            fixture.RemoteModel.CancellationResult = new(ModelGenerationFinish.Cancelled, "", CancellationConfirmed: true);
            var prepared = await fixture.Adapter().PrepareAsync(request, default);
            using var stop = new CancellationTokenSource();
            var pending = prepared.GenerateAsync(null, stop.Token);
            await fixture.RemoteModel.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await prepared.DisposeAsync();
            Check(result.Finish == ModelGenerationFinish.Cancelled && result.CancellationConfirmed &&
                fixture.Calls.Count(x => x == RemotePreparedProtocol.GenerateOperation) == 1 &&
                fixture.Calls.Count(x => x == RemotePreparedProtocol.ReleaseOperation) == 1 &&
                fixture.RemoteModel.Generations == 1 && fixture.RemoteModel.Disposals == 1,
                "client cancellation retains the sole response long enough to adopt provider-confirmed cancellation exactly once");
            Check(fixture.OutboundObservations().Single() is
                { CancellationRequested: true, Release: "confirmed",
                    Terminal: { Finish: "cancelled", CancellationConfirmed: true, GeneratedTokens: null, EvaluatedTokens: null }, Cache: null },
                "outbound capture distinguishes requested cancellation from the verified terminal and leaves absent usage unavailable");
            await using var next = await fixture.Adapter().PrepareAsync(request, default);
            Check(fixture.Calls.Count(call => call == RemotePreparedProtocol.PrepareOperation) == 2,
                "provider-confirmed cancellation does not quarantine the remote route");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.RemoteModel.Block = true; fixture.RemoteModel.CompleteAfterCancellation = true;
            var prepared = await fixture.Adapter().PrepareAsync(request, default);
            using var stop = new CancellationTokenSource();
            var pending = prepared.GenerateAsync(null, stop.Token);
            await fixture.RemoteModel.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            await fixture.ReleaseObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.RemoteModel.Finish.TrySetResult();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await prepared.DisposeAsync();
            Check(result.Finish == ModelGenerationFinish.Stop && result.Text == fixture.RemoteModel.Output &&
                fixture.Calls.Count(x => x == RemotePreparedProtocol.GenerateOperation) == 1 &&
                fixture.Calls.Count(x => x == RemotePreparedProtocol.ReleaseOperation) == 1,
                "a completion that wins the cancellation race remains truthful without replay or a second release");
            Check(fixture.OutboundObservations().Single() is
                { CancellationRequested: true, Release: "confirmed", Terminal: { Finish: "stop", GeneratedTokens: 17, EvaluatedTokens: 321 } },
                "observed cancellation never overwrites a validated completion that wins the race");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.RemoteModel.Block = true;
            fixture.RemoteModel.CancellationResult = new(ModelGenerationFinish.Cancelled, "", CancellationConfirmed: false);
            var prepared = await fixture.Adapter().PrepareAsync(request, default);
            using var stop = new CancellationTokenSource();
            var pending = prepared.GenerateAsync(null, stop.Token);
            await fixture.RemoteModel.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await prepared.DisposeAsync();
            Check(result.Finish == ModelGenerationFinish.Unknown && !result.CancellationConfirmed &&
                result.Error?.Contains("without confirmed terminal evidence", StringComparison.Ordinal) == true &&
                fixture.Calls.Count(x => x == RemotePreparedProtocol.GenerateOperation) == 1 &&
                fixture.Calls.Count(x => x == RemotePreparedProtocol.ReleaseOperation) == 1,
                "an unconfirmed provider cancellation is normalized to unknown and is never promoted by release acknowledgement");
            Check(fixture.OutboundObservations().Single() is
                { CancellationRequested: true, Phase: "unknown", Release: "unobserved",
                    Terminal: { Finish: "unknown", CancellationConfirmed: false, GeneratedTokens: null, EvaluatedTokens: null }, Cache: null },
                "unconfirmed cancellation capture preserves unknown ownership without fabricated usage or release");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.RemoteModel.Block = true;
            fixture.RemoteModel.CancellationResult = new(ModelGenerationFinish.Cancelled, "", CancellationConfirmed: true);
            fixture.DropGenerationResponse = true;
            var prepared = await fixture.Adapter().PrepareAsync(request, default);
            using var stop = new CancellationTokenSource();
            var pending = prepared.GenerateAsync(null, stop.Token);
            await fixture.RemoteModel.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await prepared.DisposeAsync();
            Check(result.Finish == ModelGenerationFinish.Unknown && !result.CancellationConfirmed &&
                fixture.Calls.Count(x => x == RemotePreparedProtocol.GenerateOperation) == 1 &&
                fixture.Calls.Count(x => x == RemotePreparedProtocol.ReleaseOperation) == 1,
                "a lost terminal transport stays unknown even when the provider produced confirmed cancellation");
            Check(fixture.OutboundObservations().Single() is
                { CancellationRequested: true, Phase: "unknown", Release: "unobserved",
                    Terminal: { Finish: "unknown", ProviderError: true, GeneratedTokens: null, EvaluatedTokens: null }, Cache: null },
                "a dropped terminal response exposes no provider counts or cache facts to outbound capture");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.RemoteModel.Block = true; fixture.FinishGenerationOnTransportCancellation = true;
            var timing = new RemotePreparedClientTiming(TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.FromSeconds(1));
            var prepared = await fixture.Adapter(timing).PrepareAsync(request, default);
            using var stop = new CancellationTokenSource();
            var pending = prepared.GenerateAsync(null, stop.Token);
            await fixture.RemoteModel.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.TransportCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await prepared.DisposeAsync();
            Check(result.Finish == ModelGenerationFinish.Unknown && !result.CancellationConfirmed &&
                fixture.Calls.Count(x => x == RemotePreparedProtocol.GenerateOperation) == 1 &&
                fixture.Calls.Count(x => x == RemotePreparedProtocol.ReleaseOperation) == 1 && fixture.RemoteModel.Disposals == 1,
                "confirmation-grace expiry cancels and observes the sole pending transport before disposing its session");
            Check(fixture.OutboundObservations().Single() is
                { CancellationRequested: true, Phase: "unknown", Release: "unobserved",
                    Terminal: { Finish: "unknown", GeneratedTokens: null, EvaluatedTokens: null }, Cache: null },
                "confirmation timeout keeps the captured attempt unresolved after observing transport completion");
        }
        await using (var fixture = new OwnerFixture())
        {
            await using var prepared = await fixture.Adapter().PrepareMultimodalAsync(imageRequest, [Image()], default);
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Stop && fixture.RemoteModel.LastImageBytes!.SequenceEqual(imageBytes) &&
                fixture.Calls.Count(x => x == RemotePreparedProtocol.GenerateOperation) == 1,
                "owner client uses version two for exact inline image preparation and one terminal generation");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.RemoveImageCapability = true;
            await Reject(() => fixture.Adapter().PrepareMultimodalAsync(imageRequest, [Image()], default),
                "owner client rejects and releases an image lease without returned image capability");
            Check(fixture.Calls.Contains(RemotePreparedProtocol.ReleaseOperation) && fixture.RemoteModel.Disposals == 1,
                "capability rejection releases the unused version-two server preparation");
            Check(fixture.OutboundObservations().Single() is
                { Release: "confirmed", Budget: null, Terminal: null, Cache: null, NativeSlotId: null, LeaseId: null },
                "rejected preparation capabilities never publish a ready budget or native preparation identity");
        }
        await using (var fixture = new OwnerFixture())
        {
            using var preparationToken = new CancellationTokenSource();
            var prepared = await fixture.Adapter().PrepareAsync(request, preparationToken.Token);
            preparationToken.Cancel();
            Check(fixture.OutboundObservations().Single() is
                { CancellationRequested: true, Terminal: null, Release: "unobserved" } && fixture.RemoteModel.Generations == 0,
                "preparation-token cancellation is observed while an unused remote lease still requires explicit release");
            await prepared.DisposeAsync(); await prepared.DisposeAsync();
            Check(fixture.Calls.Count(x => x == RemotePreparedProtocol.ReleaseOperation) == 1 && fixture.RemoteModel.Disposals == 1,
                "unused client preparation explicitly releases once");
            Check(fixture.OutboundObservations().Single() is
                { CancellationRequested: true, Release: "confirmed", Terminal: null, Cache: null, Budget.InputTokens: 321 } &&
                fixture.RemoteModel.Generations == 0,
                "unused cancellation and disposal retain preparation facts without a fabricated generation terminal");
        }
        await using (var fixture = new OwnerFixture())
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var cancelled = false;
            try { await fixture.Adapter().PrepareAsync(request, cancel.Token); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { cancelled = true; }
            Check(cancelled && fixture.OutboundObservations().Length == 0 && fixture.Calls.IsEmpty &&
                fixture.RemoteModel.Preparations == 0,
                "cancellation before remote preparation creates no ownership row, owner RPC or provider preparation");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.RemoteModel.Limit = true; ModelContextLimitException? error = null;
            try { await fixture.Adapter().PrepareAsync(request, default); }
            catch (ModelContextLimitException limit) { error = limit; }
            Check(error?.Metadata.InputTokens == 8191 && error.Metadata.ResponseBudget == 0 &&
                !fixture.Calls.Contains(RemotePreparedProtocol.GenerateOperation), "client exposes remote tokenizer overflow with no generated action");
            Check(fixture.OutboundObservations().Single() is
                { Release: "confirmed", Budget: null, Terminal: null, Cache: null,
                    AccountingLeaseId: null, NativeSlotId: null, LeaseId: null },
                "context-limited remote metadata never reaches the ready hook or invents a dispatchable lease or terminal");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.ThrowGeneration = true;
            await using var prepared = await fixture.Adapter().PrepareAsync(request, default);
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Unknown && !result.Error!.Contains("fixture-private", StringComparison.Ordinal) &&
                fixture.Calls.Count(x => x == RemotePreparedProtocol.GenerateOperation) == 1 && fixture.RemoteModel.Generations == 0,
                "unknown transport error is redacted, not retried, and never replaced by local inference");
            Check(fixture.OutboundObservations().Single() is
                { Release: "unobserved", Phase: "unknown",
                    Terminal: { Finish: "unknown", ProviderError: true, GeneratedTokens: null, EvaluatedTokens: null }, Cache: null },
                "an unverified generation transport records unknown observation without adopting counts or cache");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.MutateMetadata = true;
            await using var prepared = await fixture.Adapter().PrepareAsync(request, default);
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Unknown && result.Error!.Contains("exact prepared lease", StringComparison.Ordinal),
                "client rejects changed completion metadata with an authored actionable diagnostic");
            Check(fixture.OutboundObservations().Single() is
                { Release: "unobserved", Phase: "unknown",
                    Terminal: { Finish: "unknown", ProviderError: true, GeneratedTokens: null, EvaluatedTokens: null }, Cache: null },
                "malformed required terminal metadata is never captured as verified usage or cache evidence");
        }
        await using (var fixture = new OwnerFixture())
        {
            await using var prepared = await fixture.Adapter().PrepareAsync(request, default);
            var settings = fixture.Configuration.Get(); settings.Workers[0].AccessKey = new string('b', 48); fixture.Configuration.Save(settings);
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Unknown && !fixture.Calls.Contains(RemotePreparedProtocol.GenerateOperation),
                "owner-key drift rejects a frozen route before sending remote inference");
            Check(fixture.OutboundObservations().Single() is { Terminal: null, Cache: null } &&
                fixture.RemoteModel.Generations == 0,
                "pre-dispatch owner drift records no fabricated provider terminal");
        }
        await using (var fixture = new OwnerFixture())
        {
            await using var prepared = await fixture.Adapter().PrepareAsync(request, default);
            fixture.Identity = "replacement-worker";
            var result = await prepared.GenerateAsync(null, default);
            Check(result.Finish == ModelGenerationFinish.Unknown && !fixture.Calls.Contains(RemotePreparedProtocol.GenerateOperation),
                "replacement worker at the same URL cannot inherit an admitted lease");
        }
        await using (var fixture = new OwnerFixture())
        {
            var profile = new AgentProfile { Id = "lead", Name = "Lead", ModelId = binding.Id, ModelWorkerId = "gb10",
                ContextTokens = 8192, ResponseTokens = 512, FollowsModelBudgets = false, Role = "lead" };
            fixture.Store.Put("agent", profile.Id, profile);
            // Deliberately conflicting local ID proves that native effects use the admitted remote record.
            fixture.Store.Put("model", binding.Id, new ModelRecord { Id = binding.Id, Name = "Unrelated local",
                Path = @"C:\unrelated\local.gguf", MaximumContextTokens = 1024, ContextTokens = 1024 });
            var snapshot = new WorkerExecutionSnapshot.Snapshot(3, profile, "", "",
                new(binding.Id, binding.Path, binding.ChatTemplate, binding.StopSequences, 0, 1, -1, ModelWorkerId: "gb10",
                    ModelWorkerInstallationId: fixture.Identity), [], [], [], [], false,
                WorkerExecutionSnapshot.RoutingFingerprint(fixture.Configuration.Get()), [], []);
            var submission = new WorkflowSubmission("remote-fixture-run", "remote-fixture-chat", profile.Id,
                "fixture.conversation", "1", "Use the remote model.", null, WorkflowContract.EmptyObject, CerebralJson.Element(snapshot));
            await using var local = new ModelRuntime(fixture.Store, fixture.Configuration.Get, new EventHub());
            await using var configuredModels = new ConfiguredWorkflowModelProvider(local);
            var adapters = new WorkerModelAdapters(local, fixture.NewManagement(), null!, null!, configuredModels);
            var effects = new WorkerWorkflowEffects(fixture.Store, fixture.Configuration, local, new NoTools(),
                adapters.ForSubmission);
            var handler = effects.Commands.Single(x => x.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && x.Descriptor.Version == 1);
            var context = new WorkflowCommandContext("remote-fixture-effect", submission,
                CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([])));
            var completed = await handler.ExecuteAsync(context, default);
            var usage = local.UsageTotals();
            Check(completed.State == WorkflowEffectState.Succeeded && !local.IsLoaded(binding.Id) && fixture.RemoteModel.Generations == 1 &&
                fixture.RemoteModel.LastRequest!.ContextLimit == 8192,
                "native durable generation uses frozen remote budgets despite a conflicting same-ID local model");
            Check(usage.ConfirmedInputTokens == 321 && usage.ConfirmedOutputTokens == 17 &&
                  usage.ConfirmedInputComplete == true && usage.ConfirmedOutputComplete == true,
                "the production paired-worker route contributes exact terminal usage once to the owner Worker meter");
            Check(WorkerExecutionSnapshot.Read(submission.SettingsSnapshot).Model!.ModelWorkerInstallationId == fixture.Identity &&
                !submission.SettingsSnapshot.GetRawText().Contains(new string('a', 48), StringComparison.Ordinal),
                "snapshot freezes remote installation identity without owner credentials");
            var previousFingerprint = snapshot.RoutingFingerprint; var settings = fixture.Configuration.Get();
            settings.Workers[0].Url = "http://127.0.0.1:2668"; fixture.Configuration.Save(settings);
            Check(previousFingerprint != WorkerExecutionSnapshot.RoutingFingerprint(fixture.Configuration.Get()),
                "remote route change invalidates the accepted fingerprint");
            var denied = await handler.ExecuteAsync(context, default);
            Check(denied.State == WorkflowEffectState.Failed && fixture.RemoteModel.Generations == 1 && !local.IsLoaded(binding.Id) &&
                  local.UsageTotals() == usage,
                "native stale route fails before any model preparation or fallback");
            var oldLocal = snapshot with { Profile = new AgentProfile { Id = "local", ModelId = binding.Id },
                Model = new(binding.Id, @"C:\local.gguf", null, [], 0, 1, -1) };
            string LegacyLocalJson(int sourceVersion) => JsonSerializer.Serialize(
                oldLocal with { ResolvedSettingsVersion = sourceVersion },
                new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false })
                .Replace(",\"modelWorkerId\":\"local\"", "", StringComparison.Ordinal)
                .Replace(",\"modelWorkerInstallationId\":null", "", StringComparison.Ordinal);
            using var legacyDocument = JsonDocument.Parse(LegacyLocalJson(3));
            var normalizedThree = WorkerExecutionSnapshot.Read(legacyDocument.RootElement);
            Check(normalizedThree.ResolvedSettingsVersion == 5 &&
                normalizedThree.SourceSettingsVersion == 3 && normalizedThree.Model!.ModelWorkerId == "local" &&
                normalizedThree.Model.ModelWorkerInstallationId is null,
                "existing version-three local snapshots normalize to the current edition and retain their default local origin");
            using var versionTwo = JsonDocument.Parse(LegacyLocalJson(2));
            var normalizedTwo = WorkerExecutionSnapshot.Read(versionTwo.RootElement);
            Check(normalizedTwo.ResolvedSettingsVersion == 5 &&
                normalizedTwo.SourceSettingsVersion == 2 && normalizedTwo.Model!.ModelWorkerId == "local" &&
                normalizedTwo.Model.ModelWorkerInstallationId is null,
                "version-two local history normalizes in memory without changing its model origin");
            await Reject(() => Task.FromResult(WorkerExecutionSnapshot.Read(CerebralJson.Element(snapshot with { ResolvedSettingsVersion = 2 }))),
                "version-two remote profile never guesses an installation origin");
            await Reject(() => Task.FromResult(WorkerExecutionSnapshot.Read(CerebralJson.Element(oldLocal with
                { ResolvedSettingsVersion = 2, Model = oldLocal.Model! with { ModelWorkerInstallationId = "unverified-origin" } }))),
                "version-two local binding with a supplied remote origin is rejected");
            using var wrongIdentity = JsonDocument.Parse(CerebralJson.Element(snapshot with
                { Model = snapshot.Model! with { ModelWorkerInstallationId = null } }).GetRawText());
            await Reject(() => Task.FromResult(WorkerExecutionSnapshot.Read(wrongIdentity.RootElement)),
                "remote snapshots require the actual admitted installation identity");
        }
        // Quarantine and recovery are host ownership tests using deterministic owner replies.
        await using (var fixture = new OwnerFixture())
        {
            await using var first = await fixture.Adapter().PrepareAsync(request, default);
            await using var second = await fixture.Adapter(managementOverride: fixture.NewManagement()).PrepareAsync(request, default);
            Check(fixture.Calls.Count(call => call == RemotePreparedProtocol.PrepareOperation) == 2,
                "fresh management on the same live store recognizes concurrent owned preparations");
            await Reject(() => fixture.NewManagement().ExecuteAsync("workers.models.unload",
                CerebralJson.Element(new { workerId = "gb10", id = binding.Id }), default),
                "owner unload rejects matching live provider ownership before dispatch");
            Check(!fixture.Calls.Contains("models.unload"), "live-owned unload denial sends no remote unload");
            var recreated = new RemoteWorkerManagement(new Configuration(new StateStore(fixture.DirectoryPath)), _ => fixture.NewClient());
            await Reject(() => fixture.Adapter(managementOverride: recreated).PrepareAsync(request, default),
                "fresh store lifetime retains durable in-flight uncertainty instead of trusting restart");
            await first.DisposeAsync(); await second.DisposeAsync();
            await using var after = await fixture.Adapter(managementOverride: recreated).PrepareAsync(request, default);
            Check(fixture.Calls.Count(call => call == RemotePreparedProtocol.PrepareOperation) == 3,
                "verified unused releases remove exact in-flight markers for the next store lifetime");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.DropPrepareResponse = true;
            await Reject(() => fixture.Adapter().PrepareAsync(request, default),
                "lost preparation response quarantines unreturned provider ownership");
            Check(fixture.OutboundObservations().Single() is
                { Phase: "unknown", Release: "unobserved", AccountingLeaseId: null,
                    Budget: null, Terminal: null, Cache: null, NativeSlotId: null, LeaseId: null },
                "a dropped preparation response cannot manufacture ready metadata or an exact provider lease join");
            var calls = fixture.Calls.Count(call => call == RemotePreparedProtocol.PrepareOperation);
            await Reject(() => fixture.Adapter(managementOverride: fixture.NewManagement()).PrepareAsync(request, default),
                "new adapter and management cannot retry an uncertain preparation");
            Check(fixture.Calls.Count(call => call == RemotePreparedProtocol.PrepareOperation) == calls &&
                fixture.RemoteModel.Generations == 0, "uncertain preparation is never redispatched or promoted to inference");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.ThrowGeneration = true;
            await using (var prepared = await fixture.Adapter().PrepareAsync(request, default))
                Check((await prepared.GenerateAsync(null, default)).Finish == ModelGenerationFinish.Unknown,
                    "unknown generation records durable route uncertainty");
            var calls = fixture.Calls.Count(call => call == RemotePreparedProtocol.PrepareOperation);
            var changed = fixture.Configuration.Get(); changed.Workers[0].AccessKey = new string('c', 48);
            changed.Workers[0].Name = "Changed display name"; changed.Workers[0].Url = "http://127.0.0.1:2668";
            fixture.Configuration.Save(changed);
            await Reject(() => fixture.Adapter(managementOverride: fixture.NewManagement(), changedBinding: true).PrepareAsync(request, default),
                "credential display endpoint and binding changes cannot bypass same installation model quarantine");
            Check(fixture.Calls.Count(call => call == RemotePreparedProtocol.PrepareOperation) == calls,
                "blocked same-model prepare rejects promptly without a new remote preparation");
            await Reject(() => fixture.NewManagement().GenerateAsync(LegacyInput(), default),
                "explicit owner inference honors existing same-model prepared uncertainty");
            Check(!fixture.Calls.Contains("inference.generate"), "quarantined owner inference dispatches no legacy provider request");
            fixture.ThrowGeneration = false;
            await using var unrelated = await fixture.Adapter(modelId: "other-model").PrepareAsync(request with { ModelId = "other-model" }, default);
            Check(fixture.Calls.Count(call => call == RemotePreparedProtocol.PrepareOperation) == calls + 1,
                "a distinct unaffected model remains available while another route is quarantined");
            fixture.BlockUnload = true;
            var recovery = fixture.NewManagement().ExecuteAsync("workers.models.unload",
                CerebralJson.Element(new { workerId = "gb10", id = binding.Id }), default);
            await fixture.UnloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Reject(() => fixture.NewManagement().ExecuteAsync("workers.models.unload",
                CerebralJson.Element(new { workerId = "gb10", id = binding.Id }), default),
                "simultaneous management recovery rejects a second unload for the same verified worker model");
            Check(fixture.Calls.Count(call => call == "models.unload") == 1,
                "shared live recovery ownership permits exactly one remote unload dispatch");
            await Reject(() => fixture.Adapter().PrepareAsync(request, default),
                "durable recovery fence blocks preparations throughout unload and independent inspection");
            fixture.UnloadFinish.TrySetResult(); await recovery;
            await using var recovered = await fixture.Adapter().PrepareAsync(request, default);
            Check(fixture.Calls.Contains("models.inspect"),
                "explicit unload and independently verified unloaded status clear only fenced uncertainty");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.BlockLegacyGeneration = true;
            var legacy = fixture.NewManagement().GenerateAsync(LegacyInput(), default);
            await fixture.LegacyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Reject(() => fixture.NewManagement().ExecuteAsync("workers.models.unload",
                CerebralJson.Element(new { workerId = "gb10", id = binding.Id }), default),
                "live owner inference ownership rejects concurrent model recovery");
            Check(!fixture.Calls.Contains("models.unload"), "active owner inference dispatches no recovery unload");
            fixture.LegacyFinish.TrySetResult();
            Check((await legacy).GetProperty("text").GetString() == "Confirmed legacy response",
                "verified terminal legacy text completes its exact ownership reservation");
            var reopened = new StateStore(fixture.DirectoryPath);
            var management = new RemoteWorkerManagement(new Configuration(reopened), _ => fixture.NewClient());
            await using var next = await fixture.Adapter(managementOverride: management).PrepareAsync(request, default);
            Check(next.Metadata.InputTokens > 0, "terminal owner inference leaves no orphaned marker after store recreation");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.ThrowLegacyGeneration = true;
            await Reject(() => fixture.NewManagement().GenerateAsync(LegacyInput(), default),
                "lost legacy owner response stays uncertain");
            await Reject(() => fixture.Adapter().PrepareAsync(request, default),
                "legacy transport uncertainty blocks same-model prepared inference");
            Check(fixture.Calls.Count(call => call == "inference.generate") == 1,
                "legacy uncertainty never redispatches inference");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.BlockLegacyGeneration = true;
            using var cancelled = new CancellationTokenSource();
            var legacy = fixture.NewManagement().GenerateAsync(LegacyInput(), cancelled.Token);
            await fixture.LegacyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            var stopped = false;
            try { await legacy; } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "legacy client cancellation does not invent provider completion");
            await Reject(() => fixture.Adapter().PrepareAsync(request, default),
                "cancelled legacy owner inference retains same-model uncertainty");
        }
        await using (var fixture = new OwnerFixture())
        {
            using var session = await fixture.NewManagement().ConnectAsync("gb10", default);
            var management = fixture.NewManagement();
            var ids = Enumerable.Range(0, 128).Select(_ => management.BeginPreparedRoute(session, binding)).ToArray();
            await Reject(() => fixture.Adapter().PrepareAsync(request, default),
                "bounded ownership ledger refuses new preparation instead of evicting unknown ownership");
            Check(!fixture.Calls.Contains(RemotePreparedProtocol.PrepareOperation), "ledger exhaustion dispatches no provider request");
            foreach (var id in ids) management.CompletePreparedRoute(id);
            await using var recovered = await fixture.Adapter().PrepareAsync(request, default);
            Check(fixture.Calls.Count(call => call == RemotePreparedProtocol.PrepareOperation) == 1,
                "confirmed reservation cleanup restores capacity without an automatic uncertain reset");
        }
        foreach (var corruption in new[] { "duplicate", "route" })
        {
            await using var fixture = new OwnerFixture();
            await using var prepared = await fixture.Adapter().PrepareAsync(request, default);
            var ledger = fixture.Store.Get<JsonElement>("remote-prepared-routes", "quarantine");
            var node = System.Text.Json.Nodes.JsonNode.Parse(ledger.GetRawText())!;
            var entries = node["entries"]!.AsArray();
            if (corruption == "duplicate") entries.Add(entries[0]!.DeepClone());
            else entries[0]!["route"]!["endpointHash"] = "";
            fixture.Store.Put("remote-prepared-routes", "quarantine", CerebralJson.Element(node));
            await Reject(() => fixture.Adapter().PrepareAsync(request, default),
                "malformed " + corruption + " ledger ownership fails closed before provider access");
            Check(fixture.Calls.Count(call => call == RemotePreparedProtocol.PrepareOperation) == 1,
                "malformed ownership never causes a new preparation or ambiguous recovery clear");
            // Restore task-owned fixture state only so its normal confirmed release can dispose cleanly.
            fixture.Store.Put("remote-prepared-routes", "quarantine", ledger);
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.InspectState = "loaded";
            await Reject(() => fixture.NewManagement().ExecuteAsync("workers.models.unload",
                CerebralJson.Element(new { workerId = "gb10", id = binding.Id }), default),
                "unverified unload retains durable uncertainty while releasing only live recovery ownership");
            await Reject(() => fixture.Adapter().PrepareAsync(request, default),
                "failed recovery cannot admit a model preparation");
            fixture.InspectState = "unloaded";
            await fixture.NewManagement().ExecuteAsync("workers.models.unload",
                CerebralJson.Element(new { workerId = "gb10", id = binding.Id }), default);
            await using var prepared = await fixture.Adapter().PrepareAsync(request, default);
            Check(fixture.Calls.Count(call => call == "models.unload") == 2,
                "a later explicit verified recovery is available after failed inspection");
        }
        await using (var fixture = new OwnerFixture())
        {
            fixture.BlockUnload = true;
            using var cancel = new CancellationTokenSource();
            var recovery = fixture.NewManagement().ExecuteAsync("workers.models.unload",
                CerebralJson.Element(new { workerId = "gb10", id = binding.Id }), cancel.Token);
            await fixture.UnloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            var cancelled = false;
            try { await recovery; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "cancelled recovery exits its live ownership boundary");
            await Reject(() => fixture.Adapter().PrepareAsync(request, default),
                "cancelled recovery keeps its durable preparation fence");
            fixture.BlockUnload = false;
            await fixture.NewManagement().ExecuteAsync("workers.models.unload",
                CerebralJson.Element(new { workerId = "gb10", id = binding.Id }), default);
            await using var prepared = await fixture.Adapter().PrepareAsync(request, default);
            Check(fixture.Calls.Count(call => call == "models.unload") == 2,
                "explicit verified retry can recover after cancellation without trusting a restart");
        }
        Console.WriteLine("Remote prepared deterministic fixture checks: " + checks + ".");
    }

    private sealed class MeasuredAdapter : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("fixture.remote.exact", "1", ["text"], SupportsReasoning: true);
        public int Preparations, Generations, Disposals;
        public bool Limit, Block, CompleteAfterCancellation;
        public string Output = "Completed café.";
        public ModelGenerationResult? CancellationResult;
        public PromptCacheMetrics? PromptCache;
        public CancellationToken LastToken;
        public ModelGenerationRequest? LastRequest;
        public byte[]? LastImageBytes;
        public IReadOnlyList<RemotePreparedImageInput>? BorrowedImages;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); request.Validate(); Preparations++; LastRequest = request;
            var metadata = new PreparedModelMetadata("fixture-instance", Descriptor.AdapterId, Descriptor.Version, request.ModelId,
                Limit ? 8191 : 321, request.ContextLimit, Limit ? 0 : request.ResponseLimit, "fixture exact rendered-token evidence")
                { SafetyReserveTokens = 128, DesiredResponseBudget = request.ResponseLimit, ReasoningReserveTokens = request.ReasoningTokens };
            if (Limit) throw new ModelContextLimitException(metadata);
            return Task.FromResult<IPreparedModelGeneration>(new Prepared(this, metadata));
        }
        public Task<IPreparedModelGeneration> PrepareMultimodalAsync(ModelGenerationRequest request,
            IReadOnlyList<RemotePreparedImageInput> images, CancellationToken token)
        {
            BorrowedImages = images;
            LastImageBytes = images.Single().Data.ToArray();
            return PrepareAsync(request, token);
        }
        private sealed class Prepared(MeasuredAdapter owner, PreparedModelMetadata metadata) : IPreparedModelGeneration
        {
            public PreparedModelMetadata Metadata => metadata;
            private int disposed;
            public async Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            {
                owner.Generations++; owner.LastToken = token; owner.Started.TrySetResult();
                if (owner.Block && owner.CancellationResult is { } cancelled)
                {
                    try { await owner.Finish.Task.WaitAsync(token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return cancelled; }
                }
                else if (owner.Block)
                {
                    await owner.Finish.Task;
                    if (!owner.CompleteAfterCancellation) token.ThrowIfCancellationRequested();
                }
                return new(ModelGenerationFinish.Stop, owner.Output, 17, metadata.InputTokens) { PromptCache = owner.PromptCache };
            }
            public ValueTask DisposeAsync()
            { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Disposals++; return ValueTask.CompletedTask; }
        }
    }

    private sealed class OwnerFixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "PegasusCerebral.RemotePrepared-" + Guid.NewGuid().ToString("N"));
        public StateStore Store { get; }
        public Configuration Configuration { get; }
        public MeasuredAdapter RemoteModel { get; } = new();
        public string Identity = "fixture-worker-installation";
        public PromptCacheMetrics? TamperPromptCache;
        public string InspectState = "unloaded";
        public bool DropPrepareResponse, DropReleaseResponse, BlockUnload, BlockLegacyGeneration, ThrowLegacyGeneration;
        public TaskCompletionSource LegacyEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LegacyFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UnloadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UnloadFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ThrowGeneration, MutateMetadata, RemoveImageCapability, DropGenerationResponse,
            FinishGenerationOnTransportCancellation, OmitPromptCache, LegacyCacheFieldRemoved;
        public ConcurrentQueue<string> Calls { get; } = new();
        public TaskCompletionSource ReleaseObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TransportCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RemotePreparedInference server;
        private readonly RemoteWorkerManagement management;
        public OwnerFixture()
        {
            Directory.CreateDirectory(DirectoryPath); Store = new(DirectoryPath); Configuration = new(Store);
            var settings = Configuration.Get(); settings.Coordinator = true;
            settings.ModelDirectory = Path.Combine(DirectoryPath, "models"); settings.ProjectsDirectory = Path.Combine(DirectoryPath, "projects");
            settings.SharedDirectory = Path.Combine(DirectoryPath, "shared"); settings.ToolsDirectory = Path.Combine(DirectoryPath, "tools");
            settings.Workers = [new() { Id = "gb10", Name = "GB10", Url = "http://127.0.0.1:2667", AccessKey = new string('a', 48) }];
            Configuration.Save(settings);
            server = new(RemoteModel, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                prepareMultimodal: RemoteModel.PrepareMultimodalAsync);
            management = new(Configuration, _ => new OwnerClient(this));
        }
        public InferenceCaptureRow[] OutboundObservations() => management.Accounting.Observation.Snapshot().Rows
            .Where(row => row.Kind == nameof(ModelOwnershipKind.OutboundPreparation)).ToArray();
        public IRemoteWorkerOwnerClient NewClient() => new OwnerClient(this);
        public RemoteWorkerManagement NewManagement() => new(Configuration, _ => NewClient());
        public RemoteModelRuntimeAdapter Adapter(RemotePreparedClientTiming? timing = null,
            RemoteWorkerManagement? managementOverride = null, string modelId = "remote-model", bool changedBinding = false)
        {
            var model = new RemotePreparedModelBinding(modelId, "/srv/cerebral/models/model.gguf", changedBinding ? "changed-template" : "alpaca", [], 0, 1, -1);
            var host = managementOverride ?? management;
            return timing is null ? new(host, "gb10", model, Identity) : new(host, "gb10", model, Identity, timing);
        }
        private sealed class OwnerClient(OwnerFixture fixture) : IRemoteWorkerOwnerClient
        {
            public async Task<T> GetAsync<T>(string operation, object? arguments = null, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested(); fixture.Calls.Enqueue(operation);
                object result;
                switch (operation)
                {
                    case "health":
                        result = new HostInfo("Pegasus Cerebral", "fixture", 1, "GB10", "Arm64", "Linux", false,
                            "/srv/cerebral", InstallationId: fixture.Identity); break;
                    case "worker.management.status":
                        result = new WorkerManagementStatus(fixture.Identity, "assigned", fixture.Configuration.Get().InstallationId, "Fixture", null); break;
                    case RemotePreparedProtocol.PrepareOperation:
                        var response = CerebralJson.Read<RemotePreparedResponse>((await fixture.server.PrepareAsync(CerebralJson.Element(arguments!), cancellationToken)).Data);
                        if (fixture.DropPrepareResponse) throw new IOException("Fixture dropped an admitted prepare response.");
                        result = fixture.RemoveImageCapability
                            ? response with { Adapter = response.Adapter with { ContentCapabilities = ["text"] } }
                            : response;
                        break;
                    case "inference.generate":
                        fixture.LegacyEntered.TrySetResult();
                        if (fixture.BlockLegacyGeneration) await fixture.LegacyFinish.Task.WaitAsync(cancellationToken);
                        if (fixture.ThrowLegacyGeneration) throw new IOException("Fixture dropped a legacy terminal response.");
                        result = CerebralJson.Element(new { text = "Confirmed legacy response" }); break;
                    case "models.unload":
                        fixture.UnloadEntered.TrySetResult();
                        if (fixture.BlockUnload) await fixture.UnloadFinish.Task.WaitAsync(cancellationToken);
                        result = CerebralJson.Element(new { unloaded = true }); break;
                    case "models.inspect":
                        result = new ModelRecord { Id = "remote-model", State = fixture.InspectState }; break;
                    case RemotePreparedProtocol.GenerateOperation:
                        if (fixture.ThrowGeneration) throw new IOException("fixture-private-credential");
                        using (fixture.FinishGenerationOnTransportCancellation ? cancellationToken.Register(() =>
                        {
                            fixture.TransportCancellationObserved.TrySetResult(); fixture.RemoteModel.Finish.TrySetResult();
                        }) : default)
                        {
                            var generated = CerebralJson.Read<RemoteGeneratedResponse>((await fixture.server.GenerateAsync(CerebralJson.Element(arguments!), cancellationToken)).Data);
                            if (fixture.DropGenerationResponse) throw new IOException("Fixture dropped the terminal response.");
                            if (fixture.TamperPromptCache is { } telemetry)
                                generated = generated with { Result = generated.Result with { PromptCache = telemetry } };
                            result = fixture.MutateMetadata ? generated with { Metadata = generated.Metadata with { InputTokens = generated.Metadata.InputTokens + 1 } } : generated;
                        }
                        break;
                    case RemotePreparedProtocol.ReleaseOperation:
                        result = CerebralJson.Read<RemoteReleaseResponse>((await fixture.server.ReleaseAsync(CerebralJson.Element(arguments!), cancellationToken)).Data);
                        if (fixture.DropReleaseResponse) throw new IOException("Fixture dropped a release response.");
                        fixture.ReleaseObserved.TrySetResult(); break;
                    default: throw new InvalidOperationException("Unexpected fixture operation: " + operation);
                }
                if (fixture.OmitPromptCache && result is RemoteGeneratedResponse)
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(result, CerebralJson.Options))!;
                    fixture.LegacyCacheFieldRemoved = node["result"]!.AsObject().Remove("promptCache");
                    using var legacy = JsonDocument.Parse(node.ToJsonString());
                    return CerebralJson.Read<T>(legacy.RootElement);
                }
                return CerebralJson.Read<T>(CerebralJson.Element(result));
            }
            public void Dispose() { }
        }
        public async ValueTask DisposeAsync()
        {
            await server.DisposeAsync(); SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(DirectoryPath);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture path escaped temporary storage.");
            Directory.Delete(full, true);
        }
    }

    private sealed class NoTools : IToolDispatcher
    {
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token) =>
            throw new InvalidOperationException("No tool dispatch belongs to the prepared inference fixture.");
    }
}
