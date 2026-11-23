namespace Cerebral.Worker.Tests;

public static partial class ProgrammingHarnessTests
{
    public static async Task RunAsync()
    {
        var checks = RunPolicyChecks();
        var sourceGenerationChecks = await ProgrammingSourceGenerationTests.RunAsync();
        Console.WriteLine($"Programming harness policy tests passed ({checks + sourceGenerationChecks} deterministic checks, including {sourceGenerationChecks} bounded source-generation producer checks; no model generation or sink execution).");
    }
}

