namespace Cerebral.Desktop.Plugins;

/// <summary>
/// Optional entry base with no desktop contributions. Override only the requested
/// contributions. Initialization and disposal overrides must call their base method.
/// </summary>
public abstract class DesktopPluginBase : IDesktopPlugin
{
    private IDesktopPluginHost? host;
    private bool disposed;

    protected IDesktopPluginHost Host
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return host ?? throw new InvalidOperationException("The desktop plugin has not been initialized.");
        }
    }

    /// <summary>Optional host capability; never substitutes a relative or shared data path.</summary>
    protected IDesktopPluginEnvironment Environment => Host as IDesktopPluginEnvironment
        ?? throw new NotSupportedException("The desktop host does not provide IDesktopPluginEnvironment.");

    public virtual void Initialize(IDesktopPluginHost pluginHost)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(pluginHost);
        if (host is not null) throw new InvalidOperationException("The desktop plugin is already initialized.");
        host = pluginHost;
    }

    public virtual IReadOnlyList<DesktopPanel> Panels => [];
    public virtual IReadOnlyList<DesktopAction> ComposerActions => [];
    public virtual DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;

    public virtual void Dispose()
    {
        disposed = true;
        host = null;
    }
}
