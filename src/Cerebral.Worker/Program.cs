using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;

var sourceRecoveryRequested = args.Contains("--source-recovery-manifest", StringComparer.Ordinal);
var offlinePackageRecoveryRequested = args.Contains("--service-package-recover", StringComparer.Ordinal);
var modelAdmissionClosed = args.Contains("--model-admission-closed", StringComparer.Ordinal);
// Operator MCP is an explicit user-side client mode. Its closed argument admission
// rejects mixed host/recovery/private invocations before any Worker initialization.
if (args.Contains("--operator", StringComparer.Ordinal))
    return await OperatorMcpHost.RunInstalledAsync(args);
if (!sourceRecoveryRequested && !offlinePackageRecoveryRequested &&
    ApplicationVersion.TryWrite(args, "Worker")) return 0;

StreamWriter? localLog = null;
ConsoleCancelEventHandler? sourceRecoveryCancel = null;
try
{
    if (sourceRecoveryRequested && offlinePackageRecoveryRequested)
        throw new ArgumentException("Source and offline package recovery modes cannot be combined.");
    // Diagnostic admission is separate from Source recovery\'s existing verified owner barrier.
    if (sourceRecoveryRequested && modelAdmissionClosed)
    {
        Console.Error.WriteLine("Pegasus Cerebral: source_recovery_model_diagnostic_conflict: --model-admission-closed is for independent owner diagnostics; omit it when using --source-recovery-manifest.");
        return 1;
    }
    if (args.Count(value => value == "--model-admission-closed") > 1 ||
        modelAdmissionClosed && !args.Contains("--local-run", StringComparer.Ordinal))
        throw new ArgumentException("model_diagnostic_local_owner_mode_required");
    // A recovery invocation cannot enter installer, connector, direct-access, or installed-host
    // dispatch before its closed manifest/startup contract has been checked.
    if (!sourceRecoveryRequested)
    {
        // A stopped-owner package repair is a separate, bounded maintenance process. It must
        // finish before StateStore, native packages, Engine or any transport is composed.
        if (offlinePackageRecoveryRequested) return ServicePackageOfflineRecovery.Run(args);
        if (await ManagementCommands.TryRunAsync(args) is int managementResult) return managementResult;
        if (args.FirstOrDefault() == "connector") return await ToolDispatcher.RunConnectorAsync(args.Skip(1).ToArray());
        if (args.FirstOrDefault() is "command" or "mcp") return await DirectAccess.RunAsync(args);
    }
    // Runtime admission precedes owner directories, settings, native plugin initialization and
    // engine selection. A compatible transport alone is insufficient for persisted checkpoints.
    Cerebral.Engine.Persistence.EventJournal.ValidateCheckpointContract();
    // A packaged portable Worker must pass the same immutable payload admission as
    // Configuration before creating its data folder or loading bundled plugins.
    var packageRoot = InstallationPaths.InstallDirectory;
    if (File.Exists(Path.Combine(packageRoot, "Start-CerebralPortable.cmd")) ||
        File.Exists(Path.Combine(packageRoot, "portable-manifest.json")))
        PortablePackageAdmission.VerifyAt(packageRoot);
    var groupedPortable = InstallationPaths.IsPortable;
    string? Option(string name) => args.SkipWhile(x => x != name).Skip(1).FirstOrDefault();
    LocalWorkerEndpoint? pipeEndpoint = null;
    InstalledWorkerBinding? installedBinding = null;
    InstalledUserWorkerBinding? installedUserEnrollment = null;
    var installedPipe = args.Contains("--installed-pipe", StringComparer.Ordinal);
    var installedUserPipe = args.Contains("--installed-user-pipe", StringComparer.Ordinal);
    if (sourceRecoveryRequested && (installedPipe || installedUserPipe)) throw new ArgumentException("source_recovery_owner_mode_required");
    if (args.Count(x => x == "--installed-pipe") > 1 ||
        args.Count(x => x == "--installed-user-pipe") > 1 ||
        (installedPipe ? 1 : 0) + (installedUserPipe ? 1 : 0) +
        (args.Contains("--local-run", StringComparer.Ordinal) ? 1 : 0) > 1)
        throw new ArgumentException("Choose exactly one local Worker host mode.");
    if (installedPipe)
    {
        installedBinding = InstalledWorkerBindingStore.ReadRegistered()
            ?? throw new UnauthorizedAccessException("The installed Worker owner binding is unavailable. Repair the installation before starting the service.");
        pipeEndpoint = installedBinding.PipeEndpoint();
    }
    if (args.Any(x => x is "--pipe-name" or "--pipe-instance"))
    {
        if (installedPipe) throw new ArgumentException("Installed pipe identity comes only from protected enrollment.");
        string PipeOption(string name)
        {
            var index = Array.IndexOf(args, name);
            if (args.Count(x => x == name) != 1 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Local pipe startup requires one complete --pipe-name and --pipe-instance pair.");
            return args[index + 1];
        }
        if (!args.Contains("--local-run", StringComparer.Ordinal) && !installedUserPipe)
            throw new ArgumentException("The user application pipe requires an explicit user Worker host mode.");
        pipeEndpoint = new(PipeOption("--pipe-name"), PipeOption("--pipe-instance"));
        pipeEndpoint.Validate();
        LocalWorkerPipeServer.ValidateHostingIdentity();
    }
    if (installedUserPipe)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The installed desktop Worker requires Windows.");
        InstalledUserWorkerAdmission.RequireOrdinaryUserToken();
        InstalledUserWorkerAdmission.RequireStartupArguments(args);
        installedUserEnrollment = InstalledWorkerBindingStore.ReadRegisteredUser()
            ?? throw new UnauthorizedAccessException("The desktop Worker enrollment is unavailable.");
        if (pipeEndpoint is null) throw new ArgumentException("The installed user Worker requires its exact pipe endpoint.");
        InstalledUserWorkerAdmission.RequireEndpoint(installedUserEnrollment, pipeEndpoint);
        InstalledUserWorkerAdmission.RequireHost(installedUserEnrollment,
            InstalledWorkerWindows.ReadCurrentProcessIdentity());
        if (args.Any(x => x is "--engines" or "--service-plugins" or "--desktop-sdk"))
            throw new ArgumentException("Installed user Worker code paths come from the admitted installation.");
    }
    var dataPath = Path.GetFullPath(Option("--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Pegasus", "Cerebral"));
    if ((installedBinding is not null && !InstalledWorkerBinding.PathEquals(dataPath, installedBinding.DataDirectory, true)) ||
        (installedUserEnrollment is not null && !InstalledWorkerBinding.PathEquals(dataPath, installedUserEnrollment.DataDirectory, true)))
        throw new UnauthorizedAccessException("The installed Worker data path does not match protected enrollment.");
    var sourceManifest = sourceRecoveryRequested ? SourceRecoveryManifest.ParseFile(
        Option("--source-recovery-manifest") ?? throw new ArgumentException("source_recovery_manifest_required")) : null;
    sourceManifest?.ValidateStartup(args, dataPath, pipeEndpoint,
        Environment.ProcessPath ?? throw new InvalidOperationException("source_recovery_executable_unknown"));
    if (sourceManifest is null) Directory.CreateDirectory(dataPath);
    // One service owns each store; a second source run must not compete with an installation.
    using var stateLock = new FileStream(Path.Combine(dataPath, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var localRun = new LocalRunControl(args.Contains("--local-run", StringComparer.Ordinal) || installedUserPipe);
    var eventHub = new EventHub();
    var preflight = sourceManifest is null ? null : SourceRecoveryPreflight.Verify(sourceManifest, CancellationToken.None);
    await using var recovery = sourceManifest is null ? null : new SourceRecoveryStartup(sourceManifest, preflight!, localRun);
    localRun.SourceRecovery = recovery;
    using var sourceShutdown = new CancellationTokenSource();
    using var sourceLifetime = CancellationTokenSource.CreateLinkedTokenSource(sourceShutdown.Token,
        recovery?.Shutdown ?? CancellationToken.None);
    SourceRecoveryResumeTicket? resumeTicket = null;
    if (recovery is not null)
    {
        sourceRecoveryCancel = (_, e) =>
        {
            e.Cancel = true; recovery.RequestShutdown();
            try { sourceShutdown.Cancel(); } catch (ObjectDisposedException) { }
        };
        Console.CancelKeyPress += sourceRecoveryCancel;
        localRun.Stop = recovery.RequestShutdown;
        await recovery.StartPipeAsync(pipeEndpoint!, eventHub, sourceLifetime.Token);
        resumeTicket = await recovery.WaitForOwnerResumeAsync(sourceLifetime.Token);
        recovery.BeginValidation(resumeTicket);
        // This is a prerequisite staged by the owner; recovery never silently creates a pause record.
        _ = SourceRecoveryPreflight.VerifyLocalResourcePause(sourceManifest!, resumeTicket.Caller);
    }
    using var composition = CancellationTokenSource.CreateLinkedTokenSource(sourceLifetime.Token,
        resumeTicket?.Caller ?? CancellationToken.None);
    if (recovery is not null) composition.CancelAfter(TimeSpan.FromMinutes(2));
    if (localRun.Enabled && recovery is null)
    {
        // One current log in mutable application data, replaced on the next explicit local launch.
        localLog = new StreamWriter(Path.Combine(dataPath, "local-worker.log"), false) { AutoFlush = true };
        Console.SetOut(localLog); Console.SetError(localLog);
    }
    var store = new StateStore(dataPath, deferDiagnosticPruning: recovery is not null); var configuration = new Configuration(store);
    var inferenceAccounting = ModelExecutionAccounting.For(store, coverageComplete: true, initiallyClosed: modelAdmissionClosed);
    if (recovery is not null) configuration.AttachSourceRecoverySnapshot(recovery.ReadDetachedSettingsWhileClosed);
    var settings = configuration.Get();
    inferenceAccounting.Observation.ConfigureIssuer(settings.InstallationId,
        changed => eventHub.Publish("inference.observation.changed", null, changed));
    if (recovery is null) Configuration.EnsureMutableDirectories(settings, dataPath);
    // Apply host processor policy before composing plugin/model runtimes so supported operating systems
    // pass the effective affinity to direct child processes. Unsupported affinity never blocks startup.
    var processorPolicy = ProcessorPolicy.Apply(settings);
    if (args.Count(x => x == "--auto-port") > 1) throw new ArgumentException("Specify --auto-port at most once.");
    var requestedListenUrl = Option("--url") ?? settings.ListenUrl;
    var autoPort = args.Contains("--auto-port", StringComparer.Ordinal) ||
        Option("--url") is null && settings.AutoSelectAvailablePort ||
        Option("--url") is null && requestedListenUrl == CerebralDefaults.WorkerUrl;
    if (pipeEndpoint is not null && args.Contains("--auto-port", StringComparer.Ordinal))
        throw new ArgumentException("Automatic port selection applies only to an HTTP Worker.");
    var listenUrl = pipeEndpoint is null
        ? WorkerListenEndpoint.Resolve(requestedListenUrl, autoPort)
        : requestedListenUrl;
    var explicitLanServiceBinding = args.Contains("--allow-lan", StringComparer.Ordinal);
    if (pipeEndpoint is null && (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var listenUri) || listenUri.Scheme != "http" || (!listenUri.IsLoopback && !settings.AllowLan && !explicitLanServiceBinding))) throw new ArgumentException("Listen URL must be HTTP; LAN binding must be enabled in saved settings or explicitly in the protected service command.");
    // One composition, two transport hosts. Either pipe mode opens no HTTP listener.
    var builder = pipeEndpoint is null ? WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory }) : null;
    var pipeBuilder = pipeEndpoint is not null ? Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory }) : null;
    var services = builder?.Services ?? pipeBuilder!.Services;
    // Both HTTP and protected-pipe installations can run under the Windows
    // Service Control Manager. Register the generic pipe host as a Windows
    // service too, so it reports startup and observes service lifetime events.
    services.AddWindowsService(options => options.ServiceName = ManagementCommands.ServiceName);
    services.AddSystemd();
    if (builder is not null)
    {
        builder.WebHost.UseUrls(listenUrl);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4 * 1024 * 1024);
        services.ConfigureHttpJsonOptions(options => { options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; });
    }
    if (localRun.Enabled) (builder?.Logging ?? pipeBuilder!.Logging).SetMinimumLevel(LogLevel.Warning);
    services.AddSingleton(localRun);
    services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
    services.AddSingleton(store); services.AddSingleton(configuration); services.AddSingleton(processorPolicy);
    var skillCatalog = new SkillCatalog(store, eventHub);
    if (recovery is null) skillCatalog.EnsureBuiltIns();
    services.AddSingleton(skillCatalog);
    var servicePluginRoot = WorkerPluginPaths.ResolveServiceRoot(Option("--service-plugins"),
        localRun.Enabled, installedPipe || installedUserPipe, groupedPortable, packageRoot, AppContext.BaseDirectory);
    var desktopSdkPath = DesktopPluginSdkBinding.Resolve(Option("--desktop-sdk"), localRun.Enabled,
        installedPipe || installedUserPipe, packageRoot, groupedPortable);
    using var servicePlugins = new ServicePluginRuntime(servicePluginRoot,
        Path.Combine(dataPath, "PluginData"), eventHub,
        (pluginId, packageVersion, policy) => new ScopedPluginSettingsAccess(store, pluginId, packageVersion, policy));
    var conversation = ConversationModuleSelection.Compose(servicePlugins.WorkflowModules,
        servicePlugins.PackageBindings);
    services.AddSingleton(servicePlugins);
    services.AddSingleton(new ServicePackageLifecycle(servicePluginRoot, servicePlugins, eventHub,
        hostBuiltInPackages: conversation.HostBuiltInPackages));
    services.AddSingleton(eventHub); services.AddSingleton(sp =>
    {
        var runtime = new ModelRuntime(store, configuration.Get, sp.GetRequiredService<EventHub>());
        runtime.InitializeInferenceObservation();
        return runtime;
    });
    services.AddSingleton(new RemoteWorkerManagement(configuration));
    var engineRoot = Option("--engines");
    if (engineRoot is not null && (!localRun.Enabled || !Path.IsPathFullyQualified(engineRoot)))
        throw new ArgumentException("An explicit engine folder requires --local-run and an absolute path.");
    var engines = new Cerebral.Engine.Activation.EngineController(engineRoot ?? Path.Combine(AppContext.BaseDirectory, "Engines"),
        Path.Combine(dataPath, "Engine"));
    await engines.InitializeAsync(composition.Token);
    services.AddSingleton(engines);
    services.AddSingleton(sp =>
    {
        var modelRuntime = sp.GetRequiredService<ModelRuntime>();
        return new RemotePreparedInference(modelRuntime,
            (binding, token) => RemotePreparedInference.ValidateModelBinding(store, binding, token),
            prepareMultimodal: (request, images, token) => modelRuntime.PrepareMultimodalAsync(request, images, token));
    });
    services.AddSingleton(sp => new InterfaceToolExtension(store, eventHub,
        () => configuration.Get().MaxExecutableAttemptsPerAssignment,
        () => sp.GetRequiredService<ToolDispatcher>().FullAccessActive));
    services.AddSingleton(new PluginControlToolExtension(store, eventHub));
    services.AddSingleton(sp => new NativePluginManagementToolExtension(store, eventHub, sp.GetRequiredService<WorkerWorkflows>));
    services.AddSingleton(new ConversationToolExtension(store, eventHub));
    services.AddSingleton(sp => new AgentMetricsToolExtension(store,
        () => sp.GetRequiredService<WorkerWorkflows>(),
        () => sp.GetRequiredService<JobScheduler>(), sp.GetRequiredService<ModelRuntime>()));
    services.AddSingleton(sp => new WorkflowDataToolExtension(() => sp.GetRequiredService<WorkerWorkflows>()));
    services.AddSingleton(sp => new CollaborationToolExtension(() => sp.GetRequiredService<CollaborationService>()));
    services.AddSingleton(sp => new SkillToolExtension(store, sp.GetRequiredService<SkillCatalog>()));
    services.AddSingleton(new PluginScaffoldToolExtension(desktopSdkPath, typeof(IServicePlugin).Assembly.Location));
    services.AddSingleton(sp => new ToolDispatcher(configuration.Get,
        servicePlugins.ToolExtensions
            .Append(sp.GetRequiredService<InterfaceToolExtension>())
            .Append(sp.GetRequiredService<PluginControlToolExtension>())
            .Append(sp.GetRequiredService<NativePluginManagementToolExtension>())
            .Append(sp.GetRequiredService<ConversationToolExtension>())
            .Append(sp.GetRequiredService<AgentMetricsToolExtension>())
            .Append(sp.GetRequiredService<WorkflowDataToolExtension>())
            .Append(sp.GetRequiredService<CollaborationToolExtension>())
            .Append(sp.GetRequiredService<SkillToolExtension>())
            .Append(sp.GetRequiredService<PluginScaffoldToolExtension>()), store.DirectoryPath));
    services.AddSingleton(new Cerebral.Engine.Persistence.EventJournal(Path.Combine(dataPath, "engine.db")));
    services.AddSingleton(sp => new Cerebral.Engine.Persistence.CollaborationStore(
        sp.GetRequiredService<Cerebral.Engine.Persistence.EventJournal>()));
    services.AddSingleton(sp => new Cerebral.Engine.Persistence.ConversationCommunicationStore(
        sp.GetRequiredService<Cerebral.Engine.Persistence.EventJournal>()));
    services.AddSingleton<ICollaborationCommunicationGate>(sp =>
        new StoreCollaborationCommunicationGate(sp.GetRequiredService<Cerebral.Engine.Persistence.ConversationCommunicationStore>()));
    services.AddSingleton(sp => new Cerebral.Engine.Resources.AttachmentStore(sp.GetRequiredService<Cerebral.Engine.Persistence.EventJournal>()));
    services.AddSingleton(sp => new AttachmentArtifacts(
        sp.GetRequiredService<Cerebral.Engine.Resources.AttachmentStore>(), store,
        Path.Combine(dataPath, "Artifacts", "Attachments"),
        localRun.Enabled
            ? sp.GetRequiredService<ToolDispatcher>().ExecuteHostUserPaoAsync
            : sp.GetRequiredService<ToolDispatcher>().ExecuteHostPaoAsync));
    services.AddSingleton(sp => new ConfiguredWorkflowModelProvider(sp.GetRequiredService<ModelRuntime>()));
    services.AddSingleton(sp => new WorkerModelAdapters(sp.GetRequiredService<ModelRuntime>(),
        sp.GetRequiredService<RemoteWorkerManagement>(), sp.GetRequiredService<Cerebral.Engine.Resources.AttachmentStore>(),
        sp.GetRequiredService<AttachmentArtifacts>(), sp.GetRequiredService<ConfiguredWorkflowModelProvider>()));
    services.AddSingleton(sp => new WorkerAttachmentTransfers(store,
        sp.GetRequiredService<Cerebral.Engine.Resources.AttachmentStore>(), sp.GetRequiredService<AttachmentArtifacts>()));
    services.AddSingleton(sp =>
    {
        var packageLifecycle = sp.GetRequiredService<ServicePackageLifecycle>();
        var host = new WorkerWorkflows(store, configuration, eventHub,
            conversation.Modules.Append(new WorkerWorkflowEffects(store, configuration,
                sp.GetRequiredService<ModelRuntime>(), sp.GetRequiredService<ToolDispatcher>(),
                sp.GetRequiredService<WorkerModelAdapters>().ForSubmission,
                sp.GetRequiredService<WorkerModelAdapters>().AcquireRequiredAsync,
                sp.GetRequiredService<WorkerModelAdapters>().ForCompactor,
                () => sp.GetRequiredService<WorkerWorkflows>(), diagnosticEvents: eventHub,
                workflowSettingsRuntime: servicePlugins, workflowSettingsLifecycle: packageLifecycle)
                { OwnedCoordinationService = () => sp.GetRequiredService<CollaborationService>() }),
            conversation.Packages.Append(new ServicePluginPackageBinding(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])).ToArray(),
            sp.GetRequiredService<ToolDispatcher>(),
            sp.GetRequiredService<WorkerModelAdapters>().ValidateAcceptedContent,
            sp.GetRequiredService<Cerebral.Engine.Persistence.EventJournal>(),
            (definitionId, requestId, durableCreate) => packageLifecycle.Admit(definitionId, requestId, durableCreate),
            servicePlugins.ResponseHooks, servicePlugins.OutputObservers,
            attachments: sp.GetRequiredService<Cerebral.Engine.Resources.AttachmentStore>(), engine: engines,
            auditors: servicePlugins.WorkflowAuditors, contextRuntime: servicePlugins,
            packageAdmissionWithContributions: (definitionId, requestId, additional, durableCreate) =>
                packageLifecycle.Admit(definitionId, requestId, additional, durableCreate),
            contextPackageProblem: (definitionId, additional) => packageLifecycle.AdmissionProblem(definitionId, additional),
            workflowSettingsLifecycle: packageLifecycle);
        packageLifecycle.Attach(host.Store);
        servicePlugins.AttachSourceBoundWorkflows(host);
        // This is loaded-module capability only. The host-owned gate independently
        // requires an explicit enabled subscription and exact source-profile scope.
        // With no observer module, preserve the existing Store.Submit path entirely.
        if (servicePlugins.HasCommittedRootUserInputObservers)
        {
            var delivery = new CommittedRootUserInputDelivery(servicePlugins.CommittedRootUserInputGate,
                (code, requestId, packageId) => eventHub.Publish("workflow.input.observer", requestId,
                    new { code, packageId }));
            try { host.AttachCommittedRootUserInputDelivery(delivery); }
            catch
            {
                _ = delivery.DisposeAsync();
                throw;
            }
        }
        return host;
    });
    services.AddSingleton(sp => new ActivityJournal(store, eventHub, configuration.Get));
    services.AddSingleton<IToolDispatcher>(sp => new RecordingToolDispatcher(sp.GetRequiredService<ToolDispatcher>(), sp.GetRequiredService<ActivityJournal>()));
    services.AddSingleton<AgentEngine>(); services.AddSingleton<IAgentExecutor>(sp => sp.GetRequiredService<AgentEngine>()); services.AddSingleton<JobScheduler>();
    services.AddSingleton<ICollaborationWorkflowGateway, WorkerCollaborationWorkflowGateway>();
    services.AddSingleton<CollaborationService>();
    services.AddSingleton(sp => new RetentionCoordinator(
        sp.GetRequiredService<Cerebral.Engine.Persistence.EventJournal>(), store, configuration, eventHub));
    services.AddSingleton<PeerAccessControl>();
    services.AddSingleton<CommandDispatcher>();
    if (pipeEndpoint is not null && recovery is null)
        services.AddHostedService(sp => installedBinding is null
            ? installedUserEnrollment is { } desktop
                ? new LocalWorkerPipeServer(pipeEndpoint, sp.GetRequiredService<CommandDispatcher>(), eventHub,
                    LocalUserOwnerBinding.TryCapture(args, dataPath, pipeEndpoint), desktop)
                : new LocalWorkerPipeServer(pipeEndpoint, sp.GetRequiredService<CommandDispatcher>(), eventHub,
                    LocalUserOwnerBinding.TryCapture(args, dataPath, pipeEndpoint))
            : new LocalWorkerPipeServer(installedBinding, sp.GetRequiredService<CommandDispatcher>(), eventHub));
    services.AddHostedService(sp => sp.GetRequiredService<JobScheduler>());
    services.AddHostedService(sp => sp.GetRequiredService<RetentionCoordinator>());
    if (pipeEndpoint is not null)
    {
        var host = pipeBuilder!.Build();
        try
        {
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            localRun.Stop = lifetime.StopApplication;
            if (recovery is not null)
            {
                await SourceRecoveryRuntime.RunAsync(host, recovery, resumeTicket!,
                    host.Services.GetRequiredService<CommandDispatcher>(), engines,
                    host.Services.GetRequiredService<JobScheduler>(), host.Services.GetRequiredService<RetentionCoordinator>(),
                    host.Services.GetRequiredService<ModelRuntime>(), sourceLifetime.Token);
            }
            else
            {
                host.Services.GetRequiredService<ActivityJournal>().RecoverInterrupted();
                new KnowledgeProposals(store, configuration, host.Services.GetRequiredService<IToolDispatcher>(), eventHub).RecoverInterrupted();
                host.Services.GetRequiredService<CollaborationService>().Recover();
                await host.StartAsync();
                await host.WaitForShutdownAsync();
            }
            return 0;
        }
        finally
        {
            // The provider and ModelRuntime own asynchronous preparation/readiness cleanup.
            // Dispose the built-in host asynchronously after its scheduler has stopped.
            if (host is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync();
            else host.Dispose();
        }
    }
    await using var app = builder!.Build();
    app.Services.GetRequiredService<ActivityJournal>().RecoverInterrupted();
    new KnowledgeProposals(store, configuration, app.Services.GetRequiredService<IToolDispatcher>(), app.Services.GetRequiredService<EventHub>()).RecoverInterrupted();
    app.Services.GetRequiredService<CollaborationService>().Recover();
    localRun.Stop = app.Lifetime.StopApplication;
    // Resolve normal startup composition before request baselines. The optional pure route
    // bypasses legacy normalization middleware and requires a key even on loopback.
    var ownerCommands = app.Services.GetRequiredService<CommandDispatcher>();
    OwnerPreparedStreamingHttp.UsePreparedStreamingRoutes(app, configuration, app.Services.GetRequiredService<RemotePreparedInference>());
    OwnerModelSnapshotHttp.UseOwnerModelSnapshotRoute(app, ownerCommands);
    app.Use(async (context, next) =>
    {
        var current = configuration.Get();
        var remote = context.Connection.RemoteIpAddress;
        var local = remote is not null && IPAddress.IsLoopback(remote);
        var peerRoute = context.Request.Path.StartsWithSegments("/api/peer");
        if (context.Request.Headers.ContainsKey("Origin")) { context.Response.StatusCode = 403; await context.Response.WriteAsync("Browser-origin requests are not enabled."); return; }
        if (!local && !current.AllowLan) { context.Response.StatusCode = 403; return; }
        if (peerRoute)
        {
            var peers = context.RequestServices.GetRequiredService<PeerAccessControl>();
            if (!peers.TryAuthenticate(context.Request.Headers[PeerAccessControl.HeaderName].ToString(), out var grant))
            { context.Response.StatusCode = 401; return; }
            context.Items[typeof(PeerAccessGrant)] = grant!;
            await next(context); return;
        }
        if (!local || context.Request.Path.StartsWithSegments("/api/external"))
        {
            var supplied = context.Request.Headers["X-Cerebral-Key"].ToString();
            if (string.IsNullOrEmpty(current.AccessKey) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(current.AccessKey))) { context.Response.StatusCode = 401; return; }
        }
        await next(context);
    });
    app.MapGet("/health", (CommandDispatcher commands) => commands.Health());
    app.MapPost("/api/command", async (CommandRequest request, CommandDispatcher commands, CancellationToken ct) => await commands.ExecuteAsync(request, ct));
    app.MapPost("/api/peer/command", async (HttpContext context, CommandRequest request, PeerAccessControl peers, CancellationToken ct) =>
    {
        if (context.Items[typeof(PeerAccessGrant)] is not PeerAccessGrant grant) return Results.Unauthorized();
        return Results.Json(await peers.ExecuteAsync(grant, request, ct), CerebralJson.Options);
    });
    app.MapPost("/api/external/command", async (CommandRequest request, CommandDispatcher commands, CancellationToken ct) =>
    {
        if (!configuration.Get().ExternalCommandsEnabled) return CommandResult.Fail("Optional MCP/CLI command access is disabled in Integrator.");
        if (string.IsNullOrEmpty(request.ProfileId)) return CommandResult.Fail("Optional access requires an explicit saved agent profile.");
        return await commands.ExecuteAsync(request, ct);
    });
    app.MapPost("/api/models/upload", async (HttpContext context, CommandDispatcher commands) =>
    {
        try
        {
            var limit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>(); if (limit is not null) limit.MaxRequestBodySize = 256L * 1024 * 1024 * 1024;
            using var metadata = JsonDocument.Parse(context.Request.Query["metadata"].ToString());
            return CommandResult.Ok(await commands.ReceiveModelAsync(metadata.RootElement, context.Request.Body, context.Request.ContentLength, context.RequestAborted));
        }
        catch (Exception ex) { return CommandResult.Fail(ex.Message); }
    });
    app.MapGet("/api/events", async (HttpContext context, EventHub events) =>
    {
        context.Response.ContentType = "text/event-stream"; context.Response.Headers.CacheControl = "no-cache";
        using var eventLifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
        var (id, reader) = events.Subscribe();
        try
        {
            await context.Response.WriteAsync(": connected\n\n", context.RequestAborted); await context.Response.Body.FlushAsync(context.RequestAborted);
            await foreach (var item in reader.ReadAllAsync(eventLifetime.Token))
            {
                var json = JsonSerializer.Serialize(item, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false });
                await context.Response.WriteAsync("data: " + json + "\n\n", context.RequestAborted); await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException) { }
        finally { events.Unsubscribe(id); }
    });
    await app.StartAsync();
    WorkerListenEndpoint.WriteReceipt(dataPath, listenUrl);
    await app.WaitForShutdownAsync();
    return 0;
}
catch (OperationCanceledException) when (sourceRecoveryRequested)
{ Console.Error.WriteLine("Pegasus Cerebral: source_recovery_stopped_or_unconfirmed"); return 1; }
catch (Exception ex)
{
    Console.Error.WriteLine("Pegasus Cerebral: " + (sourceRecoveryRequested ?
        ex is SourceRecoveryException recoveryError ? recoveryError.Code : "source_recovery_startup_unconfirmed" : ex.Message));
    return 1;
}
finally
{
    if (sourceRecoveryCancel is not null) Console.CancelKeyPress -= sourceRecoveryCancel;
    localLog?.Dispose();
}

public partial class Program { }

internal static class DirectAccess
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Option(string name) => args.SkipWhile(x => x != name).Skip(1).FirstOrDefault();
        var keyPath = Option("--key-file") ?? throw new ArgumentException("Provide --key-file; access keys are not command-line arguments.");
        var key = (await File.ReadAllTextAsync(keyPath)).Trim();
        var profile = Option("--profile") ?? throw new ArgumentException("Provide --profile for saved grants.");
        using var http = new HttpClient { BaseAddress = new Uri((Option("--url") ?? CerebralDefaults.WorkerUrl).TrimEnd('/') + "/"), Timeout = TimeSpan.FromHours(8) };
        http.DefaultRequestHeaders.Add("X-Cerebral-Key", key);
        async Task<CommandResult> Execute(string operation, JsonElement arguments)
        {
            using var response = await http.PostAsJsonAsync("api/external/command", new CommandRequest(operation, arguments, profile), CerebralJson.Options);
            response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<CommandResult>(CerebralJson.Options) ?? CommandResult.Fail("Empty service response.");
        }
        if (args[0] == "command")
        {
            var operation = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Provide command operation.");
            var json = Option("--json-file") is { } file ? await File.ReadAllTextAsync(file) : Option("--json") ?? "{}";
            using var input = JsonDocument.Parse(json); var result = await Execute(operation, input.RootElement);
            Console.WriteLine(JsonSerializer.Serialize(result, CerebralJson.Options)); return result.Success ? 0 : 1;
        }
        // A removable stdio adapter; all authority and operations remain in the normal service handlers.
        while (await Console.In.ReadLineAsync() is { } line)
        {
            if (line.Length > 4 * 1024 * 1024) throw new InvalidDataException("MCP request exceeds input bound.");
            JsonElement? id = null; object response;
            try
            {
                using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                if (root.TryGetProperty("id", out var value)) id = value.Clone(); else continue;
                var method = root.GetProperty("method").GetString();
                object result = method switch
                {
                    "initialize" => new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "Pegasus Cerebral", version = ApplicationVersion.Current } },
                    "ping" => new { },
                    "tools/list" => new { tools = new[] { new { name = "cerebral_command", description = "Run a structured Cerebral command under the configured agent's grants. Operations include health, resources, metrics.status, diagnostics, jobs.submit/get/cancel/steer, sessions.get/longform/compact, tools.list/call. Administrative grants additionally expose Integrator commands.", inputSchema = new { type = "object", properties = new { operation = new { type = "string" }, arguments = new { type = "object" } }, required = new[] { "operation", "arguments" }, additionalProperties = false } } } },
                    "tools/call" => await Call(root.GetProperty("params")),
                    _ => throw new ArgumentException("Unsupported MCP method.")
                };
                response = new { jsonrpc = "2.0", id, result };
            }
            catch (Exception ex) { response = new { jsonrpc = "2.0", id, error = new { code = -32602, message = ex.Message } }; }
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false })); await Console.Out.FlushAsync();
        }
        return 0;
        async Task<object> Call(JsonElement parameters)
        {
            if (parameters.GetProperty("name").GetString() != "cerebral_command") throw new ArgumentException("Unknown MCP tool.");
            var call = parameters.GetProperty("arguments"); var result = await Execute(CommandDispatcher.Text(call, "operation"), call.GetProperty("arguments"));
            return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, CerebralJson.Options) } }, isError = !result.Success };
        }
    }
}
