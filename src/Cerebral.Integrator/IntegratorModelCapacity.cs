using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private CapacityTradeoffControl? modelCapacityControl;
    private readonly CapacityDraftGate capacityDraftGate = new();

    private UIElement CreateModelCapacityContent()
    {
        // This is the existing Models configuration surface, not an alternate editor.
        // The ordinary EditModel draft is separate and cannot overwrite an inline Apply.
        if (modelCapacityControl is not null) throw new InvalidOperationException("The owned capacity control is already mounted.");
        modelCapacityControl = new(new ConfigurationCapacityProvider(DispatchCapacityAsync, CapacityScopeCurrent),
            confirm: ReviewCapacityAsync, lifetime: Lifetime.Token);
        var expander = new Expander { Header = "Selected model · context and inference slots", Content = modelCapacityControl,
            Margin = new Thickness(0, 8, 0, 8), MaxHeight = 460 };
        expander.Expanded += async (_, _) => await RefreshSelectedCapacityAsync();
        models.SelectionChanged += async (_, _) => await RefreshSelectedCapacityAsync();
        Closed += (_, _) => modelCapacityControl?.Dispose();
        return expander;
    }

    private static string CapacityModelBinding(ModelRecord model) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(model, CerebralJson.Options)));

    private CapacityChoiceScope? CurrentCapacityScope()
    {
        if (capacityDraftGate.IsOpen || !IsConnected || refreshingModelLibrary || loadedModelWorkerId != SelectedModelWorkerId ||
            models.SelectedItem is not ModelRow row) return null;
        return new(ServiceConnectionIdentity, loadedModelWorkerId, row.Record.Id,
            CapacityModelBinding(row.Record), queueConnectionEpoch);
    }
    private bool CapacityScopeCurrent(CapacityChoiceScope scope) =>
        !Lifetime.IsCancellationRequested && CurrentCapacityScope() == scope;

    private async Task<CommandResult> DispatchCapacityAsync(string operation, object args, CancellationToken token)
    {
        if (operation is not (ModelCapacityProtocol.PreviewOperation or ModelCapacityProtocol.ApplyOperation))
            throw new ArgumentException("Only the two normal capacity configuration operations are allowed.");
        var workerId = loadedModelWorkerId;
        var connection = queueConnectionEpoch; var owner = Client;
        token.ThrowIfCancellationRequested();
        if (!IsConnected || connection != queueConnectionEpoch || !ReferenceEquals(owner, Client))
            throw new OperationCanceledException("Configuration connection changed before dispatch.");
        var route = CapacityRoute(workerId, operation, args);
        return await owner.CommandAsync(route.Operation, route.Arguments, cancellationToken: token);
    }

    internal static (string Operation, object Arguments) CapacityRoute(string workerId, string operation, object args)
    {
        if (operation is not (ModelCapacityProtocol.PreviewOperation or ModelCapacityProtocol.ApplyOperation))
            throw new ArgumentException("Only normal capacity preview/apply routes are allowed.");
        return workerId == "local" ? (operation, args) : ("workers." + operation, WorkerModelArguments(workerId, args));
    }

    private async Task RefreshSelectedCapacityAsync()
    {
        if (modelCapacityControl is null) return;
        var scope = CurrentCapacityScope();
        if (scope is null || models.SelectedItem is not ModelRow row) { InvalidateModelCapacity(); return; }
        var model = row.Record;
        await modelCapacityControl.OpenAsync(scope, new(model.ContextTokens, model.ParallelRequests,
            model.AutomaticContextSizing, model.MaximumContextTokens), Lifetime.Token);
    }
    private void InvalidateModelCapacity() =>
        modelCapacityControl?.Invalidate("The selected machine, model or connection changed. Refresh capacity for the current model.");

    private Task<bool> ReviewCapacityAsync(string effects, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var review = new FieldsDialog(this, "Review inference capacity",
            "Review every saved field and profile effect below. Apply saves the selected choice for the next load; it does not load/unload a model or change the scheduler.", "Apply selected choice")
            .Add("effects", "Complete selected-preview effects", effects, true).SetReadOnly("effects")
            .ConfigureSubmitAsync("Apply selected choice", () => Task.FromResult(true), saveOnClose: false);
        var accepted = review.ShowDialog() == true;
        token.ThrowIfCancellationRequested();
        return Task.FromResult(accepted);
    }
}

internal sealed class CapacityDraftGate
{
    internal bool IsOpen { get; private set; }
    internal IDisposable Enter(CapacityTradeoffPresenter? capacity)
    {
        if (IsOpen || capacity is not null && (capacity.Applying || capacity.HasUnconfirmed))
            throw new InvalidOperationException("Finish capacity readback before opening another model-settings draft.");
        capacity?.Invalidate("A model-settings draft is open. Refresh capacity after closing it.");
        IsOpen = true;
        return new Lease(this);
    }
    private sealed class Lease(CapacityDraftGate owner) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; owner.IsOpen = false; } }
    }
}
