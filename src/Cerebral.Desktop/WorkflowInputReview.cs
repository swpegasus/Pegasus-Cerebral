using System.Text.Json;
using System.Windows;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Visible review of one already accepted workflow's exact next-turn input.</summary>
public sealed record WorkflowInputReviewRequest(string ProfileId, WorkflowDefinitionDescriptor Definition,
    JsonElement AcceptedInput, string UserText, bool AllowEmptyUserText = false);

public static class WorkflowInputReview
{
    /// <summary>Returns null only when the owner cancels the review. The caller owns submission and its request lease.</summary>
    public static async Task<JsonElement?> ShowAsync(Window owner,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> command,
        Func<IReadOnlyList<AgentProfile>> profiles, Func<bool> current,
        WorkflowInputReviewRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ProfileId) || request.Definition is null ||
            request.Definition.RequiredCapabilities is null ||
            request.AcceptedInput.ValueKind != JsonValueKind.Object || request.UserText is null)
            throw new ArgumentException("A complete accepted workflow input review is required.", nameof(request));
        token.ThrowIfCancellationRequested();
        if (!current()) throw new InvalidOperationException("The accepted workflow owner changed before input review.");
        var definition = request.Definition with
        {
            InputSchema = request.Definition.InputSchema.Clone(),
            RequiredCapabilities = request.Definition.RequiredCapabilities.ToArray()
        };
        var target = new WorkflowInputReviewTarget(request.ProfileId, definition,
            request.AcceptedInput.Clone(), request.UserText, request.AllowEmptyUserText);
        using var form = new WorkflowStartForm(owner, command, profiles, current, token, target);
        await form.InitializeAsync();
        token.ThrowIfCancellationRequested();
        if (!current()) throw new InvalidOperationException("The accepted workflow owner changed during input review.");
        form.Window.ShowDialog();
        token.ThrowIfCancellationRequested();
        if (!current()) throw new InvalidOperationException("The accepted workflow owner changed during input review.");
        if (form.AcceptedDraft is not { } draft) return null;
        if (draft.ProfileId != target.ProfileId || draft.DefinitionId != target.Definition.Id ||
            draft.DefinitionVersion != target.Definition.Version || draft.UserText != target.UserText)
            throw new InvalidOperationException("The reviewed workflow request changed.");
        return draft.Input.Clone();
    }
}
