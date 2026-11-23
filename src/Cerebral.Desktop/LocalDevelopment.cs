using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Net.Http;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Desktop;

public static partial class LocalDevelopment
{
    public static string DataDirectory => Path.Combine(InstallationPaths.CurrentUserDataDirectory, "dev");

    private static void ValidateEndpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" ||
            !endpoint.IsLoopback || endpoint.AbsolutePath != "/" || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw new ArgumentException($"Local worker controls require a loopback HTTP service address, such as {CerebralDefaults.WorkerUrl}.");
    }
    private static void ValidateHost(HostInfo info)
    {
        if (info.Product != "Pegasus Cerebral" || info.Protocol != 1)
            throw new InvalidOperationException("A different or incompatible service owns this address. No process was started or stopped.");
    }
    public static async Task<HostInfo> StartAsync(string url, CancellationToken token)
    {
        ValidateEndpoint(url);
        using var client = new CerebralClient(url);
        using (var probe = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            probe.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var existing = await client.GetAsync<HostInfo>("health", cancellationToken: probe.Token);
                ValidateHost(existing); return existing;
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) { }
        }
        var executable = ManagementProcess.WorkerPath;
        Directory.CreateDirectory(DataDirectory);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var value in new[] { "--local-run", "--data", DataDirectory, "--url", url }) start.ArgumentList.Add(value);
        // The service owns accepted work. Closing either GUI must not dispose or kill this process.
        using var process = Process.Start(start) ?? throw new IOException("Could not start the local worker.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    var log = Path.Combine(DataDirectory, "local-worker.log");
                    throw new IOException($"Local worker exited with code {process.ExitCode}. Check {log}. Another process may already own its data directory.");
                }
                try
                {
                    var info = await client.GetAsync<HostInfo>("health", cancellationToken: timeout.Token);
                    ValidateHost(info);
                    if (!info.LocalRun || !Path.GetFullPath(info.DataDirectory).Equals(Path.GetFullPath(DataDirectory), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The responding service uses a different run mode or data directory. Inspect it in Diagnostics before continuing.");
                    return info;
                }
                catch (HttpRequestException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) { }
                await Task.Delay(200, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("Startup wait ended. The local worker may still be starting; use Connect before trying to start it again.");
        }
    }
    public static async Task StopAsync(string url, CancellationToken token)
    {
        ValidateEndpoint(url);
        using var client = new CerebralClient(url);
        var info = await client.GetAsync<HostInfo>("health", cancellationToken: token); ValidateHost(info);
        if (!info.LocalRun || !Path.GetFullPath(info.DataDirectory).Equals(Path.GetFullPath(DataDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This endpoint is not the local development worker. Use the appropriate installed-service controls.");
        var result = await client.CommandAsync("service.stop-local", new { }, cancellationToken: token);
        if (!result.Success) throw new InvalidOperationException(result.Error);
    }
}
