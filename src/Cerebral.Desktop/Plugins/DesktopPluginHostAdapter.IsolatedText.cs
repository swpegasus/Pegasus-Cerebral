using System.IO;
using System.Text.Json;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Desktop.Plugins;

public sealed partial class DesktopPluginHostAdapter
{
    internal async Task<DesktopIsolatedTextAvailability> IsolatedTextAvailabilityAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var result = await ExecuteServiceCommandAsync(IsolatedTextGenerationContract.CapabilityOperation, new { }, timeout.Token);
            var value = result.Data;
            return result.Success && value.ValueKind == JsonValueKind.Object && value.GetRawText().Length <= 8192 &&
                value.TryGetProperty("contract", out var contract) && contract.GetString() == IsolatedTextGenerationContract.Id &&
                value.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True &&
                value.TryGetProperty("loadedOnly", out var loaded) && loaded.ValueKind == JsonValueKind.True
                    ? new(true) : new(false, "This Worker does not support isolated AutoPrompt suggestions. Update Cerebral before enabling them.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { return new(false, "AutoPrompt support could not be confirmed. Reconnect to the current Worker before using suggestions."); }
    }

    internal async Task<DesktopIsolatedTextResult> GenerateIsolatedTextAsync(DesktopIsolatedTextRequest input, CancellationToken token)
    {
        if (Surface != DesktopPluginSurface.Orchestrator)
            return new(DesktopOperationState.Rejected, Message: "Suggestions run only for a final response in the main chat.");
        var availability = await IsolatedTextAvailabilityAsync(token);
        if (!availability.Available) return new(DesktopOperationState.Rejected, Message: availability.Message);
        var request = new IsolatedTextGenerationRequest(1, Guid.NewGuid().ToString("N"), input.ProfileId, input.ProfileRevision,
            input.Purpose, input.Policy, input.Text, input.MaximumOutputTokens, DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds());
        request.Validate(DateTimeOffset.UtcNow);
        var dispatched = false; var settled = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(32));
        try
        {
            timeout.Token.ThrowIfCancellationRequested(); dispatched = true;
            // Never retry or change identity after this mutation may have reached the Worker.
            var reply = await ExecuteServiceCommandAsync(IsolatedTextGenerationContract.GenerateOperation, request, timeout.Token);
            if (!reply.Success) return new(DesktopOperationState.Rejected, Message:
                "AutoPrompt needs a current dedicated local profile and its explicitly loaded model. Work and resource holds remain in force.", RequestId: request.RequestId);
            if (reply.Data.GetRawText().Length > 16384) throw new InvalidDataException("The isolated receipt exceeds its bound.");
            var result = CerebralJson.Read<IsolatedTextGenerationResult>(reply.Data);
            if (result.Contract != IsolatedTextGenerationContract.Id || result.RequestId != request.RequestId ||
                result.Text?.Length > IsolatedTextGenerationContract.MaximumOutputCharacters ||
                result.State is not ("completed" or "rejected" or "cancelled" or "uncertain"))
                throw new InvalidDataException("The isolated generation receipt is invalid.");
            settled = result.State is "completed" or "rejected" or "cancelled";
            timeout.Token.ThrowIfCancellationRequested();
            return new(result.State == "completed" ? DesktopOperationState.Completed :
                result.State == "uncertain" ? DesktopOperationState.Uncertain : DesktopOperationState.Rejected,
                result.State == "completed" ? result.Text : null, result.Message, result.RequestId);
        }
        finally
        {
            if (dispatched && !settled)
            {
                // Independent finite cleanup still targets this adapter's original owner. Owner
                // revocation can reject it; never resolve a new owner or claim confirmed cancellation.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    var reply = await ExecuteServiceCommandAsync(IsolatedTextGenerationContract.CancelOperation, request, cleanup.Token);
                    var receipt = reply.Success ? CerebralJson.Read<IsolatedTextGenerationResult>(reply.Data) : null;
                    if (receipt is null || receipt.Contract != IsolatedTextGenerationContract.Id || receipt.RequestId != request.RequestId ||
                        receipt.State is not ("completed" or "cancelled" or "rejected"))
                        ReportStatus("Suggestion cancellation is unconfirmed. Its exact workflow remains retained for review.");
                }
                catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                { ReportStatus("Suggestion cancellation could not be confirmed. The Worker retains the exact request and its deadline."); }
            }
        }
    }
}

internal partial class ScopedDesktopPluginHost : IDesktopIsolatedTextGenerationHost
{
    public async Task<DesktopIsolatedTextAvailability> GetIsolatedTextAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        return await source.IsolatedTextAvailabilityAsync(linked.Token);
    }
    public async Task<DesktopIsolatedTextResult> GenerateIsolatedTextAsync(DesktopIsolatedTextRequest request, CancellationToken cancellationToken = default)
    {
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        return await source.GenerateIsolatedTextAsync(request, linked.Token);
    }
}
