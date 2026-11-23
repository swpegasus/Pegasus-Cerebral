using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Cerebral.Contracts.Routing;

namespace Cerebral.Engine.Routing;

/// <summary>
/// Runs trusted, data-only request hooks in a validated deterministic order. Hook results are
/// adopted atomically only after the invocation completes within its deadline and the complete
/// result passes the configured bounds.
/// </summary>
public sealed partial class RequestRoutePipeline
{
    private const int MaximumIdentifierCharacters = 129;
    private sealed record RegisteredHook(IRequestRouteHook Handler, RouteHookDescriptor Descriptor);
    private readonly ImmutableArray<RegisteredHook> hooks;
    private readonly RequestRoutePipelineOptions options;

    public RequestRoutePipeline(
        IEnumerable<IRequestRouteHook> hooks,
        RequestRoutePipelineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        this.options = options ?? new RequestRoutePipelineOptions();
        ValidateOptions(this.options);
        this.hooks = OrderAndValidate([.. hooks.Select(hook => new RegisteredHook(
            hook ?? throw new RouteConfigurationException("Route contains a null hook."),
            hook.Descriptor ?? throw new RouteConfigurationException("Route hook has no descriptor.")))], this.options);
    }

    public ImmutableArray<RouteHookDescriptor> OrderedDescriptors =>
        [.. hooks.Select(static hook => hook.Descriptor)];

    public async Task<TextRouteOutcome> RouteAsync(
        TextRouteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request, options);
        cancellationToken.ThrowIfCancellationRequested();

        var original = request.Text;
        var current = original;
        var contributions = ImmutableArray<AttributedRouteContribution>.Empty;
        var invoked = ImmutableArray<string>.Empty;

        foreach (var hook in hooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = hook.Descriptor;
            invoked = invoked.Add(descriptor.Id);
            var snapshot = new TextRouteSnapshot(request.RequestId, original, current, contributions, invoked);
            var context = new RouteHookContext(descriptor.Stage, snapshot);
            var timeout = descriptor.Timeout ?? options.DefaultHookTimeout;
            var startedAt = Stopwatch.GetTimestamp();

            using var invocationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            invocationCancellation.CancelAfter(timeout);

            Task<RouteHookResult> invocation;
            try
            {
                invocation = hook.Handler.InvokeAsync(context, invocationCancellation.Token).AsTask();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return StopForHookFailure(request.RequestId, original, current, contributions, invoked,
                    descriptor.Id, "Hook invocation failed: " + BoundedExceptionMessage(exception));
            }

            RouteHookResult result;
            try
            {
                result = await invocation.WaitAsync(invocationCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObserveLateFault(invocation);
                throw;
            }
            catch (OperationCanceledException)
            {
                ObserveLateFault(invocation);
                return StopForHookFailure(request.RequestId, original, current, contributions, invoked,
                    descriptor.Id, $"Hook exceeded its {timeout.TotalMilliseconds:N0} ms deadline.");
            }
            catch (Exception exception)
            {
                return StopForHookFailure(request.RequestId, original, current, contributions, invoked,
                    descriptor.Id, "Hook invocation failed: " + BoundedExceptionMessage(exception));
            }

            // Cancellation can race a normally completing hook. Recheck both clocks before
            // validating or adopting any portion of the completed result.
            cancellationToken.ThrowIfCancellationRequested();
            if (invocationCancellation.IsCancellationRequested || Stopwatch.GetElapsedTime(startedAt) >= timeout)
                return StopForHookFailure(request.RequestId, original, current, contributions, invoked,
                    descriptor.Id, $"Hook exceeded its {timeout.TotalMilliseconds:N0} ms deadline.");

            string? validationFailure = ValidateResult(result, contributions.Length, options);
            if (validationFailure is not null)
                return StopForHookFailure(request.RequestId, original, current, contributions, invoked,
                    descriptor.Id, validationFailure);

            // Adopt the complete validated result in one step. A timeout, cancellation, or invalid
            // result reaches no mutation below this point.
            if (result.ReplacementText is not null) current = result.ReplacementText;
            if (!result.Contributions.IsDefaultOrEmpty)
                contributions = contributions.AddRange(result.Contributions.Select(value =>
                    new AttributedRouteContribution(descriptor.Id, value)));

            if (result.Disposition is RouteDisposition.Hold or RouteDisposition.Reject)
            {
                var stopped = new TextRouteSnapshot(request.RequestId, original, current, contributions, invoked);
                return new(result.Disposition, stopped, descriptor.Id, result.Reason);
            }
        }

        return new(RouteDisposition.Continue,
            new TextRouteSnapshot(request.RequestId, original, current, contributions, invoked));
    }

    private static TextRouteOutcome StopForHookFailure(
        string requestId,
        string original,
        string current,
        ImmutableArray<AttributedRouteContribution> contributions,
        ImmutableArray<string> invoked,
        string hookId,
        string reason) =>
        new(RouteDisposition.Hold,
            new TextRouteSnapshot(requestId, original, current, contributions, invoked),
            hookId,
            reason);

    private static string? ValidateResult(
        RouteHookResult? result,
        int existingContributionCount,
        RequestRoutePipelineOptions options)
    {
        if (result is null) return "Hook returned no result.";
        if (!Enum.IsDefined(result.Disposition)) return "Hook returned an unknown disposition.";

        if (result.Disposition == RouteDisposition.Continue)
        {
            if (result.Reason is not null) return "A continuing hook result cannot include a stop reason.";
            if (result.ReplacementText is { } replacement)
            {
                if (replacement.IndexOf('\0') >= 0) return "Hook replacement text contains a null character.";
                if (replacement.Length > options.MaximumTextCharacters)
                    return $"Hook replacement text exceeds {options.MaximumTextCharacters:N0} characters.";
            }
        }
        else
        {
            if (result.ReplacementText is not null) return "A held or rejected hook result cannot replace request text.";
            if (string.IsNullOrWhiteSpace(result.Reason)) return "A held or rejected hook result requires a reason.";
            if (result.Reason.Length > options.MaximumReasonCharacters)
                return $"Hook stop reason exceeds {options.MaximumReasonCharacters:N0} characters.";
        }

        if (result.Contributions.IsDefault) return "Hook returned an uninitialized contribution array.";
        if (existingContributionCount + result.Contributions.Length > options.MaximumContributions)
            return $"Route contributions exceed the {options.MaximumContributions:N0}-item limit.";

        foreach (var contribution in result.Contributions)
        {
            if (contribution is null) return "Hook returned a null contribution.";
            var failure = ValidateContribution(contribution, options.MaximumContributionCharacters);
            if (failure is not null) return failure;
        }

        return null;
    }

    private static string? ValidateContribution(RouteContribution contribution, int maximumCharacters)
    {
        static string? TextFailure(string name, string? value, int maximum)
        {
            if (string.IsNullOrWhiteSpace(value)) return $"Contribution {name} is required.";
            if (value.IndexOf('\0') >= 0) return $"Contribution {name} contains a null character.";
            return value.Length <= maximum ? null : $"Contribution {name} exceeds {maximum:N0} characters.";
        }

        return contribution switch
        {
            ContextContribution value =>
                TextFailure("name", value.Name, 256) ?? TextFailure("text", value.Text, maximumCharacters),
            InstructionContribution value =>
                TextFailure("name", value.Name, 256) ?? TextFailure("text", value.Text, maximumCharacters),
            LabelContribution value =>
                TextFailure("name", value.Name, 256) ?? TextFailure("value", value.Value, maximumCharacters),
            DiagnosticContribution value =>
                TextFailure("code", value.Code, 256) ?? TextFailure("message", value.Message, maximumCharacters),
            _ => "Hook returned an unsupported contribution type."
        };
    }

    private static ImmutableArray<RegisteredHook> OrderAndValidate(
        ImmutableArray<RegisteredHook> candidates,
        RequestRoutePipelineOptions options)
    {
        if (candidates.Length > options.MaximumHooks)
            throw new RouteConfigurationException($"Route contains {candidates.Length:N0} hooks; the limit is {options.MaximumHooks:N0}.");
        if (candidates.Any(static hook => hook is null))
            throw new RouteConfigurationException("Route hook collection contains a null hook.");

        var byId = new Dictionary<string, RegisteredHook>(StringComparer.Ordinal);
        foreach (var hook in candidates)
        {
            var descriptor = hook.Descriptor ?? throw new RouteConfigurationException("A route hook has no descriptor.");
            ValidateDescriptor(descriptor, options);
            if (!byId.TryAdd(descriptor.Id, hook))
                throw new RouteConfigurationException($"Duplicate route hook ID '{descriptor.Id}'.");
        }

        foreach (var hook in candidates)
        {
            var descriptor = hook.Descriptor;
            ValidateDependencies(descriptor, descriptor.Before, "before", byId);
            ValidateDependencies(descriptor, descriptor.After, "after", byId);
        }

        var ordered = ImmutableArray.CreateBuilder<RegisteredHook>(candidates.Length);
        foreach (var stage in Enum.GetValues<RequestRouteStage>().Order())
        {
            var stageHooks = candidates.Where(hook => hook.Descriptor.Stage == stage).ToArray();
            if (stageHooks.Length == 0) continue;

            var ids = stageHooks.Select(hook => hook.Descriptor.Id).ToHashSet(StringComparer.Ordinal);
            var outgoing = ids.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            var incomingCount = ids.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);

            void AddEdge(string from, string to)
            {
                if (outgoing[from].Add(to)) incomingCount[to]++;
            }

            foreach (var hook in stageHooks)
            {
                foreach (var target in hook.Descriptor.Before) AddEdge(hook.Descriptor.Id, target);
                foreach (var source in hook.Descriptor.After) AddEdge(source, hook.Descriptor.Id);
            }

            var ready = new SortedSet<string>(incomingCount.Where(pair => pair.Value == 0).Select(pair => pair.Key), StringComparer.Ordinal);
            var stageCount = 0;
            while (ready.Count > 0)
            {
                var id = ready.Min!;
                ready.Remove(id);
                ordered.Add(byId[id]);
                stageCount++;
                foreach (var target in outgoing[id].Order(StringComparer.Ordinal))
                    if (--incomingCount[target] == 0) ready.Add(target);
            }

            if (stageCount != stageHooks.Length)
            {
                var cycle = incomingCount.Where(pair => pair.Value > 0).Select(pair => pair.Key).Order(StringComparer.Ordinal);
                throw new RouteConfigurationException("Route hook dependency cycle: " + string.Join(", ", cycle) + ".");
            }
        }

        return ordered.MoveToImmutable();
    }

    private static void ValidateDescriptor(RouteHookDescriptor descriptor, RequestRoutePipelineOptions options)
    {
        var id = descriptor.Id;
        if (string.IsNullOrEmpty(id) || !ValidHookId().IsMatch(id))
            throw new RouteConfigurationException($"Route hook ID '{id}' must be namespaced as 'namespace:name'.");
        if (id.Length > MaximumIdentifierCharacters)
            throw new RouteConfigurationException($"Route hook ID '{id}' is too long.");
        if (!Enum.IsDefined(descriptor.Stage))
            throw new RouteConfigurationException($"Route hook '{descriptor.Id}' uses an unknown stage.");
        if (descriptor.Before.IsDefault || descriptor.After.IsDefault)
            throw new RouteConfigurationException($"Route hook '{descriptor.Id}' has an uninitialized dependency collection.");
        if (descriptor.Before.Length != descriptor.Before.Distinct(StringComparer.Ordinal).Count() ||
            descriptor.After.Length != descriptor.After.Distinct(StringComparer.Ordinal).Count())
            throw new RouteConfigurationException($"Route hook '{descriptor.Id}' repeats a dependency.");
        if (descriptor.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > options.MaximumHookTimeout))
            throw new RouteConfigurationException($"Route hook '{descriptor.Id}' timeout must be positive and no greater than {options.MaximumHookTimeout}.");
    }

    private static void ValidateDependencies(
        RouteHookDescriptor descriptor,
        ImmutableArray<string> dependencies,
        string relationship,
        IReadOnlyDictionary<string, RegisteredHook> hooks)
    {
        foreach (var dependency in dependencies)
        {
            if (string.IsNullOrEmpty(dependency) || !ValidHookId().IsMatch(dependency))
                throw new RouteConfigurationException($"Route hook '{descriptor.Id}' has invalid {relationship} dependency '{dependency}'.");
            if (!hooks.TryGetValue(dependency, out var target))
                throw new RouteConfigurationException($"Route hook '{descriptor.Id}' has missing {relationship} dependency '{dependency}'.");
            if (target.Descriptor.Stage != descriptor.Stage)
                throw new RouteConfigurationException($"Route hook '{descriptor.Id}' cannot order {relationship} '{dependency}' across routing stages.");
        }
    }

    private static void ValidateRequest(TextRouteRequest request, RequestRoutePipelineOptions options)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 256 || request.RequestId.IndexOf('\0') >= 0)
            throw new ArgumentException("Request ID is required and must contain at most 256 non-null characters.", nameof(request));
        if (request.Text is null || request.Text.IndexOf('\0') >= 0 || request.Text.Length > options.MaximumTextCharacters)
            throw new ArgumentException($"Request text must contain at most {options.MaximumTextCharacters:N0} non-null characters.", nameof(request));
    }

    private static void ValidateOptions(RequestRoutePipelineOptions options)
    {
        if (options.MaximumHooks is < 0 or > 10_000 || options.MaximumTextCharacters < 1 ||
            options.MaximumContributions < 0 || options.MaximumContributionCharacters < 1 ||
            options.MaximumReasonCharacters < 1 || options.DefaultHookTimeout <= TimeSpan.Zero ||
            options.MaximumHookTimeout < options.DefaultHookTimeout)
            throw new ArgumentException("Invalid request route pipeline bounds.", nameof(options));
    }

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string BoundedExceptionMessage(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length == 0) message = exception.GetType().Name;
        return message[..Math.Min(message.Length, 512)];
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]{0,63}:[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidHookId();
}
