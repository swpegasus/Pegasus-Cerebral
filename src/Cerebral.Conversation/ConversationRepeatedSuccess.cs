namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    public const string RepeatedSuccessGuardVersion = "1.0.6-repeat-guard.1";
    private readonly bool useRepeatedSuccessGuard;
    private enum RepeatedSuccessEdition { Enabled }

    private ConversationAgentHarness(RepeatedSuccessEdition edition) : this()
    {
        useRepeatedSuccessGuard = edition == RepeatedSuccessEdition.Enabled;
        Descriptor = Descriptor with { Version = RepeatedSuccessGuardVersion };
    }

    /// <summary>Opt-in current-generation build guard. Previously accepted definitions keep their saved policy.</summary>
    public static ConversationAgentHarness CreateRepeatedSuccessGuardEdition() => new(RepeatedSuccessEdition.Enabled);
}
