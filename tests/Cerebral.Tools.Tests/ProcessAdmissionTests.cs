using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;

internal static class ProcessAdmissionTests
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException(message);
            checks++;
        }
        var settings = new HostSettings
        {
            Executables = [
                new() { Id = "dotnet", Path = @"C:\private-location\dotnet.exe", PrefixArguments = ["private-prefix-value"] },
                new() { Id = "powershell", Path = @"C:\private-location\pwsh.exe" }
            ]
        };
        var profile = new AgentProfile
        {
            AllowedRoots = [Environment.CurrentDirectory],
            AllowedTools = ["process.run"],
            Capabilities = ["process.execute"]
        };
        await using var dispatcher = new ToolDispatcher(() => settings);
        var context = new ToolExecutionContext(profile, "admission-fixture", "admission-fixture", Environment.CurrentDirectory);
        Task<ToolResult> Call(string id, AgentProfile? selectedProfile = null) =>
            dispatcher.ExecuteAsync(new("process.run", CerebralJson.Element(new
            {
                executableId = id, workingDirectory = Environment.CurrentDirectory, arguments = Array.Empty<string>()
            })), selectedProfile is null ? context : context with { Profile = selectedProfile }, default);
        var missing = await Call(@"C:\private-location\dotnet.exe");
        Check(!missing.Success && missing.Evidence is { Outcome: ToolExecutionOutcome.Rejected, ExitCode: null } &&
            missing.Content.Contains("Registered IDs: dotnet, powershell", StringComparison.Ordinal),
            "Missing executable admission must report bounded actual IDs without dispatch.");
        Check(!missing.Content.Contains("private-location", StringComparison.Ordinal) &&
            !missing.Content.Contains("private-prefix-value", StringComparison.Ordinal),
            "Executable recovery metadata must not expose paths or prefix arguments.");
        var wrongCase = await Call("DOTNET");
        Check(!wrongCase.Success && wrongCase.Evidence?.Outcome == ToolExecutionOutcome.Rejected,
            "Diagnostic suggestions must not relax exact registered ID matching.");
        settings.Executables = [];
        var empty = await Call("unknown");
        Check(!empty.Success && empty.Content.Contains("Registered IDs: none", StringComparison.Ordinal),
            "Empty registration catalogs must remain an actionable rejection.");
        settings.Executables = Enumerable.Range(0, 20).Select(i => new ExecutableRegistration
        {
            Id = $"tool-{i:00}", Path = @"C:\private-location\unused.exe"
        }).ToList();
        settings.Executables.Add(new() { Id = @"C:\private-location\unsafe.exe", Path = @"C:\private-location\unused.exe" });
        var bounded = await Call("unknown");
        Check(!bounded.Success && bounded.Content.Length <= 512 &&
            bounded.Content.Contains("tool-07", StringComparison.Ordinal) &&
            !bounded.Content.Contains("tool-08", StringComparison.Ordinal) &&
            !bounded.Content.Contains("private-location", StringComparison.Ordinal),
            "Only eight safe public IDs may appear in the compact rejection.");
        var denied = await Call("unknown", new AgentProfile { AllowedRoots = [Environment.CurrentDirectory] });
        Check(!denied.Success && !denied.Content.Contains("Registered IDs:", StringComparison.Ordinal),
            "Registration hints must remain behind the normal process tool and capability grants.");

        var fixtureRoot = Path.Combine(Path.GetTempPath(), "cerebral-process-admission-" + Guid.NewGuid().ToString("N"));
        var admittedRoot = Path.Combine(fixtureRoot, "admitted");
        var desktopRoot = Path.Combine(admittedRoot, "Desktop");
        var outsideRoot = Path.Combine(fixtureRoot, "outside");
        Directory.CreateDirectory(desktopRoot);
        Directory.CreateDirectory(outsideRoot);
        try
        {
            foreach (var name in new[] { "Alpha.csproj", "Beta.sln", "Delta.slnx", "Gamma.csproj", "Zeta.csproj" })
                await File.WriteAllTextAsync(Path.Combine(desktopRoot, name), "fixture");
            await File.WriteAllTextAsync(Path.Combine(desktopRoot, "secrets.json"), "fixture");
            await File.WriteAllTextAsync(Path.Combine(desktopRoot, "private-tool.exe"), "fixture");
            await File.WriteAllTextAsync(Path.Combine(outsideRoot, "OutsideOnly.csproj"), "fixture");
            settings.Executables = [new() { Id = "dotnet", Path = @"C:\private-location\dotnet.exe" }];
            var buildProfile = new AgentProfile
            {
                AllowedRoots = [admittedRoot],
                AllowedTools = ["process.run"],
                Capabilities = ["process.execute"]
            };
            var buildContext = new ToolExecutionContext(buildProfile, "build-file-admission", "build-file-admission", admittedRoot);
            Task<ToolResult> Build(string project, AgentProfile? selectedProfile = null) => dispatcher.ExecuteAsync(
                new("process.run", CerebralJson.Element(new
                {
                    executableId = "dotnet", workingDirectory = admittedRoot,
                    arguments = new[] { "build", project }
                })), selectedProfile is null ? buildContext : buildContext with { Profile = selectedProfile }, default);

            var missingProject = Path.Combine("Desktop", "MilestoneCounter.csproj");
            var hinted = await Build(missingProject);
            Check(!hinted.Success && hinted.Evidence is { Outcome: ToolExecutionOutcome.Rejected, ExitCode: null } &&
                  hinted.Content.Contains("MilestoneCounter.csproj", StringComparison.Ordinal) &&
                  hinted.Content.Contains("Some project-file basenames in its admitted parent (up to 4):", StringComparison.Ordinal) &&
                  hinted.Content.Contains("Alpha.csproj", StringComparison.Ordinal) &&
                  hinted.Content.Contains("Beta.sln", StringComparison.Ordinal) &&
                  hinted.Content.Contains("Delta.slnx", StringComparison.Ordinal) &&
                  hinted.Content.Contains("Gamma.csproj", StringComparison.Ordinal) &&
                  !hinted.Content.Contains("Zeta.csproj", StringComparison.Ordinal) &&
                  hinted.Content.Contains("correct the filename segment of the originally supplied path", StringComparison.Ordinal) &&
                  hinted.Content.Contains("Nothing was executed.", StringComparison.Ordinal),
                "A missing admitted project path reports at most four actual sibling project basenames before dispatch.");
            Check(!hinted.Content.Contains(Path.Combine(desktopRoot, "Alpha.csproj"), StringComparison.Ordinal) &&
                  !hinted.Content.Contains("secrets.json", StringComparison.Ordinal) &&
                  !hinted.Content.Contains("private-tool.exe", StringComparison.Ordinal) &&
                  !hinted.Content.Contains("private-location", StringComparison.Ordinal),
                "Missing-project recovery exposes no existing paths, executable registration details, or unrelated configuration filenames.");

            var ungranted = await Build(missingProject, new AgentProfile { AllowedRoots = [admittedRoot] });
            Check(!ungranted.Success && ungranted.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
                  !ungranted.Content.Contains("project-file basenames in its admitted parent", StringComparison.Ordinal) &&
                  !ungranted.Content.Contains("Alpha.csproj", StringComparison.Ordinal),
                "Missing-project hints remain behind the exact process tool and capability grants.");

            var outside = await Build(Path.Combine(outsideRoot, "Missing.csproj"));
            Check(!outside.Success && outside.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
                  outside.Content.Contains("outside the profile's allowed roots", StringComparison.Ordinal) &&
                  !outside.Content.Contains("project-file basenames in its admitted parent", StringComparison.Ordinal) &&
                  !outside.Content.Contains("OutsideOnly.csproj", StringComparison.Ordinal),
                "An out-of-scope missing project keeps the original refusal and reveals no project hints.");

            var unrelated = await Build(Path.Combine(desktopRoot, "secrets.json.missing"));
            Check(!unrelated.Success && unrelated.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
                  unrelated.Content.Contains("The scoped path does not exist:", StringComparison.Ordinal) &&
                  !unrelated.Content.Contains("project-file basenames in its admitted parent", StringComparison.Ordinal),
                "Missing non-project arguments keep the existing generic path refusal.");
        }
        finally { Directory.Delete(fixtureRoot, recursive: true); }
        return checks;
    }
}
