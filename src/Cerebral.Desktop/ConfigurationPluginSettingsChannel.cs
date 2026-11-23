using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>One ordinary Configuration action. The channel carries navigation only, never settings or credentials.</summary>
public sealed record ConfigurationPluginSettingsRequest(string RequestId, string PluginId, string PanelId,
    string PackageDirectory, string PackageVersion, string Transport, string ConnectionIdentity, string InstallationId);

public sealed record ConfigurationPluginSettingsReply(string RequestId, string State);

public static class ConfigurationPluginSettingsChannel
{
    public const string ApplicationId = "Pegasus.Cerebral.Configuration";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    public static string ConnectionKey(string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

    public static async Task<ConfigurationPluginSettingsReply> OpenAsync(string userDataRoot,
        ConfigurationPluginSettingsRequest request, Func<Task<bool>> stillCurrent, CancellationToken cancellationToken)
    {
        RequireRequest(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        var token = deadline.Token;
        var name = PipeName(userDataRoot);
        var mayHaveOpened = false;
        var phase = "launch";
        try
        {
            var presence = DesktopApplicationInstance.ObserveExisting(ApplicationId);
            if (presence == DesktopApplicationPresence.Unavailable)
                return new(request.RequestId, "unavailable");
            if (presence == DesktopApplicationPresence.Absent)
            {
                if (!await stillCurrent().ConfigureAwait(false)) return new(request.RequestId, "changed");
                token.ThrowIfCancellationRequested();
                mayHaveOpened = true;
                ManagementProcess.OpenApplication("Integrator");
            }
            phase = "connect";
            using var pipe = await InstalledWorkerWindows.ConnectClientAsync(name, token).ConfigureAwait(false);
            phase = "verify receiver";
            var observed = InstalledWorkerWindows.ObserveServer(pipe);
            var sid = CurrentUserSid();
            InstalledWorkerWindows.RequirePipeSecurity(observed.Protection, sid, sid);
            var peer = observed.Process;
            RequirePeer(peer, "Integrator");
            if (!await stillCurrent().ConfigureAwait(false))
                return new(request.RequestId, mayHaveOpened ? "unconfirmed" : "changed");
            if (InstalledWorkerWindows.ObserveServerProcess(pipe) != peer)
                throw new UnauthorizedAccessException("Configuration changed before settings navigation.");
            token.ThrowIfCancellationRequested();
            mayHaveOpened = true;
            phase = "submit navigation";
            await LocalWorkerProtocol.WriteAsync(pipe, request, token).ConfigureAwait(false);
            var reply = await LocalWorkerProtocol.ReadAsync<ConfigurationPluginSettingsReply>(pipe, token).ConfigureAwait(false);
            return reply.RequestId == request.RequestId && reply.State is "opened" or "changed" or "unavailable" or "unconfirmed"
                ? reply : new(request.RequestId, "unconfirmed");
        }
        catch (Exception error) when (IsTransportFailure(error))
        {
            Trace.TraceWarning("Configuration plugin settings could not {0}: {1}.", phase, error.GetType().Name);
            // A transport failure after startup or dispatch does not establish that the panel stayed closed.
            return new(request.RequestId, mayHaveOpened ? "unconfirmed" : "unavailable");
        }
    }

    public static async Task ListenAsync(string userDataRoot, Func<ConfigurationPluginSettingsRequest, CancellationToken, Task<string>> navigate,
        CancellationToken token)
    {
        try
        {
            // The generic local-only factory admits a single ACL identity when both SIDs
            // match. This desktop channel is owned solely by the signed-in user.
            var sid = CurrentUserSid();
            using var pipe = InstalledWorkerWindows.CreateServer(PipeName(userDataRoot), sid, sid, maximumInstances: 1);
            while (!token.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(Deadline);
                    var peer = InstalledWorkerWindows.ObserveClient(pipe).Process;
                    RequirePeer(peer, "Orchestrator");
                    var request = await LocalWorkerProtocol.ReadAsync<ConfigurationPluginSettingsRequest>(pipe, deadline.Token)
                        .ConfigureAwait(false);
                    RequireRequest(request);
                    if (InstalledWorkerWindows.ObserveClient(pipe).Process != peer)
                        throw new UnauthorizedAccessException("The requesting desktop changed before settings navigation.");
                    var state = await navigate(request, deadline.Token).ConfigureAwait(false);
                    await LocalWorkerProtocol.WriteAsync(pipe, new ConfigurationPluginSettingsReply(request.RequestId, state),
                        deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (IsTransportFailure(error))
                {
                    Trace.TraceWarning("Configuration plugin settings request did not complete: {0}.", error.GetType().Name);
                }
                finally { if (pipe.IsConnected) pipe.Disconnect(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsTransportFailure(error))
        {
            Trace.TraceWarning("Configuration plugin settings receiver stopped: {0}.", error.GetType().Name);
        }
    }

    private static bool IsTransportFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or OperationCanceledException or TimeoutException or
        SecurityException or System.ComponentModel.Win32Exception or NotSupportedException;

    private static string PipeName(string root)
    {
        using var process = Process.GetCurrentProcess();
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
        var identity = $"{CurrentUserSid()}\n{process.SessionId}\n{path}";
        return "Pegasus.Cerebral.Configuration.Settings.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string CurrentUserSid()
    {
        using var user = WindowsIdentity.GetCurrent();
        return user.User?.Value ?? throw new UnauthorizedAccessException("The current desktop user could not be identified.");
    }

    private static void RequirePeer(InstalledWorkerProcessObservation peer, string application)
    {
        using var process = Process.GetCurrentProcess();
        if (peer.AccountSid != CurrentUserSid() || peer.SessionId != process.SessionId ||
            !peer.Executable.Equals(ManagementProcess.ApplicationPath(application), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The settings peer is not the expected Cerebral application in this user session.");
    }

    private static void RequireRequest(ConfigurationPluginSettingsRequest request)
    {
        static bool Identifier(string? value) => value is { Length: > 0 and <= 128 } &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');
        if (!Guid.TryParseExact(request.RequestId, "N", out _) || !Identifier(request.PluginId) ||
            !Identifier(request.PanelId) || !Identifier(request.PackageVersion) || !Identifier(request.InstallationId) ||
            request.Transport is not ("pipe" or "installed-pipe" or "installed-user-pipe" or "http") ||
            request.ConnectionIdentity is not { Length: 64 } || !request.ConnectionIdentity.All(Uri.IsHexDigit) ||
            request.PackageDirectory is not { Length: > 0 and <= 2048 } || request.PackageDirectory.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(request.PackageDirectory))
            throw new ArgumentException("The plugin settings request is incomplete or invalid.");
    }
}
