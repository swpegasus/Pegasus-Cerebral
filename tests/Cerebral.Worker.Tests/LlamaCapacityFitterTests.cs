using Cerebral.Core;

namespace Cerebral.Worker.Tests;

internal static class LlamaCapacityFitterTests
{
    public static void Run()
    {
        var assertions = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            assertions++;
            Console.WriteLine("PASS: " + name);
        }

        Check(LlamaCapacityFitter.ParseContext("diagnostic\n-c 248320 -ngl -1\n", 1, 262_144) == 248_320,
            "llama-fit-params short context argument becomes the effective per-slot context");
        Check(LlamaCapacityFitter.ParseContext("--ctx-size=131072 --parallel 2", 2, 262_144) == 65_536,
            "a fitted total context is divided across explicit parallel slots");
        Check(LlamaCapacityFitter.ParseContext("-c 248320", 1, 32_768) == 32_768,
            "the optional automatic context ceiling limits a larger runtime fit");
        Check(LlamaCapacityFitter.ParseContext("-ngl -1", 1, 262_144) is null &&
              LlamaCapacityFitter.ParseContext("-c nope", 1, 262_144) is null,
            "missing or malformed runtime context output fails closed to the conservative planner");
        Check(LlamaCapacityFitter.ParseLoadedContext(["initializing, n_slots = 1, n_ctx_slot = 248320, kv_unified = 'false'"]) == 248_320,
            "the server's reported per-slot context can correct the stored effective budget");
        Check(LlamaCapacityFitter.RuntimeCalibrationMb == 1_536,
            "automatic fitting retains a bounded runtime allowance beyond the user-configured reserve");
        Check(LlamaCapacityFitter.FitTargetMemoryMb(new() { ReservedMemoryMb = 1_024 }) == 2_560 &&
              LlamaCapacityFitter.FitTargetMemoryMb(new() { ReservedMemoryMb = 1_024 }, 700) == 3_260 &&
              LlamaCapacityFitter.FitTargetMemoryMb(new() { ReservedMemoryMb = 0 }) == 0,
            "retry calibration increases the runtime target without overriding an explicit zero reserve");
        var automatic = new ModelRecord
        {
            Path = "model.gguf", ContextTokens = 1_024, RecommendedContextTokens = 16_384,
            NativeContextTokens = 262_144, ParallelRequests = 2, GpuLayers = -1,
            AutomaticContextSizing = true, VisionProjectorPath = "projector.gguf"
        };
        var serverArguments = ModelRuntime.ServerArguments(automatic, 26_267, 2_560);
        var fitContextIndex = serverArguments.ToList().IndexOf("--fit-ctx");
        var requestedContextIndex = serverArguments.ToList().IndexOf("--ctx-size");
        Check(fitContextIndex >= 0 && serverArguments[fitContextIndex + 1] == "32768" &&
              requestedContextIndex >= 0 && serverArguments[requestedContextIndex + 1] == "32768" &&
              serverArguments.Contains("--fit-target") && serverArguments.Contains("--mmproj"),
            "the real server launch repairs a previously reduced automatic context and preserves that floor across every slot");
        Check(LlamaCapacityFitter.CompanionArguments(automatic, 2_560) is null,
            "a declared projector refuses a main-model-only companion fit instead of losing projector costs");
        var missingRuntime = new HostSettings { LlamaServerPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "llama-server.exe"), ReservedMemoryMb = 1_024 };
        var projectorFallback = LlamaCapacityFitter.FitAsync(automatic, missingRuntime, CancellationToken.None).GetAwaiter().GetResult();
        Check(projectorFallback.ContextTokens is null &&
              projectorFallback.Basis.Contains("cannot account", StringComparison.Ordinal) &&
              projectorFallback.Basis.Contains("does not include projector memory", StringComparison.Ordinal) &&
              projectorFallback.Basis.Contains("post-load reserve", StringComparison.Ordinal),
            "the real fitting entry point reports the projector limitation before touching an absent companion");
        automatic.VisionProjectorPath = "";
        Check(LlamaCapacityFitter.CompanionArguments(automatic, 2_560) is null,
            "an invalid but declared projector cannot silently become a text-only fit");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var refused = false;
            try { LlamaCapacityFitter.FitAsync(automatic, missingRuntime, cancelled.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { refused = true; }
            Check(refused, "pre-cancelled fitting preserves cancellation instead of returning a fallback result");
        }
        automatic.VisionProjectorPath = null;
        automatic.Path = Path.Combine("fixture model", "model's.gguf");
        var companionArguments = LlamaCapacityFitter.CompanionArguments(automatic, 3_260)!;
        Check(companionArguments.SequenceEqual(new[] { "--model", automatic.Path, "--parallel", "2", "--fit", "on", "--fit-target", "3260", "--fit-ctx", "32768" }),
            "text-only fitting sends a separately bounded model path, calibrated reserve and total two-slot context");
        automatic.MaximumContextTokens = 8_192;
        companionArguments = LlamaCapacityFitter.CompanionArguments(automatic, 2_560)!;
        Check(companionArguments[companionArguments.ToList().IndexOf("--fit-ctx") + 1] == "16384",
            "a lower declared automatic ceiling caps each companion slot before total context is constructed");
        automatic.MaximumContextTokens = 0;
        var unavailable = LlamaCapacityFitter.FitAsync(automatic, missingRuntime, CancellationToken.None).GetAwaiter().GetResult();
        Check(unavailable.ContextTokens is null && unavailable.Basis.Contains("unavailable", StringComparison.Ordinal),
            "a missing text-only companion retains the explicit conservative fallback");
        automatic.VisionProjectorPath = "projector.gguf";
        automatic.AutomaticContextSizing = false;
        automatic.ContextTokens = 8_192;
        serverArguments = ModelRuntime.ServerArguments(automatic, 26_267, 2_560);
        fitContextIndex = serverArguments.ToList().IndexOf("--fit-ctx");
        Check(fitContextIndex >= 0 && serverArguments[fitContextIndex + 1] == "16384",
            "manual context sizing preserves the configured per-request context instead of substituting the recommendation");
        var runtimeDirectory = Path.GetFullPath(Path.Combine("fixture", "runtime"));
        Check(LlamaProcessEnvironment.ComposeLibraryPath(runtimeDirectory, null, '|') == runtimeDirectory,
            "a Linux native runtime directory becomes the complete library path when no inherited path exists");
        Check(LlamaProcessEnvironment.ComposeLibraryPath(runtimeDirectory, $"other|{runtimeDirectory}|more", '|') == $"{runtimeDirectory}|other|more",
            "the bundled native runtime directory is first and deduplicated ahead of inherited library paths");

        Console.WriteLine($"Llama capacity fitter: {assertions} deterministic checks passed (argument, fitting-entry-point and captured-text fixtures; no companion process, model, GPU or inference)." );
    }
}
