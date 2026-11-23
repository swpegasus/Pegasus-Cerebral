using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length == 1 && args[0] == "--internet-owner-policy-only")
{
    var checks = await Cerebral.Worker.Tests.InternetOwnerPolicyChecks.RunAsync();
    Console.WriteLine($"{checks} Internet owner policy checks passed; disposable SQLite and deterministic native transitions; no Worker, model, browser or network launch.");
    return;
}

if (args.Length == 1 && args[0] == "--managed-internet-broker-only")
{
    var checks = await Cerebral.Worker.Tests.ManagedInternetBrokerChecks.RunAsync();
    Console.WriteLine($"{checks} managed Internet broker checks passed; fake clock and HTTP transport; no live network evidence.");
    return;
}

if (args.Length == 1 && args[0] == "--plugin-internet-marker-only")
{
    var checks = await Cerebral.Worker.Tests.PluginInternetMarkerChecks.RunAsync();
    Console.WriteLine($"{checks} plugin Internet marker checks passed; deterministic wrappers and offline catalog; no plugin, browser or network launch.");
    return;
}

if (args.Length == 1 && args[0] == "--isolated-text-only")
{
    var checks = await Cerebral.Worker.Tests.IsolatedTextGenerationTests.RunAsync();
    Console.WriteLine($"{checks} isolated text checks passed; deterministic model fakes and disposable SQLite only; no Worker/model/provider/Hold integration asserted.");
    return;
}

if (args.Length == 1 && args[0] == "--service-package-offline-recovery-only")
{
    Console.WriteLine($"Service package offline recovery checks passed ({Cerebral.Worker.Tests.ServicePackageOfflineRecoveryChecks.Run()}; disposable stores and packages, maintenance Worker executable only, no normal Worker or model launch).");
    return;
}

if (args.Length == 1 && args[0] == "--private-metrics-runtime-provenance-only")
{
    foreach (var assembly in new[] { typeof(object).Assembly, typeof(JsonSerializer).Assembly })
    {
        var version = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        Console.WriteLine($"{assembly.GetName().Name}: {assembly.Location}; informationalVersion={version}");
    }
    Console.WriteLine($"framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; dynamicCode={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
    return;
}

if (args.Length == 2 && args[0] == "--scalar-generation-host-only")
{
    var checks = await Cerebral.Worker.Tests.ScalarGenerationHostFacetTests.RunAsync(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "scalar_generation_host_checks_passed", checks,
        evidenceTier = "actual_Core_Worker_objects_with_deterministic_numeric_samples_and_private_host_lifetime",
        workerLaunch = false, modelLaunch = false, network = false, packageLoad = true
    }));
    return;
}

if (args.Length == 2 && args[0] == "--agent-profile-noop-only")
{
    var checks = await Cerebral.Worker.Tests.AgentProfileNoOpSaveTests.RunAsync(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "agent_profile_noop_save_checks_passed", checks,
        evidenceTier = "actual_CommandDispatcher_and_private_StateStore_SQLite",
        workerLaunch = false, modelLaunch = false, network = false
    }));
    return;
}

if (args.Length == 1 && args[0] == "--agent-metrics-only")
{
    var checks = await Cerebral.Worker.Tests.AgentMetricsToolTests.RunAsync();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "agent_metrics_checks_passed", checks,
        evidenceTier = "deterministic_host_scope_and_snapshot_projection",
        workerLaunch = false, modelLaunch = false, network = false, permissionUi = false
    }));
    return;
}

if (args.Length > 0 && args[0] == "--source-bound-title-loaded-only")
{
    if (args.Length != 3)
        throw new ArgumentException("Loaded title checks require an exact private parent and built package directory.");
    var checks = await Cerebral.Worker.Tests.TitleConsumerLoadedChecks.RunAsync(args[1], args[2]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "source_bound_title_loaded_checks_passed", checks,
        evidenceTier = "actual_collectible_title_loader_and_central_temporary_SQLite",
        observerDelivered = false, workflowAdmitted = false,
        normalWorker = false, model = false, physicalUi = false
    }));
    return;
}

if (args.Length == 1 && args[0] == "--source-bound-background-marker-only")
{
    var checks = Cerebral.Worker.Tests.SourceBoundBackgroundWorkerChecks.Run();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "source_bound_background_marker_checks_passed", checks,
        evidenceTier = "deterministic_frozen_snapshot_parser",
        workerLaunch = false, modelLaunch = false, network = false
    }));
    return;
}

if (args.Length == 2 && args[0] == "--source-bound-background-host-only")
{
    var checks = await Cerebral.Worker.Tests.SourceBoundHostSettingsLeaseChecks.RunAsync(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "source_bound_background_host_checks_passed", checks,
        evidenceTier = "actual_private_host_settings_and_collectible_package_lifetime",
        workerLaunch = false, modelLaunch = false, network = false
    }));
    return;
}

if (args.Length == 2 && args[0] == "--source-bound-background-scheduler-only")
{
    var checks = await Cerebral.Worker.Tests.SourceBoundSchedulerReviewChecks.RunAsync(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "source_bound_background_scheduler_checks_passed", checks,
        evidenceTier = "actual_JobScheduler_and_temporary_Engine_SQLite_with_deterministic_pause",
        workerLaunch = false, modelLaunch = false, network = false
    }));
    return;
}

if (args.Length == 2 && args[0] == "--source-bound-background-effect-only")
{
    var checks = await Cerebral.Worker.Tests.SourceBoundEffectReviewChecks.RunAsync(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "source_bound_background_effect_checks_passed", checks,
        evidenceTier = "actual_Worker_effect_and_Engine_exact_receipt_temporary_SQLite",
        workerLaunch = false, modelLaunch = false, network = false
    }));
    return;
}

if (args.Length == 2 && args[0] == "--source-bound-background-admission-only")
{
    var checks = await Cerebral.Worker.Tests.SourceBoundAdmissionReviewChecks.RunAsync(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "source_bound_background_admission_checks_passed", checks,
        evidenceTier = "actual_Worker_submit_and_temporary_Engine_SQLite_with_fixture_package",
        workerLaunch = false, modelLaunch = false, network = false
    }));
    return;
}

if (args.Length == 2 && args[0] == "--source-bound-background-review-only")
{
    void LanePassed(string lane, int checks) => Console.WriteLine(JsonSerializer.Serialize(new
    { status = "source_bound_background_review_lane_passed", lane, checks }));
    var marker = Cerebral.Worker.Tests.SourceBoundBackgroundWorkerChecks.Run();
    LanePassed("marker", marker);
    var host = await Cerebral.Worker.Tests.SourceBoundHostSettingsLeaseChecks.RunAsync(args[1]);
    LanePassed("host", host);
    var admission = await Cerebral.Worker.Tests.SourceBoundAdmissionReviewChecks.RunAsync(args[1]);
    LanePassed("admission", admission);
    var scheduler = await Cerebral.Worker.Tests.SourceBoundSchedulerReviewChecks.RunAsync(args[1]);
    LanePassed("scheduler", scheduler);
    var effect = await Cerebral.Worker.Tests.SourceBoundEffectReviewChecks.RunAsync(args[1]);
    LanePassed("effect", effect);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "source_bound_background_review_checks_passed",
        marker, host, admission, scheduler, effect,
        evidenceTier = "controlled_actual_host_and_temporary_SQLite",
        workerLaunch = false, modelLaunch = false, network = false
    }));
    return;
}

if (args.Length > 0 && args[0] == "--committed-root-user-input-only")
{
    if (args.Length != 2 || !Path.IsPathFullyQualified(args[1]))
        throw new ArgumentException("Committed-input fixtures require one exact absolute owned parent.");
    var delivery = await Cerebral.Worker.Tests.CommittedRootUserInputDeliveryChecks.RunAsync();
    var submit = await Cerebral.Worker.Tests.CommittedRootUserInputWorkerSubmitChecks.RunAsync(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "committed_root_user_input_checks_passed", delivery, submit,
        evidenceTier = "controlled_observer_gates_and_actual_Worker_submit_temporary_SQLite",
        modelLaunch = false, normalWorkerLaunch = false, network = false
    }));
    return;
}

if (args.Length > 0 && args[0] == "--workflow-portable-settings-integration-only")
{
    if (args.Length != 2 || !Path.IsPathFullyQualified(args[1]))
        throw new ArgumentException("Portable settings integration fixtures require one exact absolute private parent.");
    await Cerebral.Worker.Tests.WorkflowPortableSettingsIntegrationLane.RunAsync(args[1]);
    return;
}

if (args.Length > 0 && args[0] == "--configured-model-adapters-only")
{
    if (args.Length != 2 || !Path.IsPathFullyQualified(args[1]))
        throw new ArgumentException("Configured model adapter fixtures require one exact absolute task fixture root.");
    var configured = await Cerebral.Worker.Tests.ConfiguredModelAdaptersChecks.RunAsync(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "normal_configured_model_adapters_checks_passed",
        configured, total = configured.Total,
        evidenceTier = "deterministic_owned_lifecycle_and_fake_provider_fixtures",
        nativeInference = false, modelLaunch = false, deployment = false
    }));
    return;
}

if (args.Length > 0 && args[0] == "--native-model-adapters-only")
{
    if (args.Length != 2 || !Path.IsPathFullyQualified(args[1]))
        throw new ArgumentException("Native model adapter fixtures require one exact absolute task fixture root.");
    var native = await Cerebral.Worker.Tests.NativeModelAdaptersChecks.RunAsync(args[1]);
    var configured = await Cerebral.Worker.Tests.ConfiguredModelAdaptersChecks.RunAsync(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "normal_native_model_adapters_checks_passed",
        native, configured, total = native.Total + configured.Total,
        evidenceTier = "deterministic_owned_lifecycle_and_fake_provider_fixtures",
        nativeInference = false, modelLaunch = false, deployment = false
    }));
    return;
}

if (args.Length > 0 && args[0] == "--workflow-context-contributions-only")
{
    if (args.Length != 2 || !Path.IsPathFullyQualified(args[1]))
        throw new ArgumentException("Workflow context fixtures require one exact absolute private parent.");
    var context = await Cerebral.Worker.Tests.WorkerContextContributionTests.RunAsync(args[1]);
    Console.WriteLine($"Workflow context contributions: {context.SnapshotChecks} structural pure callback checks; {context.HostChecks} host/actual SQLite checks; {context.ProjectionChecks} actual SQLite projection checks with synthetic normalized receipts; {context.RuntimeChecks} actual WorkflowRuntime/SQLite checks with deterministic fake providers; {context.Total} total.");
    Cerebral.Worker.Tests.ContextProviderDiscoveryTests.Run();
    Console.WriteLine("No Worker launch, package Initialize, service, Source, model runtime, scheduler, provider network or inference execution. Fake provider callback execution is isolated to the runtime fixture.");
    return;
}
if (Environment.GetEnvironmentVariable(Cerebral.Worker.Tests.ModelRuntimeCleanupTests.FixtureEnvironment) is { Length: > 0 } fixtureSignal)
{
    await Cerebral.Worker.Tests.ModelRuntimeCleanupTests.RunServerAsync(args, fixtureSignal); return;
}
if (args.Length > 0 && args[0] == "--local-generation-loader-only")
{
    var reviewedPackage = args.Length == 4;
    if (args.Length is not (3 or 4) || !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]) ||
        reviewedPackage && args[3] is not ("1.0.7" or "1.0.8" or "1.0.9" or "1.0.10" or "1.0.11"))
        throw new ArgumentException("Loader fixtures require an exact absolute reviewed Conversation manifest and fresh owned parent; the optional expected package version must be 1.0.7 through 1.0.11.");
    if (reviewedPackage && (new FileInfo(args[1]).Length is <= 0 or > ServicePluginContract.MaxManifestBytes))
        throw new ArgumentException("The reviewed package manifest exceeds its existing byte bound.");
    using var manifest = JsonDocument.Parse(File.ReadAllText(args[1]));
    var exports = Cerebral.Worker.Tests.LocalGenerationConversationExportChecks.Run();
    var selection = Cerebral.Worker.Tests.LocalGenerationConversationExportChecks.CheckManifest(
        manifest.RootElement, reviewedPackage ? args[3] : "1.0.6");
    var loader = reviewedPackage
        ? await Cerebral.Worker.Tests.LoaderJoinCompatibilityChecks.VerifyAsync(args[2], args[1], args[3])
        : await Cerebral.Worker.Tests.LoaderJoinCompatibilityChecks.VerifyAsync(args[2]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "local_generation_loader_checks_passed", exports, selection, loader,
        evidenceTier = "isolated_actual_package_loader_and_strict_registry_with_deterministic_export_checks",
        realWorker = false, realModel = false, gpu = false, selectedRuntimeChanged = false
    }));
    return;
}
if (args.Length == 1 && args[0] == "--local-generation-conversation-only")
{
    var count = Cerebral.Worker.Tests.LocalGenerationConversationChecks.Run();
    Console.WriteLine(JsonSerializer.Serialize(new { status = "local_generation_conversation_checks_passed", count, realModel = false, gpu = false }));
    return;
}
if (args.Length > 0 && args[0] == "--local-generation-v2-only")
{
    if (args.Length != 2 || !Path.IsPathFullyQualified(args[1]))
        throw new ArgumentException("Local generation fixtures require one exact absolute private parent.");
    var localPolicy = await Cerebral.Worker.Tests.LocalExecutionPolicyChecks.RunAsync();
    var enginePolicy = await Cerebral.Worker.Tests.LocalGenerationEngineChecks.RunAsync();
    var conversationPolicy = Cerebral.Worker.Tests.LocalGenerationConversationChecks.Run();
    var configured = await Cerebral.Worker.Tests.ConfiguredModelAdaptersChecks.RunAsync(args[1]);
    var nativeEntry = await Cerebral.Worker.Tests.RuntimeSupportNativeEntryChecks.RunAsync();
    var registry = await Cerebral.Worker.Tests.ModelAdapterRegistryTests.RunAsync();
    var reasoning = await Cerebral.Worker.RegisteredReasoningIntegrationChecks.RunAsync();
    await Cerebral.Worker.Tests.ModelRuntimeCleanupTests.RunAsync();
    await Cerebral.Worker.Tests.ConversationHarnessTests.RunAsync();
    await Cerebral.Worker.Tests.WorkflowConversationEffectsTests.RunAsync();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "local_generation_v2_focused_checks_passed",
        localPolicy = localPolicy.Length, enginePolicy, conversationPolicy,
        configured, nativeEntry, registry = registry.Length, reasoning = reasoning.Length,
        evidence = "deterministic contracts, real SQLite and controlled executable lifecycle; no real model or GPU",
        liveModel = false, gpu = false, ui = false
    }));
    return;
}
if (args.Length == 1 && args[0] == "--tool-catalog-discovery-only")
{
    var catalog = Cerebral.Worker.Tests.ConversationCatalogDiscoveryChecks.Run();
    Console.WriteLine($"Conversation catalog discovery: {catalog} deterministic policy checks passed.");
    await Cerebral.Worker.Tests.ConversationHarnessTests.RunAsync();
    await Cerebral.Worker.Tests.WorkflowConversationEffectsTests.RunAsync();
    await Cerebral.Worker.Tests.CoordinationHarnessBindingTests.RunAsync();
    await Cerebral.Worker.Tests.NativeCortexContinuityTests.RunAsync();
    await Cerebral.Worker.Tests.PluginQaSourceCatalogTests.RunAsync();
    return;
}

if (args.Length == 1 && args[0] == "--runtime-support-query-only")
{
    var query = await Cerebral.Worker.Tests.RuntimeSupportQueryChecks.RunAsync();
    Console.WriteLine($"RuntimeSupport query: {query} deterministic checks passed.");
    return;
}
if (args.Length == 1 && args[0] == "--runtime-support-only")
{
    var scope = Cerebral.Worker.Tests.RuntimeSupportScopeCodeChecks.Run();
    var instance = Cerebral.Worker.Tests.RuntimeSupportNativeInstanceChecks.Run();
    var entry = await Cerebral.Worker.Tests.RuntimeSupportNativeEntryChecks.RunAsync();
    Console.WriteLine($"Runtime support checks passed: {scope + instance} deterministic; {entry} controlled entry lifecycle");
    return;
}
if (args.Length == 1 && args[0] == "--native-request-shape-only")
{
    var scope = Cerebral.Worker.Tests.RuntimeSupportScopeCodeChecks.Run();
    var instance = Cerebral.Worker.Tests.RuntimeSupportNativeInstanceChecks.Run();
    var entry = await Cerebral.Worker.Tests.RuntimeSupportNativeEntryChecks.RunAsync();
    var registry = await Cerebral.Worker.Tests.ModelAdapterRegistryTests.RunAsync();
    var reasoning = await Cerebral.Worker.RegisteredReasoningIntegrationChecks.RunAsync();
    await Cerebral.Worker.Tests.ModelRuntimeCleanupTests.RunAsync();
    Console.WriteLine($"Native request shape: {scope + instance} structural, {entry} controlled entry, " +
        $"{registry.Length} registry, {reasoning.Length} reasoning checks; controlled cleanup passed.");
    return;
}
if (args.Length > 0 && args[0].StartsWith("--owner-model-snapshot-", StringComparison.Ordinal))
{
    if (args.Length != 2) throw new ArgumentException("Owner snapshot fixtures require one explicit absolute task-owned root.");
    switch (args[0])
    {
        case "--owner-model-snapshot-minimal-only":
            await Cerebral.Worker.Tests.OwnerModelSnapshotPathTests.RunMinimalAsync(args[1]); break;
        case "--owner-model-snapshot-aliases-only":
            await Cerebral.Worker.Tests.OwnerModelSnapshotPathTests.RunAliasesAsync(args[1]); break;
        case "--owner-model-snapshot-catalog-only":
            await Cerebral.Worker.Tests.OwnerModelSnapshotPathTests.RunCatalogAsync(args[1]); break;
        case "--owner-model-snapshot-races-only":
            await Cerebral.Worker.Tests.OwnerModelSnapshotRaceTests.RunAsync(args[1]); break;
        case "--owner-model-snapshot-pipe-backpressure-only":
            await Cerebral.Worker.Tests.OwnerModelSnapshotBackpressureTests.RunAsync(args[1]); break;
        case "--owner-model-snapshot-http-controlled-backpressure-only":
            await Cerebral.Worker.Tests.OwnerModelSnapshotBackpressureTests.RunHttpAsync(args[1]); break;
        case "--owner-model-snapshot-http-alias-controlled-backpressure-only":
            await Cerebral.Worker.Tests.OwnerModelSnapshotBackpressureTests.RunAliasHttpAsync(args[1]); break;
        case "--owner-model-snapshot-only":
            Cerebral.Worker.Tests.OwnerModelSnapshotCodecTests.Run();
            await Cerebral.Worker.Tests.OwnerModelSnapshotPathTests.RunAsync(args[1]);
            await Cerebral.Worker.Tests.OwnerModelSnapshotPathTests.RunCatalogAsync(args[1]);
            await Cerebral.Worker.Tests.OwnerModelSnapshotPathTests.RunAliasesAsync(args[1]);
            await Cerebral.Worker.Tests.OwnerModelSnapshotRaceTests.RunAsync(args[1]);
            await Cerebral.Worker.Tests.OwnerModelSnapshotBackpressureTests.RunAsync(args[1]);
            await Cerebral.Worker.Tests.OwnerModelSnapshotBackpressureTests.RunHttpAsync(args[1]); break;
        default: throw new ArgumentException("Unknown owner snapshot fixture selector.");
    }
    return;
}
if (args.Length == 4 && args[0] == "--worker-runtime-contract-only")
{
    await Cerebral.Worker.Tests.WorkerRuntimeContractTests.RunAsync(args[1], args[2], args[3]);
    return;
}
if (args.Length == 1 && args[0] == "--conversation-paths-only")
{
    await Cerebral.Worker.Tests.ConversationPathGrantsTests.RunAsync();
    return;
}
if (args.Length == 1 && args[0] == "--conversation-properties-only")
{
    await Cerebral.Worker.Tests.ConversationPropertiesTests.RunAsync();
    await Cerebral.Worker.Tests.WorkflowConversationRootTests.RunAsync();
    return;
}
if (args.Length == 1 && args[0] == "--workflow-data-only")
{
    await Cerebral.Worker.Tests.WorkflowDataCatalogTests.RunAsync();
    await Cerebral.Worker.Tests.WorkflowDataModuleTests.RunAsync();
    await Cerebral.Worker.Tests.WorkflowDataHostTests.RunAsync();
    return;
}
if (args.Length == 1 && args[0] == "--native-cortex-continuity-only")
{
    await Cerebral.Worker.Tests.NativeCortexContinuityTests.RunAsync();
    await Cerebral.Worker.Tests.NativeContinuityAdmissionSchedulerTests.RunAsync();
    return;
}
if (args.Length == 2 && args[0] == "--native-cortex-continuity-interop")
{ Environment.ExitCode = await Cerebral.Worker.Tests.NativeContinuityInteropProbe.RunAsync(args[1]); return; }
if (args.Length == 1 && args[0] == "--cortex-asset-commands-only") { await Cerebral.Worker.Tests.CortexAssetCommandTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--image-role-readiness-only") { await Cerebral.Worker.Tests.ImageRoleReadinessTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--engine-host-live") { await Cerebral.Worker.Tests.EngineHostTests.RunAsync(); return; }
if (args.Length == 4 && args[0] == "--source-engine-canary")
{ await Cerebral.Worker.Tests.SourceEngineAcceptance.RunAsync(args[1], args[2], long.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)); return; }
if (args.Length == 4 && args[0] == "--native-cortex-worker-acceptance")
{ await Cerebral.Worker.Tests.CortexNativeWorkerAcceptance.RunAsync(args[1], args[2], args[3]); return; }
if (args.Length == 4 && args[0] == "--source-native-cortex-switch")
{ await Cerebral.Worker.Tests.CortexNativeWorkerAcceptance.SwitchSourceAsync(args[1], args[2], args[3]); return; }
if (args.Length == 1 && args[0] == "--inference-retirement-observation-only") { Cerebral.Worker.Tests.InferenceRetirementObservationTests.Run(); return; }
if (args.Length == 1 && args[0] == "--resource-usage-ownership-only") { await Cerebral.Worker.Tests.RemotePreparedInferenceTests.RunResourceUsageOwnershipAsync(); return; }
if (args.Length == 1 && args[0] == "--remote-prepared-inference-only") { await Cerebral.Worker.Tests.RemotePreparedInferenceTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--model-distribution-only") { await Cerebral.Worker.Tests.ModelDistributionTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--theme-presets-only") { await Cerebral.Worker.Tests.RemoteWorkerManagementTests.RunThemePresetsAsync(); return; }
if (args.Length == 1 && args[0] == "--native-plugin-management-only") { await Cerebral.Worker.Tests.NativePluginManagementTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--generation-telemetry-only") { await Cerebral.Worker.Tests.GenerationTelemetryTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--generation-preview-only") { await Cerebral.Worker.Tests.WorkflowGenerationPreviewTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--retained-admission-routing-only") { await Cerebral.Worker.Tests.RemoteWorkerManagementTests.RunRetainedAdmissionsAsync(); return; }
if (args.Length == 1 && args[0] == "--remote-worker-management-only") { await Cerebral.Worker.Tests.RemoteWorkerManagementTests.RunAsync(); return; }
 
if (args.Length == 1 && args[0] == "--intentional-work-hold-only") { await Cerebral.Worker.Tests.IntentionalWorkHoldTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--resource-pause-only") { await Cerebral.Worker.Tests.ResourcePauseTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--slot-priority-only") { await Cerebral.Worker.Tests.SlotPriorityTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--model-runtime-cleanup-only") { await Cerebral.Worker.Tests.ModelRuntimeCleanupTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--model-usage-only") { await Cerebral.Worker.Tests.ModelUsageMetricsTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--approved-plugin-reference-only") { await Cerebral.Worker.Tests.ApprovedPluginReferenceProjectionTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--conversation-presentation-handoff-policy-only") { await Cerebral.Worker.Tests.ConversationHarnessTests.RunPresentationHandoffPolicyOnlyAsync(); return; }
if (args.Length == 1 && args[0] == "--conversation-plugin-target-only") { await Cerebral.Worker.Tests.ConversationHarnessTests.RunPluginTargetInspectionOnlyAsync(); return; }
if (args.Length == 1 && args[0] == "--conversation-owner-verified-only") { await Cerebral.Worker.Tests.ConversationHarnessTests.RunOwnerVerifiedCompletionOnlyAsync(); return; }
if (args.Length == 1 && args[0] == "--owner-verification-budget-only") { Console.WriteLine($"Owner verification budget: {Cerebral.Worker.Tests.OwnerVerificationBudgetChecks.Run()} deterministic checks passed."); return; }
if (args.Length == 1 && args[0] == "--text-read-diagnostics-only") { await Cerebral.Worker.Tests.TextReadDiagnosticChecks.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--conversation-rejection-recovery-only") { await Cerebral.Worker.Tests.ConversationHarnessTests.RunRejectionRecoveryOnlyAsync(); return; }
if (args.Length == 1 && args[0] == "--idle-compaction-only") { await Cerebral.Worker.Tests.IdleCompactionTests.RunAsync(); await Cerebral.Worker.Tests.ContextCompactionTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--model-idle-only") { await Cerebral.Worker.Tests.ModelIdleTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--collaboration-only") { await Cerebral.Worker.Tests.CollaborationTaskTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--coordination-only") { await Cerebral.Worker.Tests.CoordinationWorkflowTests.RunAsync(); return; }
if (args.Length == 3 && args[0] == "--laya-native-workflow")
{ await Cerebral.Worker.Tests.LayaNativeWorkflowAcceptance.RunAsync(args[1], args[2]); return; }
if (args.Length == 1 && args[0] == "--workflow-audit-only") { await Cerebral.Worker.Tests.WorkflowAuditTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--native-coordination-only") { await Cerebral.Worker.Tests.NativeCoordinationTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--coordination-lock-order-only") { await Cerebral.Worker.Tests.CoordinationLockOrderTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--coordination-effect-recovery-only") { await Cerebral.Worker.Tests.CoordinationEffectRecoveryTests.RunAsync(); return; }
if (args.Length == 2 && args[0] == "--source-reference-routing-enable-read") { await Cerebral.Worker.Tests.SourceReferenceRoutingAcceptance.RunAsync(args[1], enableLookupGrant: true); return; }
if (args.Length == 2 && args[0] == "--source-reference-routing-canary") { await Cerebral.Worker.Tests.SourceReferenceRoutingAcceptance.RunAsync(args[1]); return; }
if (args.Length == 2 && args[0] == "--source-remote-model-inspect") { await Cerebral.Worker.Tests.SourceRemoteModelInspection.RunAsync(args[1]); return; }
if (args.Length is 3 or 4 && args[0] == "--source-plugin-run-inspect")
{ await Cerebral.Worker.Tests.SourcePluginRunInspection.RunAsync(args[1], args[2], args.Length == 4 ? long.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0); return; }
if (args.Length == 2 && args[0] == "--source-trail-note-generation-inspect")
{ await Cerebral.Worker.Tests.SourceTrailNoteGenerationInspection.RunAsync(args[1]); return; }
if (args.Length == 1 && args[0] == "--workflow-reference-routing-only") { await Cerebral.Worker.Tests.WorkflowReferenceRoutingTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--coordination-harness-only")
{
    await Cerebral.Worker.Tests.CoordinationHarnessBindingTests.RunAsync();
    await Cerebral.Worker.Tests.NativeCoordinationTests.RunAsync();
    await Cerebral.Worker.Tests.CoordinationEffectRecoveryTests.RunAsync();
    return;
}
if (args.Length == 4 && args[0] == "--image-source-live") { await Cerebral.Worker.Tests.LiveImageModelAcceptanceTests.RunAsync(args[1], args[2], args[3]); return; }
if (args.Length == 1 && args[0] == "--model-components-only") { await Cerebral.Worker.Tests.ModelComponentConfigurationTests.RunAsync(); return; }
if (args.Length >= 1 && args[0] == "--plugin-stress")
{
    await Cerebral.Worker.Tests.ToolEvidenceTests.RunStressAsync(args.Length >= 2 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 500); return;
}
if (args.Length == 4 && args[0] == "--resource-workflow-live") { await Cerebral.Worker.Tests.ResourceWorkflowLiveTests.RunAsync(args[1], args[2], args[3]); return; }
if (args.Length == 1 && args[0] == "--attachment-transfers-only") { await Cerebral.Worker.Tests.WorkerAttachmentTransferTests.RunAsync(); await Cerebral.Worker.Tests.AttachmentTransferClientTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--attachment-artifacts-only") { await Cerebral.Worker.Tests.AttachmentArtifactTests.RunAsync(); return; }
if (args.Length == 2 && args[0] == "--attachment-direct-boundary") { await Cerebral.Worker.Tests.AttachmentArtifactTests.RunDirectBoundaryAsync(args[1]); return; }
if (args.Length == 2 && args[0] == "--attachment-worker-live") { await Cerebral.Worker.Tests.AttachmentArtifactTests.RunWorkerLiveAsync(args[1]); return; }
if (args.Length == 2 && args[0] == "--attachment-installed-live") { await Cerebral.Worker.Tests.AttachmentArtifactTests.RunInstalledWorkerLiveAsync(args[1]); return; }
if (args.Length == 1 && args[0] == "--resource-model-only") { await Cerebral.Worker.Tests.ResourceModelTests.RunAsync(); return; }
if (args.Length == 4 && args[0] == "--resource-model-live") { await Cerebral.Worker.Tests.ResourceModelTests.RunLiveAsync(args[1], args[2], args[3]); return; }
if (args.Length == 4 && args[0] == "--model-component-download-live") { await Cerebral.Worker.Tests.ModelComponentDownloadLiveTests.RunAsync(args[1], args[2], args[3]); return; }
#if CEREBRAL_BOOKS
if (args.Length == 2 && args[0] == "--books-workflow-live-continue") { await Cerebral.Worker.Tests.BooksChapterWorkflowTests.RunAsync(live: true, retainedRoot: args[1]); return; }
if (args.Length == 1 && args[0] == "--books-workflow-live") { await Cerebral.Worker.Tests.BooksChapterWorkflowTests.RunAsync(live: true); return; }
if (args.Length == 1 && args[0] == "--books-canon-only") { await Cerebral.Worker.Tests.BooksCanonDecisionWorkflowTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--books-context-provider-only") { await Cerebral.Worker.Tests.BooksContextProviderWorkflowTests.RunAsync(); return; }
if (args.Contains("--books-workflow-only", StringComparer.Ordinal))
{
    await Cerebral.Worker.Tests.BooksCanonDecisionWorkflowTests.RunAsync();
    await Cerebral.Worker.Tests.BooksWorkflowWriteTests.RunAsync();
    await Cerebral.Worker.Tests.BookWorkflowReviewTests.RunAsync();
    await Cerebral.Worker.Tests.BooksContextProviderWorkflowTests.RunAsync();
    await Cerebral.Worker.Tests.BooksChapterWorkflowTests.RunAsync();
    await Cerebral.Worker.Tests.BooksWorkflowOutputTests.RunAsync();
    await Cerebral.Worker.Tests.BooksDuplicateFeedbackTests.RunAsync();
    await Cerebral.Worker.Tests.BookWorkflowRepairTests.RunAsync();
    await Cerebral.Worker.Tests.BooksChapterRepairWorkflowTests.RunAsync();
    return;
}
#else
if (args.Any(argument => argument.StartsWith("--books-", StringComparison.Ordinal)))
    throw new NotSupportedException("Books checks are not included in this build. Build with CerebralIncludeBooks=true and the complete local Books source.");
#endif
if (args.Length == 3 && args[0] == "--prepared-schema-live")
{
    await Cerebral.Worker.Tests.PreparedModelLiveTests.RunSchemaConformanceAsync(args[1], args[2]); return;
}
if (args.Length == 3 && args[0] == "--prepared-model-live")
{
    await Cerebral.Worker.Tests.PreparedModelLiveTests.RunAsync(args[1], args[2]); return;
}
if (args.Length == 1 && args[0] == "--programming-compaction-live")
{
    await Cerebral.Worker.Tests.ProgrammingWorkflowLiveTests.RunAsync(requireCompaction: true); return;
}
if (args.Length == 1 && args[0] == "--programming-workflow-live")
{
    // Launch this host directly: its tool calls use PAO's serialized mutation queue.
    // A PAO run wrapping this host would hold that queue while waiting for itself.
    await Cerebral.Worker.Tests.ProgrammingWorkflowLiveTests.RunAsync(); return;
}
if ((args.Length == 2 || args.Length == 5) && args[0] == "--programming-desktop-live")
{
    // This acceptance also invokes PAO from its child workflow, so launch it directly.
    if (args.Length == 2)
        await Cerebral.Worker.Tests.ProgrammingWorkflowDesktopLiveTests.RunAsync(args[1]);
    else
        await Cerebral.Worker.Tests.ProgrammingWorkflowDesktopLiveTests.RunAsync(args[1], args[2], args[3], args[4]);
    return;
}
if (args.Length == 1 && args[0] == "--plugin-authoring-live")
{
    await Cerebral.Worker.Tests.PluginAuthoringLiveTests.RunAsync(); return;
}
if (args.Length == 2 && args[0] == "--plugin-authoring-live")
{
    await Cerebral.Worker.Tests.PluginAuthoringLiveTests.RunAsync(args[1]); return;
}
if (args.Length == 5 && args[0] == "--generated-service-child")
{
    await Cerebral.Worker.Tests.PluginAuthoringLiveTests.RunGeneratedServiceChildAsync(args[1], args[2], args[3], args[4]); return;
}
if (args.Length == 4 && args[0] == "--generated-service-active-child")
{
    await Cerebral.Worker.Tests.PluginAuthoringLiveTests.RunGeneratedServiceActiveChildAsync(args[1], args[2], args[3]); return;
}
if (args.Length == 3 && args[0] == "--generated-workflow-desktop-live")
{
    await Cerebral.Worker.Tests.GeneratedWorkflowDesktopLiveTests.RunAsync(args[1], args[2]); return;
}

if (args.Length == 3 && args[0] == "--probe-managed-cortex")
{
    await Cerebral.Worker.Tests.ManagedConnectorProbe.RunAsync(args[2], args[1]); return;
}
if (args.Length == 2 && args[0] == "--lifecycle-child")
{
    var childFile = Path.GetFullPath(args[1]);
    if (!childFile.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture child path must be temporary.");
    // Publish only after the writer closes; the parent uses file existence as the readiness signal.
    await File.WriteAllTextAsync(childFile + ".pending", Environment.ProcessId.ToString());
    File.Move(childFile + ".pending", childFile);
    await Task.Delay(TimeSpan.FromSeconds(40)); return;
}
if (args.Contains("--user-launch-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.WindowsUserLaunchTests.RunAsync(); return; }
if (args.Length >= 2 && args[0] == "--workflow-desktop-live") { await Cerebral.Worker.Tests.WorkflowDesktopLiveTests.RunAsync(args[1], args.ElementAtOrDefault(2)); return; }
if (args.Contains("--workflow-accepted-content-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.WorkflowAcceptedContentTests.RunAsync(); return; }
if (args.Contains("--workflow-desktop-adapter-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.WorkflowConversationClientTests.RunAsync(); return; }
if (args.Contains("--local-pipe-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.LocalWorkerPipeTests.RunAsync(); return; }
if (args.Contains("--local-pipe-host-live", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.LocalWorkerHostTests.RunAsync(); return; }
if (args.Contains("--portable-registrations-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.PortableRegistrationTests.RunAsync(); return; }
if (args.Contains("--client-routes-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.CerebralClientRouteTests.RunAsync(); return; }
if (args.Contains("--installed-native-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.InstalledWorkerWindowsTests.RunAsync(); return; }
if (args.Contains("--installed-registration-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.InstalledWorkerRegistrationCommandTests.RunAsync(); return; }
if (args.Contains("--installed-client-capture-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.InstalledWorkerClientCaptureTests.Run(); return; }
if (args.Contains("--installed-binding-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.InstalledWorkerBindingTests.Run(); return; }
if (args.Contains("--desktop-plugin-sdk-binding-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.DesktopPluginSdkBindingTests.RunStandalone(); return; }
if (args.Contains("--cortex-first-run-discovery-only", StringComparer.Ordinal))
{
    var passed = 0;
    Cerebral.Worker.Tests.CortexFirstRunDiscoveryTests.Run((value, name) =>
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
        passed++;
    });
    Console.WriteLine($"Cortex first-run discovery: {passed} deterministic checks passed; no installed Cortex or provider contacted.");
    return;
}
if (args.Contains("--cortex-first-run-enrollment-only", StringComparer.Ordinal))
{
    var passed = 0;
    await Cerebral.Worker.Tests.CortexFirstRunEnrollmentTests.RunAsync((value, name) =>
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
        passed++;
    });
    Console.WriteLine($"Cortex first-run enrollment: {passed} fake-CLI checks passed; no installed Cortex or credential contacted.");
    return;
}
if (args.Contains("--installed-pipe-live", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.InstalledWorkerPipeLiveTests.RunAsync(); return; }
if (args.Contains("--installed-transport-live", StringComparer.Ordinal))
{
    Cerebral.Worker.Tests.InstalledWorkerBindingTests.Run();
    await Cerebral.Worker.Tests.InstalledWorkerWindowsTests.RunAsync();
    await Cerebral.Worker.Tests.InstalledWorkerPipeLiveTests.RunAsync();
    return;
}
if (args.Contains("--legacy-queue-summaries-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.LegacyJobSummaryTests.Run(); return; }
if (args.Contains("--peer-access-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.PeerAccessTests.RunAsync(); return; }
if (args.Contains("--remote-worker-deployment-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.RemoteWorkerDeploymentTests.Run(); return; }
if (args.Length == 2 && args[0] == "--worker-firewall-live") { Cerebral.Worker.Tests.WorkerFirewallLiveTests.Run(args[1]); return; }
if (args.Contains("--worker-provisioning-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.WorkerProvisioningTests.Run(); return; }
if (args.Length == 2 && args[0] == "--worker-provisioning-live") { await Cerebral.Worker.Tests.WorkerProvisioningTests.RunLiveAsync(args[1]); return; }
if (args.Contains("--configuration-backup-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ConfigurationBackupTests.RunAsync(); await Cerebral.Worker.Tests.ConfigurationConnectionBackupTests.RunAsync(); await Cerebral.Worker.Tests.ConfigurationRestoreAdmissionTests.RunAsync(); await Cerebral.Worker.Tests.ConfigurationRestoreReceiptTests.RunAsync(); return; }
if (args.Contains("--installer-paths-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.InstallerProjectPathTests.RunAsync(); return; }
if (args.Contains("--cortex-prerequisite-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.CortexPrerequisiteTests.RunAsync(); return; }
if (args.Contains("--startup-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ServiceStartupTests.RunAsync(); return; }
if (args.Contains("--activity-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ActivityJournalTests.RunAsync(); return; }
if (args.Contains("--retention-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.RetentionCoordinatorTests.RunAsync(); return; }
if (args.Contains("--prompt-cache-diagnostics-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.PromptCacheDiagnosticsTests.RunAsync(); return; }
if (args.Contains("--conversation-controls-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ConversationControlWorkerTests.RunAsync(); return; }
if (args.Contains("--conversation-workspace-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.ConversationWorkspaceTests.Run(); return; }
if (args.Length == 1 && args[0] == "--conversation-command-envelope-only") { await Cerebral.Worker.Tests.ConversationCommandEnvelopeTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--conversation-reasoning-adjustment-only") { Cerebral.Worker.Tests.ConversationReasoningAdjustmentChecks.Run(); return; }
if (args.Length == 1 && args[0] == "--conversation-noop-mutation-only") { await Cerebral.Worker.Tests.ConversationHarnessTests.RunNoOpMutationOnlyAsync(); return; }
if (args.Length == 1 && args[0] == "--conversation-qa-presentation-guard-only") { await Cerebral.Worker.Tests.ConversationHarnessTests.RunQaPresentationGuardOnlyAsync(); return; }
if (args.Length == 1 && args[0] == "--conversation-failed-review-read-only") { await Cerebral.Worker.Tests.ConversationHarnessTests.RunFailedReviewReadOnlyAsync(); return; }
if (args.Length == 1 && args[0] == "--plugin-invoke-assertions-only") { await Cerebral.Worker.Tests.PluginControlTests.RunInvokeAssertionsOnlyAsync(); await Cerebral.Worker.Tests.ConversationHarnessTests.RunInvokePostconditionOnlyAsync(); return; }
if (args.Length == 1 && args[0] == "--conversation-presentation-projection-only") { await Cerebral.Worker.Tests.ConversationHarnessTests.RunPresentationProjectionOnlyAsync(); return; }
if (args.Length == 1 && args[0] == "--qa-schema-bootstrap-only") { await Cerebral.Worker.Tests.ConversationHarnessTests.RunQaSchemaBootstrapOnlyAsync(); await Cerebral.Worker.Tests.PluginReviewContinuationTests.RunSchemaPreflightOnlyAsync(); return; }
if (args.Contains("--conversation-harness-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ConversationCommandEnvelopeTests.RunAsync(); await Cerebral.Worker.Tests.ConversationHarnessTests.RunAsync(); await Cerebral.Worker.Tests.ConversationHarnessTests.RunPresentationHandoffPolicyOnlyAsync(); return; }
if (args.Contains("--skills-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.SkillCatalogTests.RunAsync(); return; }
if (args.Contains("--conversation-effects-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.WorkflowConversationEffectsTests.RunAsync(); return; }
if (args.Contains("--historical-operations-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.HistoricalOperationContextTests.RunAsync(); return; }
if (args.Contains("--prepared-budget-shrink-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.WorkerPreparedBudgetTests.RunAsync(); return; }
if (args.Contains("--conversation-pipe-live", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ConversationPipeLiveTests.RunAsync(args.SkipWhile(x => x != "--preview").Skip(1).FirstOrDefault()); return; }
if (args.Contains("--conversation-steering-live", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ConversationWorkflowLiveTests.RunAsync(steeringOnly: true); return; }
if (args.Contains("--conversation-live", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ConversationWorkflowLiveTests.RunAsync(); return; }
if (args.Contains("--workflow-context-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.WorkflowContextTests.RunAsync(); return; }
if (args.Contains("--context-provider-discovery-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.ContextProviderDiscoveryTests.Run(); return; }
if (args.Contains("--workflow-receipts-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.WorkflowReceiptCommandTests.RunAsync(); return; }
if (args.Contains("--workflow-worker-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ServicePluginTests.RunAdmissionAsync(); await Cerebral.Worker.Tests.WorkflowWorkerTests.RunAsync(); await Cerebral.Worker.Tests.SchedulerWorkflowTests.RunAsync(); await Cerebral.Worker.Tests.WorkflowBindingTests.RunAsync(); return; }
if (args.Contains("--output-hook-plugin-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ServicePluginTests.RunAdmissionAsync(); return; }
if (args.Contains("--plugin-dependencies-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.PluginDependencyTests.RunAsync(); return; }
if (args.Contains("--service-host-facets-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.ServiceHostFacetAdmissionTests.Run(); return; }
if (args.Length == 4 && args[0] == "--private-metrics-combined-only")
{
    Cerebral.Worker.Tests.PrivateMetricsCombinedTests.Run(args[1], args[2], args[3]);
    return;
}
if (args.Length == 4 && args[0] == "--private-metrics-unload-diagnostic-only")
{
    Cerebral.Worker.Tests.PrivateMetricsUnloadDiagnosticTests.Run(args[1], args[2], args[3]);
    return;
}
if (args.Length == 4 && args[0] == "--private-metrics-cache-root-only")
{
    Cerebral.Worker.Tests.PrivateMetricsUnloadDiagnosticTests.RunCacheRoot(args[1], args[2], args[3]);
    return;
}
if (args.Length == 4 && args[0] == "--private-metrics-generated-unload-only")
{
    Cerebral.Worker.Tests.PrivateMetricsUnloadDiagnosticTests.RunGenerated(args[1], args[2], args[3]);
    return;
}
if (args.Length == 4 && args[0] == "--private-metrics-generated-combined-only")
{
    Cerebral.Worker.Tests.PrivateMetricsCombinedTests.RunGenerated(args[1], args[2], args[3]);
    return;
}
if (args.Contains("--tool-model-failures-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ToolModelProjectionFailureTests.RunAsync(); return; }
if (args.Contains("--tool-readable-projection-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ToolReadableProjectionTests.RunAsync(); return; }
if (args.Contains("--conversation-file-projection-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.ConversationHarnessTests.RunFileRemainderProjectionOnly(); return; }
if (args.Contains("--qa-source-catalog-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.PluginQaSourceCatalogTests.RunAsync(); return; }
if (args.Contains("--tool-evidence-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ToolEvidenceTests.RunAsync(); return; }
if (args.Contains("--tool-effect-identity-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ToolEffectIdentityTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--task-budget-compaction-only") { await Cerebral.Worker.Tests.ContextCompactionTests.RunTaskBudgetAsync(); return; }
if (args.Contains("--context-compaction-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ContextCompactionTests.RunAsync(); return; }
if (args.Contains("--workflow-effects-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.WorkflowEffectBridgeTests.RunAsync(); return; }
if (args.Contains("--programming-source-fix-only", StringComparer.Ordinal)) { Console.WriteLine($"Programming source fix: {Cerebral.Worker.Tests.ProgrammingHarnessTests.RunSourceFixChecks()} deterministic checks passed; no SDK/process/model/installed effects."); return; }
if (args.Contains("--programming-regression-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.ProgrammingRegressionChecks.Run(); return; }
if (args.Contains("--programming-harness-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ProgrammingHarnessTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--programming-prerequisite-only") { Console.WriteLine($"Programming prerequisite checks passed ({Cerebral.Worker.Tests.ProgrammingHarnessTests.RunPrerequisiteProgressChecks()}; deterministic child receipts, no SDK/model/UI)."); return; }
if (args.Contains("--prepared-model-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.PreparedModelTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--prepared-reasoning-only") { await Cerebral.Worker.Tests.PreparedReasoningTests.RunAsync(); return; }
if (args.Length == 3 && args[0] == "--prepared-reasoning-live") { await Cerebral.Worker.Tests.PreparedReasoningLiveTests.RunAsync(args[1], args[2]); return; }
if (args.Contains("--workflow-live-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.WorkflowWorkerTests.RunLiveAsync(); return; }
if (args.Contains("--compaction-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.CompactionReducerTests.RunAsync(); return; }
if (args.Contains("--lifecycle-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.LifecycleTests.RunAsync(); return; }
if (args.Contains("--models-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.GgufMetadataInspectorTests.Run(); Cerebral.Worker.Tests.ModelCapabilityTests.Run(); Cerebral.Worker.Tests.ModelCapacityTests.Run(); Cerebral.Worker.Tests.LlamaCapacityFitterTests.Run(); await Cerebral.Worker.Tests.ModelRepositoryTests.RunAsync(); return; }
if (args.Contains("--configuration-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.CoreConfigurationTests.RunAsync(); return; }
if (args.Contains("--processor-policy-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.ProcessorPolicyTests.Run(); return; }
if (args.Contains("--gpu-metrics-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.GpuMetricsTests.Run(); return; }
if (args.Contains("--instructions-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.AgentPromptTests.Run(); await Cerebral.Worker.Tests.SystemInstructionTests.RunAsync(); return; }
if (args.Contains("--question-answers-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.QuestionAnswerFlowTests.RunAsync(); return; }
if (args.Contains("--longform-diagnostics-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.LongFormDiagnosticsTests.RunAsync(); return; }
if (args.Contains("--longform-engine-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.LongFormEngineTests.RunAsync(); return; }
if (args.Contains("--longform-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.LongFormWorkflowTests.Run(); await Cerebral.Worker.Tests.LongFormDiagnosticsTests.RunAsync(); await Cerebral.Worker.Tests.LongFormEngineTests.RunAsync(); return; }
if (args.Contains("--generated-tools-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.GeneratedToolWorkflowTests.Run(); return; }
if (args.Contains("--action-linearization-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ActionLinearizationTests.RunAsync(); return; }
if (args.Contains("--plugin-qa-path-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.PluginReviewContinuationTests.RunAsync(verbose: false, pathAuthorityOnly: true); return; }
if (args.Contains("--plugin-review-continuation-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.PluginReviewContinuationTests.RunAsync(verbose: false); return; }
if (args.Contains("--plugin-visual-assessment-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.PluginVisualAssessmentTests.RunAsync(); return; }
if (args.Contains("--plugin-controls-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.PluginControlTests.RunAsync(verbose: false); return; }
if (args.Length == 1 && args[0] == "--work-plugin-receipt-only") { Console.WriteLine($"Work verification receipt checks passed ({Cerebral.Worker.Tests.WorkPluginVerificationOriginTests.Run()}; real disposable Engine stores with controlled outcomes, no provider/UI/installation)."); return; }
if (args.Length == 1 && args[0] == "--programming-finalization-only") { Console.WriteLine($"Programming finalization checks passed ({Cerebral.Worker.Tests.ProgrammingHarnessTests.RunFinalizationProgressChecks()}; deterministic policy outcomes, no SDK/provider/UI)."); return; }
if (args.Length == 1 && args[0] == "--programming-working-source-only") { Console.WriteLine($"Programming working-source checks passed ({Cerebral.Worker.Tests.ProgrammingHarnessTests.RunWorkingSourcePolicyChecks()}; deterministic readable-source projection and state fixtures, no SDK/provider/UI)."); return; }
if (args.Length == 1 && args[0] == "--helper-workflow-ui-origin-only") { await Cerebral.Worker.Tests.HelperWorkflowUiOriginTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--owner-followup-presentation-only") { await Cerebral.Worker.Tests.OwnerFollowupPresentationTests.RunAsync(); return; }
if (args.Contains("--interface-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.InterfaceRequestTests.RunAsync(); return; }
if (args.Contains("--plugins-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.PluginCatalogTests.RunAsync(); return; }
if (args.Contains("--service-plugins-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ServicePluginTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--conversation-module-selection-only") { Cerebral.Worker.Tests.ConversationModuleSelectionTests.Run(); return; }
if (args.Length == 1 && args[0] == "--conversation-builtin-lifecycle-only") { await Cerebral.Worker.Tests.ConversationBuiltinLifecycleTests.RunAsync(); return; }
if (args.Length == 4 && args[0] == "--conversation-package-load-live") { await Cerebral.Worker.Tests.ConversationPackageLiveTests.RunAsync(args[1], args[2], args[3]); return; }
if (args.Length == 1 && args[0] == "--service-package-lifecycle-only") { await Cerebral.Worker.Tests.ServicePackageLifecycleTests.RunAsync(); return; }
if (args.Length == 1 && args[0] == "--workflow-portable-settings-only") { Cerebral.Worker.Tests.WorkflowPortableSettingsTests.Run(); Cerebral.Worker.Tests.WorkflowPortableSettingsApprovalInputTests.Run(); Cerebral.Worker.Tests.WorkflowPortableSettingsApprovalRuntimeTests.Run(); return; }
if (args.Contains("--programming-only", StringComparer.Ordinal)) { Cerebral.Worker.Tests.ProgrammingLibraryTests.Run(); return; }
if (args.Contains("--reliability-only", StringComparer.Ordinal)) { await Cerebral.Worker.Tests.ReliabilityTests.RunAsync(); return; }
if (args.Contains("--cortex-core-only", StringComparer.Ordinal))
{
    await Cerebral.Worker.Tests.CortexPrerequisiteTests.RunAsync();
    await Cerebral.Worker.Tests.CortexContinuityTests.RunAsync();
    await Cerebral.Worker.Tests.CortexConnectionTests.RunAsync();
    await Cerebral.Worker.Tests.ConnectorPolicyTests.RunAsync();
    await Cerebral.Worker.Tests.ProjectKnowledgeTests.RunAsync();
    await Cerebral.Worker.Tests.KnowledgeProposalTests.RunAsync();
    return;
}
var schedulerOnly = args.Length == 1 && args[0] == "--scheduler-only";
if (args.Length != 0 && !schedulerOnly) throw new ArgumentException("Unknown Worker test selector. No default test matrix was run.");
if (!schedulerOnly)
{
Cerebral.Worker.Tests.GgufMetadataInspectorTests.Run();
Cerebral.Worker.Tests.ModelCapabilityTests.Run();
Cerebral.Worker.Tests.ModelCapacityTests.Run();
Cerebral.Worker.Tests.LlamaCapacityFitterTests.Run();
Cerebral.Worker.Tests.RuntimeSupportScopeCodeChecks.Run();
Cerebral.Worker.Tests.RuntimeSupportNativeInstanceChecks.Run();
await Cerebral.Worker.Tests.RuntimeSupportNativeEntryChecks.RunAsync();
await Cerebral.Worker.Tests.RuntimeSupportQueryChecks.RunAsync();
await Cerebral.Worker.Tests.ModelRepositoryTests.RunAsync();
await Cerebral.Worker.Tests.RemoteWorkerManagementTests.RunAsync();
await Cerebral.Worker.Tests.WindowsUserLaunchTests.RunAsync();
await Cerebral.Worker.Tests.InstalledWorkerRegistrationCommandTests.RunAsync();
Cerebral.Worker.Tests.InstalledWorkerClientCaptureTests.Run();
await Cerebral.Worker.Tests.InstallerProjectPathTests.RunAsync();
await Cerebral.Worker.Tests.CoreConfigurationTests.RunAsync();
Cerebral.Worker.Tests.ProcessorPolicyTests.Run();
Cerebral.Worker.Tests.GpuMetricsTests.Run();
await Cerebral.Worker.Tests.SkillCatalogTests.RunAsync();
await Cerebral.Worker.Tests.CerebralClientRouteTests.RunAsync();
await Cerebral.Worker.Tests.PeerAccessTests.RunAsync();
Cerebral.Worker.Tests.RemoteWorkerDeploymentTests.Run();
Cerebral.Worker.Tests.WorkerProvisioningTests.Run();
await Cerebral.Worker.Tests.CortexPrerequisiteTests.RunAsync();
await Cerebral.Worker.Tests.CortexContinuityTests.RunAsync();
await Cerebral.Worker.Tests.NativeCortexContinuityTests.RunAsync();
await Cerebral.Worker.Tests.NativeContinuityAdmissionSchedulerTests.RunAsync();
await Cerebral.Worker.Tests.CortexConnectionTests.RunAsync();
await Cerebral.Worker.Tests.ConnectorPolicyTests.RunAsync();
await Cerebral.Worker.Tests.ProjectKnowledgeTests.RunAsync();
await Cerebral.Worker.Tests.KnowledgeProposalTests.RunAsync();
await Cerebral.Worker.Tests.ServiceStartupTests.RunAsync();
await Cerebral.Worker.Tests.ActivityJournalTests.RunAsync();
await Cerebral.Worker.Tests.RetentionCoordinatorTests.RunAsync();
await Cerebral.Worker.Tests.PromptCacheDiagnosticsTests.RunAsync();
await Cerebral.Worker.Tests.CompactionReducerTests.RunAsync();
await Cerebral.Worker.Tests.ReliabilityTests.RunAsync();
await Cerebral.Worker.Tests.LifecycleTests.RunAsync();
await Cerebral.Worker.Tests.PluginCatalogTests.RunAsync();
await Cerebral.Worker.Tests.ServicePluginTests.RunAsync();
#if CEREBRAL_BOOKS
await Cerebral.Worker.Tests.BooksCanonDecisionWorkflowTests.RunAsync();
#endif
await Cerebral.Worker.Tests.PluginDependencyTests.RunAsync();
Cerebral.Worker.Tests.ServiceHostFacetAdmissionTests.Run();
await Cerebral.Worker.Tests.ToolEvidenceTests.RunAsync();
await Cerebral.Worker.Tests.ToolEffectIdentityTests.RunAsync();
await Cerebral.Worker.Tests.WorkflowEffectBridgeTests.RunAsync();
await Cerebral.Worker.Tests.ProgrammingHarnessTests.RunAsync();
Cerebral.Worker.Tests.ProgrammingStagedWorkflowPolicyTests.Run();
await Cerebral.Worker.Tests.SlotPriorityTests.RunAsync();
await Cerebral.Worker.Tests.PreparedModelTests.RunAsync();
Cerebral.Worker.Tests.InferenceRetirementObservationTests.Run();
await Cerebral.Worker.Tests.ModelUsageMetricsTests.RunAsync();
await Cerebral.Worker.Tests.RemotePreparedInferenceTests.RunResourceUsageOwnershipAsync();
Cerebral.Worker.Tests.ProgrammingLibraryTests.Run();
await Cerebral.Worker.Tests.QuestionAnswerFlowTests.RunAsync();
Cerebral.Worker.Tests.AgentPromptTests.Run();
Cerebral.Worker.Tests.LongFormWorkflowTests.Run();
await Cerebral.Worker.Tests.LongFormDiagnosticsTests.RunAsync();
await Cerebral.Worker.Tests.LongFormEngineTests.RunAsync();
Cerebral.Worker.Tests.GeneratedToolWorkflowTests.Run();
await Cerebral.Worker.Tests.ActionLinearizationTests.RunAsync();
await Cerebral.Worker.Tests.SystemInstructionTests.RunAsync();
await Cerebral.Worker.Tests.InterfaceRequestTests.RunAsync();
Cerebral.Worker.Tests.WorkPluginVerificationOriginTests.Run();
await Cerebral.Worker.Tests.CortexAssetCommandTests.RunAsync();
Cerebral.Worker.Tests.StateStoreTests.Run();
await Cerebral.Worker.Tests.ContextAndSharedTests.RunAsync();
await Cerebral.Worker.Tests.WorkflowContextTests.RunAsync();
Cerebral.Worker.Tests.ContextProviderDiscoveryTests.Run();
await Cerebral.Worker.Tests.WorkflowWorkerTests.RunAsync();
await Cerebral.Worker.Tests.WorkflowReceiptCommandTests.RunAsync();
await Cerebral.Worker.Tests.SchedulerWorkflowTests.RunAsync();
await Cerebral.Worker.Tests.WorkflowBindingTests.RunAsync();
await Cerebral.Worker.Tests.CollaborationTaskTests.RunAsync();
await Cerebral.Worker.Tests.ClientUploadTests.RunAsync();
}
var assertions = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); assertions++; Console.WriteLine("PASS: " + name); }
static async Task Until(Func<bool> predicate, string message, int milliseconds = 10000)
{
    var clock = Stopwatch.StartNew(); while (!predicate()) { if (clock.ElapsedMilliseconds > milliseconds) throw new Exception("TIMEOUT: " + message); await Task.Delay(25); }
}
var root = Path.Combine(Path.GetTempPath(), "cerebral-scheduler-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
await using var local = new Harness(Path.Combine(root, "local"));
await using var remote = new Harness(Path.Combine(root, "remote"));
try
{
    Cerebral.Worker.Tests.DesktopPluginSdkBindingTests.Run(root, Check);
    Cerebral.Worker.Tests.CortexFirstRunDiscoveryTests.Run(Check);
    await Cerebral.Worker.Tests.CortexFirstRunEnrollmentTests.RunAsync(Check);
    var request = new SubmitJobRequest { RequestId = "idempotent", ProfileId = "lead", Prompt = "cancel before claim" };
    var first = local.Scheduler.Submit(request); var repeated = local.Scheduler.Submit(request);
    Check(first.SessionId == repeated.SessionId && local.Store.Get<TaskSession>("session", first.SessionId)!.Messages.Count == 1, "submit idempotency preserves one session message");
    local.Scheduler.Cancel(first.Id); await local.StartAsync(); await Task.Delay(400);
    Check(local.Scheduler.Get(first.Id).Status == "cancelled" && local.Executor.Count == 0, "cancelled queued job is never claimed");
    var tombstone = local.Scheduler.Cancel("cancel-before-admission"); var lateRejected = false;
    try { local.Scheduler.Submit(new() { RequestId = "cancel-before-admission", ProfileId = "lead", Prompt = "late arrival" }); } catch (InvalidOperationException) { lateRejected = true; }
    Check(tombstone.CancellationTombstone && lateRejected, "durable cancellation tombstone rejects a late submission");

    var profile = local.Store.Get<AgentProfile>("agent", "lead")!; profile.MaxSteps = 1; local.Store.Put("agent", profile.Id, profile);
    local.Executor.Action = (job, _) =>
    {
        job.StepCount++;
        foreach (var q in new[]
        {
            new UserQuestion { Id = "required-one", JobId = job.Id, SessionId = job.SessionId, Prompt = "choose the first value", Required = true },
            new UserQuestion { Id = "required-two", JobId = job.Id, SessionId = job.SessionId, Prompt = "choose the second value", Required = true }
        }) local.Store.Put("question", q.Id, q);
        job.Status = "waiting-human"; return Task.CompletedTask;
    };
    var budget = local.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "persistent budget" });
    await Until(() => local.Scheduler.Get(budget.Id).Status == "waiting-human", "waiting human");
    local.Scheduler.Answer("required-one", "first answer"); local.Scheduler.Answer("required-one", "first answer");
    Check(local.Scheduler.Get(budget.Id).Status == "waiting-human" && local.Store.Get<UserQuestion>("question", "required-one")!.Answer == "first answer" && local.Store.Get<UserQuestion>("question", "required-two")!.Answer is null,
        "distinct required questions retain independent answers and wait for every response");
    local.Scheduler.Answer("required-two", "second answer");
    await Until(() => local.Scheduler.Get(budget.Id).Status == "failed", "step budget exhausted after answer");
    Check(local.Scheduler.Get(budget.Id).StepCount == 1 && local.Executor.Count == 1 &&
        local.Store.Get<TaskSession>("session", budget.SessionId)!.Messages.Count(x => x.Origin == "question-answer") == 2,
        "multiple answer retries are idempotent and human resume cannot reset persistent step budget");
    profile.MaxSteps = 10; local.Store.Put("agent", profile.Id, profile);
    local.Executor.Action = (job, _) => { job.Result = "done"; return Task.CompletedTask; };
    var complete = local.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "synchronous completion" });
    await Until(() => local.Scheduler.Get(complete.Id).Status == "completed", "synchronous fake completion");
    Check(local.Scheduler.Get(complete.Id).ActiveSeconds > 0, "active runtime is persisted");
    local.Store.Put("question", "late-local", new UserQuestion { Id = "late-local", JobId = complete.Id, SessionId = complete.SessionId, Prompt = "optional local detail", Required = false });
    var completedCount = local.Executor.Count;
    local.Scheduler.Answer("late-local", "later detail"); local.Scheduler.Answer("late-local", "later detail");
    Check(local.Scheduler.Get(complete.Id).Status == "completed" && local.Executor.Count == completedCount && local.Store.Get<TaskSession>("session", complete.SessionId)!.Messages.Count(x => x.Content.Contains("later detail", StringComparison.Ordinal)) == 1, "late optional answer is retained once without restarting finished work");

    var queued = local.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "steering receipts" });
    local.Scheduler.Steer(queued.Id, "same steering", "same-id"); local.Scheduler.Steer(queued.Id, "same steering", "same-id");
    Check(local.Store.Get<TaskSession>("session", queued.SessionId)!.Messages.Count(x => x.Content == "same steering") == 1, "steering idempotency prevents duplicate context");
    local.Scheduler.Cancel(queued.Id);
    var helperProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile)); helperProfile.Id = "helper"; helperProfile.Role = "helper"; local.Store.Put("agent", "helper", helperProfile);
    local.Executor.Action = (job, _) =>
    {
        if (job.ProfileId == "helper") { job.Result = job.Prompt + " result"; return Task.CompletedTask; }
        job.StepCount++;
        if (job.StepCount <= 2)
        {
            local.Executor.SubmitHelper!(new() { ProfileId = "helper", Prompt = "helper-cycle-" + job.StepCount, ParentJobId = job.Id }); job.Status = "waiting-helper";
        }
        else job.Result = "helpers integrated";
        return Task.CompletedTask;
    };
    var parent = local.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "two helper cycles" });
    await Until(() => local.Scheduler.Get(parent.Id).Status == "completed", "multiple helper cycles");
    var parentSession = local.Store.Get<TaskSession>("session", parent.SessionId)!;
    Check(local.Scheduler.Get(parent.Id).ConsumedHelperJobIds.Count == 2 && parentSession.Messages.Count(x => x.Content.Contains("helper-cycle-1 result", StringComparison.Ordinal)) == 1, "helper cycles consume each result once and release owner slot");
    Check(parentSession.Messages.Where(x => x.Content.StartsWith("Helper ", StringComparison.Ordinal)).All(x => x.Origin == "helper"), "helper result provenance does not split a manual user turn");
    var receiver = remote.Store.Get<AgentProfile>("agent", "lead")!; receiver.ModelId = "receiver-model"; receiver.Capabilities = ["filesystem.read", "filesystem.write"]; receiver.AllowedTools = ["*"];
    var grant = CerebralJson.Read<AgentProfile>(CerebralJson.Element(receiver)); grant.ModelId = "untrusted-model"; grant.Capabilities = ["filesystem.read"]; grant.AllowedTools = ["file.read"];
    var effective = JobScheduler.RestrictDelegatedProfile(receiver, grant);
    Check(effective.ModelId == "receiver-model" && effective.Capabilities.SequenceEqual(["filesystem.read"]) && effective.AllowedTools.SequenceEqual(["file.read"]), "delegation narrows grants and preserves receiver model config");
    grant.AllowedRoots = [Path.Combine(root, "not-registered")]; var rejected = false; try { JobScheduler.RestrictDelegatedProfile(receiver, grant); } catch (UnauthorizedAccessException) { rejected = true; }
    Check(rejected, "delegated workspace must be an explicitly registered worker-local root");

    // Two real schedulers/stores and HTTP transport; deterministic executors replace all model/provider work.
    await remote.StartAsync();
    var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var app = builder.Build(); var remoteDispatcher = new CommandDispatcher(remote.Store, remote.Configuration, remote.Runtime, remote.Scheduler, remote.Tools, remote.Events);
    var dropGets = false; var delaySubmit = false; var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    app.MapPost("/api/command", async (HttpContext http) =>
    {
        var command = await http.Request.ReadFromJsonAsync<CommandRequest>(CerebralJson.Options, http.RequestAborted) ?? throw new Exception("missing body");
        if (dropGets && command.Operation == "jobs.get") { http.Abort(); return; }
        var result = await remoteDispatcher.ExecuteAsync(command, http.RequestAborted);
        if (command.Operation == "jobs.submit") { accepted.TrySetResult(); if (delaySubmit) { try { await Task.Delay(30000, http.RequestAborted); } catch (OperationCanceledException) { return; } } }
        await http.Response.WriteAsJsonAsync(result, CerebralJson.Options, http.RequestAborted);
    });
    await app.StartAsync();
    var settings = local.Configuration.Get(); settings.Workers.Add(new() { Id = "remote", Url = app.Urls.Single(), AccessKey = "fixture" }); local.Configuration.Save(settings);
    profile.WorkerId = "remote"; profile.Capabilities = ["filesystem.read"]; profile.AllowedTools = ["file.read"]; local.Store.Put("agent", profile.Id, profile);
    remote.Executor.Action = (job, _) => { remote.Store.Append(job.SessionId, "assistant", "remote result"); job.Result = "remote result"; return Task.CompletedTask; };
    var remoteFirst = local.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "first remote message" });
    await Until(() => local.Scheduler.Get(remoteFirst.Id).Status == "completed", "first remote result");
    var remoteSecond = local.Scheduler.Submit(new() { ProfileId = "lead", SessionId = remoteFirst.SessionId, Prompt = "follow-up message" });
    await Until(() => local.Scheduler.Get(remoteSecond.Id).Status == "completed", "second remote result");
    var a = local.Scheduler.Get(remoteFirst.Id); var b = local.Scheduler.Get(remoteSecond.Id);
    var remoteSession = remote.Store.Get<TaskSession>("session", b.RemoteSessionId!)!;
    Check(a.RemoteSessionId == b.RemoteSessionId && remoteSession.Messages.Any(x => x.Content == "first remote message") && remoteSession.Messages.Any(x => x.Content == "follow-up message"), "remote continuation reuses explicit session and retains context");

    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    remote.Executor.Action = async (job, ct) => { await release.Task.WaitAsync(ct); job.Result = "recovered remote"; };
    accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var uncertain = local.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "network interruption" });
    await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Until(() => local.Scheduler.Get(uncertain.Id).RemoteConfirmed, "remote confirmation"); dropGets = true;
    await Until(() => local.Scheduler.Get(uncertain.Id).Status == "remote-unconfirmed", "remote network loss");
    rejected = false; try { local.Scheduler.Submit(new() { ProfileId = "lead", SessionId = uncertain.SessionId, Prompt = "conflicting work" }); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected && JobScheduler.IsActive(local.Scheduler.Get(uncertain.Id)), "unconfirmed remote work reserves its owning session");
    local.Store.Locked(() => { var retry = local.Scheduler.Get(uncertain.Id); retry.RemoteFailures = 2; retry.NextReconcileAt = DateTimeOffset.UtcNow; local.Store.Put("job", retry.Id, retry); return true; });
    await Until(() => local.Scheduler.Get(uncertain.Id).RemoteFailures == 3 && local.Scheduler.Get(uncertain.Id).NextReconcileAt is null, "bounded automatic reconciliation");
    await Task.Delay(400);
    Check(local.Scheduler.Get(uncertain.Id).RemoteFailures == 3 && local.Scheduler.Get(uncertain.Id).NextReconcileAt is null, "unavailable worker stops automatic retries after three attempts");
    dropGets = false; release.SetResult(); local.Scheduler.Reconcile(uncertain.Id);
    await Until(() => local.Scheduler.Get(uncertain.Id).Status == "completed", "explicit remote reconciliation");
    Check(local.Store.Get<TaskSession>("session", uncertain.SessionId)!.Messages.Count(x => x.Content == "recovered remote") == 1, "reconciliation imports final result exactly once");

    remote.Executor.Action = (job, _) =>
    {
        if (job.StepCount++ == 0) { remote.Store.Put("question", "remote-required", new UserQuestion { Id = "remote-required", JobId = job.Id, SessionId = job.SessionId, Prompt = "remote decision", Required = true }); job.Status = "waiting-human"; }
        else job.Result = "remote answer received";
        return Task.CompletedTask;
    };
    var questioning = local.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "remote question" });
    await Until(() => local.Store.List<UserQuestion>("question").Any(x => x.JobId == questioning.Id), "question relayed from remote worker");
    var relayed = local.Store.List<UserQuestion>("question").Single(x => x.JobId == questioning.Id); local.Scheduler.Answer(relayed.Id, "approved choice");
    await Until(() => local.Scheduler.Get(questioning.Id).Status == "completed", "remote answer forwarded");
    Check(remote.Store.Get<UserQuestion>("question", "remote-required")!.Answer == "approved choice", "required remote question and answer survive service routing");

    remote.Executor.Action = (job, _) =>
    {
        remote.Store.Put("question", "remote-optional", new UserQuestion { Id = "remote-optional", JobId = job.Id, SessionId = job.SessionId, Prompt = "optional detail", Required = false });
        job.Result = "finished with optional question"; return Task.CompletedTask;
    };
    var optional = local.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "optional question just before completion" });
    await Until(() => local.Scheduler.Get(optional.Id).Status == "completed", "optional remote question terminal synchronization");
    var optionalQuestion = local.Store.List<UserQuestion>("question").Single(x => x.JobId == optional.Id);
    Check(!optionalQuestion.Required && optionalQuestion.Answer is null, "optional remote question is imported even when first poll sees completion");
    local.Scheduler.Answer(optionalQuestion.Id, "future enrichment");
    Check(local.Scheduler.Get(optional.Id).Status == "completed" && local.Store.Get<TaskSession>("session", optional.SessionId)!.RemoteSessionIds.Count == 0 && remote.Store.Get<UserQuestion>("question", "remote-optional")!.Answer is null, "late remote answer stays local and detaches stale remote session without restarting");
    remote.Executor.Action = (job, _) => { job.Result = "continued enrichment"; return Task.CompletedTask; };
    var enriched = local.Scheduler.Submit(new() { ProfileId = "lead", SessionId = optional.SessionId, Prompt = "continue with that detail" });
    await Until(() => local.Scheduler.Get(enriched.Id).Status == "completed", "remote enriched continuation");
    var enrichedSession = remote.Store.Get<TaskSession>("session", local.Scheduler.Get(enriched.Id).RemoteSessionId!)!;
    Check(enrichedSession.Id != local.Scheduler.Get(optional.Id).RemoteSessionId && enrichedSession.Messages.Any(x => x.Content.Contains("future enrichment", StringComparison.Ordinal)), "next remote assignment receives late answer in new context snapshot");
    local.Store.Put("question", "older-pending-one", new UserQuestion { Id = "older-pending-one", JobId = enriched.Id, SessionId = enriched.SessionId, Prompt = "older optional question one" });
    local.Store.Put("question", "older-pending-two", new UserQuestion { Id = "older-pending-two", JobId = enriched.Id, SessionId = enriched.SessionId, Prompt = "older optional question two" });
    remote.Executor.Action = async (_, ct) => await Task.Delay(30000, ct);
    var busy = local.Scheduler.Submit(new() { ProfileId = "lead", SessionId = enriched.SessionId, Prompt = "new active assignment" });
    var mappingBeforeAnswers = local.Store.Get<TaskSession>("session", enriched.SessionId)!.RemoteSessionIds["remote"];
    local.Scheduler.Answer("older-pending-one", "first continuing detail"); local.Scheduler.Answer("older-pending-two", "second continuing detail");
    local.Scheduler.Answer("older-pending-one", "first continuing detail");
    var answeredSession = local.Store.Get<TaskSession>("session", enriched.SessionId)!;
    Check(local.Scheduler.Get(enriched.Id).Status == "completed" && local.Scheduler.Get(busy.Id).Status != "completed" &&
        local.Store.Get<UserQuestion>("question", "older-pending-one")!.Answer == "first continuing detail" &&
        local.Store.Get<UserQuestion>("question", "older-pending-two")!.Answer == "second continuing detail" &&
        answeredSession.Messages.Count(x => x.SourceQuestionId == "older-pending-one") == 1 &&
        answeredSession.Messages.Count(x => x.SourceQuestionId == "older-pending-two") == 1 && answeredSession.RemoteSessionIds["remote"] == mappingBeforeAnswers,
        "older optional answers enrich newer work independently without restarting finished work or replacing its remote mapping");
    await Until(() => local.Scheduler.Get(busy.Id).RemoteSessionId is not null &&
        remote.Store.Get<TaskSession>("session", local.Scheduler.Get(busy.Id).RemoteSessionId!)?.Messages.Count(x => x.Origin == "user" && x.Content.Contains("continuing detail", StringComparison.Ordinal)) == 2,
        "multiple older answers forwarded to active remote context");
    local.Scheduler.Cancel(busy.Id); await Until(() => !JobScheduler.IsActive(local.Scheduler.Get(busy.Id)), "newer task cancellation");

    remote.Executor.Action = async (_, ct) => await Task.Delay(30000, ct);
    accepted = new(TaskCreationOptions.RunContinuationsAsynchronously); delaySubmit = true;
    var cancelled = local.Scheduler.Submit(new() { ProfileId = "lead", Prompt = "cancel during submit acknowledgement" });
    await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5)); local.Scheduler.Cancel(cancelled.Id); delaySubmit = false;
    await Until(() => !JobScheduler.IsActive(remote.Scheduler.Get(cancelled.Id)), "remote cancelled after lost submit acknowledgement");
    await Until(() => !JobScheduler.IsActive(local.Scheduler.Get(cancelled.Id)), "local cancellation confirmed", 10000);
    Check(local.Scheduler.Get(cancelled.Id).Status == "cancelled" && remote.Scheduler.Get(cancelled.Id).Status == "cancelled", "cancel during remote submission cleans up accepted work");
    await app.StopAsync();
    Console.WriteLine($"RESULT: {assertions} scheduler assertions passed. Agent/model execution is deterministic fake evidence; persistence and loopback transport are real.");
}
finally
{
    await local.StopAsync(); await remote.StopAsync(); SqliteConnection.ClearAllPools();
    var full = Path.GetFullPath(root); if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && Path.GetFileName(full).StartsWith("cerebral-scheduler-tests-", StringComparison.Ordinal)) Directory.Delete(full, true);
}

sealed class FakeExecutor : IAgentExecutor
{
    public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
    public Func<bool>? ForegroundPending { get; set; }
    public Func<AgentJob, CancellationToken, Task> Action { get; set; } = (job, _) => { job.Result = "done"; return Task.CompletedTask; };
    private int count; public int Count => count;
    public Task RunAsync(AgentJob job, CancellationToken token) { Interlocked.Increment(ref count); return Action(job, token); }
}
sealed class Harness : IAsyncDisposable
{
    public StateStore Store { get; } public Configuration Configuration { get; } public EventHub Events { get; } = new(); public ModelRuntime Runtime { get; } public FakeExecutor Executor { get; } = new(); public ToolDispatcher Tools { get; } public JobScheduler Scheduler { get; }
    private bool started;
    public Harness(string path)
    {
        Store = new(path); Configuration = new(Store); var settings = Configuration.Get(); settings.MaxConcurrentJobs = 2; Configuration.Save(settings); Runtime = new(Store, Configuration.Get, Events); Tools = new(Configuration.Get); Scheduler = new(Store, Configuration, Executor, Runtime, Events, NullLogger<JobScheduler>.Instance);
        var fixtureModelPath = Path.Combine(path, "fixture.gguf"); File.WriteAllBytes(fixtureModelPath, "GGUF"u8.ToArray());
        Store.Put("model", "fixture", new ModelRecord { Id = "fixture", Name = "Scheduler fixture model", Path = fixtureModelPath, DeclaredType = "model", MetadataSource = "fixture" });
        Store.Put("agent", "lead", new AgentProfile { Id = "lead", Name = "test", ModelId = "fixture", Role = "lead", AllowedRoots = [path], MaxSteps = 10, MaxSeconds = 30, Capabilities = ["filesystem.read"], AllowedTools = ["file.read"] });
    }
    public async Task StartAsync() { if (started) return; started = true; await Scheduler.StartAsync(default); }
    public async Task StopAsync() { if (!started) return; started = false; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await Scheduler.StopAsync(timeout.Token); }
    public async ValueTask DisposeAsync() { await StopAsync(); Scheduler.Dispose(); await Runtime.DisposeAsync(); await Tools.DisposeAsync(); }
}
