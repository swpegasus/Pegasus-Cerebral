namespace Cerebral.Core;

public sealed class CortexContinuityState
{
    public string SessionId { get; set; } = "";
    public string JobId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public int SettingsRevision { get; set; }
    public string ServerId { get; set; } = "";
    public string ProviderFingerprint { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string TaskId { get; set; } = "";
    public int? Revision { get; set; }
    public string Status { get; set; } = "disabled";
    public string Message { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
