using Cerebral.Core;

namespace Cerebral.Worker.Tests;

internal sealed class ReviewPluginTestHost(string dataDirectory, string packageDirectory) : IServicePluginHost
{
    public string PluginId => "example.review";
    public string PackageDirectory { get; } = Path.GetFullPath(packageDirectory);
    public string DataDirectory { get; } = Path.GetFullPath(dataDirectory);
    public void Publish(string eventType, string? jobId, object? data) { }
}
