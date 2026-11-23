using System.Text;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Resources;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Resources;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static partial class RemotePreparedInferenceTests
{
    /// <summary>Actual Worker wrapper composition with deterministic transport and isolated SQLite; no network or model.</summary>
    public static async Task RunResourceUsageOwnershipAsync()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + name);
            checks++; Console.WriteLine("PASS: " + name);
        }
        async Task<T> Error<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T error) { return error; }
            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }
        AttachmentStore Resources(OwnerFixture fixture) =>
            new(new EventJournal(Path.Combine(fixture.DirectoryPath, "resources.db")));
        ModelGenerationRequest Request(params ContentPart[] content) =>
            new("remote-model", [new("user", content)], 8192, 512);
        IModelRuntimeAdapter Compose(ModelRuntime owner, IModelRuntimeAdapter inner, AttachmentStore attachments,
            Func<AttachmentReference, int, CancellationToken, Task<IReadOnlyList<AttachmentArtifactText>>>? archive = null) =>
            owner.TrackExternalUsage(new LlamaResourceModelAdapter(owner.TrackExternalUsage(inner),
                attachments, new("lead", "chat"), archive));
        void Empty(ModelRuntime runtime, string name) =>
            Check(runtime.Accounting.Snapshot() is { UnknownOwnership: 0, Entries.Length: 0 }, name);

        // This first case failed against unchanged product code: zero provider calls, one outer Unknown.
        await using (var fixture = new OwnerFixture())
        {
            await using var runtime = new ModelRuntime(fixture.Store, fixture.Configuration.Get, new EventHub());
            var composed = Compose(runtime, fixture.Adapter(), Resources(fixture));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var observed = await Error<OperationCanceledException>(() =>
                composed.PrepareAsync(Request(new TextContentPart("Cancel before resolving input.")), cancellation.Token));
            Check(observed.CancellationToken == cancellation.Token && fixture.Calls.IsEmpty &&
                fixture.RemoteModel.Preparations == 0 && fixture.RemoteModel.Generations == 0,
                "production resource/usage composition preserves pre-dispatch cancellation and zero provider calls");
            var snapshot = runtime.Accounting.Snapshot();
            Console.WriteLine($"Resource cancellation accounting: entries={snapshot.Entries.Length}; unknown={snapshot.UnknownOwnership}.");
            Empty(runtime, "cancellation before resource delegation leaves no outer ownership or Unknown entry");
        }
        await using (var fixture = new OwnerFixture())
        {
            await using var runtime = new ModelRuntime(fixture.Store, fixture.Configuration.Get, new EventHub());
            var attachments = Resources(fixture);
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("bounded private scope fixture"));
            var reference = await attachments.ImportAsync(new("lead", "foreign-chat"), "foreign", "notes.txt", bytes);
            _ = await Error<KeyNotFoundException>(() => Compose(runtime, fixture.Adapter(), attachments)
                .PrepareAsync(Request(new FileContentPart(reference)), default));
            Check(fixture.Calls.IsEmpty && fixture.RemoteModel.Preparations == 0,
                "foreign conversation attachment rejection stays scoped and occurs before owner RPC");
            Empty(runtime, "ordinary scoped resolution failure creates no false provider ownership");
        }
        await using (var fixture = new OwnerFixture())
        {
            await using var runtime = new ModelRuntime(fixture.Store, fixture.Configuration.Get, new EventHub());
            var attachments = Resources(fixture);
            using var bytes = new MemoryStream([1, 2, 3]);
            var reference = await attachments.ImportAsync(new("lead", "chat"), "archive", "notes.zip", bytes);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var exactError = new OperationCanceledException("Fixture archive resolution cancelled.", cancellation.Token);
            async Task<IReadOnlyList<AttachmentArtifactText>> Archive(AttachmentReference _, int maximum, CancellationToken token)
            {
                Check(maximum is > 0 and <= AttachmentStore.MaximumTextBytes, "archive resolver receives the existing bounded byte allowance");
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { throw exactError; }
                return [];
            }
            var preparation = Compose(runtime, fixture.Adapter(), attachments, Archive)
                .PrepareAsync(Request(new FileContentPart(reference)), cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            var observed = await Error<OperationCanceledException>(() => preparation);
            Check(ReferenceEquals(observed, exactError) && fixture.Calls.IsEmpty && fixture.RemoteModel.Preparations == 0,
                "cancellation during awaited archive resolution preserves the exact exception without provider dispatch");
            Empty(runtime, "asynchronous resource cancellation leaves no false outer ownership");
        }
        await using (var fixture = new OwnerFixture())
        {
            await using var runtime = new ModelRuntime(fixture.Store, fixture.Configuration.Get, new EventHub());
            var attachments = Resources(fixture);
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("exact resolved attachment"));
            var reference = await attachments.ImportAsync(new("lead", "chat"), "text", "notes.txt", bytes);
            var resource = new LlamaResourceModelAdapter(runtime.TrackExternalUsage(fixture.Adapter()), attachments, new("lead", "chat"));
            var composed = runtime.TrackExternalUsage(resource);
            Check(ReferenceEquals(composed, resource) && ReferenceEquals(runtime.TrackExternalUsage(composed), resource),
                "only the concrete same-owner tracked resource wrapper passes through with its resolver intact");
            var prepared = await composed.PrepareAsync(Request(new FileContentPart(reference)), default);
            Check(runtime.Accounting.Snapshot() is { UnknownOwnership: 0, Entries.Length: 1 } &&
                fixture.RemoteModel.LastRequest!.Messages.SelectMany(x => x.Content).OfType<TextContentPart>()
                    .Any(x => x.Text.Contains("exact resolved attachment", StringComparison.Ordinal)),
                "successful preparation resolves scoped text and retains exactly the real inner owner");
            var result = await prepared.GenerateAsync(null, default);
            _ = await Error<InvalidOperationException>(() => prepared.GenerateAsync(null, default));
            await prepared.DisposeAsync(); await prepared.DisposeAsync();
            Check(result.Finish == ModelGenerationFinish.Stop && fixture.RemoteModel.Generations == 1 &&
                fixture.RemoteModel.Disposals == 1 && fixture.Calls.Count(x => x == RemotePreparedProtocol.GenerateOperation) == 1,
                "same-owner resource success preserves one-use generation and exact idempotent disposal");
            Check(runtime.UsageTotals() is { ConfirmedInputTokens: 321, ConfirmedOutputTokens: 17,
                ConfirmedInputComplete: true, ConfirmedOutputComplete: true },
                "the original runtime meter counts the exact terminal usage once");
            Empty(runtime, "confirmed successful remote retirement leaves no outer or inner ownership");
        }
        await using (var fixture = new OwnerFixture())
        {
            await using var runtime = new ModelRuntime(fixture.Store, fixture.Configuration.Get, new EventHub());
            using var cancellation = new CancellationTokenSource();
            var prepared = await Compose(runtime, fixture.Adapter(), Resources(fixture))
                .PrepareAsync(Request(new TextContentPart("Unused preparation.")), cancellation.Token);
            cancellation.Cancel(); await prepared.DisposeAsync(); await prepared.DisposeAsync();
            Check(fixture.RemoteModel.Generations == 0 && fixture.RemoteModel.Disposals == 1 &&
                fixture.Calls.Count(x => x == RemotePreparedProtocol.ReleaseOperation) == 1 &&
                fixture.OutboundObservations().Single() is { CancellationRequested: true, Release: "confirmed", Terminal: null },
                "cancellation after successful preparation still releases the known unused lease exactly once");
            Check(runtime.UsageTotals() is { ConfirmedInputTokens: 0, ConfirmedOutputTokens: 0,
                ConfirmedInputComplete: true, ConfirmedOutputComplete: true }, "unused cancellation does not invent generation usage");
            Empty(runtime, "confirmed unused cancellation retires the actual remote owner");
        }
        foreach (var lostRelease in new[] { false, true })
        {
            await using var fixture = new OwnerFixture { RemoveImageCapability = true, DropReleaseResponse = lostRelease };
            await using var runtime = new ModelRuntime(fixture.Store, fixture.Configuration.Get, new EventHub());
            var attachments = Resources(fixture);
            using var bytes = new MemoryStream(ResourceModelTests.Image());
            var reference = await attachments.ImportAsync(new("lead", "chat"), "image", "fixture.png", bytes);
            var composed = Compose(runtime, fixture.Adapter(), attachments);
            if (lostRelease)
                _ = await Error<RemotePreparedOwnershipUnknownException>(() => composed.PrepareAsync(Request(new ImageContentPart(reference)), default));
            else
                _ = await Error<RemoteModelProtocolException>(() => composed.PrepareAsync(Request(new ImageContentPart(reference)), default));
            Check(fixture.RemoteModel.Preparations == 1 && fixture.RemoteModel.Generations == 0 &&
                fixture.RemoteModel.Disposals == 1 && fixture.Calls.Count(x => x == RemotePreparedProtocol.ReleaseOperation) == 1,
                "capability rejection retains actual image resolution and attempts the exact known unused lease release once");
            if (lostRelease)
            {
                Check(runtime.Accounting.Snapshot() is { UnknownOwnership: 1, Entries.Length: 1 } &&
                    fixture.OutboundObservations().Single() is { Phase: "unknown", Release: "unobserved", Budget: null, Terminal: null },
                    "an unconfirmed release retains the real inner Unknown without a redundant outer Unknown");
                var calls = fixture.Calls.Count(x => x == RemotePreparedProtocol.PrepareOperation);
                _ = await Error<RemoteModelProtocolException>(() => fixture.Adapter(managementOverride: fixture.NewManagement())
                    .PrepareAsync(Request(new TextContentPart("No retry.")), default));
                Check(fixture.Calls.Count(x => x == RemotePreparedProtocol.PrepareOperation) == calls,
                    "unconfirmed release preserves durable route quarantine across a new management instance");
            }
            else
            {
                Empty(runtime, "known capability rejection with confirmed release leaves no false outer Unknown");
                Check(fixture.OutboundObservations().Single() is { Release: "confirmed", Budget: null, Terminal: null, LeaseId: null },
                    "rejected remote capability publishes confirmed retirement without a ready budget or terminal");
            }
        }
        await using (var fixture = new OwnerFixture { DropPrepareResponse = true })
        {
            await using var runtime = new ModelRuntime(fixture.Store, fixture.Configuration.Get, new EventHub());
            _ = await Error<RemotePreparedOwnershipUnknownException>(() => Compose(runtime, fixture.Adapter(), Resources(fixture))
                .PrepareAsync(Request(new TextContentPart("Lost preparation response.")), default));
            Check(runtime.Accounting.Snapshot() is { UnknownOwnership: 1, Entries.Length: 1 } &&
                fixture.OutboundObservations().Single() is { Phase: "unknown", Release: "unobserved", Budget: null, LeaseId: null },
                "a lost prepare response preserves the exact inner uncertainty and publishes no fictional known lease");
            var calls = fixture.Calls.Count(x => x == RemotePreparedProtocol.PrepareOperation);
            _ = await Error<RemoteModelProtocolException>(() => fixture.Adapter(managementOverride: fixture.NewManagement())
                .PrepareAsync(Request(new TextContentPart("No redispatch.")), default));
            Check(fixture.Calls.Count(x => x == RemotePreparedProtocol.PrepareOperation) == calls && fixture.RemoteModel.Generations == 0,
                "lost preparation response retains durable quarantine without redispatch or generation");
        }
        foreach (var otherOwner in new[] { false, true })
        {
            await using var fixture = new OwnerFixture();
            await using var runtime = new ModelRuntime(fixture.Store, fixture.Configuration.Get, new EventHub());
            var otherStore = new StateStore(Path.Combine(fixture.DirectoryPath, "other-owner"));
            await using var other = new ModelRuntime(otherStore, () => new HostSettings(), new EventHub());
            var inner = otherOwner ? other.TrackExternalUsage(new MeasuredAdapter()) : new MeasuredAdapter();
            var resource = new LlamaResourceModelAdapter(inner, Resources(fixture), new("lead", "chat"));
            var composed = runtime.TrackExternalUsage(resource);
            Check(!ReferenceEquals(composed, resource), otherOwner
                ? "a resource wrapper tracked by another runtime is not recognized as this owner"
                : "an untracked resource adapter is not claimed to have an existing usage owner");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            _ = await Error<OperationCanceledException>(() => composed.PrepareAsync(Request(new TextContentPart("Control.")), cancellation.Token));
            Check(runtime.Accounting.Snapshot() is { UnknownOwnership: 1, Entries.Length: 1 } &&
                other.Accounting.Snapshot().Entries.Length == 0,
                "other-owner and untracked controls retain the existing generic uncertainty contract without touching the foreign owner");
        }
        await using (var fixture = new OwnerFixture())
        {
            await using var runtime = new ModelRuntime(fixture.Store, fixture.Configuration.Get, new EventHub());
            var failure = new IOException("Fixture generic provider preparation uncertainty.");
            var provider = new ResourceUsageThrowingAdapter(failure);
            var observed = await Error<IOException>(() => Compose(runtime, provider, Resources(fixture))
                .PrepareAsync(Request(new TextContentPart("Generic provider failure.")), default));
            Check(ReferenceEquals(observed, failure) && provider.Preparations == 1 &&
                runtime.Accounting.Snapshot() is { UnknownOwnership: 1, Entries.Length: 1 },
                "delegated generic provider failure preserves its exception and inner Unknown catch despite descriptor strings");
        }
        Check(typeof(LlamaResourceModelAdapter).IsSealed, "transparent resource recognition is restricted to the sealed concrete resolver type");
        Console.WriteLine($"Resource/usage ownership: {checks} deterministic composition/SQLite checks passed.");
    }

    private sealed class ResourceUsageThrowingAdapter(Exception failure) : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("cerebral.remote.prepared", "2.0.0", ["text"]);
        public int Preparations;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        { Preparations++; return Task.FromException<IPreparedModelGeneration>(failure); }
    }

}
