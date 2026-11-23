using Cerebral.Engine.Tests;

if (args.Length == 1 && args[0] == "--workflow-activity-totals-only")
{
    var scratch = Path.Combine(Path.GetTempPath(), "Cerebral.ActivityTotals-" + Guid.NewGuid().ToString("N"));
    try
    {
        var checks = WorkflowActivityTotalsChecks.Run(scratch);
        Console.WriteLine($"{checks} workflow Activity totals checks passed; disposable SQLite fixtures; no effect, Worker, model or network execution.");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
    }
    return;
}

if (args.Length > 0 && args[0] == "--conversation-communication-only")
{
    if (args.Length != 1) throw new ArgumentException("Conversation communication fixtures take no extra arguments.");
    await ConversationCommunicationStoreTests.RunAsync();
    return;
}

if (args.Length > 0 && args[0] == "--source-bound-conversation-rename-only")
{
    if (args.Length != 1) throw new ArgumentException("Source-bound rename fixtures take no extra arguments.");
    var checks = await SourceBoundConversationRenameChecks.RunAsync();
    Console.WriteLine($"Source-bound conversation rename: {checks} temporary SQLite transaction checks.");
    return;
}

if (args.Length > 0 && args[0] == "--committed-root-user-input-only")
{
    if (args.Length != 1) throw new ArgumentException("Committed-input store fixtures take no extra arguments.");
    var checks = await CommittedRootUserInputStoreChecks.RunAsync();
    Console.WriteLine($"Committed root User input store: {checks} temporary SQLite transaction checks.");
    return;
}

if (args.Contains("--attachments-only", StringComparer.Ordinal))
{
    await AttachmentStoreTests.RunAsync();
    return;
}

if (args.Contains("--workflow-schema-only", StringComparer.Ordinal))
{
    WorkflowSchemaTests.Run();
    return;
}

if (args.Contains("--context-only", StringComparer.Ordinal))
{
    await ContextPlannerTests.RunAsync();
    return;
}

if (args.Contains("--context-provider-only", StringComparer.Ordinal))
{
    await ContextProviderTests.RunAsync();
    return;
}

if (args.Contains("--conversation-navigation-only", StringComparer.Ordinal)) { ConversationNavigationTests.Run(); return; }

if (args.Contains("--workflow-recent-only", StringComparer.Ordinal)) { WorkflowRecentTests.Run(); return; }

if (args.Contains("--workflow-active-summaries-only", StringComparer.Ordinal)) { WorkflowActiveSummaryTests.Run(); return; }
if (args.Contains("--workflow-idle-expiry-only", StringComparer.Ordinal)) { WorkflowIdleExpiryTests.Run(); return; }

if (args.Contains("--conversation-goal-only", StringComparer.Ordinal)) { ConversationGoalTests.Run(); return; }
if (args.Contains("--conversation-operations-only", StringComparer.Ordinal)) { ConversationOperationTests.Run(); return; }

if (args.Contains("--conversation-rename-only", StringComparer.Ordinal)) { ConversationRenameTests.Run(); return; }

if (args.Contains("--workflow-steering-only", StringComparer.Ordinal)) { WorkflowSteeringTests.Run(); return; }
if (args.Contains("--output-hooks-only", StringComparer.Ordinal)) { await ResponseOutputPipelineTests.RunAsync(); return; }

if (args.Contains("--checkpoint-format-only", StringComparer.Ordinal)) { CheckpointFormatTests.Run(); return; }

if (args.Contains("--workflow-store-runtime-only", StringComparer.Ordinal))
{
    await WorkflowStoreTests.RunAsync();
    await WorkflowRuntimeTests.RunAsync();
    return;
}

ConversationGoalTests.Run();
ConversationOperationTests.Run();
ConversationRenameTests.Run();
WorkflowSteeringTests.Run();
WorkflowActiveSummaryTests.Run();
WorkflowIdleExpiryTests.Run();
ConversationNavigationTests.Run();
await ContextHistoryPlacementTests.RunAsync();
WorkflowRecentTests.Run();
EventContractTests.Run();
await RoutePipelineTests.RunAsync();
await ResponseOutputPipelineTests.RunAsync();
await EventJournalTests.RunAsync();
CheckpointFormatTests.Run();
await AttachmentStoreTests.RunAsync();
await WorkflowStoreTests.RunAsync();
await WorkflowReceiptTests.RunAsync();
await WorkflowRuntimeTests.RunAsync();
WorkflowSchemaTests.Run();
await WorkflowBudgetTests.RunAsync();
await AgentHarnessTests.RunAsync();
await ContextPlannerTests.RunAsync();
await ContextProviderTests.RunAsync();
Console.WriteLine("Cerebral Engine foundation checks passed (contract/routing unit tests and local SQLite integration).");
