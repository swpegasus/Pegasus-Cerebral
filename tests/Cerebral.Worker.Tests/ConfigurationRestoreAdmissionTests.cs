using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class ConfigurationRestoreAdmissionTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-restore-admission-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        Exception? failure = null;
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("FAIL: " + label);
            checks++;
        }

        try
        {
            var store = new StateStore(root);
            var configuration = new Configuration(store);
            var host = configuration.Get();
            var events = new EventHub();
            await using var runtime = new ModelRuntime(store, configuration.Get, events);
            await using var tools = new ToolDispatcher(configuration.Get);
            using var scheduler = new JobScheduler(store, configuration, new NoExecutor(), runtime, events, NullLogger<JobScheduler>.Instance);
            var commands = new CommandDispatcher(store, configuration, runtime, scheduler, tools, events);

            var emptyPreferences = await commands.ExecuteAsync(new("interface.preferences.get", CerebralJson.Element(new { })));
            Check(emptyPreferences.Success && CerebralJson.Read<InterfacePreferences>(emptyPreferences.Data).Revision == 0,
                "owner reads an empty typed portable interface preference record");
            var savedPreferences = await commands.ExecuteAsync(new("interface.preferences.set",
                CerebralJson.Element(new { name = InterfacePreferencePolicy.SelectedSurface, value = "tools" })));
            Check(savedPreferences.Success && CerebralJson.Read<InterfacePreferences>(savedPreferences.Data) is
                { Revision: 1, SelectedSurface: "tools" },
                "owner field-level patch persists one portable interface preference without a file side channel");
            var malformedPreference = await commands.ExecuteAsync(new("interface.preferences.set",
                CerebralJson.Element(new { name = InterfacePreferencePolicy.SelectedSurface, value = "unknown" })));
            Check(!malformedPreference.Success && new InterfacePreferencesStore(store).Get() is
                { Revision: 1, SelectedSurface: "tools" },
                "invalid interface preference patches fail without changing the saved record");
            using var duplicatePreferenceDocument = JsonDocument.Parse(
                "{\"name\":\"selectedSurface\",\"name\":\"selectedSurface\",\"value\":\"chat\"}");
            var duplicatePreference = await commands.ExecuteAsync(new("interface.preferences.set",
                duplicatePreferenceDocument.RootElement.Clone()));
            Check(!duplicatePreference.Success && new InterfacePreferencesStore(store).Get() is
                { Revision: 1, SelectedSurface: "tools" },
                "duplicate interface preference fields are rejected without changing the saved record");
            store.Put("agent", "administrator", new AgentProfile
            {
                Id = "administrator", Name = "Administrator", Capabilities = ["administration"]
            });
            var agentPreference = await commands.ExecuteAsync(new("interface.preferences.get", CerebralJson.Element(new { }), "administrator"));
            Check(!agentPreference.Success && agentPreference.Error?.Contains("owner interface", StringComparison.OrdinalIgnoreCase) == true,
                "even an administrative agent cannot read or mutate owner interface preferences");
            store.Delete("agent", "administrator");

            var exported = await commands.ExecuteAsync(new("configuration.backup.export", CerebralJson.Element(new { })));
            Check(exported.Success, "owner can export the fixture configuration");
            var document = CerebralJson.Read<ConfigurationBackupExport>(exported.Data).Document;
            var modelPath = Path.Combine(host.ModelDirectory, "incoming.gguf");
            var restoredQueueCapacity = host.QueueCapacity == 1000 ? 999 : host.QueueCapacity + 1;
            document = document with
            {
                Host = document.Host with { QueueCapacity = restoredQueueCapacity },
                Models = [new() { Id = "incoming", Name = "Restored registration", Path = modelPath }]
            };
            var previewResult = await commands.ExecuteAsync(new("configuration.restore.preview",
                CerebralJson.Element(new ConfigurationRestorePreviewRequest(document))));
            Check(previewResult.Success, "preview admits an unloaded registration before a transfer begins");
            var preview = CerebralJson.Read<ConfigurationRestorePreview>(previewResult.Data);
            var apply = new CommandRequest("configuration.restore.apply",
                CerebralJson.Element(new ConfigurationRestoreApplyRequest(preview.PreviewId, preview.ExpectedHostRevision, true)));
            var before = Saved(store);

            // Exercise the real transfer entry point: NewModel has captured this same ID and the
            // partial file is open, but no registration has been persisted when input is paused.
            using var uploadCancellation = new CancellationTokenSource();
            await using var input = new BlockedUploadStream();
            var upload = commands.ReceiveModelAsync(CerebralJson.Element(new { id = "incoming", name = "Earlier transfer" }),
                input, null, uploadCancellation.Token);
            try
            {
                await input.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                Check(File.Exists(modelPath + ".partial") && !upload.IsCompleted && store.Get<ModelRecord>("model", "incoming") is null,
                    "actual model transfer holds an open partial file before registration");
                var blocked = await commands.ExecuteAsync(apply).WaitAsync(TimeSpan.FromSeconds(3));
                Check(!blocked.Success && blocked.Error is { } error && error.Contains("model file operation", StringComparison.OrdinalIgnoreCase),
                    "restore promptly reports the active model transfer instead of waiting for network input");
                Check(Saved(store) == before && !upload.IsCompleted,
                    "denied restore leaves host, agents and model registrations unchanged while upload remains active");
            }
            finally
            {
                uploadCancellation.Cancel();
                try { await upload.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (uploadCancellation.IsCancellationRequested) { }
            }

            Check(upload.IsCanceled && !File.Exists(modelPath + ".partial") && !File.Exists(modelPath) && Saved(store) == before,
                "cancelled transfer releases its admission gate and removes the partial payload without persisting a stale model");
            var applied = await commands.ExecuteAsync(apply).WaitAsync(TimeSpan.FromSeconds(3));
            Check(applied.Success, "the same reviewed preview remains usable after the conflicting upload is cancelled");
            var restored = store.Get<ModelRecord>("model", "incoming");
            Check(configuration.Get().QueueCapacity == restoredQueueCapacity && restored is { Name: "Restored registration", State: "unloaded" } &&
                restored.Path == modelPath && !File.Exists(modelPath),
                "restore commits the intended registration and host preferences without executing or creating a model payload");
            Check(CerebralJson.Element(runtime.Diagnostics()).GetArrayLength() == 0 && scheduler.Queue().Count == 0,
                "admission and restore never start a model or agent assignment");
            Console.WriteLine($"Configuration restore admission: {checks} assertions passed through the real dispatcher, paused upload stream and SQLite store. No inference, network or installed service calls.");
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(root).StartsWith("cerebral-restore-admission-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact restore admission fixture directory.");
            try { Directory.Delete(root, recursive: true); }
            catch (Exception cleanupError) when (failure is not null)
            { throw new AggregateException("Restore admission fixture failed and cleanup also failed.", failure, cleanupError); }
        }
    }

    private static string Saved(StateStore store) => JsonSerializer.Serialize(new
    {
        host = store.Get<HostSettings>("settings", "host"),
        agents = store.List<AgentProfile>("agent").OrderBy(x => x.Id, StringComparer.Ordinal),
        models = store.List<ModelRecord>("model").OrderBy(x => x.Id, StringComparer.Ordinal)
    }, CerebralJson.Options);

    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Restore admission tests must never execute an agent.");
    }

    private sealed class BlockedUploadStream : Stream
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => entered.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
