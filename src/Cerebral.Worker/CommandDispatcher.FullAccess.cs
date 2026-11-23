namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    // Optional owner-UI metadata extends the JSON response without changing the shared SDK record.
    // Eligibility is a preflight only; Cortex still validates its own service binding and publisher.
    private object FullAccessState()
    {
        var status = tools.FullAccessStatus();
        var unavailableReason = tools.FullAccessUnavailableReason;
        return new
        {
            status.Active, status.Remembered, status.ProviderId, status.EnabledAt, status.Message,
            available = unavailableReason is null, unavailableReason
        };
    }
}
