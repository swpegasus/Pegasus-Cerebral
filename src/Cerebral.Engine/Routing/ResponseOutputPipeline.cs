using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerebral.Contracts;
using Cerebral.Contracts.Routing;

namespace Cerebral.Engine.Routing;

/// <summary>
/// Freezes and orders trusted response hooks and committed-output observers. Response results are
/// adopted only after the whole result validates. Observer failures are bounded diagnostics over
/// already committed state and never change the committed outcome.
/// </summary>
public sealed partial class ResponseOutputPipeline
{
    private const int MaximumIdentifierCharacters = 129;
    private sealed record RegisteredResponseHook(IResponseOutputHook Handler, OutputHookDescriptor Descriptor);
    private sealed record RegisteredObserver(ICommittedOutputObserver Handler, OutputHookDescriptor Descriptor);

    private readonly ImmutableArray<RegisteredResponseHook> responseHooks;
    private readonly ImmutableArray<RegisteredObserver> observers;
    private readonly ResponseOutputPipelineOptions options;

    public ResponseOutputPipeline(IEnumerable<IResponseOutputHook>? responseHooks = null,
        IEnumerable<ICommittedOutputObserver>? observers = null, ResponseOutputPipelineOptions? options = null)
    {
        this.options = options ?? new ResponseOutputPipelineOptions();
        ValidateOptions(this.options);
        var responses = (responseHooks ?? []).Select(hook =>
        {
            ArgumentNullException.ThrowIfNull(hook);
            return new RegisteredResponseHook(hook, Freeze(hook.Descriptor, OutputHookBoundary.ResponseBeforeCommit));
        }).ToImmutableArray();
        var committed = (observers ?? []).Select(observer =>
        {
            ArgumentNullException.ThrowIfNull(observer);
            return new RegisteredObserver(observer, Freeze(observer.Descriptor, OutputHookBoundary.OutputAfterCommit));
        }).ToImmutableArray();
        this.responseHooks = OrderAndValidate(responses, static item => item.Descriptor, this.options);
        this.observers = OrderAndValidate(committed, static item => item.Descriptor, this.options);
    }

    public ImmutableArray<OutputHookDescriptor> OrderedResponseDescriptors =>
        [.. responseHooks.Select(static hook => hook.Descriptor)];
    public ImmutableArray<OutputHookDescriptor> OrderedObserverDescriptors =>
        [.. observers.Select(static hook => hook.Descriptor)];

    public async Task<ResponseOutputOutcome> RouteResponseAsync(ResponseOutputRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentifier(request.RequestId, nameof(request.RequestId), 256);
        ValidateIdentifier(request.ConversationId, nameof(request.ConversationId), 128);
        ValidateIdentifier(request.TurnId, nameof(request.TurnId), 128);
        ArgumentNullException.ThrowIfNull(request.Projection);
        if (!Enum.IsDefined(request.Projection.Model) || !Enum.IsDefined(request.Projection.Human) ||
            !Enum.IsDefined(request.Projection.Retention))
            throw new ArgumentException("Response projection contains an unsupported value.", nameof(request));
        var original = FreezeContent(request.Content, options);
        var current = original;
        var human = request.Projection.Human;
        var invoked = ImmutableArray<OutputHookIdentity>.Empty;
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var hook in responseHooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = Identity(hook.Descriptor);
            invoked = invoked.Add(identity);
            var before = Snapshot(request, original, current, human, invoked);
            var timeout = hook.Descriptor.Timeout ?? options.DefaultResponseTimeout;
            var startedAt = Stopwatch.GetTimestamp();
            using var invocationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            invocationCancellation.CancelAfter(timeout);

            Task<ResponseOutputHookResult> invocation;
            try { invocation = hook.Handler.InvokeAsync(new(before), invocationCancellation.Token).AsTask(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                return Stop(request, original, current, human, invoked, identity,
                    "Response hook invocation failed: " + BoundedExceptionMessage(error));
            }

            ResponseOutputHookResult result;
            try { result = await invocation.WaitAsync(invocationCancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObserveLateFault(invocation);
                throw;
            }
            catch (OperationCanceledException)
            {
                ObserveLateFault(invocation);
                return Stop(request, original, current, human, invoked, identity,
                    $"Response hook exceeded its {timeout.TotalMilliseconds:N0} ms deadline.");
            }
            catch (Exception error)
            {
                return Stop(request, original, current, human, invoked, identity,
                    "Response hook invocation failed: " + BoundedExceptionMessage(error));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (invocationCancellation.IsCancellationRequested || Stopwatch.GetElapsedTime(startedAt) >= timeout)
                return Stop(request, original, current, human, invoked, identity,
                    $"Response hook exceeded its {timeout.TotalMilliseconds:N0} ms deadline.");

            var validation = ValidateResponseResult(result);
            if (validation is not null)
                return Stop(request, original, current, human, invoked, identity, validation);

            ImmutableArray<ContentPart>? replacement = null;
            if (result.ReplacementContent is not null)
            {
                try { replacement = FreezeContent(result.ReplacementContent, options); }
                catch (Exception error) when (IsOrdinary(error))
                {
                    return Stop(request, original, current, human, invoked, identity,
                        "Response hook returned invalid content: " + BoundedExceptionMessage(error));
                }
            }

            if (replacement is { } adopted) current = adopted;
            if (result.HumanVisibility is { } visibility) human = visibility;
            if (result.Disposition is RouteDisposition.Hold or RouteDisposition.Reject)
                return new(result.Disposition, Snapshot(request, original, current, human, invoked), identity, result.Reason);
        }

        return new(RouteDisposition.Continue, Snapshot(request, original, current, human, invoked));
    }

    public async Task<OutputObservationReport> ObserveCommittedAsync(CommittedOutputContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.SourceEvent);
        ArgumentNullException.ThrowIfNull(context.PresentationEvent);
        context.SourceEvent.Validate();
        context.PresentationEvent.Validate();
        var invoked = ImmutableArray<OutputHookIdentity>.Empty;
        var diagnostics = ImmutableArray<AttributedOutputDiagnostic>.Empty;

        foreach (var observer in observers)
        {
            var identity = Identity(observer.Descriptor);
            invoked = invoked.Add(identity);
            if (cancellationToken.IsCancellationRequested)
            {
                diagnostics = AddDiagnostic(diagnostics, context.RequestId, identity, "observer.cancelled",
                    "Committed-output observation was cancelled after the result committed.", true);
                break;
            }
            var timeout = observer.Descriptor.Timeout ?? options.DefaultObserverTimeout;
            var startedAt = Stopwatch.GetTimestamp();
            using var invocationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            invocationCancellation.CancelAfter(timeout);
            Task<OutputObserverResult> invocation;
            try { invocation = observer.Handler.ObserveAsync(context, invocationCancellation.Token).AsTask(); }
            catch (Exception error) when (IsOrdinary(error))
            {
                diagnostics = AddDiagnostic(diagnostics, context.RequestId, identity, "observer.failed", BoundedExceptionMessage(error), true);
                continue;
            }

            OutputObserverResult result;
            try { result = await invocation.WaitAsync(invocationCancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                ObserveLateFault(invocation);
                diagnostics = AddDiagnostic(diagnostics, context.RequestId, identity,
                    cancellationToken.IsCancellationRequested ? "observer.cancelled" : "observer.timeout",
                    cancellationToken.IsCancellationRequested
                        ? "Committed-output observation was cancelled after the result committed."
                        : $"Committed-output observer exceeded its {timeout.TotalMilliseconds:N0} ms deadline.", true);
                if (cancellationToken.IsCancellationRequested) break;
                continue;
            }
            catch (Exception error) when (IsOrdinary(error))
            {
                diagnostics = AddDiagnostic(diagnostics, context.RequestId, identity, "observer.failed", BoundedExceptionMessage(error), true);
                continue;
            }

            if (invocationCancellation.IsCancellationRequested || Stopwatch.GetElapsedTime(startedAt) >= timeout)
            {
                diagnostics = AddDiagnostic(diagnostics, context.RequestId, identity, "observer.timeout",
                    $"Committed-output observer exceeded its {timeout.TotalMilliseconds:N0} ms deadline.", true);
                continue;
            }
            if (result?.Diagnostics is null)
            {
                diagnostics = AddDiagnostic(diagnostics, context.RequestId, identity, "observer.invalid", "Observer returned no diagnostic collection.", true);
                continue;
            }
            foreach (var diagnostic in result.Diagnostics)
            {
                if (diagnostics.Length >= options.MaximumDiagnostics) break;
                if (diagnostic is null || !ValidDiagnosticCode().IsMatch(diagnostic.Code ?? "") ||
                    string.IsNullOrWhiteSpace(diagnostic.Message) || diagnostic.Message.Contains('\0') ||
                    diagnostic.Message.Length > options.MaximumDiagnosticCharacters)
                {
                    diagnostics = AddDiagnostic(diagnostics, context.RequestId, identity, "observer.invalid",
                        "Observer returned an invalid or oversized diagnostic.", true);
                    break;
                }
                diagnostics = diagnostics.Add(new(context.RequestId, identity, diagnostic.Code!, diagnostic.Message, false));
            }
        }
        return new(invoked, diagnostics);
    }

    private ImmutableArray<AttributedOutputDiagnostic> AddDiagnostic(
        ImmutableArray<AttributedOutputDiagnostic> diagnostics, string requestId, OutputHookIdentity identity,
        string code, string message, bool failure)
    {
        if (diagnostics.Length >= options.MaximumDiagnostics) return diagnostics;
        if (message.Length > options.MaximumDiagnosticCharacters) message = message[..options.MaximumDiagnosticCharacters];
        return diagnostics.Add(new(requestId, identity, code, message, failure));
    }

    private static ResponseOutputOutcome Stop(ResponseOutputRequest request,
        ImmutableArray<ContentPart> original, ImmutableArray<ContentPart> current, HumanVisibility human,
        ImmutableArray<OutputHookIdentity> invoked, OutputHookIdentity hook, string reason) =>
        new(RouteDisposition.Hold, Snapshot(request, original, current, human, invoked), hook, reason);

    private static ResponseOutputSnapshot Snapshot(ResponseOutputRequest request,
        ImmutableArray<ContentPart> original, ImmutableArray<ContentPart> current, HumanVisibility human,
        ImmutableArray<OutputHookIdentity> invoked) =>
        new(request.RequestId, request.ConversationId, request.TurnId, original, current,
            request.Projection with { }, human, invoked,
            human != request.Projection.Human || !Equivalent(original, current));

    private string? ValidateResponseResult(ResponseOutputHookResult? result)
    {
        if (result is null) return "Response hook returned no result.";
        if (!Enum.IsDefined(result.Disposition)) return "Response hook returned an unknown disposition.";
        if (result.Disposition == RouteDisposition.Continue)
        {
            if (result.Reason is not null) return "A continuing response hook cannot include a stop reason.";
            if (result.HumanVisibility is { } human && !Enum.IsDefined(human))
                return "Response hook returned an unsupported human visibility.";
        }
        else
        {
            if (result.ReplacementContent is not null || result.HumanVisibility is not null)
                return "A held or rejected response hook cannot replace or reroute output.";
            if (string.IsNullOrWhiteSpace(result.Reason) || result.Reason.Contains('\0'))
                return "A held or rejected response hook requires a bounded reason.";
            if (result.Reason.Length > options.MaximumReasonCharacters)
                return $"Response hook stop reason exceeds {options.MaximumReasonCharacters:N0} characters.";
        }
        return null;
    }

    private static ImmutableArray<ContentPart> FreezeContent(IReadOnlyList<ContentPart>? content,
        ResponseOutputPipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Count > options.MaximumContentParts)
            throw new ArgumentException($"Response output exceeds {options.MaximumContentParts:N0} content parts.", nameof(content));
        var frozen = content.Select(FreezePart).ToImmutableArray();
        var validation = new CerebralEvent("output-validation", "output-validation", "output-validation", 0,
            CerebralEventTypes.Message, new(EventActorKind.Agent, "output-validation"), frozen,
            new(), new(EventSourceKind.Agent, "output-validation"), DateTimeOffset.UtcNow);
        validation.Validate();
        if (JsonSerializer.SerializeToUtf8Bytes<IReadOnlyList<ContentPart>>(frozen).Length > options.MaximumContentUtf8Bytes)
            throw new ArgumentException($"Response output exceeds {options.MaximumContentUtf8Bytes:N0} UTF-8 bytes.", nameof(content));
        return frozen;
    }

    private static ContentPart FreezePart(ContentPart value) => value switch
    {
        null => throw new ArgumentException("Response output contains a null content part."),
        TextContentPart text => text with { },
        StructuredContentPart structured => structured with { Value = structured.Value.Clone() },
        FileContentPart file => file with { Attachment = Freeze(file.Attachment) },
        ImageContentPart image => image with { Attachment = Freeze(image.Attachment) },
        AudioContentPart audio => audio with { Attachment = Freeze(audio.Attachment) },
        ResourceContentPart resource => resource with { Resource = resource.Resource with { } },
        _ => throw new ArgumentException("Response output contains an unsupported content part.")
    };

    private static AttachmentReference Freeze(AttachmentReference value) => value with
    {
        Metadata = value.Metadata is null
            ? null
            : value.Metadata.ToImmutableDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal)
    };

    private static bool Equivalent(ImmutableArray<ContentPart> left, ImmutableArray<ContentPart> right) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement<IReadOnlyList<ContentPart>>(left),
            JsonSerializer.SerializeToElement<IReadOnlyList<ContentPart>>(right));

    private static OutputHookDescriptor Freeze(OutputHookDescriptor? descriptor, OutputHookBoundary expected)
    {
        if (descriptor is null) throw new RouteConfigurationException("Output hook has no descriptor.");
        var frozen = new OutputHookDescriptor(descriptor.Id, descriptor.Version, descriptor.Boundary,
            descriptor.Before, descriptor.After, descriptor.Timeout);
        if (frozen.Boundary != expected)
            throw new RouteConfigurationException($"Output hook '{frozen.Id}' is registered for the wrong boundary.");
        return frozen;
    }

    private static ImmutableArray<T> OrderAndValidate<T>(ImmutableArray<T> candidates,
        Func<T, OutputHookDescriptor> descriptorFor, ResponseOutputPipelineOptions options)
    {
        if (candidates.Length > options.MaximumHooksPerBoundary)
            throw new RouteConfigurationException($"Output boundary contains {candidates.Length:N0} hooks; the limit is {options.MaximumHooksPerBoundary:N0}.");
        var byId = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var descriptor = descriptorFor(candidate);
            ValidateDescriptor(descriptor, options);
            if (!byId.TryAdd(descriptor.Id, candidate))
                throw new RouteConfigurationException($"Duplicate output hook ID '{descriptor.Id}'.");
        }
        foreach (var candidate in candidates)
        {
            var descriptor = descriptorFor(candidate);
            ValidateDependencies(descriptor, descriptor.Before, "before", byId, descriptorFor);
            ValidateDependencies(descriptor, descriptor.After, "after", byId, descriptorFor);
        }
        var outgoing = byId.Keys.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var incoming = byId.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        void AddEdge(string from, string to) { if (outgoing[from].Add(to)) incoming[to]++; }
        foreach (var candidate in candidates)
        {
            var descriptor = descriptorFor(candidate);
            foreach (var target in descriptor.Before) AddEdge(descriptor.Id, target);
            foreach (var source in descriptor.After) AddEdge(source, descriptor.Id);
        }
        var ready = new SortedSet<string>(incoming.Where(item => item.Value == 0).Select(item => item.Key), StringComparer.Ordinal);
        var ordered = ImmutableArray.CreateBuilder<T>(candidates.Length);
        while (ready.Count != 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            ordered.Add(byId[id]);
            foreach (var target in outgoing[id].Order(StringComparer.Ordinal))
                if (--incoming[target] == 0) ready.Add(target);
        }
        if (ordered.Count != candidates.Length)
            throw new RouteConfigurationException("Output hook dependency cycle: " +
                string.Join(", ", incoming.Where(item => item.Value > 0).Select(item => item.Key).Order(StringComparer.Ordinal)) + ".");
        return ordered.MoveToImmutable();
    }

    private static void ValidateDescriptor(OutputHookDescriptor descriptor, ResponseOutputPipelineOptions options)
    {
        if (string.IsNullOrEmpty(descriptor.Id) || descriptor.Id.Length > MaximumIdentifierCharacters || !ValidHookId().IsMatch(descriptor.Id))
            throw new RouteConfigurationException($"Output hook ID '{descriptor.Id}' must be namespaced as 'namespace:name'.");
        if (string.IsNullOrEmpty(descriptor.Version) || descriptor.Version.Length > 64 || !ValidVersion().IsMatch(descriptor.Version))
            throw new RouteConfigurationException($"Output hook '{descriptor.Id}' has an invalid version.");
        if (!Enum.IsDefined(descriptor.Boundary))
            throw new RouteConfigurationException($"Output hook '{descriptor.Id}' uses an unknown boundary.");
        if (descriptor.Before.IsDefault || descriptor.After.IsDefault)
            throw new RouteConfigurationException($"Output hook '{descriptor.Id}' has an uninitialized dependency collection.");
        if (descriptor.Before.Length != descriptor.Before.Distinct(StringComparer.Ordinal).Count() ||
            descriptor.After.Length != descriptor.After.Distinct(StringComparer.Ordinal).Count())
            throw new RouteConfigurationException($"Output hook '{descriptor.Id}' repeats a dependency.");
        if (descriptor.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > options.MaximumHookTimeout))
            throw new RouteConfigurationException($"Output hook '{descriptor.Id}' timeout must be positive and no greater than {options.MaximumHookTimeout}.");
    }

    private static void ValidateDependencies<T>(OutputHookDescriptor descriptor, ImmutableArray<string> dependencies,
        string relationship, IReadOnlyDictionary<string, T> hooks, Func<T, OutputHookDescriptor> descriptorFor)
    {
        foreach (var dependency in dependencies)
        {
            if (string.IsNullOrEmpty(dependency) || !ValidHookId().IsMatch(dependency))
                throw new RouteConfigurationException($"Output hook '{descriptor.Id}' has invalid {relationship} dependency '{dependency}'.");
            if (!hooks.TryGetValue(dependency, out var target))
                throw new RouteConfigurationException($"Output hook '{descriptor.Id}' has missing {relationship} dependency '{dependency}'.");
            if (descriptorFor(target).Boundary != descriptor.Boundary)
                throw new RouteConfigurationException($"Output hook '{descriptor.Id}' cannot order {relationship} '{dependency}' across boundaries.");
        }
    }

    private static void ValidateIdentifier(string value, string name, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Contains('\0'))
            throw new ArgumentException($"{name} is required and must contain at most {maximum:N0} non-null characters.", name);
    }

    private static void ValidateOptions(ResponseOutputPipelineOptions options)
    {
        if (options.MaximumHooksPerBoundary is < 0 or > 10_000 || options.MaximumContentParts is < 1 or > CerebralEvent.MaximumContentParts ||
            options.MaximumContentUtf8Bytes < 1 || options.MaximumDiagnostics < 1 || options.MaximumDiagnosticCharacters < 1 ||
            options.MaximumReasonCharacters < 1 || options.DefaultResponseTimeout <= TimeSpan.Zero || options.DefaultObserverTimeout <= TimeSpan.Zero ||
            options.MaximumHookTimeout < options.DefaultResponseTimeout || options.MaximumHookTimeout < options.DefaultObserverTimeout)
            throw new ArgumentException("Invalid response/output pipeline bounds.", nameof(options));
    }

    private static OutputHookIdentity Identity(OutputHookDescriptor descriptor) => new(descriptor.Id, descriptor.Version);
    private static bool IsOrdinary(Exception error) => error is not (OutOfMemoryException or AccessViolationException);
    private static void ObserveLateFault(Task task) => _ = task.ContinueWith(static completed => _ = completed.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static string BoundedExceptionMessage(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length == 0) message = exception.GetType().Name;
        return message[..Math.Min(message.Length, 512)];
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]{0,63}:[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidHookId();
    [GeneratedRegex(@"^[0-9]+(?:\.[0-9]+){0,3}(?:[-+][A-Za-z0-9.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidVersion();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidDiagnosticCode();
}
