using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Resources;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Resources;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Real SQLite originals and derivative files; only the archive PAO operation is injected.</summary>
public static class AttachmentCleanupChecks
{
    public static async Task RunAsync()
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(temporary, "cerebral-attachment-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        var extracts = 0;
        var outside = Path.Combine(root, "outside-sentinel");
        void Check(bool value, string description)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + description);
            checks++;
            Console.WriteLine("PASS: " + description);
        }
        try
        {
            var state = new StateStore(root);
            var profile = new AgentProfile { Id = "cleanup-lead", Name = "Cleanup lead" };
            state.Put("agent", profile.Id, profile);
            var originals = new AttachmentStore(new EventJournal(Path.Combine(root, "engine.db")));
            var access = new AttachmentAccessContext(profile.Id, "cleanup-chat");
            var first = await ImportZipAsync(originals, access, "cleanup-first");
            var second = await ImportZipAsync(originals, access, "cleanup-second");
            var third = await ImportZipAsync(originals, access, "cleanup-third");
            var fourth = await ImportZipAsync(originals, access, "cleanup-dispose");
            var extractReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseExtract = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockNextExtract = false;

            async Task<JsonElement> Pao(JsonElement arguments, string? _, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = arguments.GetProperty("op").GetString();
                if (operation == "7z_list")
                    return CerebralJson.Element(new
                    {
                        ok = true, state = "done", data = new { count = 1, expanded_bytes = 6,
                            entries = new[] { new { path = "hello.txt", directory = false, size = 6 } }, truncated = false }
                    });
                if (operation == "7z_test")
                    return CerebralJson.Element(new { ok = true, state = "done" });
                if (operation == "7z_extract")
                {
                    if (blockNextExtract)
                    {
                        blockNextExtract = false;
                        extractReached.SetResult();
                        await releaseExtract.Task.WaitAsync(cancellationToken);
                    }
                    extracts++;
                    var destination = arguments.GetProperty("dst").GetString()!;
                    Directory.CreateDirectory(destination);
                    File.WriteAllText(Path.Combine(destination, "hello.txt"), "hello!", new UTF8Encoding(false));
                    return CerebralJson.Element(new { ok = true, state = "done" });
                }
                throw new InvalidOperationException("Unexpected injected archive operation.");
            }

            var artifactRoot = Path.Combine(root, "artifacts");
            // One source copy plus a six-byte derivative fits; a second derivative does not.
            var manualClock = new ManualTimeProvider();
            using var artifacts = new AttachmentArtifacts(originals, state, artifactRoot, Pao,
                clock: manualClock, cacheLimitBytes: first.Length + 10);
            var firstReady = await artifacts.ExtractAsync(access, first.Id, first.Revision, CancellationToken.None);
            var firstFile = Path.Combine(artifactRoot, first.Id, "contents", "hello.txt");
            Check(firstReady.State == "ready" && File.Exists(firstFile) && extracts == 1,
                "first bounded derivative is retained");

            using (var held = new FileStream(firstFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                state.Put("attachment-artifact", Key(access, first), firstReady with
                { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
                var pending = artifacts.Status(access, first.Id, first.Revision);
                Check(pending.State == "expired" && pending.ExpandedBytes == 6 && pending.Entries.Count == 0 &&
                    pending.Error?.Contains("cleanup pending", StringComparison.Ordinal) == true && File.Exists(firstFile),
                    "locked deletion leaves an expired, accounted, visible cleanup receipt");
                var refused = false;
                try { _ = await artifacts.ExtractAsync(access, second.Id, second.Revision, CancellationToken.None); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("cache is full", StringComparison.Ordinal))
                { refused = true; }
                Check(refused && extracts == 1 && File.Exists(firstFile),
                    "physical residue blocks a second extraction before dispatch");
                Check(artifacts.Status(access, first.Id, first.Revision).ExpandedBytes == 6,
                    "repeated cleanup failure preserves accounted bytes");
            }

            var cleared = artifacts.Status(access, first.Id, first.Revision);
            Check(cleared.State == "expired" && cleared.ExpandedBytes == 0 && cleared.Error is null &&
                !File.Exists(firstFile), "later access confirms physical cleanup and clears the error");
            var secondReady = await artifacts.ExtractAsync(access, second.Id, second.Revision, CancellationToken.None);
            Check(secondReady.State == "ready" && extracts == 2,
                "space released by confirmed cleanup admits the next derivative");
            Check(originals.Describe(access, first.Id, first.Revision).Length == first.Length,
                "immutable SQLite original survives derivative expiry");

            Directory.CreateDirectory(outside);
            var sentinel = Path.Combine(outside, "keep.txt");
            File.WriteAllText(sentinel, "keep");
            // This signed-in Windows session cannot create an OS reparse point. Inject the
            // attribute on a real derivative entry into the same deletion guard instead.
            var marker = Path.Combine(artifactRoot, second.Id, "contents", "link-candidate");
            Directory.CreateDirectory(marker);
            var derivative = Path.Combine(artifactRoot, second.Id);
            var deleted = AttachmentArtifacts.TryDeleteTree(derivative, out var diagnostic, path =>
                string.Equals(path, marker, StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.Directory | FileAttributes.ReparsePoint : File.GetAttributes(path));
            Check(!deleted && diagnostic?.Contains("reparse point", StringComparison.Ordinal) == true &&
                File.Exists(Path.Combine(derivative, "contents", "hello.txt")) && File.Exists(sentinel),
                "injected reparse attribute refuses deletion before touching derivative or outside data");
            Directory.Delete(marker);
            state.Put("attachment-artifact", Key(access, second), secondReady with
            { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
            var linkedCleared = artifacts.Status(access, second.Id, second.Revision);
            Check(linkedCleared.State == "expired" && linkedCleared.ExpandedBytes == 0 && linkedCleared.Error is null &&
                File.Exists(sentinel), "time expiry deletes only the derivative after the guard clears");

            var thirdReady = await artifacts.ExtractAsync(access, third.Id, third.Revision, CancellationToken.None);
            Check(thirdReady.State == "ready" && extracts == 3,
                "confirmed expiry makes room for a later derivative");

            blockNextExtract = true;
            var activeExtraction = artifacts.ExtractAsync(access, fourth.Id, fourth.Revision, CancellationToken.None);
            await extractReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queuedPrune = manualClock.Timer.FireAsync();
            await manualClock.Timer.CallbackStarted.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(20);
            Check(!queuedPrune.IsCompleted && !activeExtraction.IsCompleted,
                "timer callback waits while an accepted extraction owns the cache gate");
            artifacts.Dispose();
            releaseExtract.SetResult();
            var afterDispose = await activeExtraction.WaitAsync(TimeSpan.FromSeconds(5));
            await queuedPrune.WaitAsync(TimeSpan.FromSeconds(5));
            Check(afterDispose.State == "ready" && extracts == 4,
                "accepted extraction and queued prune release safely after disposal");
            var newCallRejected = false;
            try { _ = artifacts.Status(access, fourth.Id, fourth.Revision); }
            catch (ObjectDisposedException) { newCallRejected = true; }
            Check(newCallRejected, "disposed artifact service rejects a new request");

            Console.WriteLine($"Attachment cleanup: {checks} checks passed (real files/SQLite plus injected reparse attribute); no live PAO or Worker calls.");
        }
        finally
        {
            var canonical = Path.GetFullPath(root);
            if (!canonical.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(canonical).StartsWith("cerebral-attachment-cleanup-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its exact temporary root.");
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(canonical)) Directory.Delete(canonical, recursive: true); }
            catch (IOException) { Console.WriteLine("Fixture cleanup deferred until the SQLite handle closes."); }
        }
    }

    private static string Key(AttachmentAccessContext access, AttachmentReference reference) =>
        access.ProfileId + "|" + access.ConversationId + "|" + reference.Id + "|" + reference.Revision;

    private static async Task<AttachmentReference> ImportZipAsync(AttachmentStore store,
        AttachmentAccessContext access, string requestId)
    {
        await using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var member = archive.CreateEntry("hello.txt");
            await using var writer = member.Open();
            await writer.WriteAsync("hello!"u8.ToArray());
        }
        stream.Position = 0;
        return await store.ImportAsync(access, requestId, "fixture.zip", stream);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        internal ManualTimer Timer { get; private set; } = null!;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer = new ManualTimer(callback, state);
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private readonly TaskCompletionSource callbackStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task CallbackStarted => callbackStarted.Task;
        internal Task FireAsync() => Task.Run(() => { callbackStarted.TrySetResult(); callback(state); });
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
