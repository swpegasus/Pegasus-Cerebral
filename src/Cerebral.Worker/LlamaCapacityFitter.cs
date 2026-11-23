using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker;

internal sealed record LlamaFitContext(int? ContextTokens, string Basis);

internal static partial class LlamaCapacityFitter
{
    // llama-fit-params predicts model/KV placement but the server also creates CUDA, HTTP and
    // compute buffers. A bounded allowance keeps the user's requested post-load reserve real.
    internal const int RuntimeCalibrationMb = 1_536;

    public static long FitTargetMemoryMb(HostSettings settings, int additionalCalibrationMb = 0)
    {
        var configuredReserve = Math.Max(0, settings.ReservedMemoryMb);
        var calibration = configuredReserve == 0 ? 0 : RuntimeCalibrationMb + Math.Max(0, additionalCalibrationMb);
        return Math.Min(1_048_576L, (long)configuredReserve + calibration);
    }

    internal static int MinimumContextTokens(ModelRecord model, bool automaticSizing)
    {
        var native = Math.Clamp(model.NativeContextTokens ?? 1_048_576, 512, 1_048_576);
        var maximum = model.MaximumContextTokens > 0 ? Math.Clamp(model.MaximumContextTokens, 512, native) : native;
        var requested = automaticSizing && model.RecommendedContextTokens > 0
            ? model.RecommendedContextTokens
            : model.ContextTokens;
        return Math.Min(maximum, Math.Clamp(requested, 512, native));
    }

    internal static long MinimumTotalContextTokens(ModelRecord model, bool automaticSizing) =>
        (long)MinimumContextTokens(model, automaticSizing) * Math.Clamp(model.ParallelRequests, 1, 16);

    internal static IReadOnlyList<string>? CompanionArguments(ModelRecord model, long fitTargetMb)
    {
        // The standalone companion fits only the main model/context. Unlike llama-server,
        // its supported contract has no projector-memory accounting. Never obtain a
        // text-only fit for a vision model by dropping --mmproj from the request.
        if (model.VisionProjectorPath is not null) return null;
        return
        [
            "--model", model.Path,
            "--parallel", Math.Clamp(model.ParallelRequests, 1, 16).ToString(CultureInfo.InvariantCulture),
            "--fit", "on",
            "--fit-target", fitTargetMb.ToString(CultureInfo.InvariantCulture),
            "--fit-ctx", MinimumTotalContextTokens(model, automaticSizing: true).ToString(CultureInfo.InvariantCulture)
        ];
    }

    public static async Task<LlamaFitContext> FitAsync(ModelRecord model, HostSettings settings, CancellationToken token, int additionalCalibrationMb = 0,
        ModelExecutionAccounting? accounting = null)
    {
        token.ThrowIfCancellationRequested();
        var fitTarget = FitTargetMemoryMb(settings, additionalCalibrationMb);
        var arguments = CompanionArguments(model, fitTarget);
        if (arguments is null)
            return new(null, "Standalone llama-fit-params cannot account for the configured vision projector. Cerebral used its conservative main-model GGUF estimate, which does not include projector memory; llama-server retains the projector and native fitting, and measured post-load reserve verification remains authoritative.");

        var directory = Path.GetDirectoryName(settings.LlamaServerPath) ?? "";
        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "llama-fit-params.exe" : "llama-fit-params");
        if (!File.Exists(executable))
            return new(null, "The llama-fit-params companion is unavailable; Cerebral used its conservative GGUF estimate.");

        var native = Math.Clamp(model.NativeContextTokens ?? 1_048_576, 512, 1_048_576);
        var maximum = model.MaximumContextTokens > 0 ? Math.Clamp(model.MaximumContextTokens, 512, native) : native;
        var requests = Math.Clamp(model.ParallelRequests, 1, 16);
        var configuredReserve = Math.Max(0, settings.ReservedMemoryMb);
        var calibration = fitTarget - configuredReserve;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        LlamaProcessEnvironment.Apply(start, executable);
        var ownership = accounting?.Begin(ModelOwnershipKind.FittingProcess, model.Id, requiresAdmission: false);
        ownership?.Phase(ModelOwnershipPhase.Loading, effectStarted: true);
        ManagedProcess child;
        try { child = ManagedProcess.Start(start.FileName, start.ArgumentList, start.WorkingDirectory,
            LlamaProcessEnvironment.ForExecutable(start.FileName)); }
        catch { ownership?.Unknown("fitting-launch-outcome-unconfirmed"); throw; }
        using var childLifetime = child;
        var process = child.Process;
        ownership?.Bind(processId: process.Id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        Exception? failure = null;
        try
        {
            var outputTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                return new(null, "llama-fit-params did not produce a usable plan: " + BoundedLine(error));
            var context = ParseContext(output, requests, maximum);
            return context is { } fitted
                ? new(fitted, $"llama.cpp fitted {fitted:N0} context tokens per request with a {fitTarget:N0} MiB load target: {configuredReserve:N0} MiB configured headroom plus {calibration:N0} MiB for runtime variance.")
                : new(null, "llama-fit-params returned no valid context argument; Cerebral used its conservative GGUF estimate.");
        }
        catch (Exception error)
        {
            NativeReadinessOwnershipScope.CaptureLoaderFailure(accounting, model.Id, error);
            failure = error; throw;
        }
        finally
        {
            ownership?.Phase(ModelOwnershipPhase.Stopping);
            var confirmed = await child.StopAndConfirmAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (confirmed) ownership?.Complete();
            else
            {
                ownership?.Unknown("fitting-owned-job-or-group-cleanup-unconfirmed");
                if (failure is null) throw new IOException("Native fitting cleanup was not confirmed; no model server was started.");
            }
        }
    }

    internal static int? ParseContext(string output, int parallelRequests, int maximumContextTokens)
    {
        var matches = ContextArgumentPattern().Matches(output);
        if (matches.Count == 0 || !long.TryParse(matches[^1].Groups[1].Value, out var total)) return null;
        var requests = Math.Clamp(parallelRequests, 1, 16);
        var maximum = Math.Clamp(maximumContextTokens, 512, 1_048_576);
        var perRequest = total / requests;
        return perRequest is >= 512 and <= 1_048_576 ? (int)Math.Min(perRequest, maximum) : null;
    }

    internal static int? ParseLoadedContext(IEnumerable<string> log)
    {
        foreach (var line in log.Reverse())
        {
            var match = LoadedContextPattern().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var context) && context is >= 512 and <= 1_048_576)
                return context;
        }
        return null;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        const int maximum = 65_536;
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) return result.ToString();
            if (result.Length + count > maximum) throw new IOException("llama-fit-params output exceeded 64 KiB.");
            result.Append(buffer, 0, count);
        }
    }

    private static string BoundedLine(string value)
    {
        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length == 0 ? "No error details were returned." : text[..Math.Min(text.Length, 400)];
    }

    [GeneratedRegex(@"(?:^|\s)(?:-c|--ctx-size)(?:\s+|=)(\d+)(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ContextArgumentPattern();
    [GeneratedRegex(@"\bn_ctx_slot\s*=\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoadedContextPattern();
}
