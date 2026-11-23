using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.ReviewPlugin;

/// <summary>A small executable SDK example; no model, filesystem writes, or host-specific tools are needed.</summary>
public sealed class ExampleReviewPlugin : IServicePlugin, IWorkflowModule
{
    public const string ModuleId = "example.review";
    public const string DefinitionId = ModuleId + ":review";
    public const string MeasureCommandId = ModuleId + ":measure";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ReviewStore store = new();
    private readonly IReadOnlyList<IWorkflowCommandHandler> commands;
    private bool initialized;
    private bool disposed;

    public ExampleReviewPlugin() => commands = Array.AsReadOnly<IWorkflowCommandHandler>([new MeasureCommand(store)]);

    public string Id => ModuleId;
    public IReadOnlyList<IToolExtension> ToolExtensions { get; } = Array.Empty<IToolExtension>();
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions { get; } = Array.Empty<IServiceCommandExtension>();
    public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = Array.AsReadOnly<IWorkflowDefinition>([new ReviewWorkflow()]);
    public IReadOnlyList<IWorkflowCommandHandler> Commands => commands;

    public void Initialize(IServicePluginHost host)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (initialized) throw new InvalidOperationException("The example review package is already initialized.");
        if (host.PluginId != ModuleId) throw new InvalidOperationException("Unexpected service package identity.");
        store.Initialize(host.DataDirectory);
        initialized = true;
    }

    public void Dispose() => disposed = true;

    private sealed class ReviewWorkflow : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } = new(DefinitionId, "1.0.0",
            "Ask for a label and presentation style, then measure the supplied text without using a model.",
            Schema("""
                {"type":"object","properties":{"text":{"type":"string","minLength":1,"maxLength":32768}},
                 "required":["text"],"additionalProperties":false}
                """), ["diagnostics"]);

        public WorkflowStep Advance(WorkflowRun snapshot)
        {
            if (snapshot.Signal.Kind == WorkflowSignalKind.Start)
            {
                RequireFields(snapshot.Submission.Input, "text");
                _ = Text(snapshot.Submission.Input, "text", 32768);
                return new(WorkflowContract.EmptyObject, new AskWorkflowQuestions([
                    new("label", "Label for this text measurement"),
                    new("style", "Presentation style", Choices: ["concise", "detailed"])
                ]));
            }
            if (snapshot.Signal.Kind == WorkflowSignalKind.Answers)
            {
                var answers = snapshot.Signal.Data;
                RequireFields(answers, "label", "style");
                var label = Text(answers, "label", 200);
                var style = Style(answers);
                var arguments = JsonSerializer.SerializeToElement(new
                {
                    text = Text(snapshot.Submission.Input, "text", 32768), label, style
                }, Json);
                return new(JsonSerializer.SerializeToElement(new { label, style }, Json),
                    new InvokeWorkflowCommand(MeasureCommandId, 1, arguments));
            }
            if (snapshot.Signal.Kind == WorkflowSignalKind.EffectResult)
            {
                var result = snapshot.Signal.Data.Deserialize<WorkflowCommandResult>(Json)
                    ?? throw new InvalidDataException("The measurement returned no result.");
                if (result.State != WorkflowEffectState.Succeeded || result.Data is not { ValueKind: JsonValueKind.Object } data)
                    return new(snapshot.State, new CompleteWorkflow(result.Data ?? WorkflowContract.EmptyObject,
                        Success: false, Error: result.Error ?? "The text measurement did not succeed."));
                return new(snapshot.State, new CompleteWorkflow(data.Clone()));
            }
            throw new InvalidOperationException("This workflow signal is unsupported.");
        }
    }

    private sealed class MeasureCommand(ReviewStore store) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new(MeasureCommandId, 1,
            "Count whitespace-delimited words and Unicode scalar characters in supplied text; performs no external effects.",
            Schema("""
                {"type":"object","properties":{
                  "text":{"type":"string","minLength":1,"maxLength":32768},
                  "label":{"type":"string","minLength":1,"maxLength":200},
                  "style":{"type":"string","enum":["concise","detailed"]}},
                 "required":["text","label","style"],"additionalProperties":false}
                """),
            Schema("""
                {"type":"object","properties":{
                  "recordId":{"type":"string","minLength":1,"maxLength":200},
                  "revision":{"type":"integer","minimum":1},
                  "label":{"type":"string","minLength":1,"maxLength":200},
                  "style":{"type":"string","enum":["concise","detailed"]},
                  "wordCount":{"type":"integer","minimum":0},
                  "characterCount":{"type":"integer","minimum":0},
                  "summary":{"type":"string","minLength":1,"maxLength":500}},
                  "required":["recordId","revision","label","style","wordCount","characterCount","summary"],"additionalProperties":false}
                """), ["diagnostics"], WorkflowReplayPolicy.Idempotent, TimeoutSeconds: 5);

        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
            => MeasureAsync(context, reconcile: false, cancellationToken);

        // This command has no external effect to repeat: the exact saved arguments determine its entire result.
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
            => MeasureAsync(context, reconcile: true, cancellationToken);

        private async Task<WorkflowCommandResult> MeasureAsync(WorkflowCommandContext context, bool reconcile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireFields(context.Arguments, "text", "label", "style");
            var text = Text(context.Arguments, "text", 32768);
            var label = Text(context.Arguments, "label", 200);
            var style = Style(context.Arguments);
            var wordCount = 0;
            var characterCount = 0;
            var inWord = false;
            foreach (var character in text.EnumerateRunes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                characterCount++;
                if (System.Text.Rune.IsWhiteSpace(character)) inWord = false;
                else if (!inWord) { wordCount++; inWord = true; }
            }
            var summary = style == "concise"
                ? $"{label}: {wordCount} words, {characterCount} characters."
                : $"{label}: {wordCount} whitespace-delimited words and {characterCount} Unicode scalar characters, including whitespace. These are measured counts, not token estimates.";
            var result = reconcile
                ? await store.ReadAsync(context.EffectId, cancellationToken)
                : await store.CommitAsync(context.EffectId, context.RunId ?? context.Submission.RequestId,
                    context.Submission.RequestId, label, style, wordCount, characterCount, summary, cancellationToken);
            return result is null
                ? new WorkflowCommandResult(WorkflowEffectState.Unknown, Error: "The review record was not committed before interruption.")
                : new WorkflowCommandResult(WorkflowEffectState.Succeeded, result.Value.Data,
                    ProviderReceiptId: result.Value.ReceiptId);
        }
    }

    private sealed class ReviewStore
    {
        private string? databasePath;

        public void Initialize(string dataDirectory)
        {
            if (databasePath is not null) throw new InvalidOperationException("The review store is already initialized.");
            Directory.CreateDirectory(dataDirectory);
            databasePath = Path.Combine(Path.GetFullPath(dataDirectory), "review.db");
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS review_receipts (
                    effect_id TEXT PRIMARY KEY,
                    run_id TEXT NOT NULL,
                    request_id TEXT NOT NULL,
                    label TEXT NOT NULL,
                    style TEXT NOT NULL,
                    word_count INTEGER NOT NULL,
                    character_count INTEGER NOT NULL,
                    summary TEXT NOT NULL,
                    created_utc TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        public async Task<(JsonElement Data, string ReceiptId)?> ReadAsync(string effectId, CancellationToken token)
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT label, style, word_count, character_count, summary
                FROM review_receipts WHERE effect_id = $effect;
                """;
            command.Parameters.AddWithValue("$effect", effectId);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            var data = JsonSerializer.SerializeToElement(new
            {
                recordId = effectId,
                revision = 1,
                label = reader.GetString(0),
                style = reader.GetString(1),
                wordCount = reader.GetInt32(2),
                characterCount = reader.GetInt32(3),
                summary = reader.GetString(4)
            }, Json);
            return (data, "review:" + effectId);
        }

        public async Task<(JsonElement Data, string ReceiptId)> CommitAsync(string effectId, string runId,
            string requestId, string label, string style, int wordCount, int characterCount, string summary,
            CancellationToken token)
        {
            await using var connection = Open();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR IGNORE INTO review_receipts
                    (effect_id, run_id, request_id, label, style, word_count, character_count, summary, created_utc)
                    VALUES ($effect, $run, $request, $label, $style, $words, $characters, $summary, $created);
                    """;
                insert.Parameters.AddWithValue("$effect", effectId);
                insert.Parameters.AddWithValue("$run", runId);
                insert.Parameters.AddWithValue("$request", requestId);
                insert.Parameters.AddWithValue("$label", label);
                insert.Parameters.AddWithValue("$style", style);
                insert.Parameters.AddWithValue("$words", wordCount);
                insert.Parameters.AddWithValue("$characters", characterCount);
                insert.Parameters.AddWithValue("$summary", summary);
                insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
                await insert.ExecuteNonQueryAsync(token);
            }
            await using (var verify = connection.CreateCommand())
            {
                verify.Transaction = transaction;
                verify.CommandText = """
                    SELECT run_id, request_id, label, style, word_count, character_count, summary
                    FROM review_receipts WHERE effect_id = $effect;
                    """;
                verify.Parameters.AddWithValue("$effect", effectId);
                await using var reader = await verify.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token) || reader.GetString(0) != runId || reader.GetString(1) != requestId ||
                    reader.GetString(2) != label || reader.GetString(3) != style || reader.GetInt32(4) != wordCount ||
                    reader.GetInt32(5) != characterCount || reader.GetString(6) != summary)
                    throw new WorkflowConflictException("The review effect identifier is already bound to different input.");
            }
            await transaction.CommitAsync(token);
            return (JsonSerializer.SerializeToElement(new
            {
                recordId = effectId,
                revision = 1,
                label,
                style,
                wordCount,
                characterCount,
                summary
            }, Json), "review:" + effectId);
        }

        private SqliteConnection Open()
        {
            if (databasePath is null) throw new InvalidOperationException("The review store is unavailable before plugin initialization.");
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
            return connection;
        }
    }

    private static string Style(JsonElement value)
    {
        var style = Text(value, "style", 20);
        return style is "concise" or "detailed" ? style : throw new ArgumentException("Choose concise or detailed presentation.");
    }

    private static string Text(JsonElement value, string name, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String || property.GetString() is not { Length: > 0 } text ||
            text.EnumerateRunes().Count() > maximum || text.Contains('\0'))
            throw new ArgumentException($"{name} must be a nonempty string of at most {maximum} characters without NUL.");
        return text;
    }

    private static void RequireFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a JSON object.");
        var fields = value.EnumerateObject().Select(x => x.Name).ToArray();
        if (fields.Length != names.Length || !fields.ToHashSet(StringComparer.Ordinal).SetEquals(names))
            throw new ArgumentException("The object fields do not match the command schema.");
    }

    private static JsonElement Schema(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
