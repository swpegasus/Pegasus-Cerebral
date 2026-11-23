using System.IO;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Integrator;

/// <summary>Shared first-run sequencing. The window supplies the real UI, Worker, and Cortex
/// operations; deterministic checks supply explicit fakes for those same boundaries.</summary>
internal static class FirstRunSetupCoordinator
{
    internal const string ScanSource = "Scan the configured repository";
    internal const string ImportSource = "Import an existing model";
    internal const string DownloadSource = "Download recommended Qwen3.5-9B GGUF";

    internal static void RequireDirectOwnerTransport(string transport)
    {
        if (transport is not ("pipe" or "installed-user-pipe"))
            throw new InvalidOperationException("Automatic first-run Cortex enrollment requires this signed-in user's verified local Worker. Use the advanced native setup for an installed service Worker.");
    }

    internal static async Task SelectModelSourceAsync(string selected, Action requireCurrent,
        Func<Task> scan, Func<Task> import, Func<Task> download)
    {
        requireCurrent(); // A modal first-model choice must not capture a replacement Worker.
        switch (selected)
        {
            case ScanSource: await scan(); break;
            case ImportSource: await import(); break;
            case DownloadSource: await download(); break;
            default: throw new ArgumentException("The first-model source choice is invalid.", nameof(selected));
        }
        requireCurrent();
    }

    internal static async Task<string?> OfferOptionalCortexAsync(bool eligible, Action requireCurrent,
        Func<Task<bool>> connect, Func<Task> refresh)
    {
        if (!eligible) return null;
        string? issue = null;
        try
        {
            if (!await connect())
                issue = "Cortex was not connected during this setup. Review its connection status before trying again.";
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException and not OperationCanceledException)
        {
            requireCurrent(); // A connection change is never an optional Cortex failure.
            issue = error is CortexEnrollmentUncertainException uncertain && uncertain.AttemptId is { } id
                ? $"Cortex enrollment attempt {id} is {uncertain.State}; review it before retrying."
                : "Cortex connection setup needs review: " +
                  (error.Message.Length <= 300 ? error.Message : error.Message[..300] + "…");
        }
        requireCurrent();
        await refresh();
        requireCurrent();
        return issue;
    }

    internal static async Task<(IReadOnlyList<T> Ready, string? Issue)> InspectEligibleCortexAsync<T>(
        IEnumerable<T> providers, Action requireCurrent, Func<T, Task<CortexConnectionReport>> inspect)
    {
        var ready = new List<T>();
        string? issue = null;
        foreach (var provider in providers)
        {
            try
            {
                requireCurrent();
                var report = await inspect(provider);
                requireCurrent();
                if (report is { State: "ready", Transport: "cortex-native-pipe", NativeIdentity: not null })
                    ready.Add(provider);
                else issue ??= "The saved Cortex connection needs review and was omitted from first-run choices.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException or ArgumentException or TimeoutException or System.Text.Json.JsonException or
                System.Security.SecurityException)
            {
                requireCurrent();
                issue ??= "The saved Cortex connection could not be inspected and was omitted from first-run choices.";
            }
        }
        return (ready, issue);
    }

    internal static async Task CommitModelAndAgentsAsync(string? cortexServerId, Action requireCurrent,
        Func<Task> loadModel, Func<string?, Task> bootstrap)
    {
        requireCurrent();
        await loadModel();
        requireCurrent();
        await bootstrap(cortexServerId);
        requireCurrent();
    }
}
