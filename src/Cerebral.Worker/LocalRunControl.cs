namespace Cerebral.Worker;

// Local development lifecycle is available only for an explicitly launched user-session worker.
public sealed class LocalRunControl(bool enabled)
{
    public bool Enabled { get; } = enabled;
    public Action? Stop { get; set; }
    internal SourceRecoveryStartup? SourceRecovery { get; set; }
}
