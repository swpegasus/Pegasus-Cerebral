using System.Windows;
using Cerebral.Desktop.Plugins;
using Cerebral.PackageSettings;
using Cerebral.PluginLifetime;

namespace Cerebral.AutoPromptPlugin;

public sealed partial class AutoPromptPlugin : IDesktopPlugin, IDesktopConfigurationPlugin,
    IDesktopAgentTemplatePlugin, IDesktopComposerSuggestionPlugin
{
    public const string PackageId = "pegasus.autoprompt";
    public const string PackageVersion = "1.0.2";
    private AutoPromptHarness harness;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim generation = new(1, 1);
    private PackagePreferenceSession<AutoPromptPreferences>? preferences;
    private IDesktopPluginHost? host;
    private IDesktopServiceHost? service;
    private bool disposed;
    // Older hosts remain disabled. Only the optional isolated, Hold-aware capability may bind;
    // ordinary direct inference is never a fallback.
    public AutoPromptPlugin() : this(null) { }
    internal AutoPromptPlugin(IAutoPromptGenerationBackend? backend) => harness = new(backend);
    public IReadOnlyList<DesktopPanel> Panels => [];
    public IReadOnlyList<DesktopAction> ComposerActions => [];
    public IReadOnlyList<DesktopPanel> ConfigurationPanels => [new("autoprompt-settings", "AutoPrompt", CreateSettings)];
    public IReadOnlyList<DesktopAgentTemplate> AgentTemplates =>
        [new("autoprompt", "AutoPrompt suggestion", "helper", "autoprompt", AutoPromptHarness.Policy)
            { RequiredCapabilities = [], RequiredTools = [], MaxSteps = 1, MaxSeconds = 30 }];
    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;
    public void Initialize(IDesktopPluginHost value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (host is not null) throw new InvalidOperationException("AutoPrompt is already initialized.");
        host = value ?? throw new ArgumentNullException(nameof(value));
        if (!harness.Available && value is IDesktopIsolatedTextGenerationHost isolated) harness = new(new AutoPromptDesktopBackend(isolated));
        service = value as IDesktopServiceHost;
        preferences = new(value, PackageId, PackageVersion, new AutoPromptSettingsPolicy(), AutoPromptPreferences.Read,
            p => p.Values(), () => AutoPromptPreferences.Default);
    }
    public async Task<string?> SuggestAsync(string latestVisibleAssistantResponse, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!harness.Available)
        {
            PluginHostAccess.TryNotify(() => host?.ReportStatus("AutoPrompt suggestions are unavailable in this version. See AutoPrompt settings; your draft is unchanged."));
            return null;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        // A newer final response may arrive while a canceled request is still unwinding.
        // Wait for that request to settle instead of silently losing the newer response.
        await generation.WaitAsync(linked.Token);
        try
        {
            var settings = await (preferences ?? throw new InvalidOperationException("Initialize AutoPrompt first.")).ReadAsync(linked.Token);
            if (!settings.Configured)
            {
                PluginHostAccess.TryNotify(() => host?.ReportStatus("AutoPrompt is disabled. Enable it and select an exact AutoPrompt profile in its settings."));
                return null;
            }
            return await harness.SuggestAsync(settings, latestVisibleAssistantResponse, linked.Token);
        }
        finally { generation.Release(); }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { lifetime.Cancel(); }
        finally { preferences?.Dispose(); host = null; service = null; lifetime.Dispose(); }
    }
}
