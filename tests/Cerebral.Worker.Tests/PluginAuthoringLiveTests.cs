using System.Diagnostics;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Resources;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Resources;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>
/// Opt-in local acceptance proving that Cerebral's configured lead can consume the published desktop SDK,
/// author and build a fresh plugin package, and queue that exact package for its signed-in desktop.
/// Invoke directly rather than through PAO because file effects use PAO's serialized execution queue.
/// </summary>
public static class PluginAuthoringLiveTests
{
    private const string ModelId = "plugin-authoring-live-model";
    private const string ExpectedPluginId = "acceptance.cerebral.generated-review";
    private const string ExpectedPanelMarker = "Built by Cerebral";
    private const string ExpectedServiceAssembly = "Cerebral.GeneratedReviewService.dll";

    public static async Task RunAsync(string? projectorPath = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native WPF plugin authoring requires Windows.");
        var modelPath = RequireFile(ProgrammingWorkflowLiveTests.DefaultModelPath);
        var serverPath = RequireFile(ProgrammingWorkflowLiveTests.DefaultServerPath);
        var cortexPath = RequireFile(ProgrammingWorkflowLiveTests.DefaultCortexPath);
        projectorPath = projectorPath is null ? null : RequireFile(projectorPath);
        var dotnet = RequireFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"));
        var repository = FindRepository();
        var sdkAssembly = RequireFile(Path.Combine(repository, "src", "Cerebral.Desktop.Plugins", "bin", "Release",
            "net10.0-windows", "Cerebral.Desktop.Plugins.dll"));
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-plugin-authoring-live-" + Guid.NewGuid().ToString("N")));
        var adaptationMarker = "adapt-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(root); var success = false; var checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }

        try
        {
            var project = Path.Combine(root, "project"); var sdk = Path.Combine(project, "sdk");
            var pluginRoot = Path.Combine(project, "Cerebral.GeneratedReviewPlugin");
            var desktopRoot = Path.Combine(pluginRoot, "Desktop");
            var desktopSdk = Path.Combine(pluginRoot, "sdk");
            var serviceRoot = Path.Combine(pluginRoot, "Service");
            Directory.CreateDirectory(sdk); Directory.CreateDirectory(desktopSdk);
            File.Copy(sdkAssembly, Path.Combine(sdk, "Cerebral.Desktop.Plugins.dll"));
            File.Copy(sdkAssembly, Path.Combine(desktopSdk, "Cerebral.Desktop.Plugins.dll"));
            File.Copy(typeof(IServicePlugin).Assembly.Location, Path.Combine(sdk, "Cerebral.Core.dll"));
            File.Copy(typeof(IWorkflowModule).Assembly.Location, Path.Combine(sdk, "Cerebral.Contracts.dll"));
            var desktopContract = SdkContract();
            await File.WriteAllTextAsync(Path.Combine(project, "SDK-CONTRACT.md"), desktopContract, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(project, "DESKTOP-PROJECT-TEMPLATE.csproj.txt"),
                FencedTemplate(desktopContract, "xml"), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(project, "DESKTOP-SOURCE-TEMPLATE.cs"),
                FencedTemplate(desktopContract, "csharp"), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(project, "DESKTOP-MANIFEST-TEMPLATE.json"),
                FencedTemplate(desktopContract, "json"), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(project, "REQUEST.md"), Request(), cancellationToken);
            var serviceContract = ServiceSdkContract();
            await File.WriteAllTextAsync(Path.Combine(project, "SERVICE-SDK-CONTRACT.md"), serviceContract, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(project, "SERVICE-PROJECT-TEMPLATE.csproj.txt"),
                FencedTemplate(serviceContract, "xml"), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(project, "SERVICE-SOURCE-TEMPLATE.cs"),
                FencedTemplate(serviceContract, "csharp"), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(project, "SERVICE-MANIFEST-TEMPLATE.json"),
                FencedTemplate(serviceContract, "json"), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(project, "SERVICE-REQUEST.md"), ServiceRequest(), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(project, "NuGet.Config"),
                "<configuration><packageSources><clear /></packageSources></configuration>", cancellationToken);
            await ValidateDesktopTemplatesAsync(root, desktopContract, sdkAssembly, dotnet, cancellationToken);
            Check(true, "published desktop templates compile against the exact local public SDK assembly");
            await ValidateServiceTemplatesAsync(root, serviceContract, dotnet, cancellationToken);
            Check(true, "published service templates compile against the exact local public SDK assemblies");

            var store = new StateStore(Path.Combine(root, "state"));
            var configuration = new Configuration(store); var settings = configuration.Get();
            settings.LlamaServerPath = serverPath; settings.ReservedMemoryMb = 1024; settings.MaxConcurrentJobs = 1;
            settings.MaxExecutableAttemptsPerAssignment = 2; settings.ExternalCommandsEnabled = true;
            settings.ModelDirectory = Path.Combine(root, "models"); settings.ProjectsDirectory = project;
            settings.SharedDirectory = Path.Combine(root, "shared"); settings.CortexNamespaces = []; settings.CortexWriteNamespaces = [];
            settings.Executables = [new() { Id = "dotnet", Path = dotnet }];
            settings.McpServers = [new()
            {
                Id = "cortex", Enabled = true, ViaUserConnector = false, Executable = cortexPath,
                Arguments = [ConnectorProtocol.StableCortexMcpArgument]
            }];
            configuration.Save(settings);
            var profile = new AgentProfile
            {
                Id = "plugin-authoring-live", Name = "Cerebral plugin authoring acceptance", Role = "lead", ModelId = ModelId,
                WorkerId = "local", ModelWorkerId = "local", FollowsModelBudgets = false,
                ContextTokens = 16384, ResponseTokens = 3072, ReasoningMode = "off", Temperature = 0,
                MaxSteps = 96, MaxSeconds = 720,
                Instructions = "Act as Pegasus Cerebral's plugin engineer. Read the supplied local contract and request, use only the discovered structured tools, inspect real failures, and do not report completion until the required build and interface queue receipts exist. Source files are authoritative: never edit bin or obj output directly; repair source and rebuild.",
                AllowedRoots = [project],
                AllowedTools = ["file.read", "file.list", "file.search", "file.copy", "file.write", "file.edit", "file.mkdir", "process.run",
                    PluginScaffoldToolExtension.VerifyToolName, InterfaceToolExtension.PluginToolName],
                Capabilities = ["filesystem.read", "filesystem.write", "process.execute", "desktop.plugins.manage", "diagnostics"]
            };
            Configuration.ValidateProfile(profile); store.Put("agent", profile.Id, profile);
            store.Put("model", ModelId, new ModelRecord
            {
                Id = ModelId, Name = "Installed Qwen plugin authoring acceptance", Path = modelPath,
                VisionProjectorPath = projectorPath,
                SizeBytes = new FileInfo(modelPath).Length, ContextTokens = 16384, MaximumContextTokens = 16384,
                RecommendedContextTokens = 16384, AutomaticContextSizing = false, ParallelRequests = 1, IdleMinutes = 0
            });

            var events = new EventHub();
            var interfaceTool = new InterfaceToolExtension(store, events, () => settings.MaxExecutableAttemptsPerAssignment);
            var pluginTools = new PluginScaffoldToolExtension(sdkAssembly, typeof(IServicePlugin).Assembly.Location);
            await using var tools = new ToolDispatcher(configuration.Get, [interfaceTool, pluginTools], store.DirectoryPath);
            await using var models = new ModelRuntime(store, configuration.Get, events);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            var host = new WorkerWorkflowEffects(store, configuration, models, tools);
            var workflows = new WorkerWorkflows(store, configuration, events, [new ConversationWorkflowModule(), host],
                [ConversationWorkflowModule.PackageBinding, new(host.Id, WorkerWorkflowEffects.ModuleVersion, true, [])], tools);
            var conversationId = Guid.NewGuid().ToString("N");
            var request = new SubmitWorkflowRequest(Guid.NewGuid().ToString("N"), conversationId, profile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Build the native WPF desktop plugin described by REQUEST.md using SDK-CONTRACT.md and the local SDK assembly. " +
                $"Read both documents first. Create the exact desktop root {desktopRoot}, then call file.copy three times to copy DESKTOP-PROJECT-TEMPLATE.csproj.txt to {Path.Combine(desktopRoot, "Cerebral.GeneratedPlugin.Desktop.csproj")}, DESKTOP-SOURCE-TEMPLATE.cs to {Path.Combine(desktopRoot, "GeneratedDesktopPlugin.cs")}, and DESKTOP-MANIFEST-TEMPLATE.json to {Path.Combine(desktopRoot, "desktop-plugin.json")}. " +
                "Keep the project and manifest scaffolds, but adapt the copied source before building. Read GeneratedDesktopPlugin.cs, then use file.edit with its returned expectedSha256 so the panel's visible text is exactly " +
                $"'{ExpectedPanelMarker} · {adaptationMarker}'. Preserve the public contract and edit source only. " +
                $"Build the exact project {Path.Combine(desktopRoot, "Cerebral.GeneratedPlugin.Desktop.csproj")} in Release with the registered dotnet executable and --disable-build-servers. Repair actual compiler failures if needed. " +
                $"For the build use executableId dotnet, workingDirectory {desktopRoot}, and arguments [build, {Path.Combine(desktopRoot, "Cerebral.GeneratedPlugin.Desktop.csproj")}, --configuration, Release, --disable-build-servers, --verbosity, minimal]. " +
                $"The project copies the source manifest into build output; do not copy or edit any bin or obj file. After a confirmed successful build, call plugin.verify once with root {pluginRoot}, then call plugin.present exactly once with path {Path.Combine(desktopRoot, "bin", "Release", "net10.0-windows", "desktop-plugin.json")}. Do not use interface.present for the plugin or present the source manifest. " +
                "plugin.present only queues desktop review; say so accurately. Do not ask routine questions, use external packages, access the network, create another root, or claim that the panel was displayed.",
                null, CerebralJson.Element(new { workingDirectory = project, responseSharePercent = 25,
                    requiredSuccessfulTools = new[] { "file.copy", "file.read", "file.edit", "process.run",
                        PluginScaffoldToolExtension.VerifyToolName, InterfaceToolExtension.PluginToolName } }), StepLimit: 96);
            var admitted = await workflows.SubmitAsync(request, null, deadline.Token);
            Check(admitted.Status == WorkflowStatus.Queued, "plugin authoring Chat request is durably admitted");
            var finished = await workflows.Runtime.RunAsync(admitted.Id, deadline.Token);
            PrintProgress(workflows.Journal, conversationId);
            Console.WriteLine($"WORKFLOW: status={finished.Status}; steps={finished.Steps}; reason={finished.Error ?? finished.WaitingReason ?? "none"}");
            Check(finished.Status == WorkflowStatus.Completed, "actual configured model completes the bounded plugin-authoring conversation");

            var expectedOutput = Path.Combine(desktopRoot, "bin", "Release", "net10.0-windows");
            var manifestPath = Path.Combine(expectedOutput, "desktop-plugin.json");
            var assemblyPath = Path.Combine(expectedOutput, "Cerebral.GeneratedReviewPlugin.dll");
            Check(File.Exists(manifestPath) && File.Exists(assemblyPath), "fresh Release output contains the model-authored manifest and plugin assembly");
            using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, deadline.Token)))
                Check(manifest.RootElement.GetProperty("id").GetString() == ExpectedPluginId &&
                    manifest.RootElement.GetProperty("assembly").GetString() == Path.GetFileName(assemblyPath),
                    "built package manifest has the requested stable identity and assembly");
            Check(File.ReadAllText(Path.Combine(desktopRoot, "GeneratedDesktopPlugin.cs"))
                    .Contains(ExpectedPanelMarker + " · " + adaptationMarker, StringComparison.Ordinal),
                "configured model adapts the desktop scaffold with the fresh requested marker before building");

            var evidence = ToolResults(workflows.Journal, conversationId);
            Check(evidence.Any(result => result.Name == "process.run" && result.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, ExitCode: 0 }),
                "durable activity contains a confirmed zero-exit build receipt");
            Check(evidence.Any(result => result.Name == PluginScaffoldToolExtension.VerifyToolName &&
                                   result.Evidence?.Outcome is null or ToolExecutionOutcome.Succeeded) &&
                  evidence.Any(result => result.Name == InterfaceToolExtension.PluginToolName &&
                                   result.Evidence?.Outcome is null or ToolExecutionOutcome.Succeeded),
                "desktop authoring activity verifies the current package before queuing its exact built manifest");
            var presentation = new InterfaceRequests(new StateStore(store.DirectoryPath), events).List(conversationId).Single();
            Check(presentation.Source == "workflow" && presentation.Kind == "plugin" && presentation.Status == "pending" &&
                Path.GetFullPath(presentation.Path).Equals(Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase),
                "agent queued the exact built package once for signed-in desktop review without claiming it rendered");

            var serviceConversationId = Guid.NewGuid().ToString("N");
            var originalSummaryBlock = "var summary = style == \"concise\" ? $\"{label}: {words} words, {characters} characters.\"\n" +
                "                : $\"{label}: {words} whitespace-delimited words and {characters} Unicode scalar characters.\";";
            var adaptedSummaryBlock = "var summary = style == \"concise\" ? $\"" + adaptationMarker + ": {label}: {words} words, {characters} characters.\"\n" +
                "                : $\"" + adaptationMarker + ": {label}: {words} whitespace-delimited words and {characters} Unicode scalar characters.\";";
            var serviceRequest = new SubmitWorkflowRequest(Guid.NewGuid().ToString("N"), serviceConversationId, profile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Build the cross-platform service workflow component described by SERVICE-REQUEST.md using the local SDK assemblies. " +
                $"Create the exact service root {serviceRoot}, then call file.copy three times to copy SERVICE-PROJECT-TEMPLATE.csproj.txt to {Path.Combine(serviceRoot, "Cerebral.GeneratedReviewService.csproj")}, " +
                $"SERVICE-SOURCE-TEMPLATE.cs to {Path.Combine(serviceRoot, "GeneratedReviewServicePlugin.cs")}, and SERVICE-MANIFEST-TEMPLATE.json to {Path.Combine(serviceRoot, ServicePluginRuntime.ManifestFileName)}. " +
                "Keep the project and manifest scaffolds, but adapt the copied source before building. Read GeneratedReviewServicePlugin.cs, then use one file.edit with its returned expectedSha256. " +
                "Do not edit ModuleId, WorkflowId, CommandId, schemas, counts, or any other public identifier. Its oldText must be exactly:\n" + originalSummaryBlock +
                "\nIts newText must be exactly:\n" + adaptedSummaryBlock + "\nThis makes both concise and detailed summaries begin with the requested marker while preserving their behavior. " +
                $"Build the exact project {Path.Combine(serviceRoot, "Cerebral.GeneratedReviewService.csproj")} in Release with the registered dotnet executable and --disable-build-servers. Repair actual compiler failures if needed. " +
                $"For the build use executableId dotnet, workingDirectory {serviceRoot}, and arguments [build, {Path.Combine(serviceRoot, "Cerebral.GeneratedReviewService.csproj")}, --configuration, Release, --disable-build-servers, --verbosity, minimal]. " +
                $"After a confirmed successful build, call plugin.verify once with root {pluginRoot}. Do not call plugin.present or interface.present for the service component. " +
                "Do not ask routine questions, use external packages, access the network, create another root, or claim that the generated service loaded or ran.",
                null, CerebralJson.Element(new { workingDirectory = project, responseSharePercent = 25,
                    requiredSuccessfulTools = new[] { "file.copy", "file.read", "file.edit", "process.run", PluginScaffoldToolExtension.VerifyToolName } }), StepLimit: 96);
            var serviceAdmitted = await workflows.SubmitAsync(serviceRequest, null, deadline.Token);
            Check(serviceAdmitted.Status == WorkflowStatus.Queued, "service-plugin authoring Chat request is durably admitted");
            var serviceFinished = await workflows.Runtime.RunAsync(serviceAdmitted.Id, deadline.Token);
            PrintProgress(workflows.Journal, serviceConversationId);
            Console.WriteLine($"SERVICE WORKFLOW: status={serviceFinished.Status}; steps={serviceFinished.Steps}; reason={serviceFinished.Error ?? serviceFinished.WaitingReason ?? "none"}");
            Check(serviceFinished.Status == WorkflowStatus.Completed,
                "actual configured model completes the bounded service-plugin authoring conversation");

            var serviceOutput = Path.Combine(serviceRoot, "bin", "Release", "net10.0");
            var serviceManifestPath = Path.Combine(serviceOutput, ServicePluginRuntime.ManifestFileName);
            var serviceAssemblyPath = Path.Combine(serviceOutput, ExpectedServiceAssembly);
            Check(File.Exists(serviceManifestPath) && File.Exists(serviceAssemblyPath),
                "fresh Release output contains the model-authored service manifest and assembly");
            using (var serviceManifest = JsonDocument.Parse(await File.ReadAllTextAsync(serviceManifestPath, deadline.Token)))
                Check(serviceManifest.RootElement.GetProperty("id").GetString() == ExpectedPluginId &&
                      serviceManifest.RootElement.GetProperty("schemaVersion").GetInt32() == 2 &&
                      serviceManifest.RootElement.GetProperty("assembly").GetString() == ExpectedServiceAssembly,
                    "generated service manifest has the shared identity, schema 2 and exact assembly");
            var serviceEvidence = ToolResults(workflows.Journal, serviceConversationId);
            Check(serviceEvidence.Any(result => result.Name == "process.run" && result.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, ExitCode: 0 }) &&
                  serviceEvidence.Any(result => result.Name == PluginScaffoldToolExtension.VerifyToolName &&
                                                 result.Evidence?.Outcome is null or ToolExecutionOutcome.Succeeded),
                "service authoring activity contains a confirmed zero-exit build followed by package verification");
            var authoredServiceSource = File.ReadAllText(Path.Combine(serviceRoot, "GeneratedReviewServicePlugin.cs"));
            Check(authoredServiceSource.Contains("public const string ModuleId = \"" + ExpectedPluginId + "\";", StringComparison.Ordinal) &&
                  authoredServiceSource.Contains(adaptedSummaryBlock, StringComparison.Ordinal),
                "configured model adapts both service summaries with the fresh marker while preserving public identity");

            var serviceRepository = Path.Combine(root, "generated-service-packages");
            Directory.CreateDirectory(serviceRepository);
            var serviceChild = new ProcessStartInfo(dotnet)
            {
                WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            serviceChild.ArgumentList.Add(typeof(PluginAuthoringLiveTests).Assembly.Location);
            serviceChild.ArgumentList.Add("--generated-service-child");
            serviceChild.ArgumentList.Add(serviceRepository);
            serviceChild.ArgumentList.Add(Path.Combine(root, "generated-service-data"));
            serviceChild.ArgumentList.Add(serviceManifestPath);
            serviceChild.ArgumentList.Add(adaptationMarker);
            using (var serviceProcess = Process.Start(serviceChild) ?? throw new IOException("Could not start the isolated generated-service acceptance."))
            {
                var serviceOutputText = serviceProcess.StandardOutput.ReadToEndAsync(deadline.Token);
                var serviceErrorText = serviceProcess.StandardError.ReadToEndAsync(deadline.Token);
                await serviceProcess.WaitForExitAsync(deadline.Token);
                var childOutput = await serviceOutputText; var childError = await serviceErrorText;
                if (serviceProcess.ExitCode != 0)
                    throw new InvalidOperationException("Generated service-plugin acceptance failed: " + childError + childOutput);
                using var serviceResult = JsonDocument.Parse(childOutput);
                Check(serviceResult.RootElement.GetProperty("loaded").GetBoolean() &&
                      serviceResult.RootElement.GetProperty("lifecycle").GetBoolean() &&
                      serviceResult.RootElement.GetProperty("portableSettings").GetBoolean(),
                    "service lifecycle stages, selects, restarts into and validates the generated package plus portable setting");
                Check(serviceResult.RootElement.GetProperty("questions").GetBoolean(),
                    "generated service workflow reaches its exact two-field durable question set");
                Check(serviceResult.RootElement.GetProperty("completed").GetBoolean(),
                    "generated service workflow resumes the saved answers and returns its adapted deterministic result");
                Check(serviceResult.RootElement.GetProperty("retryStable").GetBoolean(),
                    "exact generated-workflow answer retry returns its original admission receipt and leaves completion unchanged");
                Check(serviceResult.RootElement.GetProperty("effectRows").GetInt64() == 1 &&
                      serviceResult.RootElement.GetProperty("removed").GetBoolean(),
                    "generated service commits one durable effect row, then deactivates and removes through the lifecycle");
            }

            var desktopTests = RequireFile(Path.Combine(repository, "tests", "Cerebral.Desktop.Tests", "bin", "Release",
                "net10.0-windows", "Cerebral.Desktop.Tests.dll"));
            var capturePath = Path.Combine(root, "captures", "Cerebral-GeneratedReview.png");
            var native = new ProcessStartInfo(dotnet)
            {
                WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            native.ArgumentList.Add(desktopTests); native.ArgumentList.Add("--generated-plugin-child");
            native.ArgumentList.Add(manifestPath); native.ArgumentList.Add(capturePath);
            using var nativeProcess = Process.Start(native) ?? throw new IOException("Could not start the isolated native plugin acceptance.");
            var nativeOutput = nativeProcess.StandardOutput.ReadToEndAsync(deadline.Token);
            var nativeError = nativeProcess.StandardError.ReadToEndAsync(deadline.Token);
            await nativeProcess.WaitForExitAsync(deadline.Token);
            var nativeText = await nativeOutput; var nativeErrors = await nativeError;
            if (nativeProcess.ExitCode != 0) throw new InvalidOperationException("Generated native plugin acceptance failed: " + nativeErrors + nativeText);
            string nativeFixture;
            using (var nativeResult = JsonDocument.Parse(nativeText))
            {
                Check(nativeResult.RootElement.GetProperty("success").GetBoolean() &&
                    nativeResult.RootElement.GetProperty("generatedPluginChecks").GetInt32() >= 6 &&
                    nativeResult.RootElement.GetProperty("nativePanelLayout").GetProperty("windowShown").GetBoolean(),
                    "the model-authored package loads, mounts, and renders inside a real shown native WPF window");
                nativeFixture = nativeResult.RootElement.GetProperty("fixturePath").GetString() ??
                    throw new InvalidDataException("Native plugin child did not return its exact cleanup root.");
            }
            var expectedFixtureParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            var resolvedFixture = Path.GetFullPath(nativeFixture);
            if (!resolvedFixture.StartsWith(expectedFixtureParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedFixture).StartsWith("PegasusCerebral.GeneratedPluginTests-", StringComparison.Ordinal) ||
                !Directory.Exists(resolvedFixture))
                throw new InvalidDataException("Native plugin child returned an invalid cleanup root.");
            Directory.Delete(resolvedFixture, true);
            Check(File.Exists(capturePath) && new FileInfo(capturePath).Length > 100,
                "the actual mounted model-authored panel produces a bounded PNG capture");

            var interfaceRequests = new InterfaceRequests(new StateStore(store.DirectoryPath), events);
            var claim = interfaceRequests.Claim(presentation.Id, "plugin-authoring-live-desktop");
            Check(claim.Claimed, "the signed-in desktop claim rechecks and owns the exact pending package once");
            var attachments = new AttachmentStore(workflows.Journal);
            var captureRequestId = "interface-" + presentation.Id;
            AttachmentReference captured;
            await using (var capture = File.OpenRead(capturePath))
            {
                captured = await attachments.ImportAsync(new(profile.Id, conversationId), captureRequestId,
                    Path.GetFileName(capturePath), capture, deadline.Token);
                presentation = interfaceRequests.Complete(presentation.Id, claim.Request.ClaimId!, true,
                    message: "Mounted and captured the model-authored native plugin panel.",
                    resource: new(captured.Id, captured.Revision, captured.MediaType, captured.OriginalName, captured.Length));
            }
            Check(presentation.Status == "completed" && presentation.Resource is { MediaType: "image/png" } retained &&
                attachments.Describe(new(profile.Id, conversationId), retained.Id, retained.Revision).Length == retained.Length,
                "desktop completion retains one scoped immutable PNG coordinate without putting image bytes in chat");

            if (projectorPath is not null)
            {
                profile = store.Get<AgentProfile>("agent", profile.Id)!;
                profile.RequiredModelCapabilities = [ModelCapabilityIds.ImageInput];
                store.Put("agent", profile.Id, profile);
                var visualConversation = Guid.NewGuid().ToString("N");
                var visualStore = new AttachmentStore(workflows.Journal);
                AttachmentReference visualCapture;
                await using (var image = File.OpenRead(capturePath))
                    visualCapture = await visualStore.ImportAsync(new(profile.Id, visualConversation),
                        "plugin-visual-review-capture", Path.GetFileName(capturePath), image, deadline.Token);
                var visualHost = new WorkerWorkflowEffects(store, configuration, models, tools,
                    submission => new LlamaResourceModelAdapter(models, visualStore,
                        new(submission.ProfileId, submission.ConversationId)));
                var visualWorkflows = new WorkerWorkflows(store, configuration, events,
                    [new ConversationWorkflowModule(), visualHost],
                    [ConversationWorkflowModule.PackageBinding, new(visualHost.Id, WorkerWorkflowEffects.ModuleVersion, true, [])],
                    tools, submission => LlamaResourceModelAdapter.ValidateAcceptedContent(visualStore, submission), workflows.Journal);
                var visualRequest = new SubmitWorkflowRequest("plugin-visual-review", visualConversation, profile.Id,
                    ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                    "Inspect the attached application capture. Transcribe the largest visible phrase and classify the content as a panel, dialog, menu, or other. " +
                    "Reply in one short line using phrase=<exact visible phrase>;layout=<classification>. Do not use tools or ask questions.",
                    null, CerebralJson.Element(new { responseSharePercent = 20 }), StepLimit: 20)
                    { AcceptedContent = [new ImageContentPart(visualCapture)] };
                var visualRun = await visualWorkflows.SubmitAsync(visualRequest, null, deadline.Token);
                visualRun = await visualWorkflows.Runtime.RunAsync(visualRun.Id, deadline.Token);
                var visualSummary = Summary(visualRun);
                Check(visualRun.Status == WorkflowStatus.Completed &&
                    visualSummary.Contains(ExpectedPanelMarker, StringComparison.OrdinalIgnoreCase) &&
                    visualSummary.Contains("layout=", StringComparison.OrdinalIgnoreCase),
                    "a separate native-image conversation reads the visible generated marker and returns a layout classification");
                var visualGeneration = GenerationResults(visualWorkflows.Journal, visualConversation).LastOrDefault();
                Check(visualGeneration is not null && visualGeneration.Prepared.AdapterId == "llama.cpp.native.vision" &&
                    visualGeneration.Prepared.InputTokens == visualGeneration.Generation.EvaluatedTokens,
                    "visual review retains measured native-image preparation and evaluation evidence");
            }

            await models.UnloadAsync(ModelId, deadline.Token);
            Check(!models.IsLoaded(ModelId), "test-owned inference process unloads after plugin authoring");
            Console.WriteLine(projectorPath is null
                ? $"Plugin authoring live acceptance: {checks} checks passed with actual local model, PAO file effects, repaired hidden dotnet builds, a generated service workflow/portable setting, SQLite workflow state, native WPF load/mount/capture, and immutable attachment receipt. Image-model review is a separate acceptance boundary."
                : $"Plugin authoring and visual review live acceptance: {checks} checks passed with actual local model, PAO file effects, repaired hidden dotnet builds, a generated service workflow/portable setting, SQLite workflow state, native WPF load/mount/capture, immutable attachment receipt, and native image-model review.");
            success = true;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (success)
            {
                var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(root).StartsWith("cerebral-plugin-authoring-live-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Invalid plugin-authoring cleanup root.");
                Directory.Delete(root, true);
            }
            else Console.WriteLine("RETAINED FOR DIAGNOSIS: " + root);
        }
    }

    private static string Summary(WorkflowRun run) =>
        run.Result is { } result && result.TryGetProperty("summary", out var text) ? text.GetString() ?? "" : "";

    private static string Request() => $$"""
        # Generated review plugin request

        Create one WPF desktop plugin project named `Cerebral.GeneratedReviewPlugin` inside the exact subdirectory
        `Cerebral.GeneratedReviewPlugin`. Create the project file, `GeneratedReviewPlugin.cs`, and
        `desktop-plugin.json` before the first build.
        The assembly and root namespace are `Cerebral.GeneratedReviewPlugin` and the public entry type is
        `Cerebral.GeneratedReviewPlugin.GeneratedReviewPlugin`.

        The package manifest must use schema 1, contract `pegasus.cerebral.desktop`, API 1, ID
        `{{ExpectedPluginId}}`, name `Cerebral Generated Review`, version `1.0.0`, the exact assembly name,
        and the exact public entry type. The supplied project template copies `desktop-plugin.json` to build output;
        do not copy or edit a second manifest under `bin` or `obj`.

        Implement `IDesktopPlugin` with one panel whose ID is `generated-review`, title is `Generated Review`,
        and whose newly-created WPF view visibly contains the exact text `{{ExpectedPanelMarker}}` plus a short
        explanation that this panel was produced through the public SDK. It needs no actions, message replacement,
        network access, service calls, external NuGet packages, or mutable data. Return a `Border`, `Grid`,
        `StackPanel`, or `UserControl` as the panel view; never return or nest a `Window`. Dispose cleanly.

        Reference `sdk/Cerebral.Desktop.Plugins.dll` directly with `Private=false`; use `net10.0-windows`, WPF,
        Windows targeting, implicit usings, and nullable reference types. Build Release using the existing offline
        `NuGet.Config`. Do not modify or copy the SDK binary. Never edit files under `bin` or `obj`; the source
        project and source `desktop-plugin.json` are authoritative, and every repair must be followed by a rebuild.
        """;

    private static string SdkContract() => """
        # Pegasus Cerebral desktop SDK subset

        Namespace: `Cerebral.Desktop.Plugins`. Plugin code runs in the signed-in desktop process. The entry type
        implements `IDesktopPlugin`, is public and concrete, and has a public parameterless constructor. Panel
        factories return a fresh, unparented embeddable WPF element each time; never return or nest a `Window`.

        Start from this complete compiling project shape. Keep the SDK namespace import and the `IDesktopPlugin`
        implementation; changing or removing either makes all desktop contract types unavailable.

        ```xml
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0-windows</TargetFramework>
            <UseWPF>true</UseWPF>
            <EnableWindowsTargeting>true</EnableWindowsTargeting>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <AssemblyName>Cerebral.GeneratedReviewPlugin</AssemblyName>
            <RootNamespace>Cerebral.GeneratedReviewPlugin</RootNamespace>
          </PropertyGroup>
          <ItemGroup>
            <Reference Include="Cerebral.Desktop.Plugins">
              <HintPath>..\sdk\Cerebral.Desktop.Plugins.dll</HintPath>
              <Private>false</Private>
            </Reference>
            <None Update="desktop-plugin.json" CopyToOutputDirectory="PreserveNewest" />
          </ItemGroup>
          <Target Name="RequireDesktopPluginManifest" BeforeTargets="PrepareForBuild">
            <Error Condition="!Exists('desktop-plugin.json')" Text="The required source desktop-plugin.json is missing. Restore the source manifest before building." />
          </Target>
        </Project>
        ```

        ```csharp
        using Cerebral.Desktop.Plugins;
        using System.Windows.Controls;

        namespace Cerebral.GeneratedReviewPlugin;

        public sealed class GeneratedReviewPlugin : IDesktopPlugin
        {
            public IReadOnlyList<DesktopPanel> Panels =>
            [
                new DesktopPanel("generated-review", "Generated Review", () => new Border
                {
                    Child = new TextBlock { Text = "Built by Cerebral" }
                })
            ];

            public IReadOnlyList<DesktopAction> ComposerActions => [];
            public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;
            public void Initialize(IDesktopPluginHost host) { }
            public void Dispose() { }
        }
        ```

        Use this exact version 1 manifest shape. `assembly` is the adjacent DLL filename, including its `.dll`
        extension. Each lower-camel-case field appears once; additional fields are rejected.

        ```json
        {
          "schemaVersion": 1,
          "contract": "pegasus.cerebral.desktop",
          "apiVersion": 1,
          "id": "acceptance.cerebral.generated-review",
          "name": "Cerebral Generated Review",
          "version": "1.0.0",
          "description": "A generated review panel built through the public SDK.",
          "assembly": "Cerebral.GeneratedReviewPlugin.dll",
          "entryType": "Cerebral.GeneratedReviewPlugin.GeneratedReviewPlugin"
        }
        ```

        The build copies this exact source manifest beside the output DLL. Never create or edit a second manifest
        in `bin` or `obj`; repair the source manifest and rebuild when package admission reports an error.
        """;

    private static string ServiceRequest() => $$"""
        # Generated review service request

        Add the service component of package `{{ExpectedPluginId}}` in the exact existing subdirectory
        `Cerebral.GeneratedReviewPlugin/Service`. Create `Cerebral.GeneratedReviewService.csproj`,
        `GeneratedReviewServicePlugin.cs`, and `service-plugin.json` before the first build.

        The assembly and root namespace are `Cerebral.GeneratedReviewService`. The public entry type is
        `Cerebral.GeneratedReviewService.GeneratedReviewServicePlugin`. The service manifest uses schema 2,
        contract `pegasus.cerebral.service`, API 1, the shared package ID `{{ExpectedPluginId}}`, version `1.0.0`,
        no dependencies, and one optional portable string setting named `defaultStyle`. Its maximum is 16 UTF-8
        bytes and its allowed values are `concise` and `detailed`.

        Implement `IServicePlugin` and `IWorkflowModule`. Contribute workflow
        `{{ExpectedPluginId}}:review` version `1.0.0` and deterministic command
        `{{ExpectedPluginId}}:measure` version 1. The workflow input is one required `text` string from 1 through
        32768 characters. It must first ask required fields `label` and `style`; `style` has choices `concise` and
        `detailed`. After answers it invokes the measure command and completes with that command's object result.
        The command reports `label`, `style`, whitespace-delimited `wordCount`, Unicode scalar `characterCount`,
        and `summary`. Require capability `diagnostics`. The command is idempotent, uses no model, files, network,
        packages, owner commands, legacy tools, or mutable static state, and its reconciliation is the same pure
        measurement. Dispose cleanly.

        Reference `sdk/Cerebral.Core.dll` and `sdk/Cerebral.Contracts.dll` directly with `Private=false`; use
        `net10.0`, implicit usings, and nullable reference types. Copy the one source `service-plugin.json` to build
        output. Use the complete compiling shape in `SERVICE-SDK-CONTRACT.md`. Build Release using the existing
        offline `NuGet.Config`. Never edit `bin`, `obj`, or either SDK assembly; repair source and rebuild.
        """;

    private static string ServiceSdkContract() => """"
        # Pegasus Cerebral service SDK subset

        Service code runs inside Worker as trusted native code. `IServicePlugin` is in `Cerebral.Core`.
        `IWorkflowModule` and workflow records are in `Cerebral.Contracts.Workflows`. Use this exact compiling
        project shape:

        ```xml
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <AssemblyName>Cerebral.GeneratedReviewService</AssemblyName>
            <RootNamespace>Cerebral.GeneratedReviewService</RootNamespace>
          </PropertyGroup>
          <ItemGroup>
            <Reference Include="Cerebral.Core"><HintPath>..\..\sdk\Cerebral.Core.dll</HintPath><Private>false</Private></Reference>
            <Reference Include="Cerebral.Contracts"><HintPath>..\..\sdk\Cerebral.Contracts.dll</HintPath><Private>false</Private></Reference>
            <None Update="service-plugin.json" CopyToOutputDirectory="PreserveNewest" />
          </ItemGroup>
          <Target Name="RequireServicePluginManifest" BeforeTargets="PrepareForBuild">
            <Error Condition="!Exists('service-plugin.json')" Text="The required source service-plugin.json is missing. Restore the source manifest before building." />
          </Target>
        </Project>
        ```

        Use this complete service source, changing no public identifiers or schemas:

        ```csharp
        using System.Text;
        using System.Text.Json;
        using Cerebral.Contracts.Workflows;
        using Cerebral.Core;

        namespace Cerebral.GeneratedReviewService;

        public sealed class GeneratedReviewServicePlugin : IServicePlugin, IWorkflowModule
        {
            public const string ModuleId = "@@PLUGIN_ID@@";
            private const string WorkflowId = ModuleId + ":review";
            private const string CommandId = ModuleId + ":measure";
            private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

            public string Id => ModuleId;
            public IReadOnlyList<IToolExtension> ToolExtensions { get; } = [];
            public IReadOnlyList<IServiceCommandExtension> CommandExtensions { get; } = [];
            public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new ReviewWorkflow()];
            public IReadOnlyList<IWorkflowCommandHandler> Commands { get; } = [new MeasureCommand()];
            public void Initialize(IServicePluginHost host)
            {
                ArgumentNullException.ThrowIfNull(host);
                if (host.PluginId != ModuleId) throw new InvalidOperationException("Unexpected package identity.");
            }
            public void Dispose() { }

            private sealed class ReviewWorkflow : IWorkflowDefinition
            {
                public WorkflowDefinitionDescriptor Descriptor { get; } = new(WorkflowId, "1.0.0",
                    "Ask for a label and style, then measure supplied text deterministically.",
                    Schema("""{"type":"object","properties":{"text":{"type":"string","minLength":1,"maxLength":32768}},"required":["text"],"additionalProperties":false}"""),
                    ["diagnostics"]);

                public WorkflowStep Advance(WorkflowRun snapshot)
                {
                    if (snapshot.Signal.Kind == WorkflowSignalKind.Start)
                        return new(WorkflowContract.EmptyObject, new AskWorkflowQuestions([
                            new("label", "Label for this review"),
                            new("style", "Presentation style", Choices: ["concise", "detailed"])
                        ]));
                    if (snapshot.Signal.Kind == WorkflowSignalKind.Answers)
                    {
                        var label = RequiredText(snapshot.Signal.Data, "label");
                        var style = RequiredText(snapshot.Signal.Data, "style");
                        var text = RequiredText(snapshot.Submission.Input, "text");
                        return new(JsonSerializer.SerializeToElement(new { label, style }, Json),
                            new InvokeWorkflowCommand(CommandId, 1,
                                JsonSerializer.SerializeToElement(new { text, label, style }, Json)));
                    }
                    if (snapshot.Signal.Kind == WorkflowSignalKind.EffectResult)
                    {
                        var result = snapshot.Signal.Data.Deserialize<WorkflowCommandResult>(Json)
                            ?? throw new InvalidDataException("The generated measurement returned no result.");
                        return result.State == WorkflowEffectState.Succeeded && result.Data is { } data
                            ? new(snapshot.State, new CompleteWorkflow(data.Clone()))
                            : new(snapshot.State, new CompleteWorkflow(result.Data ?? WorkflowContract.EmptyObject,
                                false, result.Error ?? "The generated measurement failed."));
                    }
                    throw new InvalidOperationException("Unsupported workflow signal.");
                }
            }

            private sealed class MeasureCommand : IWorkflowCommandHandler
            {
                public WorkflowCommandDescriptor Descriptor { get; } = new(CommandId, 1,
                    "Measure supplied text without external effects.",
                    Schema("""{"type":"object","properties":{"text":{"type":"string","minLength":1,"maxLength":32768},"label":{"type":"string","minLength":1,"maxLength":200},"style":{"type":"string","enum":["concise","detailed"]}},"required":["text","label","style"],"additionalProperties":false}"""),
                    Schema("""{"type":"object","properties":{"label":{"type":"string"},"style":{"type":"string"},"wordCount":{"type":"integer","minimum":0},"characterCount":{"type":"integer","minimum":0},"summary":{"type":"string"}},"required":["label","style","wordCount","characterCount","summary"],"additionalProperties":false}"""),
                    ["diagnostics"], WorkflowReplayPolicy.Idempotent, TimeoutSeconds: 5);

                public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    var text = RequiredText(context.Arguments, "text");
                    var label = RequiredText(context.Arguments, "label");
                    var style = RequiredText(context.Arguments, "style");
                    var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                    var characters = text.EnumerateRunes().Count();
                    var summary = style == "concise" ? $"{label}: {words} words, {characters} characters."
                        : $"{label}: {words} whitespace-delimited words and {characters} Unicode scalar characters.";
                    var data = JsonSerializer.SerializeToElement(new
                        { label, style, wordCount = words, characterCount = characters, summary }, Json);
                    return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Succeeded, data,
                        ProviderReceiptId: "measure:" + context.EffectId));
                }

                public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token) =>
                    ExecuteAsync(context, token);
            }

            private static string RequiredText(JsonElement value, string name) =>
                value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(item.GetString()) ? item.GetString()! :
                throw new ArgumentException("Required text is missing: " + name);
            private static JsonElement Schema(string value) => JsonDocument.Parse(value).RootElement.Clone();
        }
        ```

        Use this exact schema-2 manifest. Each lower-camel-case field appears once:

        ```json
        {
          "schemaVersion": 2,
          "contract": "pegasus.cerebral.service",
          "apiVersion": 1,
          "id": "@@PLUGIN_ID@@",
          "name": "Cerebral Generated Review",
          "version": "1.0.0",
          "description": "A generated deterministic review workflow built through the public SDK.",
          "assembly": "@@SERVICE_ASSEMBLY@@",
          "entryType": "Cerebral.GeneratedReviewService.GeneratedReviewServicePlugin",
          "dependencies": [],
          "portableSettings": {
            "version": 1,
            "fields": [
              {
                "name": "defaultStyle",
                "type": "string",
                "required": false,
                "maximumUtf8Bytes": 16,
                "allowedValues": ["concise", "detailed"]
              }
            ]
          }
        }
        ```

        Keep one source manifest. Build output is derived and must never be repaired directly.
        """".Replace("@@PLUGIN_ID@@", ExpectedPluginId, StringComparison.Ordinal)
            .Replace("@@SERVICE_ASSEMBLY@@", ExpectedServiceAssembly, StringComparison.Ordinal);

    private static string FencedTemplate(string contract, string language)
    {
        var marker = "```" + language + "\n";
        var start = contract.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("The service contract is missing its " + language + " template.");
        start += marker.Length;
        var end = contract.IndexOf("\n```", start, StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException("The service contract has an unterminated " + language + " template.");
        return contract[start..end] + Environment.NewLine;
    }

    private static async Task ValidateDesktopTemplatesAsync(string root, string contract, string sdkAssembly,
        string dotnet, CancellationToken cancellationToken)
    {
        var validationRoot = Path.Combine(root, "desktop-contract-validation");
        var sdk = Path.Combine(validationRoot, "sdk");
        var plugin = Path.Combine(validationRoot, "Cerebral.GeneratedReviewPlugin");
        Directory.CreateDirectory(sdk); Directory.CreateDirectory(plugin);
        File.Copy(sdkAssembly, Path.Combine(sdk, "Cerebral.Desktop.Plugins.dll"));
        var project = Path.Combine(plugin, "Cerebral.GeneratedReviewPlugin.csproj");
        await File.WriteAllTextAsync(project, FencedTemplate(contract, "xml"), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(plugin, "GeneratedReviewPlugin.cs"),
            FencedTemplate(contract, "csharp"), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(plugin, "desktop-plugin.json"),
            FencedTemplate(contract, "json"), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(validationRoot, "NuGet.Config"),
            "<configuration><packageSources><clear /></packageSources></configuration>", cancellationToken);
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = plugin, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "build", project, "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start desktop-template validation.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidDataException("Published desktop templates do not compile:\n" + await error + await output);
        Directory.Delete(validationRoot, recursive: true);
    }

    private static async Task ValidateServiceTemplatesAsync(string root, string contract, string dotnet,
        CancellationToken cancellationToken)
    {
        var validationRoot = Path.Combine(root, "contract-validation");
        var sdk = Path.Combine(validationRoot, "sdk");
        var service = Path.Combine(validationRoot, "Cerebral.GeneratedReviewPlugin", "Service");
        Directory.CreateDirectory(sdk); Directory.CreateDirectory(service);
        File.Copy(typeof(IServicePlugin).Assembly.Location, Path.Combine(sdk, "Cerebral.Core.dll"));
        File.Copy(typeof(IWorkflowModule).Assembly.Location, Path.Combine(sdk, "Cerebral.Contracts.dll"));
        var project = Path.Combine(service, "Cerebral.GeneratedReviewService.csproj");
        await File.WriteAllTextAsync(project, FencedTemplate(contract, "xml"), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(service, "GeneratedReviewServicePlugin.cs"),
            FencedTemplate(contract, "csharp"), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(service, ServicePluginRuntime.ManifestFileName),
            FencedTemplate(contract, "json"), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(validationRoot, "NuGet.Config"),
            "<configuration><packageSources><clear /></packageSources></configuration>", cancellationToken);
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = service, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "build", project, "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start service-template validation.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidDataException("Published service templates do not compile:\n" + await error + await output);
        Directory.Delete(validationRoot, recursive: true);
    }

    public static async Task RunGeneratedServiceChildAsync(string repository, string dataRoot,
        string sourceManifestPath, string adaptationMarker,
        CancellationToken cancellationToken = default)
    {
        repository = Path.GetFullPath(repository); dataRoot = Path.GetFullPath(dataRoot);
        sourceManifestPath = Path.GetFullPath(sourceManifestPath);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!repository.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
            !dataRoot.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
            !sourceManifestPath.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(adaptationMarker) || adaptationMarker.Length > 32)
            throw new UnauthorizedAccessException("Generated service acceptance is confined to temporary fixture roots.");
        Directory.CreateDirectory(dataRoot);
        var store = new StateStore(Path.Combine(dataRoot, "state"));
        var configuration = new Configuration(store);
        var events = new EventHub();
        var host = configuration.Get();
        host.DefaultModelId = "unused";
        host.ModelDirectory = Path.Combine(dataRoot, "models");
        host.ProjectsDirectory = Path.Combine(dataRoot, "projects");
        host.SharedDirectory = Path.Combine(dataRoot, "shared");
        configuration.Save(host);
        store.Put("model", "unused", new ModelRecord
        {
            Id = "unused", Name = "Unused deterministic fixture", Path = Path.Combine(dataRoot, "models", "unused.gguf")
        });
        var profile = new AgentProfile
        {
            Id = "generated-service-child", Name = "Generated service child", Role = "lead", ModelId = "unused",
            FollowsDefaultModel = true, WorkerId = "local", ModelWorkerId = "local", AllowedRoots = [dataRoot],
            Capabilities = ["diagnostics"], PluginSettings = new(StringComparer.Ordinal)
            {
                [ExpectedPluginId] = CerebralJson.Element(new { defaultStyle = "detailed" })
            }
        };
        Configuration.ValidateProfile(profile); store.Put("agent", profile.Id, profile);
        var lifecycleStore = new WorkflowStore(new EventJournal(Path.Combine(dataRoot, "lifecycle-engine.db")));
        ServicePackageLifecycleStatus selected;
        using (var emptyRuntime = new ServicePluginRuntime(repository, dataRoot, events))
        {
            var lifecycle = new ServicePackageLifecycle(repository, emptyRuntime, events);
            lifecycle.Attach(lifecycleStore);
            var staged = lifecycle.Stage(sourceManifestPath, lifecycle.Inspect(ExpectedPluginId).Revision);
            selected = lifecycle.Activate(ExpectedPluginId, staged.Revision);
        }
        var lifecycleSelected = selected is { DesiredActive: true, RestartRequired: true };
        var dotnet = RequireFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"));
        var activeChild = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        activeChild.ArgumentList.Add(typeof(PluginAuthoringLiveTests).Assembly.Location);
        activeChild.ArgumentList.Add("--generated-service-active-child");
        activeChild.ArgumentList.Add(repository);
        activeChild.ArgumentList.Add(dataRoot);
        activeChild.ArgumentList.Add(adaptationMarker);
        using var activeProcess = Process.Start(activeChild) ?? throw new IOException("Could not start the generated service Worker generation.");
        var activeOutputTask = activeProcess.StandardOutput.ReadToEndAsync(cancellationToken);
        var activeErrorTask = activeProcess.StandardError.ReadToEndAsync(cancellationToken);
        await activeProcess.WaitForExitAsync(cancellationToken);
        var activeOutput = await activeOutputTask; var activeError = await activeErrorTask;
        if (activeProcess.ExitCode != 0)
            throw new InvalidOperationException("Generated service Worker generation failed: " + activeError + activeOutput);
        var phase = CerebralJson.Read<GeneratedServicePhase>(JsonDocument.Parse(activeOutput).RootElement);
        bool removed;
        using (var inactiveRuntime = new ServicePluginRuntime(repository, dataRoot, events))
        {
            var lifecycle = new ServicePackageLifecycle(repository, inactiveRuntime, events);
            lifecycle.Attach(lifecycleStore);
            var inactive = lifecycle.Inspect(ExpectedPluginId);
            var absent = lifecycle.Remove(ExpectedPluginId, inactive.Revision);
            removed = !inactive.ObservedLoaded && !inactive.RestartRequired && absent.State == "absent";
        }
        var lifecycleComplete = lifecycleSelected && phase.Loaded && phase.Deactivated;
        if (!lifecycleComplete || !phase.PortableSettings || !phase.Completed || !phase.RetryStable || phase.EffectRows != 1 || !removed)
            throw new InvalidOperationException("Generated service acceptance did not satisfy its runtime contract.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            success = true, loaded = phase.Loaded, lifecycle = lifecycleComplete, portableSettings = phase.PortableSettings,
            questions = phase.Questions, completed = phase.Completed, retryStable = phase.RetryStable,
            effectRows = phase.EffectRows, removed
        }, CerebralJson.Options));
    }

    public static async Task RunGeneratedServiceActiveChildAsync(string repository, string dataRoot,
        string adaptationMarker, CancellationToken cancellationToken = default)
    {
        repository = Path.GetFullPath(repository); dataRoot = Path.GetFullPath(dataRoot);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!repository.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
            !dataRoot.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(adaptationMarker) || adaptationMarker.Length > 32)
            throw new UnauthorizedAccessException("Generated service Worker acceptance is confined to temporary fixture roots.");
        var store = new StateStore(Path.Combine(dataRoot, "state"));
        var configuration = new Configuration(store);
        var events = new EventHub();
        var profile = store.Get<AgentProfile>("agent", "generated-service-child")
            ?? throw new InvalidDataException("Generated service profile is missing.");
        var lifecycleStore = new WorkflowStore(new EventJournal(Path.Combine(dataRoot, "lifecycle-engine.db")));
        using var generatedRuntime = new ServicePluginRuntime(repository, dataRoot, events);
        var lifecycle = new ServicePackageLifecycle(repository, generatedRuntime, events);
        lifecycle.Attach(lifecycleStore);
        var observed = lifecycle.Inspect(ExpectedPluginId);
        var loaded = generatedRuntime.Status.Single() is { Id: ExpectedPluginId, Loaded: true, Error: null } &&
                     !observed.RestartRequired && observed.ObservedGeneration == observed.DesiredGeneration;
        var portableSettings = generatedRuntime.PortableSettingsSchemas[ExpectedPluginId].Fields.Single() is
            { Name: "defaultStyle", Type: "string", MaximumUtf8Bytes: 16 };
        var backups = new ConfigurationBackups(store, () => false,
            pluginSettingsSchemas: () => generatedRuntime.PortableSettingsSchemas);
        var exported = backups.Export(true);
        var portableEntry = exported.Document.Agents.Single(x => x.Id == profile.Id).PluginSettings!.Single();
        profile = store.Get<AgentProfile>("agent", profile.Id)!;
        profile.PluginSettings[ExpectedPluginId] = CerebralJson.Element(new { defaultStyle = "concise" });
        store.Put("agent", profile.Id, profile);
        var preview = backups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(exported.Document)), true);
        var restored = backups.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(
            preview.PreviewId, preview.ExpectedHostRevision, true)), true);
        portableSettings = portableSettings && portableEntry.Values.GetProperty("defaultStyle").GetString() == "detailed" &&
            restored.PortablePluginSettingsRestored == 1 &&
            store.Get<AgentProfile>("agent", profile.Id)!.PluginSettings[ExpectedPluginId]
                .GetProperty("defaultStyle").GetString() == "detailed";

        var generatedWorkflows = new WorkerWorkflows(store, configuration, events,
            generatedRuntime.WorkflowModules, generatedRuntime.PackageBindings);
        var generatedRequest = new SubmitWorkflowRequest("generated-review-service-run", "generated-review-service-chat",
            profile.Id, ExpectedPluginId + ":review", "1.0.0", "Review this generated service passage.",
            "Acceptance uses the generated deterministic workflow without model inference.",
            CerebralJson.Element(new { text = "one two three four" }), StepLimit: 8);
        var generatedRun = generatedWorkflows.Submit(generatedRequest, null);
        generatedRun = await generatedWorkflows.Runtime.RunAsync(generatedRun.Id, cancellationToken);
        var questions = generatedRun.Status == WorkflowStatus.Waiting && generatedRun.Questions?.Fields.Count == 2;
        if (!questions) throw new InvalidOperationException("Generated service workflow did not publish its exact question set.");
        var generatedAnswer = new WorkflowAnswerRequest("generated-review-service-answer", generatedRun.Id,
            generatedRun.Questions!.Id, generatedRun.Questions.Revision,
            new Dictionary<string, string> { ["label"] = "Generated passage", ["style"] = "detailed" });
        generatedWorkflows.Answer(generatedAnswer, null);
        generatedRun = await generatedWorkflows.Runtime.RunAsync(generatedRun.Id, cancellationToken);
        var completed = generatedRun.Status == WorkflowStatus.Completed &&
                        generatedRun.Result?.GetProperty("wordCount").GetInt32() == 4 &&
                        generatedRun.Result?.GetProperty("style").GetString() == "detailed" &&
                        generatedRun.Result?.GetProperty("summary").GetString()?.StartsWith(adaptationMarker + ": ", StringComparison.Ordinal) == true;
        var effectCountBeforeRetry = EffectResultCount(store.DirectoryPath, generatedRun.Id);
        var answerRetry = generatedWorkflows.Answer(generatedAnswer, null);
        var retainedRun = generatedWorkflows.Store.Get(generatedRun.Id);
        var effectRows = EffectResultCount(store.DirectoryPath, generatedRun.Id);
        var retryStable = answerRetry.Id == generatedRun.Id && answerRetry.Status == WorkflowStatus.Queued &&
                          answerRetry.Questions?.Answers.GetValueOrDefault("label") == "Generated passage" &&
                          retainedRun.Status == WorkflowStatus.Completed && retainedRun.Revision == generatedRun.Revision &&
                          effectCountBeforeRetry == 1 && effectRows == effectCountBeforeRetry;
        var deactivated = lifecycle.Deactivate(ExpectedPluginId, observed.Revision);
        Console.WriteLine(JsonSerializer.Serialize(new GeneratedServicePhase(loaded, portableSettings, questions,
            completed, retryStable, effectRows, !deactivated.DesiredActive && deactivated.RestartRequired), CerebralJson.Options));
    }

    private sealed record GeneratedServicePhase(bool Loaded, bool PortableSettings, bool Questions,
        bool Completed, bool RetryStable, long EffectRows, bool Deactivated);

    private static long EffectResultCount(string stateRoot, string runId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(stateRoot, "engine.db"), Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM workflow_effect_results WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", runId);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static string RequireFile(string path) => File.Exists(path) ? Path.GetFullPath(path) :
        throw new FileNotFoundException("Plugin-authoring live prerequisite is missing.", path);

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pegasus.Cerebral.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Pegasus Cerebral repository root was not found.");
    }

    private static IReadOnlyList<WorkerWorkflowEffects.ToolData> ToolResults(EventJournal journal, string conversationId)
    {
        var results = new List<WorkerWorkflowEffects.ToolData>(); long after = 0; long? snapshot = null;
        do
        {
            var page = journal.ReadPage(conversationId, after, 100, snapshot); snapshot ??= page.SnapshotSequence;
            foreach (var part in page.Events.Where(e => e.Type == CerebralEventTypes.ToolResult)
                         .SelectMany(e => e.Content.OfType<StructuredContentPart>())
                         .Where(p => p.Schema == "workflow.command.result"))
            {
                var result = CerebralJson.Read<WorkflowCommandResult>(part.Value);
                if (result.Data is { } data && data.TryGetProperty("name", out _))
                    results.Add(CerebralJson.Read<WorkerWorkflowEffects.ToolData>(data));
            }
            if (page.NextAfterSequence is not { } next) break;
            after = next;
            if (results.Count > 512) throw new InvalidDataException("Plugin-authoring activity exceeded its inspection bound.");
        } while (true);
        return results;
    }

    private static IReadOnlyList<WorkerWorkflowEffects.GenerationData> GenerationResults(EventJournal journal, string conversationId)
    {
        var results = new List<WorkerWorkflowEffects.GenerationData>(); long after = 0; long? snapshot = null;
        do
        {
            var page = journal.ReadPage(conversationId, after, 100, snapshot); snapshot ??= page.SnapshotSequence;
            foreach (var part in page.Events.Where(e => e.Type == CerebralEventTypes.ToolResult)
                         .SelectMany(e => e.Content.OfType<StructuredContentPart>())
                         .Where(p => p.Schema == "workflow.command.result"))
            {
                var result = CerebralJson.Read<WorkflowCommandResult>(part.Value);
                if (result.Data is { } data && data.TryGetProperty("generation", out _))
                    results.Add(CerebralJson.Read<WorkerWorkflowEffects.GenerationData>(data));
            }
            if (page.NextAfterSequence is not { } next) break;
            after = next;
            if (results.Count > 64) throw new InvalidDataException("Plugin visual-review generation evidence exceeded its inspection bound.");
        } while (true);
        return results;
    }

    private static void PrintProgress(EventJournal journal, string conversationId)
    {
        foreach (var result in ToolResults(journal, conversationId))
            Console.WriteLine($"TOOL: {result.Name}; outcome={result.Evidence?.Outcome}; provider={result.Evidence?.ProviderId}; exit={result.Evidence?.ExitCode}; {Bound(result.Content)}");
    }

    private static string Bound(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
