using System.IO;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Integrator;

internal interface ICapacityChoicesProvider
{
    bool IsCurrent(CapacityChoiceScope scope);
    Task<ModelCapacityPreview?> ReadAsync(CapacityChoiceScope scope, ModelCapacityChoice? choice, CancellationToken token);
    Task<ModelCapacityApplyResult> ApplyAsync(CapacityChoiceScope scope, ModelCapacityApplyRequest request, CancellationToken token);
}
internal sealed class UnavailableCapacityChoicesProvider : ICapacityChoicesProvider
{
    public bool IsCurrent(CapacityChoiceScope scope) => false;
    public Task<ModelCapacityPreview?> ReadAsync(CapacityChoiceScope scope, ModelCapacityChoice? choice, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult<ModelCapacityPreview?>(null); }
    public Task<ModelCapacityApplyResult> ApplyAsync(CapacityChoiceScope scope, ModelCapacityApplyRequest request, CancellationToken token) =>
        throw new NotSupportedException("Connect to a Worker supporting capacity preview and apply.");
}

// Uses only the normal connected Configuration transport. No remote management route,
// owner credential creation, feature probing mutation, or alternate command fallback.
internal sealed class ConfigurationCapacityProvider(
    Func<string, object, CancellationToken, Task<CommandResult>> command,
    Func<CapacityChoiceScope, bool> current) : ICapacityChoicesProvider
{
    public bool IsCurrent(CapacityChoiceScope scope) => current(scope);
    private void RequireCurrent(CapacityChoiceScope scope)
    { if (!IsCurrent(scope)) throw new OperationCanceledException("The selected model or connection changed."); }
    public async Task<ModelCapacityPreview?> ReadAsync(CapacityChoiceScope scope, ModelCapacityChoice? choice, CancellationToken token)
    {
        RequireCurrent(scope); token.ThrowIfCancellationRequested();
        var result = await command(ModelCapacityProtocol.PreviewOperation,
            new ModelCapacityPreviewRequest(ModelCapacityProtocol.Version, scope.ModelId, choice), token);
        token.ThrowIfCancellationRequested(); RequireCurrent(scope);
        if (!result.Success)
            throw new NotSupportedException("Capacity preview is unavailable. Update this Worker or refresh the selected model. " + Bounded(result.Error));
        return CerebralJson.Read<ModelCapacityPreview>(result.Data);
    }
    public async Task<ModelCapacityApplyResult> ApplyAsync(CapacityChoiceScope scope, ModelCapacityApplyRequest request, CancellationToken token)
    {
        RequireCurrent(scope); token.ThrowIfCancellationRequested();
        if (request.ModelId != scope.ModelId || request.Version != ModelCapacityProtocol.Version)
            throw new InvalidDataException("Capacity request does not match this selected model.");
        // Exactly one transport call. Any missing/malformed acknowledgement is handled as
        // unconfirmed by the presenter, even when cancellation arrives after dispatch.
        var result = await command(ModelCapacityProtocol.ApplyOperation, request, token);
        if (!result.Success) throw new InvalidDataException("The Worker did not return a capacity-save acknowledgement. " + Bounded(result.Error));
        RequireApplyShape(result.Data);
        return CerebralJson.Read<ModelCapacityApplyResult>(result.Data);
    }
    private static void RequireApplyShape(JsonElement value)
    {
        string[] required = ["version", "modelId", "state", "mutationCommitted", "reason", "saved", "affectedProfiles", "warnings"];
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Capacity acknowledgement is not an object.");
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length != required.Length || properties.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != required.Length ||
            required.Any(name => !value.TryGetProperty(name, out _)) ||
            value.GetProperty("version").ValueKind != JsonValueKind.Number || !value.GetProperty("version").TryGetInt32(out _) ||
            value.GetProperty("modelId").ValueKind != JsonValueKind.String ||
            value.GetProperty("state").ValueKind != JsonValueKind.String ||
            value.GetProperty("reason").ValueKind != JsonValueKind.String ||
            value.GetProperty("mutationCommitted").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            value.GetProperty("saved").ValueKind is not (JsonValueKind.Object or JsonValueKind.Null) ||
            value.GetProperty("affectedProfiles").ValueKind != JsonValueKind.Array ||
            value.GetProperty("warnings").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Capacity acknowledgement has missing, duplicate, unknown or invalid fields.");
    }
    private static string Bounded(string? text) => string.IsNullOrWhiteSpace(text) ? "" : text[..Math.Min(text.Length, 512)];
}

internal sealed class CapacityTradeoffPresenter(ICapacityChoicesProvider provider, TimeProvider clock) : IDisposable
{
    private sealed record Destination(string Connection, string Worker, string Model);
    private readonly Dictionary<Destination, ModelCapacityConfiguration> unconfirmed = [];
    private CancellationTokenSource? pending;
    private long generation;
    private bool disposed;
    internal CapacityChoiceScope? Scope { get; private set; }
    internal CapacityTradeoffView? View { get; private set; }
    internal bool Reading { get; private set; }
    internal bool Applying { get; private set; }
    internal string Message { get; private set; } = "Select a model and refresh its capacity choices.";
    internal event Action? Changed;
    private Destination Key(CapacityChoiceScope scope) => new(scope.ConnectionIdentity, scope.WorkerId, scope.ModelId);
    internal bool HasUnconfirmed => Scope is { } scope && unconfirmed.ContainsKey(Key(scope));
    internal bool IsCurrent => !disposed && Scope is { } scope && provider.IsCurrent(scope);
    internal bool CanApply => !Reading && !Applying && !HasUnconfirmed && IsCurrent &&
        View is { CanApply: true };

    internal void Invalidate(string reason)
    {
        generation++; pending?.Cancel(); View = null; Reading = false; Message = reason;
        Changed?.Invoke();
    }
    internal void Poll()
    {
        if (Scope is null) return;
        if (!IsCurrent) Invalidate("The model or connection changed. Refresh this selected model.");
    }

    internal async Task ReadAsync(CapacityChoiceScope scope, ModelCapacityChoice? choice, CancellationToken token)
    {
        if (disposed) return;
        if (Applying)
        {
            Invalidate("The previous scope's save is settling. Refresh this selected model after it finishes; cancellation does not prove zero change.");
            Scope = scope; Changed?.Invoke(); return;
        }
        Invalidate("Reading capacity preview…");
        Scope = scope; var version = generation;
        pending?.Dispose(); pending = CancellationTokenSource.CreateLinkedTokenSource(token);
        var request = pending; request.CancelAfter(TimeSpan.FromSeconds(30)); Reading = true; Changed?.Invoke();
        try
        {
            if (!provider.IsCurrent(scope)) throw new NotSupportedException("Capacity choices are unavailable for this model connection. Connect through its normal Configuration route.");
            var result = await provider.ReadAsync(scope, choice, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (disposed || version != generation || Scope != scope || !provider.IsCurrent(scope)) return;
            if (result is null) throw new NotSupportedException("This Worker does not provide capacity choices. Update it and refresh.");
            var projected = CapacityTradeoffPresentation.Project(scope, result, clock.GetUtcNow());
            if (choice is not null && projected.Preview.Choice != choice)
                throw new InvalidDataException("The Worker previewed a different manual choice. Nothing was applied.");
            View = projected;
            if (unconfirmed.TryGetValue(Key(scope), out var target))
            {
                if (projected.Preview.Saved == target)
                {
                    unconfirmed.Remove(Key(scope));
                    Message = "Readback now matches the requested saved values. The earlier acknowledgement remains unconfirmed; no retry was sent.";
                }
                else Message = "The earlier save remains unconfirmed. Readback differs from the requested values. Apply stays disabled; refresh/read back again.";
            }
            else Message = projected.Preview.ApplyEligibility.Reason;
        }
        catch (Exception error) when (Ordinary(error))
        {
            if (version == generation && Scope == scope && !disposed)
            { View = null; Message = error is OperationCanceledException ? "Preview cancelled. Refresh the current model." : error.Message; }
        }
        finally
        {
            if (version == generation && !disposed) { Reading = false; Changed?.Invoke(); }
        }
    }

    internal async Task ApplyAsync(Func<string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        if (!CanApply || View is not { } captured || Scope is not { } scope) return;
        var version = generation; Applying = true; Changed?.Invoke();
        var dispatched = false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        pending?.Cancel(); pending?.Dispose(); pending = operation;
        try
        {
            if (!await confirm(captured.Details, operation.Token)) { Message = "No capacity change was submitted."; return; }
            operation.Token.ThrowIfCancellationRequested();
            if (disposed || version != generation || Scope != scope || !provider.IsCurrent(scope) ||
                View != captured) return;
            var request = new ModelCapacityApplyRequest(ModelCapacityProtocol.Version, scope.ModelId,
                captured.Preview.Choice, captured.Preview.ExpectedCurrent!, captured.Preview.Planned.RequiredPerSlotContextTokens);
            operation.CancelAfter(TimeSpan.FromSeconds(60));
            dispatched = true;
            var result = await provider.ApplyAsync(scope, request, operation.Token);
            CapacityTradeoffPresentation.ValidateResult(result, captured.Preview);
            if (result.MutationCommitted) unconfirmed.Remove(Key(scope));
            // A valid response can settle uncertainty internally after a scope change, but
            // its presentation must never repaint the newly selected model/connection.
            if (disposed || version != generation || Scope != scope || !provider.IsCurrent(scope)) return;
            View = null;
            Message = result.MutationCommitted
                ? "Saved for the next model load. No model load or scheduler change was requested." +
                    (result.Warnings.Count == 0 ? "" : "\nWarnings: " + string.Join("\n", result.Warnings))
                : "No settings changed · " + result.State + ": " + result.Reason + "\nRefresh before another explicit review.";
        }
        catch (Exception error) when (Ordinary(error))
        {
            if (dispatched) unconfirmed[Key(scope)] = captured.Preview.Requested;
            if (!disposed && version == generation && Scope == scope)
            {
                View = null;
                Message = dispatched ? "Save outcome unconfirmed. Read back/refresh before any further Apply. No retry was sent. " + error.Message :
                    "No save was dispatched. " + error.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(pending, operation)) pending = null;
            Applying = false;
            if (!disposed) Changed?.Invoke();
        }
    }
    internal static bool Ordinary(Exception error) => error is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException);
    public void Dispose()
    {
        disposed = true; generation++; pending?.Cancel(); pending?.Dispose(); pending = null;
        View = null; Changed = null;
    }
}
