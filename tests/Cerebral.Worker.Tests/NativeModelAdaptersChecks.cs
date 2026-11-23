using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

internal sealed record NativeModelAdaptersCheckResult(
    int RegistryChecks, int ReadinessChecks, int ReasoningChecks, int DraftChecks)
{
    internal int Total => RegistryChecks + ReadinessChecks + ReasoningChecks + DraftChecks;
}

// Exercises normal Worker code with deterministic providers and an in-memory HTTP handler.
// No native model, provider network, GPU, service or installed application is involved.
internal static class NativeModelAdaptersChecks
{
    internal static async Task<NativeModelAdaptersCheckResult> RunAsync(string root)
    {
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("Native adapter fixtures require one absolute task-owned parent.", nameof(root));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fixture = Path.Combine(parent, "native-model-adapters-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(fixture) || File.Exists(fixture))
            throw new IOException("The unique native adapter fixture directory already exists.");
        Directory.CreateDirectory(fixture);
        Exception? failure = null;
        try
        {
            var registry = await ModelAdapterRegistryTests.RunAsync();
            var readiness = await NativeReadinessChecks.RunAsync(fixture);
            var reasoning = await RegisteredReasoningIntegrationChecks.RunAsync();
            var draft = await GenerationDraftChecks.RunAsync();
            return new(registry.Length, readiness.Length, reasoning.Length, draft.Length);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try
            {
                // Delete only the exact leaf created for this invocation, never the supplied parent.
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(fixture)), parent,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new IOException("Native adapter fixture cleanup escaped its parent.");
                // All fixture connections are closed; release their process-local pooled handles.
                SqliteConnection.ClearAllPools();
                Directory.Delete(fixture, recursive: true);
            }
            catch (Exception cleanup) when (failure is not null)
            { throw new AggregateException("Native adapter fixture failed and cleanup was not confirmed.", failure, cleanup); }
        }
    }
}
