using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Client;

public sealed record ConversationControlSnapshot(string ConversationId, bool DispatchPending, bool Uncertain,
    WorkflowSteeringRequest? Steering = null, ConversationRenameRequest? Rename = null,
    ConversationCompactionRequest? Compaction = null)
{
    public string? RequestId => Steering?.RequestId ?? Rename?.RequestId ?? Compaction?.RequestId;
}

public sealed record ConversationControlReceipt(WorkflowSteeringReceipt? Steering = null,
    ConversationRenameResult? Rename = null, ConversationCompactionReceipt? Compaction = null);

/// <summary>
/// One captured service connection and one conversation. Retains exact mutation input after an
/// uncertain acknowledgement. Retry is explicit; reading conversation state never clears uncertainty.
/// The caller owns the transport lifetime and must not replace its delegate with a different service.
/// </summary>
public sealed class ConversationControlClient
{
    public const string ChatDefinitionId = ConversationWorkflowContract.DefinitionId;
    public const string ChatDefinitionVersion = ConversationWorkflowContract.DefinitionVersion;
    private readonly Func<CommandRequest, CancellationToken, Task<CommandResult>> command;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ConversationControlSnapshot snapshot;

    public ConversationControlClient(string conversationId, Func<CommandRequest, CancellationToken, Task<CommandResult>> command)
    {
        Identifier(conversationId, nameof(conversationId));
        ArgumentNullException.ThrowIfNull(command);
        this.command = command; snapshot = new(conversationId, false, false);
    }

    public ConversationControlSnapshot Snapshot => Volatile.Read(ref snapshot);

    public Task<WorkflowSteeringReceipt> SteerAsync(WorkflowSteeringRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Identifier(request.RequestId, nameof(request.RequestId)); Identifier(request.RunId, nameof(request.RunId));
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Enter steering information.");
        BoundedText(request.Text, 8192, nameof(request.Text));
        if (request.AssignmentText is { } assignment) BoundedText(assignment, 8192, nameof(request.AssignmentText));
        return ExecuteAsync<WorkflowSteeringReceipt>("jobs.workflow.steer", request, request, null, null,
            result => result.RequestId == request.RequestId && result.RunId == request.RunId &&
                result.Sequence > 0 && result.AcceptedAt != default, token);
    }

    public Task<ConversationRenameResult> RenameAsync(ConversationRenameRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Identifier(request.RequestId, nameof(request.RequestId));
        if (request.ConversationId != snapshot.ConversationId || request.DefinitionId != ChatDefinitionId ||
            request.ExpectedTitleRevision < 0)
            throw new ArgumentException("Rename must target this ordinary conversation and its saved title revision.");
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title != request.Title.Trim() ||
            request.Title.EnumerateRunes().Count() > ConversationRenameContract.MaximumTitleScalars ||
            request.Title.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'))
            throw new ArgumentException("Enter a single-line conversation name of at most 160 characters.");
        BoundedText(request.Title, ConversationRenameContract.MaximumTitleUtf8Bytes, nameof(request.Title));
        return ExecuteAsync<ConversationRenameResult>("conversations.rename", request, null, request, null,
            result => result.ConversationId == request.ConversationId && result.Title == request.Title &&
                result.TitleRevision == request.ExpectedTitleRevision + 1, token);
    }

    public Task<ConversationCompactionReceipt> RequestCompactionAsync(ConversationCompactionRequest request,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Identifier(request.RequestId, nameof(request.RequestId));
        if (request.ConversationId != snapshot.ConversationId || request.DefinitionId != ChatDefinitionId ||
            request.ExpectedConversationRevision < 0)
            throw new ArgumentException("Compaction must target this ordinary conversation and its saved event revision.");
        return ExecuteAsync<ConversationCompactionReceipt>("conversations.compact", request, null, null, request,
            result => result.RequestId == request.RequestId && result.ConversationId == request.ConversationId &&
                result.RequestedAtRevision == request.ExpectedConversationRevision && result.RequestedAt != default,
            token);
    }

    public async Task<ConversationControlReceipt> RetryAsync(CancellationToken token = default)
    {
        var retained = Snapshot;
        if (!retained.Uncertain || retained.DispatchPending)
            throw new InvalidOperationException("There is no unconfirmed control request to retry.");
        if (retained.Steering is { } steering) return new(Steering: await SteerAsync(steering, token).ConfigureAwait(false));
        if (retained.Rename is { } rename) return new(Rename: await RenameAsync(rename, token).ConfigureAwait(false));
        if (retained.Compaction is { } compaction) return new(Compaction: await RequestCompactionAsync(compaction, token).ConfigureAwait(false));
        throw new InvalidOperationException("The retained control request is missing.");
    }

    private async Task<T> ExecuteAsync<T>(string operation, object arguments, WorkflowSteeringRequest? steering,
        ConversationRenameRequest? rename, ConversationCompactionRequest? compaction,
        Func<T, bool> validate, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false))
            throw new InvalidOperationException("Wait for the pending control request's receipt.");
        try
        {
            token.ThrowIfCancellationRequested();
            var previous = Snapshot;
            if (previous.Uncertain && (previous.Steering != steering || previous.Rename != rename || previous.Compaction != compaction))
                throw new InvalidOperationException("Retry the retained request unchanged before sending a different action.");
            var frozen = CerebralJson.Element(arguments);
            Volatile.Write(ref snapshot, previous with { DispatchPending = true, Steering = steering, Rename = rename, Compaction = compaction });
            try
            {
                // ProfileId intentionally absent: neither rename nor owner steering impersonates an agent.
                var response = await command(new(operation, frozen), token).ConfigureAwait(false);
                if (!response.Success)
                    throw new WorkflowConversationCommandException(operation, response.Error ?? "The service rejected this action.");
                if (Encoding.UTF8.GetByteCount(response.Data.GetRawText()) > 8192)
                    throw new InvalidDataException("The service returned an oversized control receipt.");
                var receipt = response.Data.Deserialize<T>(CerebralJson.Options);
                if (receipt is null || !validate(receipt))
                    throw new InvalidDataException("The service receipt does not match this exact control request.");
                Volatile.Write(ref snapshot, new(previous.ConversationId, false, false));
                return receipt;
            }
            catch (WorkflowConversationCommandException)
            {
                // A first-attempt rejection is confirmed. Rejection of a later retry (for example
                // revoked authority) cannot prove the original request did not already commit.
                Volatile.Write(ref snapshot, previous.Uncertain ? previous with { DispatchPending = false } :
                    new(previous.ConversationId, false, false)); throw;
            }
            catch
            {
                Volatile.Write(ref snapshot, Snapshot with { DispatchPending = false, Uncertain = true }); throw;
            }
        }
        finally { gate.Release(); }
    }

    private static void Identifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new ArgumentException("A bounded explicit identifier is required.", name);
    }

    private static void BoundedText(string value, int maxBytes, string name)
    {
        if (value.Contains('\0')) throw new ArgumentException("Text cannot contain NUL.", name);
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(value) > maxBytes)
                throw new ArgumentException("Text exceeds the supported request size.", name);
        }
        catch (EncoderFallbackException error) { throw new ArgumentException("Text contains invalid Unicode.", name, error); }
    }
}
