using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>Actual same-user semantic pipe and native Work controls; Worker replies are deterministic fixtures.</summary>
    public async Task<int> CheckDesktopWorkflowControlAsync()
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run on the active WPF dispatcher.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-work-control-" + Guid.NewGuid().ToString("N")));
        var original = workflowWorkspace; var originalPrompt = prompt.Text; var originalPage = Tabs.SelectedItem;
        var checks = await CheckPluginChangeReviewCardsAsync(deadline.Token);
        var reads = 0; var writes = 0; var loseAck = false; var failReadAfterAnswer = false;
        string? failReadId = null;
        var runs = new Dictionary<string, WorkflowRun>(StringComparer.Ordinal);
        TaskCompletionSource<CommandResult>? heldRead = null;
        TaskCompletionSource<bool>? readStarted = null;
        string? heldId = null;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        Check(InterfaceOwnerIsSelected("chat-owner", "chat-owner", null),
            "The selected Chat can review its own interface request without a Work item.");
        Check(InterfaceOwnerIsSelected("work-owner", "chat-owner", "work-owner"),
            "An explicitly selected Work item retains its interface owner.");
        Check(!InterfaceOwnerIsSelected("other-owner", "chat-owner", "work-owner") &&
              !InterfaceOwnerIsSelected("", "", null),
            "Unselected and empty interface owners remain rejected.");
        var failedReview = new InterfaceRequest
        {
            Source = "workflow", Kind = "plugin", Status = "failed", ClaimId = "owner-claim",
            ClaimedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            Error = "Actual fixture failure", PackageFingerprint = new string('a', 64)
        };
        Check(IsPluginRepairCandidate(failedReview),
            "A historical unclassified failed owner review remains available for explicit repair.");
        foreach (var stage in Enum.GetValues<InterfaceFailureStage>())
        {
            failedReview.FailureStage = stage;
            Check(IsPluginRepairCandidate(failedReview) ==
                  (stage is InterfaceFailureStage.NativeLoad or InterfaceFailureStage.ContributionValidation),
                "The native repair option follows the retained failure stage: " + stage);
        }
        failedReview.FailureStage = (InterfaceFailureStage)int.MaxValue;
        Check(!IsPluginRepairCandidate(failedReview),
            "An unknown failure stage cannot expose native source repair.");
        var captureFailure = new DesktopPluginPresentationException(InterfaceFailureStage.AttachmentImport,
            new InvalidOperationException("Storage fixture is locked."));
        Check(captureFailure.Stage == InterfaceFailureStage.AttachmentImport &&
              captureFailure.InnerException is InvalidOperationException &&
              captureFailure.Message.Contains("Storage fixture is locked.", StringComparison.Ordinal),
            "Presentation failures retain their actual stage and underlying error without claiming a successful mount.");
        var controlView = new StackPanel();
        var refreshButton = new Button { Content = "Refresh" };
        controlView.Children.Add(new TextBlock { Text = "Fixture draft" }); controlView.Children.Add(refreshButton);
        Check(ReferenceEquals(FindPluginControlButton(controlView, "Refresh"), refreshButton),
            "Plugin control lookup resolves only the exact named button in its supplied view.");
        controlView.Children.Add(new Button { Content = "Refresh" });
        var ambiguousButton = false;
        try { _ = FindPluginControlButton(controlView, "Refresh"); } catch (InvalidOperationException) { ambiguousButton = true; }
        Check(ambiguousButton, "Duplicate plugin button names reject invocation instead of choosing an arbitrary control.");
        var clippedView = new StackPanel();
        clippedView.Children.Add(new TextBlock { Text = new string('x', 2049) });
        var clippedWindow = new Window { Content = clippedView, Width = 200, Height = 100, ShowActivated = false };
        try
        {
            using (DesktopBackgroundRenderScope.Begin(clippedWindow))
            {
                clippedWindow.UpdateLayout();
                var clippedSnapshot = CerebralJson.Element(PluginControlSnapshot("fixture", clippedView));
                Check(clippedSnapshot.GetProperty("limited").GetBoolean() &&
                      clippedSnapshot.GetProperty("texts")[0].GetString()!.Length == 2048,
                    "Clipped visible plugin diagnostic text is explicitly marked partial.");
            }
        }
        finally { clippedWindow.Close(); }
        Check(DesktopControlProtocol.Operations.Contains("plugin.surface.setText"),
            "The semantic owner pipe advertises the same exact native text operation.");
        var fieldView = new StackPanel();
        var field = new TextBox { Text = "Line one\n世界" };
        fieldView.Children.Add(field);
        fieldView.Children.Add(new TextBlock { Text = "Visible note status" });
        fieldView.Children.Add(new TextBlock { Text = "Hidden saved sentinel", Visibility = Visibility.Hidden });
        fieldView.Children.Add(new TextBlock { Text = "Collapsed saved sentinel", Visibility = Visibility.Collapsed });
        var collapsedGroup = new StackPanel { Visibility = Visibility.Collapsed };
        collapsedGroup.Children.Add(new TextBlock { Text = "Collapsed parent saved sentinel" });
        fieldView.Children.Add(collapsedGroup);
        var fieldWindow = new Window { Content = fieldView, Width = 200, Height = 100, ShowActivated = false };
        try
        {
            using (DesktopBackgroundRenderScope.Begin(fieldWindow))
            {
                fieldWindow.UpdateLayout();
                var inspectedFields = PluginControlSnapshot("fixture", fieldView);
                Check(inspectedFields.Texts.Contains("Visible note status", StringComparer.Ordinal) &&
                      !inspectedFields.Texts.Contains("Hidden saved sentinel", StringComparer.Ordinal) &&
                      !inspectedFields.Texts.Contains("Collapsed saved sentinel", StringComparer.Ordinal) &&
                      !inspectedFields.Texts.Contains("Collapsed parent saved sentinel", StringComparer.Ordinal),
                    "Only visible labels enter persistence evidence, including inherited visibility from a collapsed parent.");
                var inspectedField = inspectedFields.Fields.Single();
                var prepared = PreparePluginControlTextEdit(fieldView,
                    new PluginControlTextEdit(inspectedField.Id, "Line one\n世界", "Changed\nΔ"));
                prepared.Provider.SetValue("Changed\nΔ");
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, deadline.Token);
                var changedFields = PluginControlSnapshot("fixture", fieldView);
                Check(changedFields.Fields.Single().Id == inspectedField.Id &&
                      changedFields.Fields.Single().Text == "Changed\nΔ" &&
                      changedFields.Fields.Single().Visible && !changedFields.Fields.Single().ReadOnly,
                    "An inspected live TextBox keeps one opaque identity and preserves exact Unicode and newlines after native delivery.");

                field.IsReadOnly = true;
                var readOnlyRejected = false;
                try { _ = PreparePluginControlTextEdit(fieldView,
                    new PluginControlTextEdit(inspectedField.Id, "Changed\nΔ", "another")); }
                catch (InvalidOperationException) { readOnlyRejected = true; }
                field.IsReadOnly = false; field.IsEnabled = false;
                var disabledRejected = false;
                try { _ = PreparePluginControlTextEdit(fieldView,
                    new PluginControlTextEdit(inspectedField.Id, "Changed\nΔ", "another")); }
                catch (InvalidOperationException) { disabledRejected = true; }
                field.IsEnabled = true; field.Visibility = Visibility.Collapsed;
                var hiddenRejected = false;
                try { _ = PreparePluginControlTextEdit(fieldView,
                    new PluginControlTextEdit(inspectedField.Id, "Changed\nΔ", "another")); }
                catch (InvalidOperationException) { hiddenRejected = true; }
                field.Visibility = Visibility.Visible;
                var staleRejected = false;
                try { _ = PreparePluginControlTextEdit(fieldView,
                    new PluginControlTextEdit(inspectedField.Id, "stale", "another")); }
                catch (InvalidOperationException) { staleRejected = true; }
                Check(readOnlyRejected && disabledRejected && hiddenRejected && staleRejected,
                    "Native text admission rejects read-only, disabled, collapsed, and stale field state before mutation.");

                fieldView.Children.Clear();
                var replacement = new TextBox { Text = "Changed\nΔ" };
                fieldView.Children.Add(replacement); fieldWindow.UpdateLayout();
                var replacementField = PluginControlSnapshot("fixture", fieldView).Fields.Single();
                var oldInstanceRejected = false;
                try { _ = PreparePluginControlTextEdit(fieldView,
                    new PluginControlTextEdit(inspectedField.Id, "Changed\nΔ", "another")); }
                catch (InvalidOperationException) { oldInstanceRejected = true; }
                Check(oldInstanceRejected && replacementField.Id != inspectedField.Id,
                    "A replaced live TextBox receives a new opaque identity even when its current text matches.");

                fieldView.Children.Clear();
                for (var index = 0; index < 10; index++)
                    fieldView.Children.Add(new TextBox { Text = new string((char)('a' + index), 700) });
                fieldWindow.UpdateLayout();
                var boundedFields = PluginControlSnapshot("fixture", fieldView);
                Check(boundedFields.Limited && boundedFields.Fields.Count == 6 &&
                      boundedFields.Fields.Sum(item => item.Text.Length) == PluginControlTextEdits.MaximumCharacters &&
                      boundedFields.Fields.Last().Limited,
                    "Many editable fields share one aggregate value bound and explicitly mark omitted or clipped evidence limited.");
            }
        }
        finally { fieldWindow.Close(); }
        WorkflowRun MakeRun(string id) => new()
        {
            Id = id, Revision = 2, Status = WorkflowStatus.Waiting,
            Submission = new(id, "conversation-" + id, "review-profile", "fixture.review:review", "1.0.0", "Review a passage",
                "PRIVATE-ASSIGNMENT-MUST-NOT-BE-PROJECTED", CerebralJson.Element(new { privateInput = "PRIVATE-INPUT" }),
                CerebralJson.Element(new { privateSettings = "PRIVATE-SETTINGS" })),
            Questions = new("question-" + id, 1, [new("label", "Label for this review"),
                new("style", "Presentation style", true, ["concise", "detailed"])], new Dictionary<string, string>()),
            CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch
        };
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            if (!Dispatcher.CheckAccess() || request.ProfileId is not null)
                throw new InvalidOperationException("Work must retain the signed-in owner's UI-bound command route.");
            if (request.Operation == "jobs.workflow.get")
            {
                reads++;
                var id = request.Arguments.GetProperty("id").GetString()!;
                if (failReadId == id) throw new IOException("Fixture post-acceptance read failure.");
                if (id == heldId && heldRead is not null) { readStarted!.TrySetResult(true); return heldRead.Task.WaitAsync(token); }
                return Task.FromResult(runs.TryGetValue(id, out var run) ? CommandResult.Ok(run) : CommandResult.Fail("Unknown fixture run."));
            }
            if (request.Operation == "jobs.workflow.events.previous")
                return Task.FromResult(CommandResult.Ok(new { events = Array.Empty<CerebralEvent>(), snapshotSequence = 0,
                    nextBeforeSequence = (long?)null }));
            if (request.Operation == "jobs.workflow.answer")
            {
                var value = CerebralJson.Read<WorkflowAnswerRequest>(request.Arguments);
                var run = runs[value.RunId];
                if (run.Questions is not { } q || q.Id != value.QuestionSetId || q.Revision != value.ExpectedQuestionRevision)
                    return Task.FromResult(CommandResult.Fail("Question revision changed at the service."));
                writes++;
                runs[run.Id] = run with { Revision = run.Revision + 1, Status = WorkflowStatus.Completed, Questions = null,
                    Result = CerebralJson.Element(new { label = value.Answers["label"], style = value.Answers["style"], wordCount = 4 }) };
                if (failReadAfterAnswer) failReadId = run.Id;
                if (loseAck) throw new IOException("Fixture lost acknowledgement after commit.");
                return Task.FromResult(CommandResult.Ok(runs[run.Id]));
            }
            if (request.Operation == "jobs.workflow.resume")
            {
                var id = request.Arguments.GetProperty("id").GetString()!;
                var expectedRevision = request.Arguments.GetProperty("expectedRevision").GetInt64();
                var run = runs[id];
                if (run.Revision != expectedRevision || run.Status != WorkflowStatus.Waiting || run.Questions is not null ||
                    run.Effect?.State is WorkflowEffectState.Unknown or WorkflowEffectState.Dispatched)
                    return Task.FromResult(CommandResult.Fail("The fixture run is not resumable at that revision."));
                writes++;
                runs[id] = run with { Revision = run.Revision + 1, Status = WorkflowStatus.Queued, WaitingReason = null };
                return Task.FromResult(CommandResult.Ok(runs[id]));
            }
            if (request.Operation == "jobs.workflow.reconcile")
            {
                var id = request.Arguments.GetProperty("id").GetString()!;
                var run = runs[id];
                if (run.Effect?.State != WorkflowEffectState.Unknown)
                    return Task.FromResult(CommandResult.Fail("The fixture run has no uncertain effect."));
                writes++;
                runs[id] = run with { Revision = run.Revision + 1, Status = WorkflowStatus.Queued,
                    WaitingReason = null, Effect = run.Effect with { State = WorkflowEffectState.Failed, Error = "Fixture reconciliation found no completed effect." } };
                return Task.FromResult(CommandResult.Ok(runs[id]));
            }
            throw new InvalidOperationException("Unexpected fixture command: " + request.Operation);
        }
        using var workspace = new WorkflowWorkspace(Dispatch, () => Array.Empty<AgentProfile>(), (_, _) => { });
        DesktopControlServer? server = null;
        try
        {
            workflowWorkspace = workspace; prompt.Text = "PRESERVED CHAT DRAFT";
            var chatId = CurrentConversationId;
            server = await DesktopControlServer.StartAsync(this, root, deadline.Token);
            Task<DesktopControlResponse> Call(string operation, object arguments, string? id = null) =>
                DesktopControlClient.ExecuteAsync(server.DescriptorPath, operation, id ?? Guid.NewGuid().ToString("N"),
                    JsonSerializer.Serialize(arguments, CerebralJson.Options), deadline.Token);
            object Answers(string runId = "review-one", string? questionId = null, long questionRevision = 1) =>
                new { runId, questionId = questionId ?? "question-" + runId, questionRevision,
                    answers = new { label = "Generated passage", style = "detailed" } };
            runs["review-one"] = MakeRun("review-one");
            runs["review-two"] = MakeRun("review-two");
            var opened = await Call("workflow.open", new { runId = "review-one" });
            Check(opened.Success && opened.Data.GetProperty("runId").GetString() == "review-one" &&
                opened.Data.GetProperty("questionCount").GetInt32() == 2, "Open returns the exact Work run and two-field form.");
            Check(opened.Data.GetProperty("questionId").GetString() == "question-review-one" &&
                opened.Data.GetProperty("questionRevision").GetInt64() == 1, "The control form exposes durable question coordinates.");
            Check(Tabs.SelectedItem is TabItem { Tag: "work" } && prompt.Text == "PRESERVED CHAT DRAFT" && CurrentConversationId == chatId,
                "Opening Work must not replace Chat or its draft.");
            Check(!opened.Data.GetRawText().Contains("PRIVATE-", StringComparison.Ordinal), "The projection must omit assignments, input and settings.");
            var before = reads;
            Check((await Call("workflow.inspect", new { runId = "review-two" })).State == "rejected" && reads == before,
                "Inspect cannot silently switch the selected run.");
            Check((await Call("workflow.answer", Answers("review-two"))).State == "rejected" && writes == 0,
                "A question on another run cannot be answered.");
            Check((await Call("workflow.answer", Answers(questionId: "different"))).State == "rejected" && writes == 0,
                "Question identity must match exactly.");
            Check((await Call("workflow.answer", Answers(questionRevision: 2))).State == "rejected" && writes == 0,
                "Question revision must match exactly.");
            Check((await Call("workflow.answer", new { runId = "review-one", questionId = "question-review-one", questionRevision = 1,
                answers = new { label = "Incomplete" } })).State == "rejected" && writes == 0, "All required unanswered fields must be supplied.");
            Check((await Call("workflow.answer", new { runId = "review-one", questionId = "question-review-one", questionRevision = 1,
                answers = new { label = "Label", style = "invented" } })).State == "rejected" && writes == 0,
                "Offered choices must be validated before dispatch.");
            Check((await Call("workflow.answer", new { runId = "review-one", questionId = "question-review-one", questionRevision = "1",
                answers = new { label = "Label", style = "detailed" } })).State == "rejected", "Revisions must be integers.");
            var duplicateRejected = false;
            try
            {
                _ = await DesktopControlClient.ExecuteAsync(server.DescriptorPath, "workflow.open", Guid.NewGuid().ToString("N"),
                    "{\"runId\":\"review-one\",\"runId\":\"review-two\"}", deadline.Token);
            }
            catch (InvalidDataException) { duplicateRejected = true; }
            Check(duplicateRejected, "The semantic client must reject duplicate workflow argument names before choosing a target.");
            Check((await Call("workflow.open", new { runId = "missing" })).State == "rejected", "A missing service run remains a rejected open.");
            var inspect = await Call("workflow.inspect", new { runId = "review-one" });
            Check(inspect.Success && inspect.Data.GetProperty("questionCount").GetInt32() == 2, "Inspect refreshes the current exact form.");
            var requestId = Guid.NewGuid().ToString("N");
            var answered = await Call("workflow.answer", Answers(), requestId);
            Check(answered.State == "accepted" && writes == 1 && answered.Data.GetProperty("status").GetString() == "completed",
                "Two typed answers must pass through the native Work adapter once.");
            Check(answered.Data.GetProperty("result").GetProperty("wordCount").GetInt32() == 4 &&
                answered.Data.GetProperty("questionCount").GetInt32() == 0, "Completion returns bounded result data and closes the form.");
            Check((await Call("workflow.answer", Answers(), requestId)).State == "accepted" && writes == 1,
                "Exact semantic retry returns the retained receipt without another answer call.");
            Check((await Call("workflow.answer", Answers(questionRevision: 2), requestId)).State == "conflict" && writes == 1,
                "Changed input cannot reuse an accepted request ID.");
            Check((await Call("workflow.answer", Answers())).State == "rejected" && writes == 1, "A completed question cannot be resubmitted as new work.");

            await Call("workflow.open", new { runId = "review-two" });
            loseAck = true;
            var uncertain = await Call("workflow.answer", Answers("review-two"));
            Check(uncertain.State == "uncertain" && workspace.Snapshot!.MutationUncertain && writes == 2,
                "A lost mutation acknowledgement must never be called a rejection or success.");
            Check((await Call("workflow.open", new { runId = "review-one" })).State == "rejected",
                "Uncertain work cannot be replaced by opening another run.");
            loseAck = false;
            var reconciled = await Call("workflow.inspect", new { runId = "review-two" });
            Check(reconciled.Success && reconciled.Data.GetProperty("status").GetString() == "completed" && writes == 2,
                "Inspection recovers the saved result without replaying the answer.");

            runs["projection-failure"] = MakeRun("projection-failure");
            await Call("workflow.open", new { runId = "projection-failure" });
            failReadAfterAnswer = true;
            var confirmed = await Call("workflow.answer", Answers("projection-failure"));
            Check(confirmed.State == "accepted" && !workspace.Snapshot!.MutationUncertain &&
                confirmed.Data.GetProperty("projectionWarning").ValueKind == JsonValueKind.String,
                "Confirmed answer acceptance survives a later display read failure.");
            failReadAfterAnswer = false; failReadId = null;

            // Hold actual adapter reads while the UI selection changes. No timing-based sleep is used.
            runs["slow-open"] = MakeRun("slow-open"); heldId = "slow-open";
            heldRead = new(TaskCreationOptions.RunContinuationsAsynchronously); readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var slow = Call("workflow.open", new { runId = heldId });
            await readStarted.Task.WaitAsync(deadline.Token);
            await Call("workflow.open", new { runId = "review-one" });
            heldRead.SetResult(CommandResult.Ok(runs["slow-open"])); heldId = null;
            Check((await slow).State == "rejected" && workspace.Snapshot!.RunId == "review-one", "A slow open cannot replace a newer run selection.");
            heldId = "review-one"; heldRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var staleInspect = Call("workflow.inspect", new { runId = heldId });
            await readStarted.Task.WaitAsync(deadline.Token); workspace.Reset();
            heldRead.SetResult(CommandResult.Ok(runs["review-one"])); heldId = null;
            Check((await staleInspect).State == "rejected" && workspace.Snapshot is null, "A connection reset invalidates an in-flight inspection.");

            runs["large-result"] = MakeRun("large-result") with { Status = WorkflowStatus.Completed, Questions = null,
                Result = CerebralJson.Element(new { text = new string('x', 10_000) }), Error = new string('e', 1500) };
            var large = await Call("workflow.open", new { runId = "large-result" });
            Check(large.Success && large.Data.GetProperty("resultOmitted").GetBoolean() && large.Data.GetProperty("result").ValueKind == JsonValueKind.Null &&
                large.Data.GetProperty("error").GetString()!.Length <= 1025, "Large result and diagnostic output must remain bounded and explicitly omitted.");
            var hugeFields = Enumerable.Range(0, 3).Select(i => new WorkflowQuestion("field-" + i, new string('l', 2048), true,
                Enumerable.Range(0, 8).Select(j => j + new string('c', 900)).ToArray())).ToArray();
            runs["large-form"] = MakeRun("large-form") with { Questions = new("large-question", 1, hugeFields, new Dictionary<string, string>()) };
            var largeForm = await Call("workflow.open", new { runId = "large-form" });
            Check(largeForm.Success && largeForm.Data.GetProperty("questionsOmitted").GetBoolean() &&
                largeForm.Data.GetProperty("questionCount").GetInt32() == 3 && largeForm.Data.GetProperty("questions").ValueKind == JsonValueKind.Null,
                "Large forms must be explicitly omitted instead of clipping labels or choice semantics.");
            Check(JsonSerializer.SerializeToUtf8Bytes(largeForm.Data).Length < 48 * 1024 &&
                JsonSerializer.SerializeToUtf8Bytes(opened.Data).Length < 48 * 1024, "Semantic Work responses stay inside their compact budget.");

            runs["resume-one"] = MakeRun("resume-one") with { Questions = null, WaitingReason = "Ready to continue." };
            await Call("workflow.open", new { runId = "resume-one" });
            Check((await Call("workflow.resume", new { runId = "resume-one", expectedRevision = 1 })).State == "rejected",
                "Semantic resume rejects a stale durable revision.");
            var resumed = await Call("workflow.resume", new { runId = "resume-one", expectedRevision = 2 });
            Check(resumed.State == "accepted" && resumed.Data.GetProperty("status").GetString() == "queued" && writes == 4,
                "Semantic resume continues the exact selected revision through the native mutation gate once.");

            runs["reconcile-one"] = MakeRun("reconcile-one") with
            {
                Questions = null,
                WaitingReason = "Inspect the uncertain effect.",
                Effect = new WorkflowEffect("effect-one", "fixture.command", 1, CerebralJson.Element(new { }),
                    WorkflowReplayPolicy.Never, WorkflowEffectState.Unknown)
            };
            var uncertainRun = await Call("workflow.open", new { runId = "reconcile-one" });
            Check(uncertainRun.Data.GetProperty("effectState").GetString() == "unknown",
                "Workflow projection exposes the bounded effect state needed for safe recovery.");
            var recovered = await Call("workflow.reconcile", new { runId = "reconcile-one" });
            Check(recovered.State == "accepted" && recovered.Data.GetProperty("effectState").GetString() == "failed" &&
                recovered.Data.GetProperty("status").GetString() == "queued" && writes == 5,
                "Semantic reconciliation inspects the exact selected uncertain effect without replaying it.");
            Check(prompt.Text == "PRESERVED CHAT DRAFT" && CurrentConversationId == chatId, "All Work operations leave Chat untouched.");
            await server.DisposeAsync(); server = null;
            Check(!File.Exists(Path.Combine(root, "Desktop", "control.json")), "The owned control endpoint is removed on shutdown.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, desktopWorkflowControlChecks = checks,
                evidence = "actual same-user pipe and WPF Work adapter with deterministic Worker replies",
                workerExecuted = false, modelExecuted = false, windowShown = false }));
            return checks;
        }
        finally
        {
            deadline.Cancel(); heldRead?.TrySetCanceled();
            if (server is not null) await server.DisposeAsync();
            workflowWorkspace = original; prompt.Text = originalPrompt; Tabs.SelectedItem = originalPage;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
