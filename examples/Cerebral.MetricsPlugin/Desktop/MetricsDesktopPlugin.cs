using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.IO;
using Cerebral.Desktop.Plugins;

namespace Cerebral.MetricsPlugin.Desktop;

public sealed class MetricsDesktopPlugin : DesktopPluginBase, IDesktopConfigurationPlugin
{
    private readonly List<MetricsDashboardView> views = [];
    private readonly CancellationTokenSource lifetime = new();
    public IReadOnlyList<DesktopPanel> ConfigurationPanels => [new("metrics", "Metrics", CreateView)];
    private System.Windows.FrameworkElement CreateView()
    {
        var view = new MetricsDashboardView(LoadAsync, SaveAsync);
        views.Add(view); return view;
    }
    private async Task<(MetricsStatus Status, MetricsQueryResult Result)> LoadAsync(MetricsQuery query, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
        var status = await ReadAsync("metrics-history.status", EmptyArguments(), MetricsContract.TypeInfo<MetricsStatus>(), linked.Token);
        var result = await ReadAsync("metrics-history.query", JsonSerializer.SerializeToElement(query, MetricsContract.TypeInfo<MetricsQuery>()),
            MetricsContract.TypeInfo<MetricsQueryResult>(), linked.Token);
        if (status.Schema != "cerebral.metrics-history.status/2" || result.Schema != "cerebral.metrics-history.query/2")
            throw new InvalidDataException("Install matching Metrics0.2 service and desktop components, then refresh.");
        return (status, result);
    }
    private Task<MetricsSettings> SaveAsync(MetricsSettingsChange change, CancellationToken token) =>
        ReadAsync("metrics-history.settings.set", JsonSerializer.SerializeToElement(change, MetricsContract.TypeInfo<MetricsSettingsChange>()),
            MetricsContract.TypeInfo<MetricsSettings>(), token);
    private async Task<T> ReadAsync<T>(string operation, JsonElement arguments, JsonTypeInfo<T> typeInfo, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
        var service = Host as IDesktopServiceHost ?? throw new InvalidOperationException("Connect Configuration to a Worker with the metrics service component.");
        // Only framework-owned JSON crosses into the noncollectible desktop host;
        // its shared serializer must never reflect over private plugin DTO types.
        var result = await service.ExecuteServiceCommandAsync(operation, arguments, linked.Token);
        if (!result.Success) throw new InvalidOperationException("Metrics service is unavailable. Check the connection and installed metrics component, then refresh.");
        return result.Data.Deserialize(typeInfo) ?? throw new InvalidDataException("The metrics service returned an unsupported result.");
    }
    private static JsonElement EmptyArguments()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
    public override void Dispose()
    {
        lifetime.Cancel(); foreach (var view in views) view.Dispose(); views.Clear(); lifetime.Dispose(); base.Dispose();
    }
}
