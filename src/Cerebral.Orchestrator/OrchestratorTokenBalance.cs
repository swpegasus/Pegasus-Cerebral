using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly TokenBalanceControl conversationTokenBalance = new();
    private readonly Expander conversationTokenBalanceExpander = new() { IsExpanded = false, Margin = new Thickness(8, 0, 0, 0), MaxWidth = 390 };
    private int? selectedResponseSharePercent;

    private void InitializeConversationTokenBalance()
    {
        var content = new StackPanel();
        var explanation = Ui.Text("Sets the next assignment's balance inside the loaded model's fixed context. Reasoning stays separate; this does not reload the model or change reserved memory.", 12, Ui.Muted);
        explanation.Margin = new Thickness(0, 3, 0, 4);
        content.Children.Add(explanation);
        content.Children.Add(conversationTokenBalance);
        var reset = Ui.Button("Reset to configured balance", () => Run(UseAgentDefaultTokenBalanceAsync));
        reset.FontSize = 12;
        AutomationProperties.SetAutomationId(reset, "UseAgentTokenBalance");
        content.Children.Add(reset);
        conversationTokenBalanceExpander.Content = content;
        AutomationProperties.SetAutomationId(conversationTokenBalanceExpander, "ConversationTokenBalance");
        conversationTokenBalance.ResponseTokensChanged += (_, _) =>
        {
            if (conversationTokenBalance.TotalTokens < AgentTokenLimits.MinimumContextTokens) return;
            selectedResponseSharePercent = Math.Clamp(
                (int)Math.Round(100d * conversationTokenBalance.ResponseTokens / conversationTokenBalance.TotalTokens, MidpointRounding.AwayFromZero),
                AgentTokenLimits.MinimumResponseSharePercent, AgentTokenLimits.MaximumResponseSharePercent);
            pendingSubmission = null;
            // The service persists a whole percentage, so immediately project that exact value back into
            // the token control. Configure suppresses ResponseTokensChanged while it snaps the slider.
            RefreshConversationTokenBalance();
            SaveChatResponseBalance();
            if (IsNativeChat)
            {
                nativePreferencePending = true; nativePreferenceEditGeneration++;
                Run(PersistSelectedNativeResponsePreferenceAsync);
            }
        };
        RefreshConversationTokenBalance();
    }

    private void RefreshConversationTokenBalance(bool loadSessionPreference = false)
    {
        if (loadSessionPreference) selectedResponseSharePercent = session?.ResponseSharePercent;
        var profile = TryConversationProfile();
        if (profile is null)
        {
            conversationTokenBalanceExpander.IsEnabled = false;
            conversationTokenBalanceExpander.Header = "Token balance · configure Cerebral";
            return;
        }

        conversationTokenBalanceExpander.IsEnabled = true;
        var responseTokens = selectedResponseSharePercent is { } share
            ? AgentTokenLimits.ResolveResponseShare(profile, share)
            : profile.ResponseTokens;
        var reasoningTokens = profile.ReasoningMode == "on" ? profile.ReasoningTokens : 0;
        conversationTokenBalance.Configure(profile.ContextTokens, responseTokens, reasoningTokens, true,
            selectedResponseSharePercent is null ? "Configured Cerebral balance. Move the slider to customize only this conversation." : "Conversation preference. Applies to the next accepted assignment.");
        UpdateConversationTokenBalanceHeader();
    }

    private void UpdateConversationTokenBalanceHeader()
    {
        var profile = TryConversationProfile();
        var responseTokens = selectedResponseSharePercent is { } selectedShare && profile is not null
            ? AgentTokenLimits.ResolveResponseShare(profile, selectedShare)
            : conversationTokenBalance.ResponseTokens;
        conversationTokenBalanceExpander.Header = selectedResponseSharePercent is { } share
            ? $"Token balance · {share}% response ({responseTokens:N0} tokens)"
            : $"Token balance · configured ({responseTokens:N0} response tokens)";
    }

    private DesktopResponseBalanceSnapshot PluginResponseBalance()
    {
        var profile = ConversationProfile();
        var response = selectedResponseSharePercent is { } share
            ? AgentTokenLimits.ResolveResponseShare(profile, share)
            : profile.ResponseTokens;
        return new(PluginBinding(), selectedResponseSharePercent, profile.ContextTokens, response);
    }

    private async Task<DesktopOperationResult> SetPluginResponseBalanceAsync(DesktopConversationBinding expectedBinding,
        int? responseSharePercent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedBinding != PluginBinding())
            return new(DesktopOperationState.Rejected,
                "The connection or conversation changed. Review the current conversation before changing its response balance.");
        AgentTokenLimits.ValidateResponseShare(responseSharePercent, nameof(responseSharePercent));
        ConversationPropertiesSnapshot? accepted;
        var editGeneration = ++nativePreferenceEditGeneration;
        try
        {
            accepted = await PersistNativeResponsePreferenceAsync(expectedBinding, responseSharePercent, cancellationToken);
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or System.Net.Http.HttpRequestException or TimeoutException or
            OperationCanceledException or System.Text.Json.JsonException)
        { return new(DesktopOperationState.Uncertain, "Preference acceptance is unconfirmed. Reopen Properties to reload it. " + error.Message); }
        catch (InvalidOperationException error)
        { return new(DesktopOperationState.Rejected, error.Message); }
        var message = responseSharePercent is { } share
            ? $"Response balance saved at {share}% for the next accepted assignment."
            : "The next accepted assignment uses the configured Cerebral response balance.";
        if (expectedBinding == PluginBinding() && editGeneration == nativePreferenceEditGeneration)
        {
            selectedResponseSharePercent = responseSharePercent; pendingSubmission = null;
            nativePreferencePending = IsNativeChat && accepted is null;
            SaveChatResponseBalance(); RefreshConversationTokenBalance(); SetStatus(message);
        }
        return new(DesktopOperationState.Completed, message);
    }

    private async Task UseAgentDefaultTokenBalanceAsync()
    {
        if (IsNativeChat)
        {
            var result = await SetPluginResponseBalanceAsync(PluginBinding(), null, Lifetime.Token);
            SetStatus(result.Message ?? result.State.ToString(), result.State != DesktopOperationState.Completed); return;
        }
        var currentId = session?.Id;
        if (currentId is not null)
        {
            var updated = await Get<TaskSession>("sessions.response-balance", new { id = currentId, responseSharePercent = (int?)null });
            // The command may finish after the user selects another conversation. It still updates the
            // requested session, but must not overwrite the newly selected conversation's local state.
            if (session?.Id != currentId) return;
            session = updated;
        }
        selectedResponseSharePercent = null;
        pendingSubmission = null;
        RefreshConversationTokenBalance();
        SetStatus("This conversation will use the configured Cerebral token balance.");
    }
}
