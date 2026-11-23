using System.IO;
using System.Text.Json;
using System.Windows;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

internal static class DesktopControlCommand
{
    public static Task<int> RunAsync(string[] args) => Cerebral.Desktop.SemanticControlCommand.RunAsync(args,
        "--desktop-control-call", DesktopControlProtocol.Product, DesktopControlProtocol.PipePrefix, typeof(Program).Assembly.Location);
}

internal static class DesktopControlChecks
{
    public static int Run(Application app)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup += async (_, _) =>
        {
            var exit = 0;
            var root = Path.Combine(Path.GetTempPath(), "Pegasus-Cerebral-Desktop-Control-" + Guid.NewGuid().ToString("N"));
            var window = new OrchestratorWindow();
            // Rendering fixture controls must not connect to the saved live owner or load its state.
            typeof(Cerebral.Desktop.ConnectedWindow).GetMethod("EnterInspectionMode",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [null]);
            DesktopControlServer? server = null;
            try
            {
                var checks = 0;
                server = await DesktopControlServer.StartAsync(window, root);
                Check(File.Exists(server.DescriptorPath), "The current-user endpoint descriptor was not published."); checks++;
                Check(server.Endpoint.ProcessId == Environment.ProcessId && server.Endpoint.Protocol == DesktopControlProtocol.Version,
                    "The endpoint does not identify this desktop generation."); checks++;

                var inspect = await Call(server, "snapshot.inspect", new { });
                Check(inspect.Success && inspect.Data.GetProperty("connected").ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "Snapshot inspection did not return semantic connection state."); checks++;

                var setId = Guid.NewGuid().ToString("N");
                var set = await Call(server, "composer.set", new { text = "semantic control" }, setId);
                Check(set.Success && set.Data.GetProperty("characters").GetInt32() == 16,
                    "Composer control did not apply the bounded draft."); checks++;
                var repeated = await Call(server, "composer.set", new { text = "semantic control" }, setId);
                Check(repeated.Success && repeated.Data.GetProperty("characters").GetInt32() == 16,
                    "An equivalent request retry did not return its retained receipt."); checks++;
                var conflict = await Call(server, "composer.set", new { text = "changed" }, setId);
                Check(conflict.State == "conflict", "Changed input reused a retained request ID."); checks++;

                var surface = await Call(server, "surface.open", new { id = "files" });
                Check(surface.Success && surface.Data.GetProperty("surface").GetString() == "files",
                    "Named surface selection did not use the semantic identifier."); checks++;
                var after = await Call(server, "snapshot.inspect", new { includeLastResponse = true });
                Check(after.Data.GetProperty("surface").GetString() == "files" &&
                    after.Data.GetProperty("composerCharacters").GetInt32() == 16,
                    "The semantic snapshot did not reflect the controlled desktop state."); checks++;

                var pluginSurfaces = await Call(server, "plugin.surface.inspect", new { });
                Check(pluginSurfaces.Success && pluginSurfaces.Data.GetProperty("sidebars").ValueKind == JsonValueKind.Array &&
                    pluginSurfaces.Data.GetProperty("transcriptVisible").ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "Plug-in surface inspection did not return bounded sidebar and transcript state."); checks++;

                var correction = await Call(server, "interface.correct", new
                {
                    id = "not-an-owner-review", sessionId = "other-conversation",
                    packageFingerprint = new string('a', 64)
                });
                Check(!correction.Success, "A disconnected or unselected review must not mutate the draft."); checks++;
                var afterCorrection = await Call(server, "snapshot.inspect", new { });
                Check(afterCorrection.Data.GetProperty("composerCharacters").GetInt32() == 16,
                    "Rejected plugin correction changed the existing composer."); checks++;


                _ = await Call(server, "surface.open", new { id = "chat" });
                var absentGuidance = await Call(server, "input-guidance.inspect", new { });
                Check(!absentGuidance.Success, "An absent review must not manufacture input-guidance controls."); checks++;
                var guidanceHost = (System.Windows.Controls.Border)typeof(OrchestratorWindow)
                    .GetField("desktopInputGuidanceHost", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(window)!;
                var guidanceView = new System.Windows.Controls.StackPanel();
                var correctionField = new System.Windows.Controls.TextBox { Text = "original correction" };
                var acceptedClicks = 0;
                var useDraft = new System.Windows.Controls.Button { Content = "Use test draft" };
                useDraft.Click += (_, _) => acceptedClicks++;
                guidanceView.Children.Add(correctionField); guidanceView.Children.Add(useDraft);
                guidanceHost.Child = new System.Windows.Controls.ScrollViewer { Content = guidanceView };
                guidanceHost.Visibility = Visibility.Visible;
                var guidance = await Call(server, "input-guidance.inspect", new { });
                Check(guidance.Success, "The native review controls were not inspectable: " + guidance.Error); checks++;
                var guidanceId = guidance.Data.GetProperty("id").GetString()!;
                var fieldId = guidance.Data.GetProperty("controls").GetProperty("fields")[0].GetProperty("id").GetString()!;
                var correctionEdit = await Call(server, "input-guidance.setText",
                    new { id = guidanceId, fieldId, expectedText = "original correction", text = "refined correction" });
                Check(correctionEdit.Success && correctionField.Text == "refined correction",
                    "An exact inspected correction field was not edited through its native value provider."); checks++;
                var staleEdit = await Call(server, "input-guidance.setText",
                    new { id = guidanceId, fieldId, expectedText = "original correction", text = "stale edit" });
                Check(!staleEdit.Success && correctionField.Text == "refined correction",
                    "A stale correction overwrote newer text."); checks++;
                var invoke = await Call(server, "input-guidance.invoke", new { id = guidanceId, name = "Use test draft" });
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check(invoke.Success && invoke.State == "accepted" && acceptedClicks == 1,
                    "Native review invocation did not deliver exactly one click with a delivery-only receipt."); checks++;
                guidanceHost.Child = new System.Windows.Controls.ScrollViewer { Content = new System.Windows.Controls.StackPanel() };
                var staleInvoke = await Call(server, "input-guidance.invoke", new { id = guidanceId, name = "Use test draft" });
                Check(!staleInvoke.Success && acceptedClicks == 1,
                    "A replaced input-guidance view accepted an old action."); checks++;
                guidanceHost.Visibility = Visibility.Collapsed;


                var nativeInspect = await Call(server, "native-plugins.inspect", new { limit = 1 });
                Check(nativeInspect.State == "completed" && nativeInspect.Data.GetProperty("reviews").GetArrayLength() == 0 &&
                    nativeInspect.Data.GetProperty("omitted").GetInt32() == 0,
                    "Installed-plugin inspection manufactured a review in a disconnected window."); checks++;
                foreach (var arguments in new[] { "{\"limit\":0}", "{\"limit\":9}", "{\"limit\":1.5}",
                    "{\"limit\":null}", "{\"limit\":\"1\"}", "{\"unknown\":true}", "{\"limit\":1,\"limit\":2}" })
                {
                    Check(await RejectedArguments(server, "native-plugins.inspect", arguments),
                        "An invalid installed-plugin inspection argument was accepted."); checks++;
                }
                var absentNativeId = Guid.NewGuid().ToString("N");
                var nativeReviewId = Guid.NewGuid().ToString("N");
                var absentNative = await Call(server, "native-plugins.review",
                    new { id = absentNativeId, approved = true }, nativeReviewId);
                Check(absentNative.State == "rejected",
                    "A missing local installed-plugin card allowed an owner action."); checks++;
                var repeatedNative = await Call(server, "native-plugins.review",
                    new { id = absentNativeId, approved = true }, nativeReviewId);
                Check(repeatedNative.State == absentNative.State && repeatedNative.Error == absentNative.Error,
                    "A native review retry did not return its retained desktop receipt."); checks++;
                var conflictingNative = await Call(server, "native-plugins.review",
                    new { id = absentNativeId, approved = false }, nativeReviewId);
                Check(conflictingNative.State == "conflict",
                    "Changed installed-plugin review input reused the same receipt."); checks++;
                foreach (var arguments in new[]
                {
                    "{\"id\":\"bad-id\",\"approved\":true}",
                    "{\"id\":\"" + new string('0', 32) + "\",\"approved\":true}",
                    "{\"id\":\"" + absentNativeId + "\"}",
                    "{\"id\":\"" + absentNativeId + "\",\"approved\":\"true\"}",
                    "{\"id\":\"" + absentNativeId + "\",\"approved\":true,\"approved\":false}",
                    "{\"id\":\"" + absentNativeId + "\",\"approved\":true,\"sessionId\":null}",
                    "{\"id\":\"" + absentNativeId + "\",\"approved\":true,\"bypass\":true}"
                })
                {
                    Check(await RejectedArguments(server, "native-plugins.review", arguments),
                        "Invalid installed-plugin review arguments reached an action."); checks++;
                }
                var wrongNativeOwner = await Call(server, "native-plugins.review",
                    new { id = absentNativeId, approved = false, sessionId = "another-chat" });
                Check(wrongNativeOwner.State == "rejected",
                    "Installed-plugin review accepted another conversation owner."); checks++;
                var afterNative = await Call(server, "snapshot.inspect", new { });
                Check(afterNative.Data.GetProperty("composerCharacters").GetInt32() == 16 &&
                    afterNative.Data.GetProperty("connected").GetBoolean() == inspect.Data.GetProperty("connected").GetBoolean(),
                    "Installed-plugin inspection/rejection changed the draft or established a connection."); checks++;
                var retainedOutcomeChecks = NativePluginRetainedOutcomeChecks() + await NativePluginCancellationChecks(window)
                    + NativePluginProjectionIdentityChecks(window);

                await server.DisposeAsync(); server = null;
                Check(!File.Exists(Path.Combine(root, "Desktop", "control.json")),
                    "Desktop shutdown retained an endpoint for a dead generation."); checks++;
                var normalChecks = await DesktopControlNormalChecks.RunAsync(window, Path.Combine(root, "normal"));
                Console.WriteLine($"Desktop semantic control: {checks} same-user pipe and WPF checks, {normalChecks} normal-mode identity/admission/receipt/capture checks, {retainedOutcomeChecks} deterministic retained-outcome/layout checks passed; no service or model command executed.");
            }
            catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
            finally
            {
                if (server is not null) await server.DisposeAsync();
                window.Close();
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
                app.Shutdown(exit);
            }
        };
        return app.Run();
    }


    private static int NativePluginRetainedOutcomeChecks()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var stateMethod = typeof(OrchestratorWindow).GetMethod("NativePluginManagementRetainedControlState", flags)!;
        var projectionMethod = typeof(OrchestratorWindow).GetMethod("NativePluginManagementResultControlSnapshot", flags)!;
        var presentedMethod = typeof(OrchestratorWindow).GetMethod("NativePluginManagementElementIsPresented", flags)!;
        var claimId = Guid.NewGuid().ToString("N");
        var expected = new NativePluginManagementRequest
        {
            Id = Guid.NewGuid().ToString("N"), SessionId = "fixture-chat", ProfileId = "fixture-lead",
            ProfileRevision = 5, JobId = "fixture-run", Operation = "set-enabled",
            CatalogRequestId = Guid.NewGuid().ToString("N"), CatalogFingerprint = new string('c', 64),
            Selection = new("fixture.plugin", false, "private-generation-marker", "private-package-marker"),
            Enabled = true, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        NativePluginManagementRequest Copy() => JsonSerializer.Deserialize<NativePluginManagementRequest>(
            JsonSerializer.Serialize(expected, CerebralJson.Options), CerebralJson.Options)!;
        var retained = Copy(); retained.Status = "completed"; retained.ClaimId = claimId;
        var entry = new NativePluginCatalogEntry(expected.Selection.Id, "Fixture plug-in", "1.0",
            expected.Selection.Generation, expected.Selection.PackageFingerprint, true, true, null);
        retained.Result = new(new("private-catalog-marker", [entry], 3), [entry.Id]);
        string State(NativePluginManagementRequest value, bool approved = true, string? claim = null, bool rejectedBeforeClaim = false) =>
            (string)stateMethod.Invoke(null, [expected, value, approved, claim ?? claimId, rejectedBeforeClaim])!;
        void Reject(NativePluginManagementRequest value, string label, bool approved = true, string? claim = null,
            bool rejectedBeforeClaim = false)
        {
            try { _ = State(value, approved, claim, rejectedBeforeClaim); }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is InvalidOperationException) { return; }
            throw new InvalidOperationException(label);
        }

        var checks = 0;
        Check(State(retained) == "completed", "An exact retained loaded completion was not recognized."); checks++;
        var disableExpected = expected.Enabled;
        expected.Enabled = false;
        var disabled = Copy(); disabled.Status = "completed"; disabled.ClaimId = claimId;
        disabled.Result = new(new("private-catalog-marker", [entry with { Enabled = false, Loaded = false }]), [entry.Id]);
        Check(State(disabled) == "completed", "An exact retained unloaded completion was not recognized."); checks++;
        expected.Enabled = disableExpected;
        var rejected = Copy(); rejected.Status = "rejected"; rejected.ClaimId = claimId; rejected.Error = "Fixture rejected.";
        Check(State(rejected) == "rejected", "A retained rejection was confused with delivery."); checks++;
        var uncertain = Copy(); uncertain.Status = "uncertain"; uncertain.ClaimId = claimId; uncertain.Error = "Fixture uncertainty.";
        Check(State(uncertain) == "uncertain", "A retained uncertainty was confused with completion."); checks++;
        var working = Copy(); working.Status = "claimed"; working.ClaimId = claimId;
        Check(State(working) == "uncertain", "A claim without terminal confirmation was reported completed."); checks++;
        var decline = Copy(); decline.Status = "rejected"; decline.Error = "The owner declined this plugin change.";
        Check(State(decline, approved: false) == "rejected", "An unclaimed retained decline was not recognized."); checks++;
        Check(State(decline, rejectedBeforeClaim: true) == "rejected", "A confirmed host preclaim rejection was not recognized."); checks++;
        Reject(decline, "A rejection from an unconfirmed claim was accepted."); checks++;
        Reject(retained, "Declining adopted another completed action.", approved: false); checks++;
        var wrongClaim = Copy(); wrongClaim.Status = "rejected"; wrongClaim.ClaimId = Guid.NewGuid().ToString("N");
        Reject(wrongClaim, "A retained outcome from another claim was accepted."); checks++;
        foreach (var changed in new Action<NativePluginManagementRequest>[]
        {
            value => value.Id = Guid.NewGuid().ToString("N"), value => value.SessionId = "another-chat",
            value => value.ProfileRevision++, value => value.JobId = "another-run", value => value.Enabled = false
        })
        {
            var other = JsonSerializer.Deserialize<NativePluginManagementRequest>(
                JsonSerializer.Serialize(retained, CerebralJson.Options), CerebralJson.Options)!;
            changed(other); Reject(other, "A changed retained effect was accepted."); checks++;
        }
        foreach (var badEntry in new[] { entry with { Loaded = false }, entry with { Enabled = false },
            entry with { Generation = "another-generation" }, entry with { PackageFingerprint = "another-package" },
            entry with { Error = "load error" } })
        {
            var invalid = Copy(); invalid.Status = "completed"; invalid.ClaimId = claimId;
            invalid.Result = new(new("private-catalog-marker", [badEntry]), []);
            Reject(invalid, "An unconfirmed catalog/runtime state was reported completed."); checks++;
        }
        var missingResult = Copy(); missingResult.Status = "completed"; missingResult.ClaimId = claimId;
        Reject(missingResult, "A retained completion without observation was accepted."); checks++;
        var snapshot = CerebralJson.Element(projectionMethod.Invoke(null, [retained.Result]));
        var serialized = snapshot.GetRawText();
        Check(!serialized.Contains("private-", StringComparison.Ordinal) &&
            snapshot.GetProperty("catalog").GetProperty("plugins")[0].GetProperty("loaded").GetBoolean() &&
            snapshot.GetProperty("catalog").GetProperty("omitted").GetInt32() == 3 &&
            snapshot.GetProperty("changedPluginIds")[0].GetString() == entry.Id,
            "The public result omitted actual outcomes or exposed catalog/package identifiers."); checks++;

        var panel = new System.Windows.Controls.StackPanel();
        var card = new System.Windows.Controls.Border();
        var button = new System.Windows.Controls.Button();
        card.Child = button; panel.Children.Add(card);
        bool Presented(FrameworkElement element, FrameworkElement ancestor) =>
            (bool)presentedMethod.Invoke(null, [element, ancestor])!;
        Check(Presented(button, panel), "A mounted visible review control was not recognized."); checks++;
        card.Visibility = Visibility.Collapsed;
        Check(!Presented(button, panel), "A collapsed review card was treated as presented."); checks++;
        card.Visibility = Visibility.Visible; button.Visibility = Visibility.Hidden;
        Check(!Presented(button, card), "A hidden review control was treated as presented."); checks++;
        button.Visibility = Visibility.Visible; panel.Children.Remove(card);
        Check(!Presented(button, panel), "A detached review card was treated as mounted."); checks++;
        return checks;
    }


    private static async Task<int> NativePluginCancellationChecks(OrchestratorWindow window)
    {
        const System.Reflection.BindingFlags privateInstance = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        const System.Reflection.BindingFlags privateStatic = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var ownerType = typeof(OrchestratorWindow).GetNestedType("NativePluginOwnerIdentity", System.Reflection.BindingFlags.NonPublic)!;
        var reviewType = typeof(OrchestratorWindow).GetNestedType("NativePluginManagementReview", System.Reflection.BindingFlags.NonPublic)!;
        var reviews = (System.Collections.IDictionary)typeof(OrchestratorWindow)
            .GetField("nativePluginManagementReviews", privateInstance)!.GetValue(window)!;
        var host = (System.Windows.Controls.StackPanel)typeof(OrchestratorWindow)
            .GetField("nativePluginManagementReviewsHost", privateInstance)!.GetValue(window)!;
        var eventMethod = typeof(OrchestratorWindow).GetMethod("NativePluginManagementEventAsync", privateInstance)!;
        var snapshotMethod = typeof(OrchestratorWindow).GetMethod("NativePluginManagementSnapshotInvalidates", privateStatic)!;
        var workflowMethod = typeof(OrchestratorWindow).GetMethod("NativePluginManagementWorkflowInvalidates", privateStatic)!;
        var request = new NativePluginManagementRequest
        {
            Id = Guid.NewGuid().ToString("N"), SessionId = "cancel-fixture-chat", JobId = "cancel-fixture-run",
            ProfileId = "cancel-fixture-lead", ProfileRevision = 3, Operation = "set-enabled", Enabled = true,
            Selection = new("cancel.fixture", false, "fixture-generation", "fixture-package"),
            CatalogFingerprint = "fixture-catalog", CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        NativePluginManagementRequest Copy() => JsonSerializer.Deserialize<NativePluginManagementRequest>(
            JsonSerializer.Serialize(request, CerebralJson.Options), CerebralJson.Options)!;
        var checks = 0;
        object? owner = null;
        var approve = new System.Windows.Controls.Button();
        var decline = new System.Windows.Controls.Button();
        object AddReview(bool busy = false)
        {
            owner = Activator.CreateInstance(ownerType, System.Reflection.BindingFlags.Public | privateInstance, null,
                [request, new Cerebral.Desktop.ConnectionPreferences(), "fixture-endpoint", 1, 1L, request.CreatedAt], null)!;
            var view = new System.Windows.Controls.Border();
            var buttons = new System.Windows.Controls.StackPanel();
            approve = new(); decline = new(); buttons.Children.Add(approve); buttons.Children.Add(decline); view.Child = buttons;
            var review = Activator.CreateInstance(reviewType, System.Reflection.BindingFlags.Public | privateInstance, null,
                [owner, view, new System.Windows.Controls.TextBlock(), approve, decline, "Fixture", "1"], null)!;
            reviewType.GetProperty("Busy", privateInstance)!.SetValue(review, busy);
            reviews.Add(request.Id, review); host.Children.Add(view); return review;
        }
        Task Notify(NativePluginManagementRequest value) => (Task)eventMethod.Invoke(window,
            [new ServiceEvent(1, "desktop.plugins.management.updated", value.JobId, CerebralJson.Element(value), DateTimeOffset.UtcNow)])!;
        try
        {
            foreach (var terminal in new[] { "cancelled", "rejected", "expired", "uncertain", "completed" })
            {
                AddReview();
                var value = Copy(); value.Status = terminal; await Notify(value);
                Check(!reviews.Contains(request.Id) && !approve.IsEnabled && !decline.IsEnabled &&
                    (bool)ownerType.GetProperty("Invalidated", privateInstance)!.GetValue(owner!)!,
                    "A terminal native request retained enabled review controls or captured admission."); checks++;
            }
            AddReview();
            var foreign = Copy(); foreign.ProfileRevision++; foreign.Status = "cancelled"; await Notify(foreign);
            Check(reviews.Contains(request.Id) && approve.IsEnabled,
                "Another immutable native effect retired this review."); checks++;
            reviews.Remove(request.Id); host.Children.Clear();
            AddReview(busy: true);
            var claimed = Copy(); claimed.Status = "claimed"; await Notify(claimed);
            Check(reviews.Contains(request.Id) && !(bool)ownerType.GetProperty("Invalidated", privateInstance)!.GetValue(owner!)!,
                "The handler's own claimed update revoked an in-flight review."); checks++;
            reviews.Remove(request.Id); host.Children.Clear();

            var run = new Cerebral.Client.WorkflowConversationRun(request.JobId, request.SessionId, request.ProfileId,
                "", "fixture", "1", 10, Cerebral.Contracts.Workflows.WorkflowStatus.Running, null, null, null, null,
                false, request.Id, Cerebral.Contracts.Workflows.WorkflowEffectState.Dispatched, 1);
            bool SnapshotInvalid(Cerebral.Client.WorkflowConversationRun value, long? observed = null) =>
                (bool)snapshotMethod.Invoke(null, [request, value, observed])!;
            Check(!SnapshotInvalid(run), "A valid dispatched effect revoked its review."); checks++;
            Check(!SnapshotInvalid(run with { EffectState = Cerebral.Contracts.Workflows.WorkflowEffectState.Prepared }),
                "A preceding prepared snapshot retired a fresh request before dispatch was observed."); checks++;
            Check(SnapshotInvalid(run with { CancelRequested = true }), "Cancellation did not revoke the exact effect."); checks++;
            Check(SnapshotInvalid(run with { EffectState = Cerebral.Contracts.Workflows.WorkflowEffectState.Cancelled }),
                "A cancelled effect retained approval."); checks++;
            Check(SnapshotInvalid(run with { Status = Cerebral.Contracts.Workflows.WorkflowStatus.Waiting }),
                "A no-longer-running exact owner retained approval."); checks++;
            var preceding = run with { EffectId = "preceding-effect", Status = Cerebral.Contracts.Workflows.WorkflowStatus.Completed };
            Check(!SnapshotInvalid(preceding), "An unobserved preceding snapshot retired a new request."); checks++;
            Check(SnapshotInvalid(preceding with { Revision = 11 }, observed: 10),
                "A newer snapshot after the observed dispatch retained an obsolete card."); checks++;
            Check(!SnapshotInvalid(run with { Revision = 9, CancelRequested = true }, observed: 10),
                "An older snapshot retired the latest observed dispatch."); checks++;
            Check(!SnapshotInvalid(run with { Id = "another-run", CancelRequested = true }, observed: 10) &&
                !SnapshotInvalid(run with { ConversationId = "another-chat", CancelRequested = true }, observed: 10),
                "A foreign run/conversation snapshot retired this review."); checks++;

            ServiceEvent Workflow(string id, string conversation, string status, DateTimeOffset at) =>
                new(2, "workflow.updated", id, CerebralJson.Element(new { id, conversationId = conversation, status }), at);
            bool WorkflowInvalid(ServiceEvent value) => (bool)workflowMethod.Invoke(null, [request, value])!;
            Check(WorkflowInvalid(Workflow(request.JobId, request.SessionId, "Cancelled", request.CreatedAt.AddSeconds(1))),
                "A current owner cancellation event retained its card."); checks++;
            Check(WorkflowInvalid(Workflow(request.JobId, request.SessionId, "Waiting", request.CreatedAt.AddSeconds(1))),
                "A current owner waiting event retained its card."); checks++;
            Check(!WorkflowInvalid(Workflow(request.JobId, request.SessionId, "Running", request.CreatedAt.AddSeconds(1))),
                "A running event retired an active request."); checks++;
            Check(!WorkflowInvalid(Workflow(request.JobId, request.SessionId, "Cancelled", request.CreatedAt.AddSeconds(-1))),
                "A pre-request event retired a fresh card."); checks++;
            Check(!WorkflowInvalid(Workflow("another-run", request.SessionId, "Cancelled", request.CreatedAt.AddSeconds(1))) &&
                !WorkflowInvalid(Workflow(request.JobId, "another-chat", "Cancelled", request.CreatedAt.AddSeconds(1))),
                "A foreign workflow event retired this card."); checks++;
            return checks;
        }
        finally { reviews.Remove(request.Id); host.Children.Clear(); }
    }

    private static int NativePluginProjectionIdentityChecks(OrchestratorWindow window)
    {
        const System.Reflection.BindingFlags privateInstance = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var windowType = typeof(OrchestratorWindow);
        var connectedType = typeof(Cerebral.Desktop.ConnectedWindow);
        var connectionField = connectedType.GetField("activeConnection", privateInstance)!;
        var connectedProperty = connectedType.GetProperty("IsConnected", privateInstance)!;
        var conversationField = windowType.GetField("durableConversationId", privateInstance)!;
        var conversationProperty = windowType.GetProperty("CurrentConversationId", privateInstance)!;
        var source = (int)windowType.GetField("knowledgeConnectionVersion", privateInstance)!.GetValue(window)!;
        var selection = Convert.ToInt64(windowType.GetField("conversationSelectionVersion", privateInstance)!.GetValue(window)!);
        var liveAt = (DateTimeOffset)windowType.GetField("nativePluginManagementLiveAt", privateInstance)!.GetValue(window)!;
        var ownerType = windowType.GetNestedType("NativePluginOwnerIdentity", System.Reflection.BindingFlags.NonPublic)!;
        var reviewType = windowType.GetNestedType("NativePluginManagementReview", System.Reflection.BindingFlags.NonPublic)!;
        var projectionMethod = windowType.GetMethod("NativePluginOwnerProjectionIsCurrent", privateInstance)!;
        var admissionMethod = windowType.GetMethod("NativePluginOwnerIsCurrent", privateInstance)!;
        var revokeMethod = windowType.GetMethod("RevokeNativePluginManagementReview",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var originalConnection = connectionField.GetValue(window);
        var originalConnected = connectedProperty.GetValue(window);
        var originalConversation = conversationField.GetValue(window);
        var connection = new Cerebral.Desktop.ConnectionPreferences { Transport = "installed-pipe", AutoStartLocalServices = false };
        var checks = 0;
        try
        {
            // These are inert snapshots in an inspection-mode fixture: no connection, client,
            // profile, owner command, or settings write is established by the private setters.
            connectionField.SetValue(window, connection); connectedProperty.SetValue(window, true);
            if (conversationProperty.GetValue(window) is null) conversationField.SetValue(window, "projection-fixture-chat");
            var request = new NativePluginManagementRequest
            {
                Id = Guid.NewGuid().ToString("N"), SessionId = (string)conversationProperty.GetValue(window)!,
                ProfileId = "unavailable-fixture-profile", ProfileRevision = 3, JobId = "projection-fixture-run",
                Operation = "set-enabled", Enabled = true, Status = "completed",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-11), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            object Owner(Cerebral.Desktop.ConnectionPreferences? captured = null, string endpoint = "installed-owner-pipe",
                int? capturedSource = null, long? capturedSelection = null, DateTimeOffset? capturedLiveAt = null) =>
                Activator.CreateInstance(ownerType, System.Reflection.BindingFlags.Public | privateInstance, null,
                    [request, captured ?? connection, endpoint, capturedSource ?? source, capturedSelection ?? selection, capturedLiveAt ?? liveAt], null)!;
            bool Current(object owner) => (bool)projectionMethod.Invoke(window, [owner])!;
            bool Admitted(object owner) => (bool)admissionMethod.Invoke(window, [owner])!;
            var owner = Owner();
            Check(Current(owner) && !Admitted(owner),
                "Expired or unavailable-profile admission was confused with captured projection identity."); checks++;
            var approve = new System.Windows.Controls.Button();
            var decline = new System.Windows.Controls.Button();
            var review = Activator.CreateInstance(reviewType, System.Reflection.BindingFlags.Public | privateInstance, null,
                [owner, new System.Windows.Controls.Border(), new System.Windows.Controls.TextBlock(), approve, decline, "Fixture", "1"], null)!;
            revokeMethod.Invoke(null, [review]);
            Check(Current(owner) && !Admitted(owner) && !approve.IsEnabled && !decline.IsEnabled,
                "Normal terminal-card retirement falsely changed projection identity or retained action admission."); checks++;
            var ownSession = request.SessionId; request.SessionId = "another-fixture-chat";
            Check(!Current(owner), "A changed selected conversation retained current projection identity."); checks++;
            request.SessionId = ownSession;
            Check(!Current(Owner(new Cerebral.Desktop.ConnectionPreferences { Transport = "installed-pipe", AutoStartLocalServices = false })),
                "A replacement connection with equal values retained the original captured owner identity."); checks++;
            Check(!Current(Owner(endpoint: "another-endpoint")), "A changed endpoint retained current projection identity."); checks++;
            Check(!Current(Owner(capturedSource: source + 1)), "A changed source generation retained current projection identity."); checks++;
            Check(!Current(Owner(capturedSelection: selection + 1)), "Navigation away and back retained an obsolete selection generation."); checks++;
            Check(!Current(Owner(capturedLiveAt: liveAt.AddTicks(-1))), "A new live admission epoch retained an obsolete captured connection."); checks++;
            connectedProperty.SetValue(window, false);
            Check(!Current(owner), "A disconnected desktop retained current projection identity."); checks++;
            connectedProperty.SetValue(window, true);
            Check(Current(owner) && !Admitted(owner),
                "A retired card changed projection identity despite the unchanged captured selection and connection."); checks++;
            return checks;
        }
        finally
        {
            connectionField.SetValue(window, originalConnection);
            connectedProperty.SetValue(window, originalConnected);
            conversationField.SetValue(window, originalConversation);
        }
    }

    private static async Task<bool> RejectedArguments(DesktopControlServer server, string operation, string arguments)
    {
        try
        {
            return (await DesktopControlClient.ExecuteAsync(server.DescriptorPath, operation,
                Guid.NewGuid().ToString("N"), arguments)).State == "rejected";
        }
        catch (InvalidDataException error) when (error.Message == "A local Worker message contains duplicate JSON properties.")
        {
            // Duplicate JSON is rejected by the shared pipe framing before any UI handler runs.
            return true;
        }
    }

    private static Task<DesktopControlResponse> Call(DesktopControlServer server, string operation, object arguments,
        string? requestId = null) => DesktopControlClient.ExecuteAsync(server.DescriptorPath, operation,
            requestId ?? Guid.NewGuid().ToString("N"), JsonSerializer.Serialize(arguments, CerebralJson.Options));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
