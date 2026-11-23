using System.Text.Json;

namespace Cerebral.Worker.Tests;

/// <summary>Normal Programming regression entrypoint; no model/provider or application submission.</summary>
public static class ProgrammingRegressionChecks
{
    public static void Run()
    {
        var legacy = ProgrammingHarnessTests.RunPolicyChecks();
        var staged = ProgrammingStagedWorkflowPolicyTests.Run();
        ProgrammingLibraryTests.Run();
        Console.WriteLine(JsonSerializer.Serialize(new { success = true, legacy, staged,
            library = "ProgrammingLibraryTests.Run completed, including bounded storage-failure/progress checks; its exact count is printed above",
            evidenceTier = "deterministic harness fixtures plus actual disposable local SQLite/filesystem fixtures",
            modelCalls = 0, productApiCalls = 0 }));
    }
}
