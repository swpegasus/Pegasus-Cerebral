namespace Cerebral.Tools;

/// <summary>Optional host-owned default deadline for a fixed extension operation.</summary>
public interface IToolExecutionTimeoutPolicy
{
    /// <summary>
    /// Returns a default from 1 through 600 seconds for an advertised exact tool name.
    /// A schema-supported timeoutSeconds argument still overrides this default.
    /// Caller, dispatcher lifetime and engine deadlines continue to cancel the operation.
    /// </summary>
    int GetDefaultTimeoutSeconds(string toolName);
}
