using System.Text.Json;
using System.Windows.Threading;
using Cerebral.Contracts.Models;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

internal static class IsolatedTextHostTests
{
    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        var input = new DesktopIsolatedTextRequest("dedicated", 3, "autoprompt", "Fixed policy", "Latest public answer", 128);
        DesktopPluginHostAdapter Adapter(Func<string, object, CancellationToken, Task<DesktopServiceCommandResult>> transport, Action<string>? status = null) =>
            new(Dispatcher.CurrentDispatcher, () => new(new("view", "main-chat"), "Main", null, null, false),
                () => new(new("view", "main-chat"), 0, ""), (_, _) => throw new InvalidOperationException("No draft edits allowed."),
                (_, _) => throw new InvalidOperationException("No submission allowed."), (_, _) => throw new InvalidOperationException("No steering allowed."),
                (_, _) => throw new InvalidOperationException("No stop allowed."), status ?? (_ => { }), serviceCommand: transport);
        static DesktopServiceCommandResult Reply(object value) => new(true, CerebralJson.Element(value));
        static DesktopServiceCommandResult Capability() => Reply(new { contract = IsolatedTextGenerationContract.Id, supported = true, loadedOnly = true });
        var calls = new List<string>(); JsonElement submitted = default;
        var adapter = Adapter((operation, value, token) =>
        {
            calls.Add(operation);
            if (operation == IsolatedTextGenerationContract.CapabilityOperation) return Task.FromResult(Capability());
            submitted = CerebralJson.Element(value);
            return Task.FromResult(Reply(new IsolatedTextGenerationResult(IsolatedTextGenerationContract.Id,
                submitted.GetProperty("requestId").GetString()!, "completed", "Could you explain that step?")));
        });
        var result = await adapter.GenerateIsolatedTextAsync(input, CancellationToken.None);
        Check(result.State == DesktopOperationState.Completed && result.Text == "Could you explain that step?" && calls.Count == 2,
            "Capability discovery precedes exactly one isolated generation; there is no direct inference fallback.");
        Check(submitted.EnumerateObject().Count() == 9 && submitted.GetProperty("text").GetString() == input.Text &&
            submitted.GetProperty("policy").GetString() == input.Policy && !submitted.TryGetProperty("conversationId", out _) &&
            !submitted.TryGetProperty("history", out _) && submitted.GetProperty("profileRevision").GetInt64() == 3,
            "Only own policy/text/profile/capacity plus host identity/deadline cross the operation boundary.");
        Check(Guid.TryParseExact(result.RequestId, "N", out _), "The host creates a stable private request identity.");
        var generations = 0;
        var unsupported = Adapter((operation, value, token) => { if (operation != IsolatedTextGenerationContract.CapabilityOperation) generations++; return Task.FromResult(new DesktopServiceCommandResult(false, default)); });
        Check((await unsupported.GenerateIsolatedTextAsync(input, CancellationToken.None)).State == DesktopOperationState.Rejected && generations == 0,
            "Unsupported Workers stay disabled before mutation.");
        foreach (var ownerChanged in new[] { false, true })
        {
            JsonElement generated = default, cancelled = default; var generateCalls = 0; var cancelCalls = 0; var warnings = new List<string>();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var pendingAdapter = Adapter(async (operation, value, token) =>
            {
                if (operation == IsolatedTextGenerationContract.CapabilityOperation) return Capability();
                if (operation == IsolatedTextGenerationContract.GenerateOperation)
                {
                    generateCalls++; generated = CerebralJson.Element(value); started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("Unreachable");
                }
                Check(operation == IsolatedTextGenerationContract.CancelOperation, "Cleanup uses only the typed cancellation route.");
                cancelCalls++; cancelled = CerebralJson.Element(value);
                return ownerChanged ? new(false, default) : Reply(new IsolatedTextGenerationResult(IsolatedTextGenerationContract.Id,
                    cancelled.GetProperty("requestId").GetString()!, "cancelled"));
            }, warnings.Add);
            var pending = pendingAdapter.GenerateIsolatedTextAsync(input, cancellation.Token);
            await started.Task; cancellation.Cancel();
            try { await pending; throw new InvalidOperationException("Cancellation was ignored."); }
            catch (OperationCanceledException) { checks++; }
            Check(generateCalls == 1 && cancelCalls == 1 && JsonElement.DeepEquals(generated, cancelled),
                "Cancellation uses the full original immutable request, even if it overtakes admission; no replay or new ID.");
            Check(ownerChanged ? warnings.Any(s => s.Contains("unconfirmed", StringComparison.Ordinal)) : warnings.Count == 0,
                "Owner-revoked cleanup reports uncertainty without silently resolving a replacement owner.");
        }
        var uncertainCalls = 0;
        var uncertain = Adapter((operation, value, token) =>
        {
            if (operation == IsolatedTextGenerationContract.CapabilityOperation) return Task.FromResult(Capability());
            uncertainCalls++;
            var request = CerebralJson.Element(value);
            return Task.FromResult(Reply(new IsolatedTextGenerationResult(IsolatedTextGenerationContract.Id,
                request.GetProperty("requestId").GetString()!, "uncertain", Message: "Retained for review")));
        });
        var unknown = await uncertain.GenerateIsolatedTextAsync(input, CancellationToken.None);
        Check(unknown.State == DesktopOperationState.Uncertain && unknown.Text is null && uncertainCalls == 2,
            "Unknown output never becomes a suggestion; one exact cancellation follows without retrying generation.");
        return checks;
    }
}
