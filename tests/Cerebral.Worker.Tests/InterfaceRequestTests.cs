using Cerebral.Core;
using Cerebral.Engine.Workflows;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class InterfaceRequestTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-interface-tests-" + Guid.NewGuid().ToString("N")));
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var source = Path.Combine(project, "notes.txt"); await File.WriteAllTextAsync(source, "interface fixture");
        var app = Path.Combine(project, "fixture.exe"); await File.WriteAllTextAsync(app, "Not an executable; admission fixture only. Never launched.");
        var package = Path.Combine(project, "fixture-package"); Directory.CreateDirectory(package);
        var pluginAssembly = Path.Combine(package, "fixture-plugin.dll");
        // Metadata admission uses a real compiled type; this fixture is never loaded as a plugin.
        File.Copy(typeof(InterfaceEntryFixture).Assembly.Location, pluginAssembly);
        var pluginManifest = Path.Combine(package, "desktop-plugin.json");
        await File.WriteAllTextAsync(pluginManifest, """
            {"schemaVersion":1,"contract":"pegasus.cerebral.desktop","apiVersion":1,"id":"acceptance.interface-fixture","name":"Interface fixture","version":"1.0.0","description":"Admission only","assembly":"fixture-plugin.dll","entryType":"Cerebral.Worker.Tests.InterfaceEntryFixture"}
            """);
        var assertions = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); assertions++; Console.WriteLine("PASS: " + name); }
        bool Rejects(Action action) { try { action(); return false; } catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException or InvalidDataException or InvalidOperationException) { return true; } }
        try
        {
            var store = new StateStore(Path.Combine(root, "data")); var events = new EventHub(); var requests = new InterfaceRequests(store, events);
            var profile = new AgentProfile { Id = "lead", Role = "lead", AllowedRoots = [project], Capabilities = ["filesystem.read", "process.execute", "desktop.plugins.manage", "diagnostics"], AllowedTools = ["file.read", "file.list", "process.run", InterfaceToolExtension.ToolName, InterfaceToolExtension.PluginToolName, InterfaceToolExtension.StatusToolName, ConversationToolExtension.ResponseBalanceToolName] };
            store.Put("agent", profile.Id, profile);
            var session = new TaskSession { Id = "conversation", ProfileId = profile.Id }; store.Put("session", session.Id, session);
            var job = new AgentJob { Id = "job", ProfileId = profile.Id, Profile = profile, SessionId = session.Id, Status = "completed" }; store.Put("job", job.Id, job);
            var first = requests.Create(job, profile, "file", source);
            Check(first.Status == "pending" && first.ClaimId is null && store.Get<TaskSession>("session", session.Id)!.Messages.Single().Content.Contains("not proof", StringComparison.Ordinal), "presentation is persisted as pending and context does not claim it opened");
            var duplicate = requests.Create(job, profile, "file", source);
            Check(duplicate.Id == first.Id && requests.List(session.Id).Count == 1 && store.Get<TaskSession>("session", session.Id)!.Messages.Count(x => x.Content.StartsWith("Interface request", StringComparison.Ordinal)) == 1 && store.Get<TaskSession>("session", session.Id)!.Messages[^1].Content.Contains("still pending", StringComparison.Ordinal), "repeated presentation preserves one request and returns its pending state to the model");
            var reopened = new InterfaceRequests(new StateStore(store.DirectoryPath), events);
            Check(reopened.List(session.Id).Single().Status == "pending", "offline pending presentation survives reopening the actual SQLite store");
            var claims = await Task.WhenAll(Task.Run(() => requests.Claim(first.Id, "window-a")), Task.Run(() => requests.Claim(first.Id, "window-b")));
            var winner = claims.Single(x => x.Claimed);
            Check(claims.Count(x => x.Claimed) == 1, "two competing interfaces can claim a pending presentation only once");
            Check(!reopened.Claim(first.Id, winner.Request.ClaimId!).Claimed && reopened.List(session.Id).Single().Status == "claimed", "claimed requests survive reopening without automatic replay after an unknown outcome");
            Check(Rejects(() => requests.Complete(first.Id, "wrong-window", true)), "completion requires the exact saved interface claim");
            Check(Rejects(() => requests.Complete(first.Id, winner.Request.ClaimId!, false,
                      "A file presentation cannot classify a plugin stage.", failureStage: InterfaceFailureStage.ViewCapture)),
                "non-plugin failures cannot report a native plugin failure stage");
            Check(Rejects(() => requests.Complete(first.Id, winner.Request.ClaimId!, true,
                      message: "File displayed.", failureStage: InterfaceFailureStage.ViewCapture)),
                "successful interface completion cannot report a failure stage");
            var done = requests.Complete(first.Id, winner.Request.ClaimId!, true, message: "File displayed in the Orchestrator editor.");
            requests.Complete(first.Id, winner.Request.ClaimId!, true, message: "File displayed in the Orchestrator editor.");
            var receipt = store.Get<TaskSession>("session", session.Id)!.Messages;
            Check(done.Status == "completed" && done.FailureStage is null && receipt.Count == 3 &&
                  receipt[^1].Origin == "service" && receipt[^1].Content.Contains("File displayed", StringComparison.Ordinal),
                "acknowledged completion adds one truthful service receipt without a failure stage and identical retries are idempotent");
            Check(Rejects(() => requests.Complete(first.Id, winner.Request.ClaimId!, false, "different outcome")), "an existing interface outcome cannot be silently changed");
            if (OperatingSystem.IsWindows())
            {
                var plugin = requests.Create(job, profile, "plugin", pluginManifest);
                var pluginClaim = requests.Claim(plugin.Id, "plugin-window");
                var capture = new InterfaceResourceReceipt(Guid.NewGuid().ToString("N"), 1, "image/png", "PluginPanel-fixture.png", 1234);
                var pluginDone = requests.Complete(plugin.Id, pluginClaim.Request.ClaimId!, true,
                    message: "Installed, mounted, and captured the plugin panel.", resource: capture);
                requests.Complete(plugin.Id, pluginClaim.Request.ClaimId!, true,
                    message: "Installed, mounted, and captured the plugin panel.", resource: capture);
                Check(pluginDone.Resource == capture && store.Get<TaskSession>("session", session.Id)!.Messages[^1].Content.Contains("Capture attachment: id=" + capture.Id, StringComparison.Ordinal),
                    "plugin presentation retains one compact immutable image receipt and an identical completion retry is idempotent");
                Check(Rejects(() => requests.Complete(plugin.Id, pluginClaim.Request.ClaimId!, true,
                    message: "Installed, mounted, and captured the plugin panel.", resource: capture with { Revision = 2 })),
                    "a completed plugin presentation cannot substitute a different capture coordinate");
                var retryJob = new AgentJob { Id = "plugin-retry-job", ProfileId = profile.Id, Profile = profile,
                    SessionId = session.Id, Status = "completed" };
                store.Put("job", retryJob.Id, retryJob);
                var failedPlugin = requests.Create(retryJob, profile, "plugin", pluginManifest);
                var failedPluginClaim = requests.Claim(failedPlugin.Id, "failed-plugin-window");
                Check(Rejects(() => requests.Complete(failedPlugin.Id, failedPluginClaim.Request.ClaimId!, false,
                          "Undefined failure stage.", failureStage: (InterfaceFailureStage)999)),
                    "undefined native plugin failure stages are rejected before changing the claimed receipt");
                failedPlugin = requests.Complete(failedPlugin.Id, failedPluginClaim.Request.ClaimId!, false,
                    "The first UI capture path was unavailable.", controlTestsAllowed: true,
                    failureStage: InterfaceFailureStage.ViewCapture);
                var identicalFailure = requests.Complete(failedPlugin.Id, failedPluginClaim.Request.ClaimId!, false,
                    failedPlugin.Error, controlTestsAllowed: true, failureStage: InterfaceFailureStage.ViewCapture);
                Check(identicalFailure.FailureStage == InterfaceFailureStage.ViewCapture &&
                      identicalFailure.PluginTests is null &&
                      Rejects(() => requests.Complete(failedPlugin.Id, failedPluginClaim.Request.ClaimId!, false,
                          failedPlugin.Error, controlTestsAllowed: true,
                          failureStage: InterfaceFailureStage.AttachmentImport)) &&
                      requests.GetForOwner(failedPlugin.Id).FailureStage == InterfaceFailureStage.ViewCapture,
                    "typed infrastructure failure is exact-replay immutable and never creates source repair work");
                var retryPlugin = requests.RetryPlugin(failedPlugin.Id);
                Check(failedPlugin.Status == "failed" && retryPlugin.Id != failedPlugin.Id && retryPlugin.Status == "pending" &&
                      requests.RetryPlugin(failedPlugin.Id).Id == retryPlugin.Id,
                    "owner retry preserves a failed plugin receipt and creates one idempotent pending review");
                Check(Rejects(() => requests.RetryPlugin(plugin.Id)),
                    "completed plugin reviews cannot be reset or replayed as retries");
                var withoutPluginGrant = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
                withoutPluginGrant.Capabilities.Remove("desktop.plugins.manage");
                Check(!InterfacePathPolicy.AllowedKinds(withoutPluginGrant).Contains("plugin") &&
                    Rejects(() => requests.Create(job, withoutPluginGrant, "plugin", pluginManifest)),
                    "native plugin presentation requires the explicit local lead capability");
            }
            Check(Rejects(() => requests.Create(job, profile, "file", Path.Combine(project, "missing.txt"))), "presentation rejects a missing file");
            Check(Rejects(() => requests.Create(job, profile, "folder", source)), "folder presentation rejects an existing ordinary file");
            Check(Rejects(() => requests.Create(job, profile, "file", "notes.txt")), "presentation rejects relative paths");
            var interfaceTool = new InterfaceToolExtension(store, events, () => 2);
            var conversationTool = new ConversationToolExtension(store, events);
            var pluginPresentDescriptor = interfaceTool.Tools.Single(x => x.Name == InterfaceToolExtension.PluginToolName);
            Check(pluginPresentDescriptor.InputSchema.GetProperty("required").EnumerateArray().Single().GetString() == "path" &&
                  !pluginPresentDescriptor.InputSchema.GetProperty("properties").TryGetProperty("kind", out _),
                "plugin presentation exposes one unambiguous package-path argument");
            var workflowContext = new ToolExecutionContext(profile, "native-conversation", "native-effect", project,
                FullAccess: false, EffectId: "effect-1") { EnforceProfileScope = true };
            var balanceContext = workflowContext with { SessionId = "balance-legacy-conversation", JobId = "balance-legacy-job" };
            store.Put("session", balanceContext.SessionId,
                new TaskSession { Id = balanceContext.SessionId, ProfileId = profile.Id, Title = "Response balance legacy fixture" });
            if (OperatingSystem.IsWindows())
            {
                var pluginContext = workflowContext with { SessionId = "plugin-tool-conversation", JobId = "plugin-present-effect", EffectId = "effect-plugin-present" };
                var pluginPresent = await WorkflowUiOriginTestRuns.Execute(interfaceTool, store,
                    new ToolCall(InterfaceToolExtension.PluginToolName, CerebralJson.Element(new { path = pluginManifest })),
                    pluginContext, CancellationToken.None);
                Check(pluginPresent.Success && pluginPresent.EffectiveOutcome == ToolExecutionOutcome.Succeeded,
                    "plugin presentation admission returns success before reading its retained request: " + pluginPresent.Content);
                var queuedPlugin = requests.List(pluginContext.SessionId).Single();
                Check(pluginPresent.Success && queuedPlugin.Kind == "plugin" && queuedPlugin.Path == pluginManifest,
                    "plugin presentation always queues the validated package as kind plugin");
                Check(queuedPlugin.GeneratedSourceRoot is null,
                    "imported package admission cannot invent a generated source root from its package location");
                var generated = Path.Combine(project, "generated", "Desktop");
                var generatedOutput = Path.Combine(generated, "bin", "Release", "net10.0-windows");
                Directory.CreateDirectory(generatedOutput);
                var generatedManifest = Path.Combine(generatedOutput, "desktop-plugin.json");
                File.Copy(pluginManifest, Path.Combine(generated, "desktop-plugin.json"));
                File.Copy(pluginManifest, generatedManifest);
                // Meet the project prerequisite so this case reaches contribution validation.
                // The fixture assembly is copied below; no project build or UI executes here.
                await File.WriteAllTextAsync(Path.Combine(generated, "Cerebral.InertFixture.Desktop.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\" />");
                await File.WriteAllTextAsync(Path.Combine(generated, "GeneratedDesktopPlugin.cs"), """
                    Panels { get; } = [];
                    ComposerActions { get; } = [];
                    CreateMessagePresentation(DesktopMessageSnapshot message) => null
                    """);
                File.Copy(pluginAssembly, Path.Combine(generatedOutput, "fixture-plugin.dll"));
                File.SetLastWriteTimeUtc(Path.Combine(generatedOutput, "fixture-plugin.dll"), DateTime.UtcNow.AddSeconds(1));
                var inertContext = pluginContext with { SessionId = "inert-plugin-conversation", JobId = "inert-effect" };
                var inertPresentation = await WorkflowUiOriginTestRuns.Execute(interfaceTool, store,
                    new ToolCall(InterfaceToolExtension.PluginToolName, CerebralJson.Element(new { path = generatedManifest })),
                    inertContext, CancellationToken.None);
                Check(!inertPresentation.Success && inertPresentation.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                      inertPresentation.Content.Contains("inert scaffold", StringComparison.Ordinal) && requests.List(inertContext.SessionId).Count == 0,
                    "inert SDK output is rejected with its useful diagnostic before any interface request is persisted: " + inertPresentation.Content);
                await File.WriteAllTextAsync(Path.Combine(generatedOutput, "fixture-plugin.dll"), "Malformed PE admission fixture.");
                var malformedContext = pluginContext with { SessionId = "malformed-plugin-conversation", JobId = "malformed-effect" };
                var malformedPresentation = await WorkflowUiOriginTestRuns.Execute(interfaceTool, store,
                    new ToolCall(InterfaceToolExtension.PluginToolName, CerebralJson.Element(new { path = generatedManifest })),
                    malformedContext, CancellationToken.None);
                Check(!malformedPresentation.Success && malformedPresentation.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                      malformedPresentation.Content.Contains("malformed managed metadata", StringComparison.Ordinal) &&
                      requests.List(malformedContext.SessionId).Count == 0,
                    "malformed compiled-package metadata returns a bounded rejection without persisting a presentation or escaping the tool boundary");
                await CheckGeneratedSourceCoordinatesAsync(project, pluginManifest, pluginAssembly, profile,
                    store, requests, interfaceTool, pluginContext, Check, Rejects);
                var queuedPluginClaim = requests.Claim(queuedPlugin.Id, "workflow-plugin-failure");
                var failedWorkflowPlugin = requests.Complete(queuedPlugin.Id, queuedPluginClaim.Request.ClaimId!, false,
                    "The first workflow plugin capture was unavailable.", controlTestsAllowed: true,
                    failureStage: InterfaceFailureStage.AttachmentImport);
                Check(failedWorkflowPlugin.FailureStage == InterfaceFailureStage.AttachmentImport &&
                      failedWorkflowPlugin.PluginTests is null &&
                      Rejects(() => requests.SchedulePluginRepair(failedWorkflowPlugin.Id, pluginContext.SessionId, true)),
                    "attachment-import failure remains truthful and cannot schedule generated-source repair");
                var retriedWorkflowPlugin = requests.RetryPlugin(failedWorkflowPlugin.Id);
                Check(retriedWorkflowPlugin.Status == "pending" && retriedWorkflowPlugin.SessionId == pluginContext.SessionId &&
                      retriedWorkflowPlugin.Source == "workflow",
                    "failed workflow plugin review retries without requiring a legacy TaskSession row");
            }
            var balanceDescriptor = conversationTool.Tools.Single(x => x.Name == ConversationToolExtension.ResponseBalanceToolName);
            var balanceSchema = balanceDescriptor.InputSchema.GetProperty("properties").GetProperty("responseSharePercent");
            Check(balanceDescriptor.Capability == "diagnostics" &&
                balanceSchema.GetProperty("minimum").GetInt32() == AgentTokenLimits.MinimumResponseSharePercent &&
                balanceSchema.GetProperty("maximum").GetInt32() == AgentTokenLimits.MaximumResponseSharePercent &&
                !balanceDescriptor.InputSchema.GetProperty("additionalProperties").GetBoolean(),
                "agent response-balance tool advertises the bounded closed schema through the normal catalog");
            var balanceResult = await conversationTool.ExecuteAsync(
                new ToolCall(ConversationToolExtension.ResponseBalanceToolName,
                    CerebralJson.Element(new { responseSharePercent = 26 })), balanceContext, CancellationToken.None);
            Check(balanceResult.Success && balanceResult.Content.Contains("next accepted assignment", StringComparison.Ordinal) &&
                store.GetSessionMetadata(balanceContext.SessionId)!.ResponseSharePercent == 26,
                "agent response-balance request persists only the current conversation's next-run preference");
            var originalShare = store.GetSessionMetadata(balanceContext.SessionId)!.ResponseSharePercent;
            try
            {
                await conversationTool.ExecuteAsync(new ToolCall(ConversationToolExtension.ResponseBalanceToolName,
                    CerebralJson.Element(new { responseSharePercent = 51 })), balanceContext, CancellationToken.None);
                Check(false, "out-of-range response balance is rejected");
            }
            catch (ArgumentOutOfRangeException)
            {
                Check(store.GetSessionMetadata(balanceContext.SessionId)!.ResponseSharePercent == originalShare,
                    "out-of-range response balance is rejected without changing the saved conversation");
            }
            var foreignProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
            foreignProfile.Id = "foreign";
            try
            {
                await conversationTool.ExecuteAsync(new ToolCall(ConversationToolExtension.ResponseBalanceToolName,
                    CerebralJson.Element(new { responseSharePercent = 20 })),
                    balanceContext with { Profile = foreignProfile }, CancellationToken.None);
                Check(false, "foreign agent response balance is rejected");
            }
            catch (UnauthorizedAccessException)
            {
                Check(store.GetSessionMetadata(balanceContext.SessionId)!.ResponseSharePercent == originalShare,
                    "agent response-balance cannot modify another profile's conversation");
            }
            var workflowResult = await WorkflowUiOriginTestRuns.Execute(interfaceTool, store,
                new ToolCall(InterfaceToolExtension.ToolName, CerebralJson.Element(new { kind = "file", path = source })),
                workflowContext, CancellationToken.None);
            var workflowRequest = requests.List(workflowContext.SessionId).Single();
            var repeatedWorkflowResult = await WorkflowUiOriginTestRuns.Execute(interfaceTool, store,
                new ToolCall(InterfaceToolExtension.ToolName, CerebralJson.Element(new { kind = "file", path = source })),
                workflowContext, CancellationToken.None);
            Check(workflowResult.Success && repeatedWorkflowResult.Success && workflowRequest.Source == "workflow" &&
                requests.List(workflowContext.SessionId).Single().Id == workflowRequest.Id &&
                workflowResult.Evidence?.RequestId == workflowRequest.Id,
                "durable conversation tool queues one retained request without creating a legacy session or job");
            var workflowClaim = requests.Claim(workflowRequest.Id, "native-window");
            var workflowDone = requests.Complete(workflowRequest.Id, workflowClaim.Request.ClaimId!, true,
                message: "Displayed by the signed-in Cerebral desktop.");
            Check(workflowClaim.Claimed && workflowDone.Status == "completed" &&
                new InterfaceRequests(new StateStore(store.DirectoryPath), events).List(workflowContext.SessionId).Single().Status == "completed",
                "native conversation presentation can be claimed and completed durably without a legacy transcript receipt");
            var statusDescriptor = interfaceTool.Tools.Single(item => item.Name == InterfaceToolExtension.StatusToolName);
            var statusRequestSchema = statusDescriptor.InputSchema.GetProperty("properties").GetProperty("requestId");
            var statusReferences = statusRequestSchema.GetProperty("oneOf").EnumerateArray().ToArray();
            Check(statusDescriptor.Capability == "filesystem.read" &&
                  statusDescriptor.InputSchema.GetProperty("required").EnumerateArray().Single().GetString() == "requestId" &&
                  statusDescriptor.InputSchema.GetProperty("properties").EnumerateObject().Count() == 1 &&
                  statusDescriptor.Description.Contains("plugin.present", StringComparison.Ordinal) &&
                  statusDescriptor.Description.Contains("literal reviewed", StringComparison.Ordinal) &&
                  statusDescriptor.Description.Contains("plugin generation", StringComparison.Ordinal) &&
                  statusDescriptor.Description.Contains("plugin.controls receipt", StringComparison.Ordinal) &&
                  statusRequestSchema.GetProperty("description").GetString()!.Contains("plugin.present's requestId", StringComparison.Ordinal) &&
                  statusReferences.Length == 2 &&
                  statusReferences[0].GetProperty("minLength").GetInt32() == 32 &&
                  statusReferences[0].GetProperty("maxLength").GetInt32() == 32 &&
                  statusReferences[1].GetProperty("enum").EnumerateArray().Single().GetString() == InterfaceRequests.ReviewedReference &&
                  !statusDescriptor.InputSchema.GetProperty("additionalProperties").GetBoolean(),
                "presentation status exposes concrete IDs plus one explicit automatic-QA reference and distinguishes generations and control receipts");
            WorkflowSchema.Validate(CerebralJson.Element(new { requestId = InterfaceRequests.ReviewedReference }),
                statusDescriptor.InputSchema);
            WorkflowSchema.Validate(CerebralJson.Element(new { requestId = Guid.NewGuid().ToString("N") }),
                statusDescriptor.InputSchema);
            Check(Rejects(() => WorkflowSchema.Validate(CerebralJson.Element(new { requestId = "latest" }),
                      statusDescriptor.InputSchema)),
                "the actual workflow schema accepts reviewed and concrete IDs but rejects arbitrary presentation aliases");
            var nativeStatusContext = workflowContext with { SessionId = "native-status-without-legacy-row", JobId = "native-status-job" };
            var nativeStatusRequest = WorkflowUiOriginTestRuns.Present(requests, store, nativeStatusContext, "file", source);
            async Task<(ToolResult Result, System.Text.Json.JsonElement Body)> Status(string id, ToolExecutionContext? context = null)
            {
                var result = await interfaceTool.ExecuteAsync(
                    new ToolCall(InterfaceToolExtension.StatusToolName, CerebralJson.Element(new { requestId = id })),
                    context ?? nativeStatusContext, CancellationToken.None);
                if (!result.Success) return (result, default);
                using var document = System.Text.Json.JsonDocument.Parse(result.Content);
                return (result, document.RootElement.Clone());
            }
            var reviewedId = Guid.NewGuid().ToString("N");
            var reviewedRunId = InterfaceRequests.PluginTestPrefix + reviewedId;
            Check(PluginControlReferences.TryResolveReviewedPresentationId(reviewedRunId, out var parsedReviewedId) &&
                  parsedReviewedId == reviewedId &&
                  !PluginControlReferences.TryResolveReviewedPresentationId("plugin-tests-" + Guid.Empty.ToString("N"), out _) &&
                  !PluginControlReferences.TryResolveReviewedPresentationId("prefix-" + reviewedId, out _),
                "shared reviewed-reference parsing accepts only the exact fixed nonempty QA identity without normalization");
            var reviewedPlugin = new InterfacePluginReceipt("fixture.reviewed", "1.0.0",
                "1.0.0-" + Guid.NewGuid().ToString("N"), pluginManifest,
                [new InterfacePluginMountReceipt("sidebar", "fixture.reviewed.panel")], true);
            var reviewedTests = new InterfacePluginTestContinuation(reviewedRunId, "origin-run", 1,
                "admitted", reviewedRunId);
            const string reviewedConversation = "native-reviewed-without-legacy-row";
            var reviewedRequest = new InterfaceRequest
            {
                Id = reviewedId, SessionId = reviewedConversation, JobId = "presenting-run",
                ProfileId = profile.Id, Source = "workflow", Kind = "plugin", Path = pluginManifest,
                Status = "completed", Plugin = reviewedPlugin, PluginTests = reviewedTests
            };
            store.Put("interface", reviewedId, reviewedRequest);
            var reviewedSeed = nativeStatusContext with
            { SessionId = reviewedConversation, JobId = reviewedTests.OriginRunId, EffectId = null };
            WorkflowUiOriginTestRuns.Metadata(store, reviewedSeed);
            WorkflowUiOriginTestRuns.Metadata(store, reviewedSeed with { JobId = reviewedRequest.JobId });
            var reviewedContext = WorkflowUiOriginTestRuns.ApprovedQa(store, reviewedSeed, reviewedRequest);
            var reviewedStatus = await Status(InterfaceRequests.ReviewedReference, reviewedContext);
            Check(reviewedStatus.Result.Success && reviewedStatus.Body.GetProperty("requestId").GetString() == reviewedId &&
                  reviewedStatus.Result.Evidence?.RequestId == reviewedId,
                "reviewed resolves only the exact presentation encoded in the admitted automatic-QA job identity");
            Check(!(await Status(InterfaceRequests.ReviewedReference,
                      reviewedContext with { JobId = InterfaceRequests.PluginTestPrefix + Guid.NewGuid().ToString("N") })).Result.Success &&
                  !(await Status(InterfaceRequests.ReviewedReference,
                      reviewedContext with { JobId = "ordinary-conversation-run" })).Result.Success &&
                  !(await Status(InterfaceRequests.ReviewedReference,
                      reviewedContext with { SessionId = "foreign-conversation" })).Result.Success &&
                  !(await Status(InterfaceRequests.ReviewedReference,
                      reviewedContext with { Profile = foreignProfile })).Result.Success,
                "reviewed rejects wrong jobs, ordinary runs, foreign conversations and foreign profiles without selecting another receipt");
            reviewedRequest.PluginTests = null; store.Put("interface", reviewedId, reviewedRequest);
            Check(!(await Status(InterfaceRequests.ReviewedReference, reviewedContext)).Result.Success,
                "reviewed requires the retained automatic-QA continuation marker");
            reviewedRequest.PluginTests = reviewedTests;
            reviewedRequest.Plugin = reviewedPlugin with { ControlTestsAllowed = false };
            store.Put("interface", reviewedId, reviewedRequest);
            Check(!(await Status(InterfaceRequests.ReviewedReference, reviewedContext)).Result.Success,
                "reviewed cannot manufacture owner control-test approval");
            reviewedRequest.Plugin = reviewedPlugin;
            reviewedRequest.PluginTests = reviewedTests with { RunId = "different-run" };
            store.Put("interface", reviewedId, reviewedRequest);
            Check(!(await Status(InterfaceRequests.ReviewedReference, reviewedContext)).Result.Success,
                "reviewed requires the continuation marker to name the current automatic-QA run exactly");
            reviewedRequest.PluginTests = reviewedTests; store.Put("interface", reviewedId, reviewedRequest);
            var pendingStatus = await Status(nativeStatusRequest.Id);
            Check(pendingStatus.Result.Success && pendingStatus.Body.GetProperty("status").GetString() == "pending" &&
                  pendingStatus.Result.Evidence?.State == "pending" && requests.GetForOwner(nativeStatusRequest.Id).ClaimId is null &&
                  store.GetSessionMetadata(nativeStatusContext.SessionId) is null,
                "native status reads pending receipt without a legacy session, claim or invented completion");
            var nativeStatusClaim = requests.Claim(nativeStatusRequest.Id, "owner-status-claim");
            var claimedStatus = await Status(nativeStatusRequest.Id);
            Check(claimedStatus.Result.Success && claimedStatus.Body.GetProperty("status").GetString() == "claimed" &&
                  !claimedStatus.Body.TryGetProperty("claimId", out _) && !claimedStatus.Body.TryGetProperty("path", out _) &&
                  !claimedStatus.Body.TryGetProperty("profileId", out _),
                "claimed status preserves state and withholds owner claim tokens and unrelated fields");
            requests.Complete(nativeStatusRequest.Id, nativeStatusClaim.Request.ClaimId!, true, message: "Rendered by owner; controls not tested.");
            var completedStatus = await Status(nativeStatusRequest.Id);
            Check(completedStatus.Result.Success && completedStatus.Body.GetProperty("status").GetString() == "completed" &&
                  completedStatus.Body.GetProperty("message").GetString() == "Rendered by owner; controls not tested." &&
                  requests.List(nativeStatusContext.SessionId).Count == 1,
                "completed status returns the actual retained observation without replaying presentation");
            var failedStatusRequest = WorkflowUiOriginTestRuns.Present(requests, store, nativeStatusContext with { JobId = "native-status-failure" }, "file", source);
            requests.Claim(failedStatusRequest.Id, "owner-status-failure");
            requests.Complete(failedStatusRequest.Id, "owner-status-failure", false, "Actual fixture display failure.");
            var failedStatus = await Status(failedStatusRequest.Id);
            Check(failedStatus.Result.Success && failedStatus.Body.GetProperty("status").GetString() == "failed" &&
                  failedStatus.Body.GetProperty("error").GetString() == "Actual fixture display failure." &&
                  failedStatus.Body.GetProperty("failureStage").ValueKind == System.Text.Json.JsonValueKind.Null &&
                  failedStatus.Body.GetProperty("sourceManifest").ValueKind == System.Text.Json.JsonValueKind.Null &&
                  failedStatus.Result.EffectiveOutcome == ToolExecutionOutcome.Succeeded,
                "successful receipt retrieval keeps the failed presentation distinct from a failed status read and preserves a historical null stage");
            var unknownStatus = await Status(Guid.NewGuid().ToString("N"));
            Check(!(await Status(nativeStatusRequest.Id, nativeStatusContext with { SessionId = "other-conversation" })).Result.Success &&
                  !(await Status(nativeStatusRequest.Id, nativeStatusContext with { Profile = foreignProfile })).Result.Success &&
                  !unknownStatus.Result.Success &&
                  unknownStatus.Result.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                  unknownStatus.Result.Evidence is { ProviderId: "cerebral.interface", RequestId: null } &&
                  unknownStatus.Result.Content.Contains("plugin.present's exact requestId", StringComparison.Ordinal) &&
                  unknownStatus.Result.Content.Contains("plugin generation", StringComparison.Ordinal) &&
                  !unknownStatus.Result.Content.Contains(nativeStatusRequest.Id, StringComparison.Ordinal),
                "status rejects foreign conversations, foreign profiles and unknown IDs without exposing receipts");
            var revokedStatusProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
            revokedStatusProfile.AllowedTools.Remove(InterfaceToolExtension.StatusToolName);
            store.Put("agent", profile.Id, revokedStatusProfile);
            Check(!(await Status(nativeStatusRequest.Id)).Result.Success &&
                  !(await Status(InterfaceRequests.ReviewedReference, reviewedContext)).Result.Success,
                "concrete and reviewed status reads recheck current grants instead of trusting the accepted profile snapshot");
            store.Put("agent", profile.Id, profile);
            var badStatus = await interfaceTool.ExecuteAsync(
                new ToolCall(InterfaceToolExtension.StatusToolName, CerebralJson.Element(new { requestId = nativeStatusRequest.Id, profileId = profile.Id })),
                nativeStatusContext, CancellationToken.None);
            Check(!badStatus.Success && badStatus.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                  badStatus.Content.Contains("plugin.present's exact requestId", StringComparison.Ordinal) &&
                  !(await Status("not-a-receipt")).Result.Success && !(await Status(Guid.Empty.ToString("N"))).Result.Success &&
                  !(await Status(new string('z', 32))).Result.Success,
                "status rejects extra ownership arguments and malformed, same-length non-GUID, or empty receipt IDs");
            if (OperatingSystem.IsWindows())
            {
                var stagedFailureRequest = WorkflowUiOriginTestRuns.Present(requests, store,
                    nativeStatusContext with { JobId = "native-status-failure-stage" }, "plugin", pluginManifest);
                requests.Claim(stagedFailureRequest.Id, "owner-status-failure-stage");
                requests.Complete(stagedFailureRequest.Id, "owner-status-failure-stage", false,
                    "Actual fixture attachment import failure.",
                    failureStage: InterfaceFailureStage.AttachmentImport);
                var stagedFailureStatus = await Status(stagedFailureRequest.Id);
                Check(stagedFailureStatus.Result.Success &&
                      stagedFailureStatus.Body.GetProperty("failureStage").GetString() == "attachmentImport" &&
                      stagedFailureStatus.Body.GetProperty("sourceManifest").GetString() == Path.GetFullPath(pluginManifest) &&
                      !(await Status(stagedFailureRequest.Id,
                          nativeStatusContext with { SessionId = "foreign-stage-conversation" })).Result.Success &&
                      !(await Status(stagedFailureRequest.Id,
                          nativeStatusContext with { Profile = foreignProfile })).Result.Success,
                    "authorized status exposes the retained typed failure stage while foreign scope remains denied");

                var captureRequest = WorkflowUiOriginTestRuns.Present(requests, store, nativeStatusContext with { JobId = "native-status-capture" }, "plugin", pluginManifest);
                requests.Claim(captureRequest.Id, "owner-status-capture");
                var captureReceipt = new InterfaceResourceReceipt(Guid.NewGuid().ToString("N"), 3, "image/png", "Mounted-fixture.png", 4567);
                requests.Complete(captureRequest.Id, "owner-status-capture", true, message: "Actual fixture receipt only.", resource: captureReceipt);
                var captureStatus = await Status(captureRequest.Id);
                var resource = captureStatus.Body.GetProperty("resource");
                Check(captureStatus.Result.Success && resource.GetProperty("id").GetString() == captureReceipt.Id &&
                      resource.GetProperty("revision").GetInt64() == 3 && resource.GetProperty("length").GetInt64() == 4567,
                    "status returns the same immutable capture coordinate after owner completion");
            }
            if (OperatingSystem.IsWindows())
            {
                var rejectedPlugin = await WorkflowUiOriginTestRuns.Execute(interfaceTool, store,
                    new ToolCall(InterfaceToolExtension.ToolName, CerebralJson.Element(new { kind = "plugin", path = source })),
                    workflowContext with { JobId = "invalid-plugin-effect", EffectId = "effect-invalid-plugin" }, CancellationToken.None);
                Check(!rejectedPlugin.Success && rejectedPlugin.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                    requests.List(workflowContext.SessionId).Count == 1,
                    "invalid native package paths are deterministic rejections and never create an uncertain interface effect");
                var invalidPackage = Path.Combine(project, "invalid-package"); Directory.CreateDirectory(invalidPackage);
                await File.WriteAllTextAsync(Path.Combine(invalidPackage, "wrong.dll"), "Never loaded.");
                var invalidManifest = Path.Combine(invalidPackage, "desktop-plugin.json");
                await File.WriteAllTextAsync(invalidManifest, "{\"id\":\"invented-format\",\"entryPoint\":\"wrong.dll\"}");
                var rejectedManifest = await WorkflowUiOriginTestRuns.Execute(interfaceTool, store,
                    new ToolCall(InterfaceToolExtension.ToolName, CerebralJson.Element(new { kind = "plugin", path = invalidManifest })),
                    workflowContext with { JobId = "invalid-manifest-effect", EffectId = "effect-invalid-manifest" }, CancellationToken.None);
                Check(!rejectedManifest.Success && rejectedManifest.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                    rejectedManifest.Content.Contains("schemaVersion", StringComparison.Ordinal) && requests.List(workflowContext.SessionId).Count == 1,
                    "a filename-compatible but invented plugin manifest is rejected before it reaches the desktop queue");
                var nestedPackage = Path.Combine(project, "nested-package"); Directory.CreateDirectory(Path.Combine(nestedPackage, "bin"));
                await File.WriteAllTextAsync(Path.Combine(nestedPackage, "bin", "nested.dll"), "Never loaded.");
                var nestedManifest = Path.Combine(nestedPackage, "desktop-plugin.json");
                await File.WriteAllTextAsync(nestedManifest, """
                    {"schemaVersion":1,"contract":"pegasus.cerebral.desktop","apiVersion":1,"id":"acceptance.nested","name":"Nested","version":"1.0.0","description":"Invalid source layout","assembly":"bin/nested.dll","entryType":"Acceptance.Nested"}
                    """);
                var rejectedNested = await WorkflowUiOriginTestRuns.Execute(interfaceTool, store,
                    new ToolCall(InterfaceToolExtension.ToolName, CerebralJson.Element(new { kind = "plugin", path = nestedManifest })),
                    workflowContext with { JobId = "nested-plugin-effect", EffectId = "effect-nested-plugin" }, CancellationToken.None);
                Check(!rejectedNested.Success && rejectedNested.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
                    rejectedNested.Content.Contains("same directory", StringComparison.Ordinal) && requests.List(workflowContext.SessionId).Count == 1,
                    "a source-tree manifest cannot masquerade as a portable built plugin package");
            }
            var outside = Path.Combine(root, "outside.txt"); await File.WriteAllTextAsync(outside, "outside");
            Check(Rejects(() => requests.Create(job, profile, "file", outside)) && Rejects(() => requests.Create(job, profile, "file", Path.Combine(project, "..", "outside.txt"))), "presentation preserves project confinement including parent traversal");
            var helper = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile)); helper.Role = "helper";
            var remote = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile)); remote.WorkerId = "remote";
            Check(InterfacePathPolicy.AllowedKinds(helper).Count == 0 && InterfacePathPolicy.AllowedKinds(remote).Count == 0, "helpers and remote execution profiles cannot directly open a user interface");
            var noTool = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile)); noTool.AllowedTools = [];
            Check(InterfacePathPolicy.AllowedKinds(noTool).Count == 0, "presentation does not bypass exact saved tool grants");
            if (OperatingSystem.IsWindows())
            {
                var application = requests.Create(job, profile, "application", app);
                Check(application.Status == "pending" && Rejects(() => requests.Create(job, profile, "application", source)), "application admission requires an existing exe and does not execute it");
                var noExecute = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile)); noExecute.Capabilities = ["filesystem.read"];
                Check(!InterfacePathPolicy.AllowedKinds(noExecute).Contains("application"), "application presentation requires process.execute in addition to file access");
                var claim = requests.Claim(application.Id, "application-window");
                var failed = requests.Complete(application.Id, claim.Request.ClaimId!, false, "Fixture application was intentionally not launched.");
                Check(failed.Status == "failed" && failed.Error!.Contains("not launched", StringComparison.Ordinal), "an interface failure is retained as failure instead of reporting the application opened");
            }
            var folder = requests.Create(job, profile, "folder", project);
            var restricted = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile)); restricted.Capabilities = [];
            store.Put("agent", restricted.Id, restricted);
            var denied = requests.Claim(folder.Id, "revoked-grants");
            Check(!denied.Claimed && denied.Request.Status == "failed" && denied.Request.ClaimId is null, "claim rechecks current grants and records revoked access without handing work to the UI");
            store.Put("agent", profile.Id, profile);
            var disappearing = Path.Combine(project, "deleted.txt"); await File.WriteAllTextAsync(disappearing, "remove fixture");
            var pending = requests.Create(job, profile, "file", disappearing); File.Delete(disappearing);
            Check(requests.Claim(pending.Id, "missing-file").Request.Status == "failed", "claim reports a file removed while the interface was offline");
            Check(requests.List(session.Id, 1).Count == 1 && Rejects(() => requests.List(session.Id, 101)), "interface history results have an explicit bounded limit");

            var configuration = new Configuration(store);
            await using var runtime = new ModelRuntime(store, configuration.Get, events);
            await using var tools = new ToolDispatcher(configuration.Get, [interfaceTool, conversationTool], store.DirectoryPath);
            var advertisedTools = await tools.ListAsync(profile, CancellationToken.None);
            Check(advertisedTools.Any(x => x.Name == ConversationToolExtension.ResponseBalanceToolName),
                "response-balance control is discoverable through the agent's ordinary granted tool catalog");
            using var scheduler = new JobScheduler(store, configuration, new NoExecutor(), runtime, events, NullLogger<JobScheduler>.Instance);
            var commands = new CommandDispatcher(store, configuration, runtime, scheduler, tools, events);
            if (OperatingSystem.IsWindows())
            {
                var stageContext = workflowContext with
                {
                    SessionId = "failure-stage-command-conversation",
                    JobId = "failure-stage-command-job"
                };
                var stageRequest = WorkflowUiOriginTestRuns.Present(requests, store, stageContext, "plugin", pluginManifest);
                var stageClaim = requests.Claim(stageRequest.Id, "failure-stage-command-owner");
                var numericStage = await commands.ExecuteAsync(new("interface.complete", CerebralJson.Element(new
                {
                    id = stageRequest.Id,
                    claimId = stageClaim.Request.ClaimId,
                    success = false,
                    error = "Fixture attachment import failed.",
                    controlTestsAllowed = true,
                    failureStage = (int)InterfaceFailureStage.AttachmentImport
                })));
                Check(!numericStage.Success && requests.GetForOwner(stageRequest.Id).Status == "claimed",
                    "interface.complete accepts only a named failure-stage value and leaves rejected input uncommitted");
                var namedStage = await commands.ExecuteAsync(new("interface.complete", CerebralJson.Element(new
                {
                    id = stageRequest.Id,
                    claimId = stageClaim.Request.ClaimId,
                    success = false,
                    error = "Fixture attachment import failed.",
                    controlTestsAllowed = true,
                    failureStage = InterfaceFailureStage.AttachmentImport
                })));
                var retainedStage = requests.GetForOwner(stageRequest.Id);
                Check(namedStage.Success && retainedStage.Status == "failed" &&
                      retainedStage.FailureStage == InterfaceFailureStage.AttachmentImport &&
                      retainedStage.PluginTests is null,
                    "owner command persists the closed typed failure stage without source repair for attachment infrastructure");
            }
            var listed = await commands.ExecuteAsync(new("interface.list", CerebralJson.Element(new { sessionId = session.Id }), profile.Id));
            var agentClaim = await commands.ExecuteAsync(new("interface.claim", CerebralJson.Element(new { id = first.Id, claimId = "agent" }), profile.Id));
            var agentReceipt = await commands.ExecuteAsync(new("interface.complete", CerebralJson.Element(new { id = first.Id, claimId = "agent", success = true }), profile.Id));
            var agentRetry = await commands.ExecuteAsync(new("interface.retry", CerebralJson.Element(new { id = requests.List(session.Id).First(x => x.Kind == "plugin" && x.Status == "failed").Id }), profile.Id));
            var agentRepair = await commands.ExecuteAsync(new("interface.repair", CerebralJson.Element(new
            {
                id = requests.List(session.Id).First(x => x.Kind == "plugin" && x.Status == "failed").Id,
                sessionId = session.Id,
                controlTestsAllowed = true
            }), profile.Id));
            Check(listed.Success && !agentClaim.Success && !agentReceipt.Success && !agentRetry.Success && !agentRepair.Success,
                "ordinary agent access can read presentation state but cannot manufacture GUI claims, retries, completion receipts, or owner repair consent");
            var ownerList = await commands.ExecuteAsync(new("interface.list", CerebralJson.Element(new { sessionId = session.Id, limit = 2 })));
            Check(ownerList.Success && ownerList.Data.GetArrayLength() == 2, "shared interface.list command returns bounded records to the installation owner");
            var ownerExact = await commands.ExecuteAsync(new("interface.get", CerebralJson.Element(new { id = first.Id, sessionId = session.Id })));
            Check(ownerExact.Success && ownerExact.Data.GetProperty("id").GetString() == first.Id,
                "owner exact receipt inspection returns the original request with its explicit chat binding");
            var wrongChat = await commands.ExecuteAsync(new("interface.get", CerebralJson.Element(new { id = first.Id, sessionId = "other-chat" })));
            var extraField = await commands.ExecuteAsync(new("interface.get", CerebralJson.Element(new { id = first.Id, sessionId = session.Id, fullAccess = true })));
            using var duplicateRead = System.Text.Json.JsonDocument.Parse("{\"id\":\"" + first.Id + "\",\"sessionId\":\"other-chat\",\"sessionId\":\"" + session.Id + "\"}");
            var duplicateExact = await commands.ExecuteAsync(new("interface.get", duplicateRead.RootElement));
            Check(!wrongChat.Success && !extraField.Success && !duplicateExact.Success,
                "exact receipt read rejects another chat, extra authority fields and duplicate binding fields");
            var agentExact = await commands.ExecuteAsync(new("interface.get", CerebralJson.Element(new { id = first.Id, sessionId = session.Id }), profile.Id));
            var adminProfile = new AgentProfile { Id = "receipt-admin", Role = "lead", Capabilities = ["administration"] };
            store.Put("agent", adminProfile.Id, adminProfile);
            var adminExact = await commands.ExecuteAsync(new("interface.get", CerebralJson.Element(new { id = first.Id, sessionId = session.Id }), adminProfile.Id));
            Check(!agentExact.Success && !adminExact.Success,
                "ordinary and administration agents cannot use the owner-only receipt recovery read");
            Check(Rejects(() => requests.GetForOwner(first.Id, "other-chat")) &&
                  Rejects(() => requests.GetForOwner("../outside", session.Id)),
                "the exact owner store read validates its request ID and chat even without the command wrapper");

            if (OperatingSystem.IsWindows())
            {
                await RunPackageBindingChecks(root, project, pluginManifest, profile, store, requests, interfaceTool, Check, Rejects);
                await RunInstalledPluginReceiptChecks(root, project, pluginManifest, pluginAssembly, profile,
                    store, requests, Check, Rejects);
            }
            Console.WriteLine($"Interface presentation: {assertions} checks passed (real SQLite and filesystem, no application launch or model inference).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (!root.StartsWith(temporaryRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact temporary test workspace.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckGeneratedSourceCoordinatesAsync(string project, string importedManifest,
        string compiledAssembly, AgentProfile profile, StateStore store, InterfaceRequests requests,
        InterfaceToolExtension tool, ToolExecutionContext originalContext, Action<bool, string> check,
        Func<Action, bool> rejects)
    {
        // Real compiled metadata and SQLite admission fixtures only. No plugin is loaded or mounted.
        var sourceRoot = Path.Combine(project, "nested-generated", "Desktop");
        var desktop = Path.Combine(sourceRoot, "Desktop");
        var output = Path.Combine(desktop, "bin", "Release", "net10.0-windows");
        Directory.CreateDirectory(output);
        var sourceManifest = Path.Combine(desktop, NativeDesktopPluginPackage.ManifestFileName);
        var manifest = Path.Combine(output, NativeDesktopPluginPackage.ManifestFileName);
        File.Copy(importedManifest, sourceManifest);
        File.Copy(importedManifest, manifest);
        var assembly = Path.Combine(output, "fixture-plugin.dll");
        File.Copy(compiledAssembly, assembly);
        await File.WriteAllTextAsync(Path.Combine(desktop, "GeneratedDesktopPlugin.cs"),
            "public sealed class AdmissionFixture { public object[] Panels => [new object()]; }");
        var guide = Path.Combine(sourceRoot, "PLUGIN.md");
        await File.WriteAllTextAsync(guide, "SDK admission fixture; no implementation is executed.");
        var projectPath = Path.Combine(desktop, "Cerebral.GeneratedPlugin.Desktop.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.SetLastWriteTimeUtc(assembly, DateTime.UtcNow.AddMinutes(1));
        var context = originalContext with { SessionId = "generated-coordinate-session", JobId = "generated-coordinate-job" };
        var fingerprint = NativeDesktopPluginPackage.ComputeFingerprint(manifest);
        var admitted = WorkflowUiOriginTestRuns.Present(requests, store, context, "plugin", manifest, fingerprint);
        check(admitted.GeneratedSourceRoot == sourceRoot && admitted.Path == manifest &&
              admitted.PackageFingerprint == fingerprint && sourceRoot != desktop,
            "SDK admission retains the exact nested guide root with its immutable request, source manifest and package fingerprint");
        var replay = WorkflowUiOriginTestRuns.Present(requests, store, context, "plugin", manifest, fingerprint);
        check(replay.Id == admitted.Id && System.Text.Json.JsonElement.DeepEquals(
                  CerebralJson.Element(replay), CerebralJson.Element(admitted)),
            "identical generated-package admission replays the same immutable coordinate record");
        var reopened = new InterfaceRequests(new StateStore(store.DirectoryPath), new EventHub());
        check(reopened.GetForOwner(admitted.Id).GeneratedSourceRoot == sourceRoot,
            "generated source reference survives actual SQLite reopen without source rediscovery");

        var status = await tool.ExecuteAsync(new ToolCall(InterfaceToolExtension.StatusToolName,
            CerebralJson.Element(new { requestId = admitted.Id })), context, CancellationToken.None);
        using (var body = System.Text.Json.JsonDocument.Parse(status.Content))
            check(status.Success && body.RootElement.GetProperty("requestId").GetString() == admitted.Id &&
                  body.RootElement.GetProperty("generatedSourceRoot").GetString() == sourceRoot &&
                  body.RootElement.GetProperty("sourceManifest").GetString() == manifest &&
                  body.RootElement.GetProperty("packageFingerprint").GetString() == fingerprint &&
                  !body.RootElement.TryGetProperty("claimId", out _),
                "actual interface.status exposes only the exact admission-bound generated source reference and package identity");
        var readable = ToolModelProjection.ReadableProjection(InterfaceToolExtension.StatusToolName, status.Content);
        var projected = ToolModelProjection.Create("generated-coordinate-effect", InterfaceToolExtension.StatusToolName,
            "Succeeded", status.Content, status.Truncated, status.Evidence, null, readable.Text, !readable.Complete);
        var modelText = ToolModelProjection.Render(projected);
        using (var payload = System.Text.Json.JsonDocument.Parse(modelText[(modelText.IndexOf("Payload:\n", StringComparison.Ordinal) + "Payload:\n".Length)..]))
            check(ToolModelProjection.IncludesPayload(InterfaceToolExtension.StatusToolName) && readable.Complete &&
                  payload.RootElement.GetProperty("generatedSourceRoot").GetString() == sourceRoot &&
                  projected.Receipt == "generated-coordinate-effect",
                "the normal model-facing status projection preserves the exact generated root and actual effect receipt");
        var tests = new InterfacePluginTestContinuation("plugin-tests-" + admitted.Id, context.JobId, 1);
        var installed = new InterfacePluginReceipt("acceptance.interface-fixture", "1.0.0", "fixture-generation",
            importedManifest, [new("sidebar", "fixture.panel")], true);
        var assignment = WorkerWorkflows.BuildPluginTestAssignment(admitted, installed, tests,
            true, new("direct", null, [], [], false, null));
        using (var binding = System.Text.Json.JsonDocument.Parse(assignment[(assignment.LastIndexOf("Binding: ", StringComparison.Ordinal) + "Binding: ".Length)..]))
            check(binding.RootElement.GetProperty("generatedSourceRoot").GetString() == sourceRoot &&
                  binding.RootElement.GetProperty("presentationId").GetString() == admitted.Id &&
                  binding.RootElement.GetProperty("presentedSourceManifest").GetString() == manifest &&
                  assignment.Contains("plugin.projects with that exact root before authoring or building", StringComparison.Ordinal),
                "automatic QA carries the provider coordinate as reference and still requires exact current SDK source/build inspection");
        var repairAssignment = WorkerWorkflows.BuildPluginRepairAssignment(admitted,
            tests with { Mode = InterfacePluginTestModes.RepairBeforeInstall });
        check(repairAssignment.Contains("generatedSourceRoot", StringComparison.Ordinal) &&
              repairAssignment.Contains("plugin.projects with that exact root before authoring or building", StringComparison.Ordinal),
            "failed-review repair carries the same admission reference without an installed-plugin claim");

        var injected = await WorkflowUiOriginTestRuns.Execute(tool, store, new ToolCall(InterfaceToolExtension.PluginToolName,
            CerebralJson.Element(new { path = importedManifest, generatedSourceRoot = sourceRoot })),
            context with { SessionId = "forged-coordinate-session", JobId = "forged-coordinate-job" }, CancellationToken.None);
        check(!injected.Success && requests.List("forged-coordinate-session").Count == 0,
            "agent-supplied generated source coordinates are rejected rather than minted by presentation input");
        var narrow = new AgentProfile { Id = profile.Id, Role = profile.Role, WorkerId = profile.WorkerId,
            AllowedRoots = [output], AllowedTools = [.. profile.AllowedTools], Capabilities = [.. profile.Capabilities] };
        check(rejects(() => InterfacePathPolicy.ValidatePresentation(narrow, "plugin", manifest)),
            "package-output scope cannot grant reads of the generated source ancestors");

        var legacyContext = context with { JobId = "historical-generated-coordinate-job" };
        var historical = WorkflowUiOriginTestRuns.Present(requests, store, legacyContext, "plugin", manifest, fingerprint);
        historical.GeneratedSourceRoot = null;
        store.Put("interface", historical.Id, historical);
        var historicalReplay = WorkflowUiOriginTestRuns.Present(reopened, store, legacyContext, "plugin", manifest, fingerprint);
        check(historicalReplay.Id == historical.Id && historicalReplay.GeneratedSourceRoot is null &&
              !CerebralJson.Element(historicalReplay).TryGetProperty("generatedSourceRoot", out _),
            "historical JSON without coordinate metadata reads and replays unchanged instead of acquiring fresh provenance");
        var historicalClaim = requests.Claim(historical.Id, "historical-generated-owner");
        var historicalFailure = requests.Complete(historical.Id, historicalClaim.Request.ClaimId!, false,
            "Historical fixture capture unavailable.", failureStage: InterfaceFailureStage.AttachmentImport);
        var historicalRetry = requests.RetryPlugin(historicalFailure.Id);
        check(historicalClaim.Claimed && historicalFailure.GeneratedSourceRoot is null &&
              reopened.GetForOwner(historical.Id).GeneratedSourceRoot is null &&
              historicalRetry.Id != historical.Id && historicalRetry.GeneratedSourceRoot == sourceRoot,
            "same-record historical claim/completion retains null while an explicit new owner retry derives fresh admission provenance");
        var legacySession = new TaskSession { Id = "generated-coordinate-legacy-session", ProfileId = profile.Id };
        store.Put("session", legacySession.Id, legacySession);
        var legacyJob = new AgentJob { Id = "generated-coordinate-legacy-job", ProfileId = profile.Id,
            Profile = profile, SessionId = legacySession.Id, Status = "completed" };
        store.Put("job", legacyJob.Id, legacyJob);
        var legacyAdmission = requests.Create(legacyJob, profile, "plugin", manifest);
        check(legacyAdmission.Source == "legacy" && legacyAdmission.GeneratedSourceRoot == sourceRoot &&
              legacyAdmission.PackageFingerprint == fingerprint &&
              requests.Create(legacyJob, profile, "plugin", manifest).Id == legacyAdmission.Id,
            "legacy scheduler admission derives and durably replays the same provider-validated source coordinate");
        var claim = requests.Claim(admitted.Id, "generated-coordinate-owner");
        var failed = requests.Complete(admitted.Id, claim.Request.ClaimId!, false, "Fixture capture unavailable.",
            failureStage: InterfaceFailureStage.AttachmentImport);
        check(claim.Claimed && failed.GeneratedSourceRoot == sourceRoot && failed.PackageFingerprint == fingerprint,
            "owner completion cannot replace the admission-bound source coordinate");
        var retry = requests.RetryPlugin(failed.Id);
        check(retry.Id != failed.Id && retry.GeneratedSourceRoot == sourceRoot && retry.Path == manifest &&
              retry.PackageFingerprint == fingerprint && requests.RetryPlugin(failed.Id).Id == retry.Id,
            "explicit owner retry revalidates the same generated coordinate and replays one exact pending request");

        var mismatched = WorkflowUiOriginTestRuns.Present(requests, store, context with { JobId = "mismatched-coordinate-job" }, "plugin", manifest);
        mismatched.GeneratedSourceRoot = project;
        store.Put("interface", mismatched.Id, mismatched);
        check(rejects(() => WorkflowUiOriginTestRuns.Present(requests, store, context with { JobId = "mismatched-coordinate-job" }, "plugin", manifest)) &&
              !requests.Claim(mismatched.Id, "mismatched-coordinate-owner").Claimed,
            "mismatched retained source metadata cannot replay or cross the owner claim boundary");
        File.Delete(guide);
        check(InterfacePathPolicy.ValidatePresentation(profile, "plugin", manifest).GeneratedSourceRoot is null,
            "a validated Release layout without its SDK guide supplies no inferred source root");
        check(rejects(() => requests.RetryPlugin(failed.Id)),
            "retry does not substitute missing source provenance for an existing admission coordinate");
        await File.WriteAllTextAsync(guide, "Do not infer paths from this prose: " + project);
        check(InterfacePathPolicy.ValidatePresentation(profile, "plugin", manifest).GeneratedSourceRoot == sourceRoot,
            "guide prose naming another directory cannot substitute the exact source root derived from verified Release geometry");
        File.Delete(projectPath);
        var missingProjectRefused = false;
        try { _ = InterfacePathPolicy.ValidatePresentation(profile, "plugin", manifest); }
        catch (InvalidDataException error)
        { missingProjectRefused = error.Message.Contains("no project file", StringComparison.Ordinal); }
        check(missingProjectRefused && rejects(() => requests.RetryPlugin(failed.Id)) &&
              requests.GetForOwner(failed.Id).GeneratedSourceRoot == sourceRoot,
            "a missing component project refuses presentation validation and retry without rewriting retained provenance");
    }

    private static async Task RunPackageBindingChecks(string root, string project, string sourceManifest,
        AgentProfile profile, StateStore store, InterfaceRequests requests, InterfaceToolExtension tool,
        Action<bool, string> check, Func<Action, bool> rejects)
    {
        var package = Path.Combine(project, "package-binding"); Directory.CreateDirectory(package);
        foreach (var file in NativeDesktopPluginPackage.EnumeratePackageFiles(Path.GetDirectoryName(sourceManifest)!))
            File.Copy(file, Path.Combine(package, Path.GetRelativePath(Path.GetDirectoryName(sourceManifest)!, file)));
        var manifest = Path.Combine(package, NativeDesktopPluginPackage.ManifestFileName);
        var assembly = Path.Combine(package, "fixture-plugin.dll");
        var context = new ToolExecutionContext(profile, "package-binding-no-legacy-row", "package-binding-job", project);
        var fingerprint = NativeDesktopPluginPackage.ComputeFingerprint(manifest);
        var first = WorkflowUiOriginTestRuns.Present(requests, store, context, "plugin", manifest, fingerprint);
        check(first.PackageFingerprint == fingerprint && WorkflowUiOriginTestRuns.Present(requests, store, context, "plugin", manifest, fingerprint).Id == first.Id,
            "unchanged package bytes reuse the same exact source-bound review request");
        await File.AppendAllTextAsync(assembly, "Changed build.");
        var changed = NativeDesktopPluginPackage.ComputeFingerprint(manifest);
        var second = WorkflowUiOriginTestRuns.Present(requests, store, context, "plugin", manifest, changed);
        check(second.Id != first.Id && second.PackageFingerprint == changed && requests.GetForOwner(first.Id).PackageFingerprint == fingerprint,
            "a changed build at the same path queues a distinct request without altering older review evidence");
        var staleClaim = requests.Claim(first.Id, "stale-review-owner");
        check(!staleClaim.Claimed && staleClaim.Request.Status == "failed" &&
            staleClaim.Request.Error!.Contains("stale", StringComparison.Ordinal) && store.GetSessionMetadata(context.SessionId) is null,
            "stale review fails before install and stores an actionable workflow outcome without creating a legacy transcript");
        var count = requests.List(context.SessionId).Count;
        var staleExpected = await WorkflowUiOriginTestRuns.Execute(tool, store, new(InterfaceToolExtension.PluginToolName,
            CerebralJson.Element(new { path = manifest, expectedPackageFingerprint = fingerprint })), context, CancellationToken.None);
        check(!staleExpected.Success && staleExpected.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
            staleExpected.Content.Contains("changed after verification", StringComparison.Ordinal) && requests.List(context.SessionId).Count == count,
            "plugin.present rejects a stale verified preimage before creating or recovering an owner review");
        foreach (var invalid in new[] { "", new string('f', 63), new string('F', 64), new string('g', 64) })
            check(rejects(() => WorkflowUiOriginTestRuns.Present(requests, store, context, "plugin", manifest, invalid)),
                "package preimage validation rejects noncanonical fingerprint length or characters");
        var generic = await WorkflowUiOriginTestRuns.Execute(tool, store, new(InterfaceToolExtension.ToolName,
            CerebralJson.Element(new { kind = "plugin", path = manifest, expectedPackageFingerprint = changed })), context, CancellationToken.None);
        check(!generic.Success && generic.EffectiveOutcome == ToolExecutionOutcome.Rejected,
            "generic interface.present does not gain the plugin-only expected fingerprint argument");
        using (var duplicate = System.Text.Json.JsonDocument.Parse("{\"path\":" + System.Text.Json.JsonSerializer.Serialize(manifest) +
            ",\"expectedPackageFingerprint\":\"" + fingerprint + "\",\"expectedPackageFingerprint\":\"" + changed + "\"}"))
        {
            var rejected = await WorkflowUiOriginTestRuns.Execute(tool, store, new(InterfaceToolExtension.PluginToolName, duplicate.RootElement), context, CancellationToken.None);
            check(!rejected.Success && requests.List(context.SessionId).Count == count,
                "duplicate preimage fields fail closed instead of selecting a convenient fingerprint");
        }
        var matched = await WorkflowUiOriginTestRuns.Execute(tool, store, new(InterfaceToolExtension.PluginToolName,
            CerebralJson.Element(new { path = manifest, expectedPackageFingerprint = changed })), context, CancellationToken.None);
        using (var document = System.Text.Json.JsonDocument.Parse(matched.Content))
            check(matched.Success && document.RootElement.GetProperty("requestId").GetString() == second.Id &&
                document.RootElement.GetProperty("packageFingerprint").GetString() == changed,
                "matching plugin.present echoes the saved exact requested package fingerprint and reused request ID");
        var currentClaim = requests.Claim(second.Id, "exact-current-owner");
        await File.AppendAllTextAsync(assembly, "Later source mutation.");
        check(currentClaim.Claimed && !requests.Claim(second.Id, "different-owner").Claimed &&
            requests.GetForOwner(second.Id).PackageFingerprint == changed,
            "a claimed review never replays or replaces its captured fingerprint after later source mutation");
        var retained = await tool.ExecuteAsync(new(InterfaceToolExtension.StatusToolName, CerebralJson.Element(new { requestId = second.Id })),
            context, CancellationToken.None);
        using (var document = System.Text.Json.JsonDocument.Parse(retained.Content))
            check(retained.Success && document.RootElement.GetProperty("packageFingerprint").GetString() == changed,
                "interface.status reads saved review identity rather than mutable current package bytes");
        var retry = requests.RetryPlugin(first.Id);
        check(retry.Id != second.Id && retry.PackageFingerprint == NativeDesktopPluginPackage.ComputeFingerprint(manifest) &&
            requests.RetryPlugin(first.Id).Id == retry.Id,
            "explicit retry binds current bytes and never reuses another claimed package generation");
        var old = new InterfaceRequest
        {
            SessionId = context.SessionId, JobId = "older-package-binding", ProfileId = profile.Id,
            Source = "workflow", Kind = "plugin", Path = manifest
        };
        WorkflowUiOriginTestRuns.Live(store, context with { JobId = old.JobId },
            new ToolCall(InterfaceToolExtension.PluginToolName, CerebralJson.Element(new { path = manifest })));
        store.Put("interface", old.Id, old);
        check(!requests.Claim(old.Id, "old-pending-owner").Claimed && requests.GetForOwner(old.Id).PackageFingerprint is null &&
            requests.GetForOwner(old.Id).Status == "failed",
            "older pending plugin review fails conservatively without synthesizing identity or installing current mutable bytes");
        old = new InterfaceRequest
        {
            SessionId = context.SessionId, JobId = "older-claimed-binding", ProfileId = profile.Id,
            Source = "workflow", Kind = "plugin", Path = manifest, Status = "claimed", ClaimId = "older-already-claimed"
        };
        store.Put("interface", old.Id, old);
        check(requests.Complete(old.Id, old.ClaimId, false, "Older owner action failed.").PackageFingerprint is null,
            "older already-claimed outcomes remain compatible without backfilled fingerprint evidence");
        var invalidContext = context with { JobId = "invalid-package-binding" };
        var invalidRequest = WorkflowUiOriginTestRuns.Present(requests, store, invalidContext, "plugin", manifest);
        var manifestText = await File.ReadAllTextAsync(manifest);
        try
        {
            await File.WriteAllTextAsync(manifest, "{invalid manifest");
            var malformedClaim = requests.Claim(invalidRequest.Id, "invalid-manifest-owner");
            check(!malformedClaim.Claimed && malformedClaim.Request.Status == "failed" && malformedClaim.Request.Error is not null,
                "invalid manifest syntax at claim settles as a retained failure before owner installation");
            var rejected = await WorkflowUiOriginTestRuns.Execute(tool, store, new(InterfaceToolExtension.PluginToolName,
                CerebralJson.Element(new { path = manifest })), invalidContext, CancellationToken.None);
            check(!rejected.Success && rejected.EffectiveOutcome == ToolExecutionOutcome.Rejected,
                "invalid manifest syntax during presentation is deterministic rejection evidence, not an unowned exception");
        }
        finally { await File.WriteAllTextAsync(manifest, manifestText); }
    }

    private static async Task RunInstalledPluginReceiptChecks(string root, string project, string sourceManifest,
        string sourceAssembly, AgentProfile profile, StateStore store, InterfaceRequests requests,
        Action<bool, string> check, Func<Action, bool> rejects)
    {
        const string pluginId = "acceptance.interface-fixture";
        var generation = "1.0.0-" + Guid.NewGuid().ToString("N");
        var repository = Path.Combine(root, "installed-plugins");
        var directory = Path.Combine(repository, ".packages", pluginId, generation);
        var installedManifest = Path.Combine(directory, NativeDesktopPluginPackage.ManifestFileName);
        var activePath = Path.Combine(repository, ".settings", pluginId + ".active.json");
        Directory.CreateDirectory(directory); Directory.CreateDirectory(Path.GetDirectoryName(activePath)!);
        File.Copy(sourceManifest, installedManifest); File.Copy(sourceAssembly, Path.Combine(directory, "fixture-plugin.dll"));
        var sourceText = await File.ReadAllTextAsync(sourceManifest);
        var activeText = "{\"Generation\":\"" + generation + "\"}";
        await File.WriteAllTextAsync(activePath, activeText);
        var valid = new InterfacePluginReceipt(pluginId, "1.0.0", generation, installedManifest,
            [new("sidebar", pluginId + ".main"), new("sidebar", pluginId + ".summary")], true);
        var context = new ToolExecutionContext(profile, "installed-receipt-conversation", "installed-receipt-job", project);
        var request = WorkflowUiOriginTestRuns.Present(requests, store, context, "plugin", sourceManifest);
        requests.Claim(request.Id, "installed-receipt-owner");
        InterfaceRequest Complete(InterfacePluginReceipt value) => requests.Complete(request.Id,
            "installed-receipt-owner", true, message: "Owner installed this exact fixture generation.", plugin: value);

        var malformed = new (InterfacePluginReceipt Receipt, string Name)[]
        {
            (valid with { SchemaVersion = 2 }, "unsupported receipt schema"),
            (valid with { Id = "../other" }, "traversing plugin identity"),
            (valid with { Version = "invalid-version" }, "invalid version"),
            (valid with { Version = new string('1', 65) }, "oversized version"),
            (valid with { Generation = "../outside" }, "traversing generation"),
            (valid with { Generation = "1.0.0-" + Guid.Empty.ToString("N") }, "empty catalog generation ID"),
            (valid with { Generation = "2.0.0-" + Guid.NewGuid().ToString("N") }, "generation/version mismatch"),
            (valid with { Generation = new string('x', 161) }, "oversized generation"),
            (valid with { ManifestPath = "desktop-plugin.json" }, "relative installed path"),
            (valid with { ManifestPath = installedManifest + "\0" }, "NUL in installed path"),
            (valid with { ManifestPath = new string('x', 32769) }, "oversized installed path"),
            (valid with { ManifestPath = Path.Combine(directory, "..", generation, "desktop-plugin.json") }, "noncanonical path traversal"),
            (valid with { ManifestPath = sourceManifest }, "source manifest posing as installed geometry"),
            (valid with { Mounts = null! }, "missing mount collection"),
            (valid with { Mounts = [null!] }, "null mount"),
            (valid with { Mounts = [new("status", pluginId + ".main")] }, "unsupported control slot"),
            (valid with { Mounts = [new("sidebar", "foreign.main")] }, "foreign plugin mount"),
            (valid with { Mounts = [new("sidebar", pluginId + ".")] }, "empty local mount"),
            (valid with { Mounts = [new("sidebar", pluginId + "." + new string('x', 65))] }, "oversized local mount"),
            (valid with { Mounts = [new("sidebar", pluginId + ".main\0")] }, "NUL in mount"),
            (valid with { Mounts = [new("sidebar", pluginId + ".main"), new("sidebar", pluginId + ".main")] }, "duplicate qualified mount"),
            (valid with { Mounts = Enumerable.Range(0, 73).Select(index => new InterfacePluginMountReceipt("sidebar",
                pluginId + ".panel" + index)).ToArray() }, "oversized mount list")
        };
        foreach (var (receipt, name) in malformed)
            check(rejects(() => Complete(receipt)) && requests.GetForOwner(request.Id).Status == "claimed",
                "installed plugin receipt rejects " + name + " without completing the request");

        await File.WriteAllTextAsync(activePath, "{\"Generation\":\"1.0.0-" + Guid.NewGuid().ToString("N") + "\"}");
        check(rejects(() => Complete(valid)), "a retired generation cannot create a new installed receipt");
        await File.WriteAllTextAsync(activePath, "{\"Generation\":\"" + generation + "\",\"generation\":\"" + generation + "\"}");
        check(rejects(() => Complete(valid)), "duplicate active-generation fields are rejected");
        await File.WriteAllTextAsync(activePath, "{}");
        check(rejects(() => Complete(valid)), "missing active-generation metadata fails closed");
        await File.WriteAllTextAsync(activePath, new string(' ', 4097));
        check(rejects(() => Complete(valid)), "active-generation metadata has a 4096-byte bound");
        await File.WriteAllTextAsync(activePath, activeText);
        await File.WriteAllTextAsync(installedManifest, sourceText.Replace("\"version\":\"1.0.0\"", "\"version\":\"2.0.0\"", StringComparison.Ordinal));
        check(rejects(() => Complete(valid)), "the current installed manifest version must match the receipt");
        await File.WriteAllTextAsync(installedManifest, sourceText);
        await File.WriteAllTextAsync(sourceManifest, sourceText.Replace("\"id\":\"" + pluginId + "\"", "\"id\":\"acceptance.changed\"", StringComparison.Ordinal));
        check(rejects(() => Complete(valid)), "the receipt cannot substitute another package for the admitted source request");
        await File.WriteAllTextAsync(sourceManifest, sourceText);
        var installedAssembly = Path.Combine(directory, "fixture-plugin.dll");
        var installedAssemblyBytes = await File.ReadAllBytesAsync(installedAssembly);
        await File.AppendAllTextAsync(installedAssembly, "Changed installed entry bytes.");
        check(rejects(() => Complete(valid)) && requests.GetForOwner(request.Id).Status == "claimed",
            "installed entry bytes must match the reviewed fingerprint without replaying the owner claim");
        await File.WriteAllBytesAsync(installedAssembly, installedAssemblyBytes);
        var addedDependency = Path.Combine(directory, "new-dependency.dll");
        await File.WriteAllTextAsync(addedDependency, "Changed installed runtime dependency.");
        check(rejects(() => Complete(valid)) && requests.GetForOwner(request.Id).Status == "claimed",
            "unreviewed installed dependencies cannot be hidden behind matching manifest and entry bytes");
        File.Delete(addedDependency);
        var heldAssembly = Path.Combine(directory, "fixture-plugin.dll.held");
        try { File.Move(installedAssembly, heldAssembly); }
        catch (UnauthorizedAccessException error)
        {
            throw new UnauthorizedAccessException(
                $"Installed-receipt fixture could not hold the adjacent entry; source={installedAssembly}; target={heldAssembly}; sourceAttributes={File.GetAttributes(installedAssembly)}; targetExists={File.Exists(heldAssembly)}; hresult=0x{error.HResult:X8}.",
                error);
        }
        try { check(rejects(() => Complete(valid)), "the installed receipt requires its adjacent built assembly without executing it"); }
        finally { File.Move(heldAssembly, installedAssembly); }

        foreach (var linkPath in new[] { installedManifest, activePath })
        {
            var heldPath = linkPath + ".held";
            File.Move(linkPath, heldPath);
            var linked = false;
            try
            {
                try { File.CreateSymbolicLink(linkPath, heldPath); linked = true; }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                { Console.WriteLine("SKIP: installed receipt link fixture could not be created: " + error.GetType().Name); }
                if (linked)
                    check(rejects(() => Complete(valid)), linkPath == installedManifest
                        ? "installed receipt rejects a linked manifest before reading its identity"
                        : "installed receipt rejects linked active-generation metadata before granting control");
            }
            finally
            {
                if (linked) File.Delete(linkPath);
                File.Move(heldPath, linkPath);
            }
        }

        var failedRequest = WorkflowUiOriginTestRuns.Present(requests, store, context with { JobId = "failed-installed-receipt" }, "plugin", sourceManifest);
        requests.Claim(failedRequest.Id, "failed-installed-owner");
        check(rejects(() => requests.Complete(failedRequest.Id, "failed-installed-owner", false, "Actual fixture failure.", plugin: valid)) &&
              requests.GetForOwner(failedRequest.Id).Plugin is null && requests.GetForOwner(failedRequest.Id).Status == "claimed",
            "a failed owner action cannot publish a plugin binding or control permission");
        var fileRequest = WorkflowUiOriginTestRuns.Present(requests, store, context with { JobId = "file-installed-receipt" }, "file", sourceManifest);
        requests.Claim(fileRequest.Id, "file-installed-owner");
        check(rejects(() => requests.Complete(fileRequest.Id, "file-installed-owner", true, plugin: valid)),
            "a non-plugin presentation cannot carry installed plugin authority");

        var callerMounts = valid.Mounts.ToList();
        var completed = Complete(valid with { Mounts = callerMounts });
        callerMounts.Clear();
        check(completed.Plugin is { ControlTestsAllowed: true } retained && retained.Generation == generation &&
              retained.ManifestPath == installedManifest && retained.Mounts.Count == 2 &&
              requests.GetForOwner(request.Id).Plugin!.Mounts.Count == 2,
            "owner completion retains a detached typed immutable installed coordinate and explicit control choice");
        var decoded = CerebralJson.Read<InterfacePluginReceipt>(CerebralJson.Element(valid));
        check(!ReferenceEquals(decoded.Mounts, valid.Mounts) && Complete(decoded).Id == request.Id &&
              Complete(decoded with { Mounts = decoded.Mounts.Reverse().ToArray() }).Id == request.Id,
            "serialized and reordered equivalent mount lists retry structurally without replay");
        check(rejects(() => Complete(valid with { ControlTestsAllowed = false })) &&
              rejects(() => Complete(valid with { Mounts = [new("sidebar", pluginId + ".replacement")] })),
            "a terminal retry cannot alter owner control consent or mounted identities");

        File.Move(sourceManifest, sourceManifest + ".held");
        File.Move(installedManifest, installedManifest + ".held");
        File.Move(activePath, activePath + ".held");
        try
        {
            check(Complete(decoded).Id == request.Id,
                "an identical completed receipt retries before any source, manifest or catalog filesystem reads");
        }
        finally
        {
            File.Move(sourceManifest + ".held", sourceManifest);
            File.Move(installedManifest + ".held", installedManifest);
            File.Move(activePath + ".held", activePath);
        }
        var compatibility = WorkflowUiOriginTestRuns.Present(requests, store, context with { JobId = "old-plugin-receipt" }, "plugin", sourceManifest);
        requests.Claim(compatibility.Id, "old-plugin-owner");
        requests.Complete(compatibility.Id, "old-plugin-owner", true, message: "Older owner completion without typed binding.");
        check(requests.GetForOwner(compatibility.Id).Plugin is null,
            "older plugin receipts remain readable without gaining a control binding");
        var defaultConsent = valid with { ControlTestsAllowed = false };
        var noConsent = WorkflowUiOriginTestRuns.Present(requests, store, context with { JobId = "plugin-no-consent" }, "plugin", sourceManifest);
        requests.Claim(noConsent.Id, "plugin-no-consent-owner");
        requests.Complete(noConsent.Id, "plugin-no-consent-owner", true, plugin: defaultConsent);
        check(requests.GetForOwner(noConsent.Id).Plugin is { ControlTestsAllowed: false },
            "successful installation can retain identity without granting control testing");

        var controlsProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
        controlsProfile.AllowedTools.Add("plugin.controls");
        var controlContext = context with { Profile = controlsProfile, JobId = "installed-control-receipt-job" };
        try
        {
            store.Put("agent", profile.Id, controlsProfile);
            controlContext = WorkflowUiOriginTestRuns.Metadata(store, controlContext);
            check(requests.GetForContext(request.Id, controlContext, "plugin.controls", "desktop.plugins.manage").Id == request.Id,
                "the fixed plugin-controls route can read its own retained receipt with current local lead grants");
            check(rejects(() => requests.GetForContext(request.Id, controlContext, "plugin.controls", "filesystem.read")) &&
                  rejects(() => requests.GetForContext(request.Id, controlContext, "file.read", "filesystem.read")) &&
                  rejects(() => requests.GetForContext(request.Id, controlContext, InterfaceToolExtension.StatusToolName, "desktop.plugins.manage")),
                "host receipt access rejects every unapproved tool/capability pairing");
            check(rejects(() => requests.GetForContext(request.Id, controlContext with { SessionId = "foreign-conversation" },
                    "plugin.controls", "desktop.plugins.manage")),
                "a control grant does not expose another conversation's presentation");
            var acceptedWithoutTool = CerebralJson.Read<AgentProfile>(CerebralJson.Element(controlsProfile));
            acceptedWithoutTool.AllowedTools.Remove("plugin.controls");
            var acceptedWithoutCapability = CerebralJson.Read<AgentProfile>(CerebralJson.Element(controlsProfile));
            acceptedWithoutCapability.Capabilities.Remove("desktop.plugins.manage");
            var frozenWithoutTool = WorkflowUiOriginTestRuns.Metadata(store,
                controlContext with { Profile = acceptedWithoutTool, JobId = "controls-accepted-without-tool" });
            var frozenWithoutCapability = WorkflowUiOriginTestRuns.Metadata(store,
                controlContext with { Profile = acceptedWithoutCapability, JobId = "controls-accepted-without-capability" });
            check(rejects(() => requests.GetForContext(request.Id, frozenWithoutTool,
                    "plugin.controls", "desktop.plugins.manage")) &&
                  rejects(() => requests.GetForContext(request.Id, frozenWithoutCapability,
                    "plugin.controls", "desktop.plugins.manage")) &&
                  rejects(() => requests.GetForContext(request.Id, controlContext with { Profile = acceptedWithoutTool },
                    "plugin.controls", "desktop.plugins.manage")) &&
                  rejects(() => requests.GetForContext(request.Id, controlContext with { Profile = acceptedWithoutCapability },
                    "plugin.controls", "desktop.plugins.manage")),
                "a later grant increase does not widen the accepted workflow's receipt access");
            var revokedTool = CerebralJson.Read<AgentProfile>(CerebralJson.Element(controlsProfile));
            revokedTool.AllowedTools.Remove("plugin.controls"); store.Put("agent", profile.Id, revokedTool);
            check(rejects(() => requests.GetForContext(request.Id, controlContext, "plugin.controls", "desktop.plugins.manage")),
                "receipt access rechecks a revoked current control tool grant");
            var revokedCapability = CerebralJson.Read<AgentProfile>(CerebralJson.Element(controlsProfile));
            revokedCapability.Capabilities.Remove("desktop.plugins.manage"); store.Put("agent", profile.Id, revokedCapability);
            check(rejects(() => requests.GetForContext(request.Id, controlContext with { FullAccess = true },
                    "plugin.controls", "desktop.plugins.manage")),
                "Full Access does not replace the current plugin-management capability");
            var remote = CerebralJson.Read<AgentProfile>(CerebralJson.Element(controlsProfile)); remote.WorkerId = "remote";
            store.Put("agent", profile.Id, remote);
            check(rejects(() => requests.GetForContext(request.Id, controlContext, "plugin.controls", "desktop.plugins.manage")),
                "a remote current profile cannot read a local plugin-control binding");
            var compactor = CerebralJson.Read<AgentProfile>(CerebralJson.Element(controlsProfile)); compactor.Role = "compactor";
            store.Put("agent", profile.Id, compactor);
            check(rejects(() => requests.GetForContext(request.Id, controlContext, "plugin.controls", "desktop.plugins.manage")),
                "a compactor current profile cannot inherit owner receipt access");
        }
        finally { store.Put("agent", profile.Id, profile); }
    }

    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Interface request tests do not execute a model.");
    }
}

// Parameterless metadata fixture only; no native plugin activation or functional claim.
public sealed class InterfaceEntryFixture { }
