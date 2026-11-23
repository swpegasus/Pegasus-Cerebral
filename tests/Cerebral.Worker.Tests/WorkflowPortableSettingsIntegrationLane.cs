using System.Text.Json;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Private SQLite, a normally loaded local service package, and deterministic tool/provider doubles.</summary>
internal static class WorkflowPortableSettingsIntegrationLane
{
    private const string PackageId = "fixture.portable-lane";
    private const string ToolName = PackageId + ".inspect";
    private const string ProviderId = "fixture.portable-provider";
    private const string DefinitionId = "fixture.portable-flow:run";
    private const string GetterSwitch = "CEREBRAL_PORTABLE_LANE_THROW_VERSION";
    private const string ChildRootVariable = "CEREBRAL_PORTABLE_LANE_CHILD_ROOT";
    private const string Secret = "private-portable-lane-marker";
    private const string Passed = "Portable workflow settings integration lane passed (private SQLite, loaded local fixture, deterministic tools/provider; no model, browser, or network).";
    private const int MaximumDiagnosticCharacters = 4_096;
    private sealed record BoundedRead(string Text, bool Truncated);

    internal static async Task RunAsync(string fixtureParent)
    {
        if (!Path.IsPathFullyQualified(fixtureParent)) throw new ArgumentException("An absolute fixture parent is required.");
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixtureParent));
        Directory.CreateDirectory(parent);
        ScopedFilePolicy.RejectLinks(parent);
        if (Environment.GetEnvironmentVariable(ChildRootVariable) is { Length: > 0 } childRoot)
        {
            var exactRoot = Path.GetFullPath(childRoot);
            var name = Path.GetFileName(exactRoot);
            var suffix = name.StartsWith("portable-lane-", StringComparison.Ordinal) ? name[14..] : "";
            if (!string.Equals(Path.GetDirectoryName(exactRoot), parent,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                suffix.Length != 32 || suffix.Any(character => !Uri.IsHexDigit(character)) ||
                !Directory.Exists(exactRoot))
                throw new ArgumentException("The private fixture child root is invalid.");
            await RunLoadedPackageChecksAsync(exactRoot);
            await RunOwnerMapDataChecksAsync(Path.Combine(exactRoot, "owner-map"));
            Console.WriteLine(Passed);
            return;
        }
        var root = Path.Combine(parent, "portable-lane-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process? child = null;
        CancellationTokenSource? drainCancellation = null;
        Task<BoundedRead>? stdout = null;
        Task<BoundedRead>? stderr = null;
        Exception? primary = null;
        var exitConfirmed = false;
        var streamsSettled = false;
        try
        {
            var host = Environment.ProcessPath ?? throw new InvalidOperationException("The test host path is unavailable.");
            var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(WorkflowPortableSettingsIntegrationLane).Assembly.Location);
            start.ArgumentList.Add("--workflow-portable-settings-integration-only");
            start.ArgumentList.Add(parent);
            start.Environment[ChildRootVariable] = root;
            child = Process.Start(start) ?? throw new InvalidOperationException("The private fixture child did not start.");
            drainCancellation = new CancellationTokenSource();
            stdout = ReadBoundedAsync(child.StandardOutput, drainCancellation.Token);
            stderr = ReadBoundedAsync(child.StandardError, drainCancellation.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await child.WaitForExitAsync(timeout.Token); exitConfirmed = true; }
            catch (OperationCanceledException) { throw new TimeoutException("The private portable settings fixture exceeded its time bound."); }
            BoundedRead[] output;
            try { output = await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); streamsSettled = true; }
            catch (TimeoutException) { throw new TimeoutException("The private fixture output did not settle after child exit."); }
            if (output.Any(item => item.Truncated))
                throw new InvalidDataException("The private fixture output exceeded its bound.");
            if (child.ExitCode != 0 || !output[0].Text.Contains(Passed, StringComparison.Ordinal))
                throw new InvalidOperationException("The private portable settings fixture failed: " +
                    output[1].Text.Replace(Secret, "[redacted]", StringComparison.Ordinal));
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            primary = error;
            if (child is not null && !exitConfirmed)
                exitConfirmed = await StopChildBoundedAsync(child);
            if (stdout is not null && stderr is not null && !streamsSettled && drainCancellation is not null)
            {
                drainCancellation.Cancel();
                streamsSettled = await TrySettleReadersBoundedAsync(stdout, stderr);
            }
        }
        finally
        {
            try
            {
                Environment.SetEnvironmentVariable(GetterSwitch, null);
                SqliteConnection.ClearAllPools();
                // A timed-out or unconfirmed child may still own its files. Retain that exact root.
                if (child is null || exitConfirmed && streamsSettled)
                {
                    ScopedFilePolicy.RejectLinks(root);
                    Directory.Delete(root, true);
                }
                else Console.Error.WriteLine("Private fixture exit or output is unconfirmed; exact root retained: " + root);
            }
            catch (Exception cleanupError) when (cleanupError is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                if (primary is null) primary = cleanupError;
                else Console.Error.WriteLine("Private fixture cleanup failed after the primary error; exact root retained: " + root);
            }
            finally
            {
                try { drainCancellation?.Dispose(); }
                catch (Exception disposalError) when (disposalError is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                { primary ??= disposalError; }
                try { child?.Dispose(); }
                catch (Exception disposalError) when (disposalError is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                { primary ??= disposalError; }
            }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        Console.WriteLine(Passed);
    }

    private static async Task<BoundedRead> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) return new(text.ToString(), truncated);
            var retained = Math.Min(count, MaximumDiagnosticCharacters - text.Length);
            if (retained != 0) text.Append(buffer, 0, retained);
            if (retained != count) truncated = true;
        }
    }

    private static async Task<bool> StopChildBoundedAsync(Process child)
    {
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await child.WaitForExitAsync(timeout.Token); return true; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            try { return child.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    private static async Task<bool> TrySettleReadersBoundedAsync(Task<BoundedRead> stdout, Task<BoundedRead> stderr)
    {
        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)); return true; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { return false; }
    }

    private static async Task RunLoadedPackageChecksAsync(string root)
    {
        var repository = Path.Combine(root, "repository");
        var data = Path.Combine(root, "plugin-data");
        var state = new StateStore(Path.Combine(root, "state"));
        var configuration = new Configuration(state);
        var settings = configuration.Get();
        settings.QueueCapacity = 32;
        configuration.Save(settings);
        state.Put("navigation", "sidebar", new NavigationState { Nodes =
            [new() { Id = "portable-project", Kind = "project", Title = "Portable fixture", Path = root }] });
        var manifest = PreparePackage(root);
        using (var beforeLoad = new ServicePluginRuntime(repository, data, new EventHub()))
        {
            var lifecycle = new ServicePackageLifecycle(repository, beforeLoad, new EventHub());
            lifecycle.Attach(new WorkflowStore(new EventJournal(Path.Combine(root, "engine.db"))));
            var staged = lifecycle.Stage(manifest, 0);
            _ = lifecycle.Activate(PackageId, staged.Revision);
        }
        Environment.SetEnvironmentVariable(GetterSwitch, Secret);
        try
        {
            using var rejectedLoad = new ServicePluginRuntime(repository, data, new EventHub());
            var status = rejectedLoad.Status.Single(item => item.Id == PackageId);
            Check(!status.Loaded && status.Error is { Length: > 0 } &&
                  !status.Error.Contains(Secret, StringComparison.Ordinal) &&
                  rejectedLoad.LoadedWorkflowToolOwner(ToolName) is null,
                "load-time validator getter refusal has a fixed public status and publishes no tool");
        }
        finally { Environment.SetEnvironmentVariable(GetterSwitch, null); }
        using var loaded = new ServicePluginRuntime(repository, data, new EventHub());
        Check(loaded.Status.Single(item => item.Id == PackageId).Loaded &&
              loaded.LoadedWorkflowToolOwner(ToolName) == PackageId,
            "only the published fixture tool resolves to its loaded owner");
        var lifecycleOwner = new ServicePackageLifecycle(repository, loaded, new EventHub());
        var review = lifecycleOwner.Inspect(PackageId, state).WorkflowSettings
            ?? throw new Exception("FAIL: loaded fixture has no portable settings review");
        var approval = lifecycleOwner.ApproveWorkflowSettings(state,
            new(PackageId, review.PackageVersion, review.LoadedGeneration,
                review.PackageFingerprint, review.SchemaFingerprint, ["style"], 0, "portable-lane-approval"));
        Check(approval.CurrentlyActive && approval.Approval.Revision == 1,
            "exact loaded fixture approval was persisted before capture");
        var profile = new AgentProfile
        {
            Id = "portable-lane-profile", Name = "Portable fixture", Role = "lead", Revision = 1,
            WorkerId = "local", FollowsDefaultModel = false, FollowsModelBudgets = false,
            AutomaticCompaction = false, AllowedRoots = [root], AllowedTools = [ToolName, "file.read"],
            Capabilities = ["fixture.portable", "filesystem.read"]
        };
        SetSettings(profile, "A", Secret);
        state.Put("agent", profile.Id, profile);
        var tools = new Tools();
        var events = new EventHub();
        var effects = new WorkerWorkflowEffects(state, configuration, new NoModels(), tools,
            workflowSettingsRuntime: loaded, workflowSettingsLifecycle: lifecycleOwner);
        var modules = new IWorkflowModule[] { new Module(), effects };
        var bindings = loaded.PackageBindings.Concat([
            new ServicePluginPackageBinding("fixture.portable-flow", "1.0.0", true, []),
            new ServicePluginPackageBinding(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])
        ]).ToArray();
        var host = new WorkerWorkflows(state, configuration, events, modules, bindings, tools,
            contextRuntime: loaded,
            packageAdmissionWithContributions: (definition, request, roots, create) =>
                lifecycleOwner.Admit(definition, request, roots, create),
            workflowSettingsLifecycle: lifecycleOwner);
        lifecycleOwner.Attach(host.Store);
        SubmitWorkflowRequest Request(string id) => new(id, "portable-chat-" + id, profile.Id,
            DefinitionId, "1.0.0", "Inspect the local fixture.", null, WorkflowContract.EmptyObject);

        // A validator-owned getter may throw a value-bearing message after load. No durable run
        // or event may exist because the value-bearing snapshot has not passed admission.
        Environment.SetEnvironmentVariable(GetterSwitch, Secret);
        await RefuseAsync(() => host.SubmitAsync(Request("getter-refusal"), null, default), Secret,
            "Selected service tool settings failed workflow admission.");
        Check(host.Store.TryGet("getter-refusal") is null && host.Journal.GetRevision("portable-chat-getter-refusal") == 0,
            "throwing validator version getter is redacted before durable create");
        Environment.SetEnvironmentVariable(GetterSwitch, null);

        var accepted = await host.SubmitAsync(Request("accepted-portable"), null, default);
        var frozen = WorkerExecutionSnapshot.Read(accepted.Submission.SettingsSnapshot);
        var entry = frozen.ApprovedPluginSettings?.Single()
            ?? throw new Exception("FAIL: accepted fixture has no approved settings entry");
        Check(entry.PluginId == PackageId && entry.ApprovalRevision == 1 && entry.HadProfileEntry &&
              entry.ReviewedValues.GetProperty("style").GetString() == "A" &&
              !accepted.Submission.SettingsSnapshot.GetRawText().Contains(Secret, StringComparison.Ordinal) &&
              frozen.PackageBindings.Any(item => item.Id == PackageId && item.Generation == entry.LoadedGeneration),
            "nonempty approved subset joins its loaded package binding and omits opaque settings in durable state");
        Refuse(() => WorkerExecutionSnapshot.Bounded(frozen with
            { PackageBindings = frozen.PackageBindings.Where(item => item.Id != PackageId).ToArray() }),
            "approved entry without its package binding");
        var template = entry;
        var sixteen = Enumerable.Range(0, 16).Select(index => template with
        { PluginId = "fixture.approved-" + index }).ToArray();
        Check(WorkflowPortableSettingsProjection.ValidateCollection(sixteen).Count == 16,
            "sixteen nonempty approved entries fit the closed collection bound");
        Refuse(() => WorkflowPortableSettingsProjection.ValidateCollection([
            .. sixteen, template with { PluginId = "fixture.approved-overflow" }
        ]), "seventeen approved entries");

        // A derived Data run receives a different definition but keeps the source's approved
        // settings and exact selected tool owner. The package graph must add Data without dropping it.
        var dataSource = await host.SubmitAsync(Request("portable-data-source") with
            { OwnerProjectId = "portable-project" }, null, default);
        JsonElement Schema(string json)
        { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
        var edition = new WorkflowDataEdition(WorkflowDataCatalog.Schema, "fixture.portable-data", "1.0.0",
            "Read an exact fixture path without model inference.",
            Schema("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}"""),
            Schema("""{"type":"object","properties":{"content":{"type":"string"}},"required":["content"],"additionalProperties":false}"""),
            ["filesystem.read"],
            [new("read", "file.read", Schema("""{"path":{"$input":"/path"}}"""))],
            Schema("""{"content":{"$stage":{"id":"read","pointer":"/content"}}}"""));
        var dataEntry = host.RegisterWorkflowData(new(dataSource.Id, edition), profile).Entry;
        var dataId = "portable-data-child";
        var dataRun = host.Submit(new(dataId, WorkerWorkflows.WorkflowDataConversationId(dataId), profile.Id,
            WorkflowDataModule.DefinitionId, WorkflowDataModule.DefinitionVersion, "Read the saved fixture.", null,
            CerebralJson.Element(new WorkflowDataRunInput(dataSource.Id, edition.Id, edition.Version,
                dataEntry.Hash, CerebralJson.Element(new { path = Path.Combine(root, "fixture.txt") })))), profile);
        var derived = WorkerExecutionSnapshot.Read(dataRun.Submission.SettingsSnapshot);
        Check(derived.ToolReceiptConsumer is { SchemaVersion: 1, Supported: false } &&
              derived.ApprovedPluginSettings?.Single().PluginId == PackageId &&
              derived.PackageBindings.Any(item => item.Id == PackageId && item.Version == entry.PluginVersion &&
                  item.Generation == entry.LoadedGeneration) &&
              derived.PackageBindings.Any(item => item.Id == WorkflowDataModule.ModuleId) &&
              JsonElement.DeepEquals(CerebralJson.Element(derived.ApprovedPluginSettings),
                  CerebralJson.Element(WorkerExecutionSnapshot.Read(dataSource.Submission.SettingsSnapshot).ApprovedPluginSettings)),
            "derived Data admission retains the exact approved value/owner while freezing its own unsupported receiver");
        using (var ownerLostRuntime = new ServicePluginRuntime(Path.Combine(root, "owner-loss-repository"),
                   Path.Combine(root, "owner-loss-data"), new EventHub()))
        {
            Check(ownerLostRuntime.LoadedWorkflowToolOwner(ToolName) is null,
                "the second host has no current owner for the accepted selected tool");
            var ownerLostHost = new WorkerWorkflows(state, configuration, events, modules, bindings, tools,
                contextRuntime: ownerLostRuntime,
                packageAdmissionWithContributions: (definition, request, roots, create) =>
                    lifecycleOwner.Admit(definition, request, roots, create),
                workflowSettingsLifecycle: lifecycleOwner);
            var refusedDataId = "portable-owner-loss-data";
            var refusedDataConversation = WorkerWorkflows.WorkflowDataConversationId(refusedDataId);
            try
            {
                ownerLostHost.Submit(new(refusedDataId, refusedDataConversation, profile.Id,
                    WorkflowDataModule.DefinitionId, WorkflowDataModule.DefinitionVersion,
                    "Read the saved fixture.", null,
                    CerebralJson.Element(new WorkflowDataRunInput(dataSource.Id, edition.Id, edition.Version,
                        dataEntry.Hash, CerebralJson.Element(new { path = Path.Combine(root, "fixture.txt") })))), profile);
                throw new Exception("FAIL: a lost accepted selected tool owner produced a Data child");
            }
            catch (WorkflowDataAdmissionRejectedException error)
            {
                Check(error.Message == "An accepted selected tool changed its loaded service owner." &&
                      ownerLostHost.Store.TryGet(refusedDataId) is null &&
                      ownerLostHost.Journal.GetRevision(refusedDataConversation) == 0,
                    "a selected owner disappears before derivation: actual Data admission refuses without a durable child");
            }
        }

        var tool = effects.Commands.Single(item => item.Descriptor.Id == WorkerWorkflowEffects.ToolCommand);
        WorkflowCommandContext Context(string effectId) => new(effectId, accepted.Submission,
            CerebralJson.Element(new WorkerWorkflowEffects.ToolArguments(ToolName,
                CerebralJson.Element(new { }), root))) { RunId = accepted.Id };
        var changed = state.Get<AgentProfile>("agent", profile.Id)!;
        SetSettings(changed, "A", "opaque-rotated");
        state.Put("agent", changed.Id, changed);
        var success = await tool.ExecuteAsync(Context("portable-opaque-rotation"), default);
        Check(success.State == WorkflowEffectState.Succeeded && tools.Executions == 1 &&
              tools.LastContext?.Profile.PluginSettings[PackageId].GetProperty("style").GetString() == "A" &&
              tools.LastContext.Profile.PluginSettings[PackageId].EnumerateObject().Count() == 1,
            "opaque-only persisted change permits dispatch with only the accepted reviewed field");

        // The second ListAsync occurs inside ToolAsync, between the first and second Resolve.
        // Keep profile revision fixed to isolate reviewed-value drift from the normal revision gate.
        tools.BeforeList = () =>
        {
            var live = state.Get<AgentProfile>("agent", profile.Id)!;
            SetSettings(live, "B", Secret);
            state.Put("agent", live.Id, live);
            tools.BeforeList = null;
        };
        var refused = await tool.ExecuteAsync(Context("portable-reviewed-drift"), default);
        Check(refused.State == WorkflowEffectState.Failed && tools.Executions == 1 &&
              !CerebralJson.Element(refused).GetRawText().Contains(Secret, StringComparison.Ordinal),
            "persisted reviewed-field drift after discovery refuses before Execute without leaking values");

        var arguments = CerebralJson.Element(new WorkerWorkflowEffects.ToolArguments(ToolName,
            CerebralJson.Element(new { }), root));
        var prior = new WorkflowEffect("portable-reconcile", WorkerWorkflowEffects.ToolCommand, 1, arguments,
            WorkflowReplayPolicy.Never, WorkflowEffectState.Unknown,
            CerebralJson.Element(new WorkerWorkflowEffects.ToolData(ToolName, "uncertain", false,
                new(ToolExecutionOutcome.Unknown, "fixture.portable-lane", "portable-reconcile"))));
        var recovered = await tool.ReconcileAsync(new("portable-reconcile", accepted.Submission, arguments)
        { RunId = accepted.Id, PreviousEffect = prior }, default);
        Check(recovered.State == WorkflowEffectState.Succeeded && tools.Reconciliations == 1 && tools.Executions == 1 &&
              tools.LastReconcileContext?.Profile.PluginSettings[PackageId].GetProperty("style").GetString() == "A" &&
              tools.LastReconcileContext.Profile.PluginSettings[PackageId].EnumerateObject().Count() == 1,
            "reviewed-value drift after Unknown permits exact receipt-only reconciliation without replay");
        var wrong = await tool.ReconcileAsync(new("portable-reconcile", accepted.Submission, arguments)
        { RunId = accepted.Id, PreviousEffect = prior with { Id = "different-effect" } }, default);
        Check(wrong.State == WorkflowEffectState.Unknown && tools.Reconciliations == 1 && tools.Executions == 1,
            "mismatched previous effect identity refuses receipt lookup and never executes");
        await RunUnionChecksAsync(Path.Combine(root, "union"), loaded);
    }

    private static async Task RunUnionChecksAsync(string root, ServicePluginRuntime runtime)
    {
        Directory.CreateDirectory(root);
        var state = new StateStore(Path.Combine(root, "state"));
        var configuration = new Configuration(state);
        var settings = configuration.Get();
        configuration.Save(settings);
        var provider = new ServicePluginPackageBinding(ProviderId, "1.0.0", true, [], Generation: "provider-generation");
        var toolOwner = runtime.PackageBindings.Single(item => item.Id == PackageId);
        var workflow = new ServicePluginPackageBinding("fixture.portable-flow", "1.0.0", true, []);
        var contribution = new ProviderContribution();
        var source = new WorkerContextContributionSource(provider, contribution, () => { });
        var packageGraph = new WorkflowPackageBindings([provider, toolOwner, workflow]);
        var host = new WorkerContextContributionHost(state, packageGraph, runtime, (_, _) => [],
            fixtureSources: [source]);
        var identity = new WorkflowSubmission("portable-union", "portable-union-chat", "portable-union-profile",
            DefinitionId, "1.0.0", "Inspect.", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject);
        var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version,
            new AgentProfile { Id = "portable-union-profile", WorkerId = "local" }, "", "", null,
            [new(ToolName, "Fixture inspection", CerebralJson.Element(new { type = "object" }), "fixture.portable")],
            [], [], [], false, "portable-route", packageGraph.Capture(DefinitionId), [])
            { SelectedToolOwners = [new(ToolName, PackageId)] };
        var captured = await host.CaptureAsync(identity, snapshot, default);
        Check(captured.PackageBindings.Any(item => item.Id == ProviderId) &&
              captured.PackageBindings.Any(item => item.Id == PackageId),
            "ready provider and selected tool owner both enter the actual context host package graph");
        var fifteen = Enumerable.Range(0, 15).Select(index => "fixture.root-" + index).ToArray();
        Check(WorkflowPackageBindings.AdditionalRoots([.. fifteen, PackageId]).Count == 16,
            "sixteen combined root identities fit the package boundary");
        Refuse(() => WorkflowPackageBindings.AdditionalRoots([.. fifteen, PackageId, ProviderId]),
            "seventeen combined root identities");
    }

    private static string PreparePackage(string root)
    {
        var directory = Path.Combine(root, "source", PackageId);
        Directory.CreateDirectory(directory);
        var assembly = Path.GetFileName(typeof(PortableSettingsLanePlugin).Assembly.Location);
        File.Copy(typeof(PortableSettingsLanePlugin).Assembly.Location, Path.Combine(directory, assembly));
        var manifest = new ServicePluginManifest
        {
            SchemaVersion = 2, Id = PackageId, Name = "Portable workflow lane", Version = "1.0.0",
            Assembly = assembly, EntryType = typeof(PortableSettingsLanePlugin).FullName!,
            PortableSettings = new ServicePluginPortableSettings
            {
                Version = 1,
                Fields = [new() { Name = "style", Type = "string", MaximumUtf8Bytes = 64 }]
            }
        };
        var path = Path.Combine(directory, ServicePluginRuntime.ManifestFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, CerebralJson.Options));
        return path;
    }

    private static async Task RunOwnerMapDataChecksAsync(string root)
    {
        const string a = "fixture.map-a";
        const string b = "fixture.map-b";
        const string toolA = "fixture.map.read-a";
        const string toolB = "fixture.map.read-b";
        const string switchName = "CEREBRAL_PORTABLE_LANE_OWNER_SWAP";
        Directory.CreateDirectory(root);
        var repository = Path.Combine(root, "repository");
        foreach (var id in new[] { a, b })
        {
            var directory = Path.Combine(repository, id);
            Directory.CreateDirectory(directory);
            var assembly = Path.GetFileName(typeof(OwnerMapLanePlugin).Assembly.Location);
            File.Copy(typeof(OwnerMapLanePlugin).Assembly.Location, Path.Combine(directory, assembly));
            var manifest = new ServicePluginManifest { SchemaVersion = 2, Id = id, Name = id,
                Version = "1.0.0", Assembly = assembly, EntryType = typeof(OwnerMapLanePlugin).FullName! };
            File.WriteAllText(Path.Combine(directory, ServicePluginRuntime.ManifestFileName),
                JsonSerializer.Serialize(manifest, CerebralJson.Options));
        }
        var state = new StateStore(Path.Combine(root, "state"));
        var configuration = new Configuration(state);
        configuration.Get();
        state.Put("navigation", "sidebar", new NavigationState { Nodes =
            [new() { Id = "owner-map-project", Kind = "project", Title = "Owner map fixture", Path = root }] });
        var profile = new AgentProfile { Id = "owner-map-profile", Name = "Owner map fixture", Role = "lead",
            WorkerId = "local", FollowsDefaultModel = false, FollowsModelBudgets = false,
            AutomaticCompaction = false, AllowedRoots = [root],
            AllowedTools = [toolA, toolB, "file.read"], Capabilities = ["fixture.map", "filesystem.read"] };
        state.Put("agent", profile.Id, profile);
        var events = new EventHub();
        var tools = new OwnerMapTools();
        var previous = Environment.GetEnvironmentVariable(switchName);
        try
        {
            Environment.SetEnvironmentVariable(switchName, null);
            using var original = new ServicePluginRuntime(repository, Path.Combine(root, "original-data"), events);
            Check(original.LoadedWorkflowToolOwner(toolA) == a &&
                  original.LoadedWorkflowToolOwner(toolB) == b && original.Status.All(item => item.Loaded),
                "two ordinary loaded packages own distinct selected tools without portable settings");
            var bindings = original.PackageBindings.Concat(
                [new ServicePluginPackageBinding("fixture.portable-flow", "1.0.0", true, [])]).ToArray();
            var lifecycle = new ServicePackageLifecycle(repository, original, events);
            var sourceHost = new WorkerWorkflows(state, configuration, events, [new Module()], bindings, tools,
                contextRuntime: original,
                packageAdmissionWithContributions: (definition, request, roots, create) =>
                    lifecycle.Admit(definition, request, roots, create));
            lifecycle.Attach(sourceHost.Store);
            var source = await sourceHost.SubmitAsync(new SubmitWorkflowRequest("owner-map-source",
                "owner-map-source-chat", profile.Id, DefinitionId, "1.0.0", "Read a fixture.", null,
                WorkflowContract.EmptyObject) { OwnerProjectId = "owner-map-project" }, null, default);
            var accepted = WorkerExecutionSnapshot.Read(source.Submission.SettingsSnapshot);
            Check(accepted.ApprovedPluginSettings?.Count == 0 &&
                  accepted.SelectedToolOwners?.Single(item => item.ToolName == toolA).PackageId == a &&
                  accepted.SelectedToolOwners?.Single(item => item.ToolName == toolB).PackageId == b &&
                  accepted.SelectedToolOwners?.Single(item => item.ToolName == "file.read").PackageId is null,
                "normal capture freezes exact two-service plus built-in selected tool owners without approvals");
            JsonElement Schema(string json)
            { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
            var edition = new WorkflowDataEdition(WorkflowDataCatalog.Schema, "fixture.owner-map-data", "1.0.0",
                "Read the private fixture path.", Schema("""{"type":"object"}"""),
                Schema("""{"type":"object"}"""), ["filesystem.read"],
                [new("read", "file.read", Schema("""{"path":"fixture.txt"}"""))], Schema("""{}"""));
            var entry = sourceHost.RegisterWorkflowData(new(source.Id, edition), profile).Entry;
            void RefuseData(WorkerWorkflows receiver, string id, string label)
            {
                var conversation = WorkerWorkflows.WorkflowDataConversationId(id);
                try
                {
                    receiver.Submit(new(id, conversation, profile.Id, WorkflowDataModule.DefinitionId,
                        WorkflowDataModule.DefinitionVersion, "Read the fixture.", null,
                        CerebralJson.Element(new WorkflowDataRunInput(source.Id, edition.Id, edition.Version,
                            entry.Hash, WorkflowContract.EmptyObject))), profile);
                    throw new Exception("FAIL: changed selected tool owner produced a Data child: " + label);
                }
                catch (WorkflowDataAdmissionRejectedException error)
                {
                    Check(error.Message == "An accepted selected tool changed its loaded service owner." &&
                          receiver.Store.TryGet(id) is null && receiver.Journal.GetRevision(conversation) == 0,
                        label + " refuses through actual Data admission without a durable child");
                }
            }
            using (var disappeared = new ServicePluginRuntime(Path.Combine(root, "absent-repository"),
                       Path.Combine(root, "absent-data"), events))
            {
                var receiver = new WorkerWorkflows(state, configuration, events, [new Module()], bindings, tools,
                    contextRuntime: disappeared);
                RefuseData(receiver, "owner-map-disappeared", "unapproved selected owner disappearance");
            }
            Environment.SetEnvironmentVariable(switchName, "swap");
            using var swapped = new ServicePluginRuntime(repository, Path.Combine(root, "swapped-data"), events);
            Check(swapped.LoadedWorkflowToolOwner(toolA) == b &&
                  swapped.LoadedWorkflowToolOwner(toolB) == a &&
                  JsonElement.DeepEquals(CerebralJson.Element(original.PackageBindings),
                      CerebralJson.Element(swapped.PackageBindings)),
                "tool ownership swaps while the two loaded package bindings stay byte-identical");
            var swappedHost = new WorkerWorkflows(state, configuration, events, [new Module()], bindings, tools,
                contextRuntime: swapped);
            RefuseData(swappedHost, "owner-map-swapped", "same-graph selected owner remap");
        }
        finally { Environment.SetEnvironmentVariable(switchName, previous); }
    }

    private static void SetSettings(AgentProfile profile, string style, string opaque)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { style, opaque }));
        profile.PluginSettings[PackageId] = document.RootElement.Clone();
    }

    private static async Task RefuseAsync(Func<Task> action, string forbidden, string expected)
    {
        try { await action(); }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidDataException)
        {
            Check(!error.ToString().Contains(forbidden, StringComparison.Ordinal) &&
                  error.Message == expected, "fixture refusal has fixed redacted text");
            return;
        }
        throw new Exception("FAIL: expected portable admission refusal");
    }

    private static void Refuse(Action action, string label)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException) { return; }
        throw new Exception("FAIL: expected refusal for " + label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
    }

    private sealed class Module : IWorkflowModule
    {
        public string Id => "fixture.portable-flow";
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new Definition()];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
        private sealed class Definition : IWorkflowDefinition
        {
            public WorkflowDefinitionDescriptor Descriptor { get; } = new(DefinitionId, "1.0.0",
                "Portable settings fixture", WorkflowContract.EmptyObject, []);
            public WorkflowStep Advance(WorkflowRun run) => new(WorkflowContract.EmptyObject,
                new CompleteWorkflow(WorkflowContract.EmptyObject));
        }
    }

    private sealed class Tools : IToolDispatcher
    {
        internal int Executions;
        internal int Reconciliations;
        internal Action? BeforeList;
        internal ToolExecutionContext? LastContext;
        internal ToolExecutionContext? LastReconcileContext;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); BeforeList?.Invoke();
            return Task.FromResult<IReadOnlyList<ToolDescriptor>>([
                new(ToolName, "Fixture inspection", CerebralJson.Element(new { type = "object" }), "fixture.portable"),
                new("file.read", "Fixture data read", CerebralJson.Element(new { type = "object" }), "filesystem.read")
            ]);
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Executions++; LastContext = context;
            return Task.FromResult(new ToolResult(true, "fixture accepted")
            { Evidence = new(ToolExecutionOutcome.Succeeded, "fixture.portable-lane", context.EffectId) });
        }
        public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context,
            ToolExecutionEvidence? previousEvidence, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reconciliations++; LastReconcileContext = context;
            return Task.FromResult(new ToolResult(true, "fixture retained receipt")
            { Evidence = (previousEvidence ?? new(ToolExecutionOutcome.Unknown, "fixture.portable-lane")) with
                { Outcome = ToolExecutionOutcome.Succeeded } });
        }
    }

    private sealed class OwnerMapTools : IToolDispatcher
    {
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ToolDescriptor>>([
                new("fixture.map.read-a", "First selected fixture", CerebralJson.Element(new { type = "object" }), "fixture.map"),
                new("fixture.map.read-b", "Second selected fixture", CerebralJson.Element(new { type = "object" }), "fixture.map"),
                new("file.read", "Built-in fixture read", CerebralJson.Element(new { type = "object" }), "filesystem.read")
            ]);
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token) =>
            throw new Exception("FAIL: owner-map admission fixture dispatched a tool");
        public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context,
            ToolExecutionEvidence? previousEvidence, CancellationToken token) =>
            throw new Exception("FAIL: owner-map admission fixture reconciled a tool");
    }

    private sealed class NoModels : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor => new("portable-lane-no-model", "1.0.0", []);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token) =>
            throw new Exception("FAIL: portable settings lane started a model");
    }

    private sealed class ProviderContribution : IWorkflowContextContribution
    {
        public WorkflowContextContributionDescriptor Descriptor { get; } = new(1, ProviderId + ":contribution",
            new(ProviderId + ":resolve", 1, "Portable root fixture", ["fixture.record"], 1, 1024),
            WorkflowContextHistoryProjection.ReferenceOnly, []);
        public Task<WorkflowContextSelection> CaptureAsync(WorkflowContextFacts facts, CancellationToken token) =>
            Task.FromResult(new WorkflowContextSelection(WorkflowContextDisposition.Ready, null, null, null,
                new ContextProviderRequest(Descriptor.Provider.Id, 1,
                    [new(ProviderId + ":record", "fixture.record", "source", 1, BeforeCurrentInput: true)], 1024)));
        public void Validate(WorkflowContextFacts current, WorkflowContextFacts accepted,
            WorkflowContextSelection selection, CancellationToken token) { }
        public Task<ContextProviderRequest> RestoreAsync(WorkflowContextAcceptedFacts facts,
            WorkflowContextSelection selection, CancellationToken token) => Task.FromResult(selection.Request! with
            { ContextRevision = facts.ContextRevision, ExclusionRevision = facts.ExclusionRevision });
        public WorkflowContextSelection ReadLegacy(WorkflowContextLegacyFacts facts, CancellationToken token) =>
            new(WorkflowContextDisposition.Unsupported, "fixture-unsupported", null, null, null);
    }
}

/// <summary>Loaded only from a private test package; it makes no browser, model, or network calls.</summary>
public sealed class PortableSettingsLanePlugin : IServicePlugin, IServiceWorkflowPortableSettingsValidator
{
    private string? id;
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [];
    public IReadOnlyList<IToolExtension> ToolExtensions => [new PortableSettingsLaneTool(id!)];
    public int WorkflowPortableSettingsValidatorVersion =>
        Environment.GetEnvironmentVariable("CEREBRAL_PORTABLE_LANE_THROW_VERSION") is { Length: > 0 } marker
            ? throw new InvalidOperationException(marker)
            : ServiceWorkflowPortableSettingsValidatorContract.Version;
    public void ValidateWorkflowPortableSettings(JsonElement approvedProjectedValues)
    {
        if (approvedProjectedValues.ValueKind != JsonValueKind.Object ||
            approvedProjectedValues.EnumerateObject().Any(field => field.Name != "style"))
            throw new InvalidDataException("Fixture portable values changed.");
    }
    public void Initialize(IServicePluginHost host) => id = host.PluginId;
    public void Dispose() { }
}

public sealed class PortableSettingsLaneTool(string id) : IToolExtension
{
    public IReadOnlyList<ToolDescriptor> Tools { get; } =
        [new(id + ".inspect", "Fixture inspection", CerebralJson.Element(new { type = "object" }), "fixture.portable")];
    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new ToolResult(true, "fixture accepted"));
}

/// <summary>Private fixture package: identical bytes can expose a swapped semantic tool map.</summary>
public sealed class OwnerMapLanePlugin : IServicePlugin
{
    private string? id;
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [];
    public IReadOnlyList<IToolExtension> ToolExtensions => [new OwnerMapLaneTool(id!,
        Environment.GetEnvironmentVariable("CEREBRAL_PORTABLE_LANE_OWNER_SWAP") == "swap")];
    public void Initialize(IServicePluginHost host) => id = host.PluginId;
    public void Dispose() { }
}

public sealed class OwnerMapLaneTool(string id, bool swapped) : IToolExtension
{
    public IReadOnlyList<ToolDescriptor> Tools { get; } = [new(
        (id == "fixture.map-a") ^ swapped ? "fixture.map.read-a" : "fixture.map.read-b",
        "Swappable owner fixture", CerebralJson.Element(new { type = "object" }), "fixture.map")];
    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token) =>
        throw new Exception("FAIL: owner-map package fixture dispatched a tool");
}
