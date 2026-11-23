namespace Cerebral.Desktop.Plugins;

/// <summary>Optional owner capability. The host supplies request identity/deadline and owns
/// cancellation. Policy and Text are the complete model messages; the selected profile supplies
/// only exact local model/capacity metadata, never instructions, tools, roots, history or grants.</summary>
public interface IDesktopIsolatedTextGenerationHost
{
    Task<DesktopIsolatedTextAvailability> GetIsolatedTextAvailabilityAsync(CancellationToken cancellationToken = default);
    Task<DesktopIsolatedTextResult> GenerateIsolatedTextAsync(DesktopIsolatedTextRequest request, CancellationToken cancellationToken = default);
}
public sealed record DesktopIsolatedTextRequest(string ProfileId, long ProfileRevision, string Purpose,
    string Policy, string Text, int MaximumOutputTokens = 256);
public sealed record DesktopIsolatedTextAvailability(bool Available, string? Message = null);
public sealed record DesktopIsolatedTextResult(DesktopOperationState State, string? Text = null,
    string? Message = null, string? RequestId = null);
