using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Auditing.Laya;

public sealed record LayaPluginStatus(bool Ready, string AdapterId, string PackageId, long Generation,
    string ModelIdentity, LayaReadiness? Diagnostics, string? Error, long CompletedRequests,
    object? LastResult, int? ProcessId, int ActiveRequests, int QueuedRequests, bool LatencyIsOwnerEstimate)
{
    public LayaRejectionDiagnostic? LastRejection { get; init; }
}

/// <summary>Optional stock CPU auditor package. It contributes no agent tools or execution authority.</summary>
public sealed class LayaStockPlugin : IServicePlugin, IWorkflowAuditModule
{
    private readonly Func<ILayaChild> children;
    private LayaProcessClient? client;
    private LayaStockAdvisorModule? module;
    private LayaConfiguration? config;
    private string? error;
    private bool initialized, disposed;
    public LayaStockPlugin() : this(() => new LayaOwnedChild()) { }
    internal LayaStockPlugin(Func<ILayaChild> children) => this.children = children;
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [new StatusCommand(this)];
    public IReadOnlyList<IWorkflowAuditAdapter> WorkflowAuditors { get; private set; } = [];
    public void Initialize(IServicePluginHost host)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (initialized) throw new InvalidOperationException("Laya plugin initialization cannot be replayed.");
        initialized = true;
        try
        {
            config = LayaConfiguration.Read(Path.Combine(Path.GetFullPath(host.DataDirectory), LayaConfiguration.FileName));
            if (!config.Enabled) { error = "Optional native Laya CPU auditing is disabled in its plugin INI."; return; }
            client = new(config, children());
            // This package admission is synchronous and bounded. No descriptor appears while native
            // load or CPU placement is pending; asynchronous registry readmission is a host concern.
            client.StartAsync().GetAwaiter().GetResult();
            module = new(config, client);
            WorkflowAuditors = Array.AsReadOnly<IWorkflowAuditAdapter>([new LayaWorkflowAuditAdapter(module)]);
        }
        catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            error = client?.Failure ?? (failure is FileNotFoundException ? "Optional Laya plugin configuration is missing."
                : "Optional Laya CPU admission failed; verify its frozen INI, launch hashes and native readiness.");
            client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            WorkflowAuditors = [];
        }
    }
    public LayaPluginStatus Status() => new(!disposed && client?.Ready == true && module is not null,
        LayaStockAdvisorModule.AdapterId, LayaStockAdvisorModule.PackageId, config?.Generation ?? 0,
        module?.Capability.ModelIdentity ?? "unavailable", client?.Readiness, client?.Failure ?? error,
        module?.CompletedRequests ?? 0, module?.LastResult, client?.ProcessId, client?.ActiveRequests ?? 0,
        client?.QueuedRequests ?? 0, true) { LastRejection = module?.LastRejection };
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; WorkflowAuditors = [];
        client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private sealed class StatusCommand(LayaStockPlugin plugin) : IServiceCommandExtension
    {
        public IReadOnlyList<string> Operations => ["laya.status"];
        public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation != "laya.status" || arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
                throw new ArgumentException("Laya contributes only its parameter-free owner status operation.");
            return Task.FromResult<object?>(plugin.Status());
        }
    }
}
