using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.HostedProviderProtocol;

internal sealed record RenderedCall(JsonElement GenerationBody, JsonElement CountBody);
internal sealed record PreparedCall(string CallId, string Fingerprint, string RenderedFingerprint,
    string Format, int OutputTokens, JsonElement Model, RenderedCall Rendered);
internal sealed class StreamResult
{
    internal string Outcome = "unknown";
    internal string? FailureCode = "uncertainOutcome", ResponseId, ActualModel;
    internal bool Confirmed;
    internal int? InputTokens { get; set; }
    internal int? OutputTokens { get; set; }
    internal int? ReasoningTokens { get; set; }
    internal int? CacheReadTokens { get; set; }
    internal int? CacheWriteTokens { get; set; }
    internal object Usage => new { inputTokens = InputTokens, totalOutputTokens = OutputTokens,
        visibleOutputTokens = (int?)null, reasoningTokens = ReasoningTokens,
        cacheReadInputTokens = CacheReadTokens, cacheWriteInputTokens = CacheWriteTokens };
}

internal sealed class VisibleOutput(int maximumBytes, string callId, Action<JsonElement>? sink)
{
    private readonly StringBuilder text = new();
    private int bytes, sequence;
    private bool previewDisabled;
    internal string Text => text.ToString();
    internal void Append(string delta)
    {
        var additional = ProviderJson.Utf8.GetByteCount(delta);
        if (additional > maximumBytes - bytes) throw new ProviderFailure("outputLimit");
        bytes += additional; text.Append(delta);
        if (sink is null || previewDisabled || delta.Length == 0) return;
        // A private best-effort sink cannot invalidate the authoritative awaited terminal.
        var fragment = new StringBuilder(); var size = 0;
        foreach (var rune in delta.EnumerateRunes())
        {
            if (size + rune.Utf8SequenceLength > 4096) { Emit(fragment.ToString()); fragment.Clear(); size = 0; }
            fragment.Append(rune.ToString()); size += rune.Utf8SequenceLength;
        }
        if (fragment.Length > 0) Emit(fragment.ToString());
    }
    private void Emit(string value)
    {
        if (previewDisabled) return;
        try { sink!(JsonSerializer.SerializeToElement(new { schemaVersion = 1, callId, sequence = checked(++sequence), text = value })); }
        catch { previewDisabled = true; }
    }
}

/// <summary>Source-linked protocol mechanics. Each vendor owns rendering and event interpretation.</summary>
internal abstract class HostedProviderRuntime : IServiceCommandExtension, IDisposable
{
    private readonly ProviderProfile? profile;
    private readonly ProviderDependencies? dependencies;
    private readonly HttpClient? client;
    private readonly SemaphoreSlim slots;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object lifecycleGate = new();
    private int activeCalls;
    private bool disposed, cleaned;
    protected HostedProviderRuntime(ProviderProfile? profile, ProviderDependencies? dependencies)
    {
        this.profile = profile; this.dependencies = dependencies;
        slots = new(profile?.ConfiguredMaxConcurrentRequests ?? 1);
        if (dependencies is not null)
        {
            if (dependencies.Handler is HttpClientHandler http)
            { http.AllowAutoRedirect = false; http.UseCookies = false; http.UseDefaultCredentials = false; }
            if (dependencies.Handler is SocketsHttpHandler sockets)
            { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; sockets.Credentials = null; }
            client = new(dependencies.Handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
        }
    }
    protected abstract string PackageId { get; }
    protected abstract string ProviderId { get; }
    protected abstract string Label { get; }
    protected abstract string ProtocolId { get; }
    protected abstract string CountQuality { get; }
    protected abstract Uri CountUri { get; }
    protected abstract Uri GenerateUri { get; }
    protected abstract RenderedCall Render(JsonElement request, string modelKey);
    protected abstract void Authenticate(HttpRequestMessage message, string credential);
    protected abstract int ParseCount(JsonElement response);
    protected abstract Task ReadStream(Stream stream, VisibleOutput output, StreamResult result, CancellationToken token);
    public IReadOnlyList<string> Operations => Array.AsReadOnly(new[] { "describe", "count", "generate", "reconcile" }
        .Select(x => PackageId + ".model-provider.v1." + x).ToArray());

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            activeCalls++;
        }
        try
        {
            using (linked)
            {
                // Covers response bodies too; HttpClient.Timeout ends once response headers have arrived.
                linked.CancelAfter(TimeSpan.FromMinutes(5));
                var prefix = PackageId + ".model-provider.v1.";
                if (!operation.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Unknown provider operation.");
                // Clone freezes the caller-owned JsonDocument lifetime and request bytes before awaiting.
                var value = arguments.Clone();
                return operation[prefix.Length..] switch
                {
                    "describe" => Describe(value),
                    "count" => await Count(value, linked.Token),
                    "generate" => await Generate(value, linked.Token),
                    "reconcile" => Reconcile(value),
                    _ => throw new ArgumentException("Unknown provider operation.")
                };
            }
        }
        finally { lock (lifecycleGate) { activeCalls--; Cleanup(); } }
    }
    private JsonElement Describe(JsonElement value)
    {
        ProviderJson.Validate(value, 8192); ProviderJson.Fields(value, "schemaVersion", "configurationRef"); ValueContract.Version(value);
        var config = value.GetProperty("configurationRef");
        if (config.ValueKind != JsonValueKind.Null)
        {
            ValueContract.Configuration(config);
            if (profile is null || config.GetProperty("id").GetString() != profile.Id || config.GetProperty("revision").GetInt32() != profile.Revision)
                throw new InvalidDataException("The provider configuration revision is unavailable.");
        }
        var configured = profile?.CredentialReferenceId is not null && dependencies is not null;
        var result = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, providerId = ProviderId, label = Label,
            configurationRef = profile is null ? null : new { id = profile.Id, revision = profile.Revision },
            state = profile is null ? "unconfigured" : !profile.Enabled ? "disabled" : configured ? "configuredUnverified" : "unconfigured",
            credential = new { state = configured ? "configuredUnverified" : "missing",
                referenceId = profile?.CredentialReferenceId, revision = profile?.CredentialReferenceRevision },
            configuredMaxConcurrentRequests = profile?.ConfiguredMaxConcurrentRequests ?? 1,
            providerQuotaConcurrentRequests = (int?)null,
            models = profile?.Models.Select(x => x.Declaration).ToArray() ?? [],
            bindingPayload = profile is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>
            { ["credentialReferenceId"] = profile.CredentialReferenceId, ["credentialReferenceRevision"] = profile.CredentialReferenceRevision },
            supportsRemoteReconcile = false, supportsPrivateFragments = dependencies?.PrivateFragmentSink is not null
        });
        ProviderJson.Validate(result, 65_536); return result;
    }
    private JsonElement Binding(JsonElement binding)
    {
        ProviderJson.Fields(binding, "schemaVersion", "kind", "package", "providerId", "modelKey", "configurationRef", "modelDeclarationRevision", "payload");
        ValueContract.Version(binding); _ = ValueContract.Enum(binding, "kind", "hostedProvider");
        var package = binding.GetProperty("package");
        ProviderJson.Fields(package, "id", "version", "generation");
        if (ValueContract.Text(package, "id", 100) != PackageId || ValueContract.Text(package, "version", 64) != "1.0.0")
            throw new ProviderFailure("packageUnavailable");
        _ = ValueContract.Text(package, "generation", 128); // Actual loaded generation is stamped/admitted by the model host.
        if (ValueContract.Text(binding, "providerId", 128) != ProviderId) throw new ProviderFailure("packageUnavailable");
        var config = binding.GetProperty("configurationRef"); ValueContract.Configuration(config);
        if (profile is null || dependencies is null || !profile.Enabled || profile.CredentialReferenceId is null)
            throw new ProviderFailure("configurationMissing");
        if (config.GetProperty("id").GetString() != profile.Id || config.GetProperty("revision").GetInt32() != profile.Revision)
            throw new ProviderFailure("configurationStale");
        var payload = binding.GetProperty("payload"); ProviderJson.Validate(payload, 4096);
        ProviderJson.Fields(payload, "credentialReferenceId", "credentialReferenceRevision");
        if (ValueContract.Text(payload, "credentialReferenceId", 128) != profile.CredentialReferenceId ||
            ValueContract.Integer(payload, "credentialReferenceRevision", 1, int.MaxValue) != profile.CredentialReferenceRevision)
            throw new ProviderFailure("configurationStale");
        var key = ValueContract.Text(binding, "modelKey", 128);
        var model = profile.Models.SingleOrDefault(x => x.Declaration.GetProperty("key").GetString() == key)?.Declaration
            ?? throw new ProviderFailure("capabilityUnsupported");
        if (ValueContract.NullableText(binding, "modelDeclarationRevision", 128) != model.GetProperty("revision").GetString())
            throw new ProviderFailure("configurationStale");
        return model;
    }
    private PreparedCall Prepare(JsonElement value, bool generation)
    {
        ProviderJson.Validate(value, 1_200_000);
        ProviderJson.Fields(value, generation
            ? ["schemaVersion", "callId", "binding", "request", "requestFingerprint", "countProof", "maximumOutputUtf8Bytes", "fragmentMode"]
            : ["schemaVersion", "callId", "binding", "request", "requestFingerprint"]);
        ValueContract.Version(value);
        var callId = ValueContract.Hex(value, "callId", 32);
        var fingerprint = ValueContract.Hex(value, "requestFingerprint", 64);
        var model = Binding(value.GetProperty("binding"));
        var request = value.GetProperty("request"); ProviderJson.Validate(request, 1_048_576); ProviderJson.Fields(request, "messages", "output");
        var messages = request.GetProperty("messages");
        if (messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() is < 1 or > 1000) throw new InvalidDataException("The message list is invalid.");
        foreach (var message in messages.EnumerateArray())
        {
            ProviderJson.Fields(message, "role", "text"); _ = ValueContract.Enum(message, "role", "system", "user", "assistant");
            if (message.GetProperty("text").ValueKind != JsonValueKind.String) throw new InvalidDataException("A message text value is required.");
        }
        var output = request.GetProperty("output"); ProviderJson.Fields(output, "format", "schema", "maximumTotalOutputTokens", "temperature", "reasoning");
        var format = ValueContract.Enum(output, "format", "text", "objectJsonSchema", "jsonStringCarrier");
        if (!model.GetProperty("responseFormats").EnumerateArray().Any(x => x.GetString() == format)) throw new ProviderFailure("capabilityUnsupported");
        var schema = output.GetProperty("schema");
        if (format == "text") { if (schema.ValueKind != JsonValueKind.Null) throw new ProviderFailure("schemaUnsupported"); }
        else
        {
            if (schema.ValueKind != JsonValueKind.Object) throw new ProviderFailure("schemaUnsupported");
            ProviderJson.Validate(schema, 65_536);
            if (format == "objectJsonSchema") ObjectSchema.Validate(schema);
        }
        var outputTokens = ValueContract.Integer(output, "maximumTotalOutputTokens", 1, 131_072);
        if (model.GetProperty("declaredMaximumOutputTokens").ValueKind != JsonValueKind.Null &&
            outputTokens > model.GetProperty("declaredMaximumOutputTokens").GetInt32()) throw new ProviderFailure("outputLimit");
        var temperature = output.GetProperty("temperature");
        if (temperature.ValueKind != JsonValueKind.Null &&
            (temperature.ValueKind != JsonValueKind.Number || !temperature.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0 || number > 2))
            throw new InvalidDataException("The temperature is invalid.");
        if (temperature.ValueKind != JsonValueKind.Null && !ValueContract.Flag(model, "supportsTemperature")) throw new ProviderFailure("capabilityUnsupported");
        var reasoning = output.GetProperty("reasoning"); ProviderJson.Fields(reasoning, "mode", "effort");
        var mode = ValueContract.Enum(reasoning, "mode", "default", "off", "effort");
        var effort = ValueContract.NullableText(reasoning, "effort", 32);
        if (mode != "effort" && effort is not null || mode == "effort" && effort is null) throw new InvalidDataException("The reasoning option is invalid.");
        if (mode == "off" && !ValueContract.Flag(model, "supportsReasoningOff") ||
            mode == "effort" && !model.GetProperty("effortValues").EnumerateArray().Any(x => x.GetString() == effort))
            throw new ProviderFailure("capabilityUnsupported");
        if (ProviderJson.PortableFingerprint(value.GetProperty("binding"), request) != fingerprint) throw new ProviderFailure("configurationStale");
        if (model.GetProperty("countQuality").GetString() != CountQuality) throw new ProviderFailure("countUnavailable");
        var rendered = Render(request, value.GetProperty("binding").GetProperty("modelKey").GetString()!);
        ProviderJson.Validate(rendered.GenerationBody, 1_048_576); ProviderJson.Validate(rendered.CountBody, 1_048_576);
        var renderedFingerprint = ProviderJson.RenderedFingerprint(ProtocolId, rendered.GenerationBody);
        if (generation)
        {
            var proof = value.GetProperty("countProof");
            ProviderJson.Fields(proof, "requestFingerprint", "providerRequestFingerprint", "quality", "inputTokens");
            if (ValueContract.Hex(proof, "requestFingerprint", 64) != fingerprint ||
                ValueContract.Hex(proof, "providerRequestFingerprint", 64) != renderedFingerprint ||
                ValueContract.Enum(proof, "quality", "providerReported", "providerEstimate") != CountQuality)
                throw new ProviderFailure("configurationStale");
            var input = ValueContract.Integer(proof, "inputTokens", 0, int.MaxValue);
            if (model.GetProperty("declaredContextTokens").ValueKind != JsonValueKind.Null &&
                (long)input + outputTokens > model.GetProperty("declaredContextTokens").GetInt32()) throw new ProviderFailure("contextLimit");
            _ = ValueContract.Integer(value, "maximumOutputUtf8Bytes", 1, 98_304);
            _ = ValueContract.Enum(value, "fragmentMode", "none", "private");
        }
        return new(callId, fingerprint, renderedFingerprint, format, outputTokens, model, rendered);
    }
    private async Task<string> Credential(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var secret = await dependencies!.ResolveCredential(profile!.CredentialReferenceId!, profile.CredentialReferenceRevision!.Value, token);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(secret) || secret.Length > 4096 || secret.Any(char.IsControl)) throw new ProviderFailure("configurationMissing");
        return secret;
    }
    private async Task<JsonElement> Count(JsonElement value, CancellationToken token)
    {
        var callId = SafeIdentity(value, "callId", 32); var fingerprint = SafeIdentity(value, "requestFingerprint", 64);
        string? rendered = null; string? failure = null; int? count = null;
        var entered = false;
        try
        {
            var prepared = Prepare(value, false); rendered = prepared.RenderedFingerprint;
            if (!await slots.WaitAsync(0, token)) throw new ProviderFailure("rateLimited");
            entered = true;
            using var message = Request(CountUri, prepared.Rendered.CountBody, await Credential(token));
            token.ThrowIfCancellationRequested();
            using var response = await client!.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw new ProviderFailure(HttpFailure(response.StatusCode));
            CheckJsonMediaType(response);
            var data = await BoundedBody(response.Content, 8192, token);
            count = ParseCount(ProviderJson.Parse(ProviderJson.Utf8.GetString(data)));
        }
        catch (ProviderFailure e) { failure = e.Code; }
        catch (OperationCanceledException) { failure = "countUnavailable"; }
        catch (Exception e) when (ProtocolException(e)) { failure = "protocolMalformed"; }
        catch (HttpRequestException) { failure = "providerUnavailable"; }
        catch (Exception) { failure = "providerUnavailable"; } // Resolver/handler diagnostics must not disclose private values.
        finally { if (entered) slots.Release(); }
        return JsonSerializer.SerializeToElement(new { schemaVersion = 1, callId, requestFingerprint = fingerprint,
            providerRequestFingerprint = rendered, quality = count is null ? "unavailable" : CountQuality, inputTokens = count, failureCode = failure });
    }
    private async Task<JsonElement> Generate(JsonElement value, CancellationToken token)
    {
        var callId = SafeIdentity(value, "callId", 32); var fingerprint = SafeIdentity(value, "requestFingerprint", 64);
        string? rendered = null, requestId = null; var format = "text"; var sent = false; var entered = false;
        var result = new StreamResult(); VisibleOutput? visible = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (value.TryGetProperty("request", out var requested) && requested.TryGetProperty("output", out var requestedOutput) &&
                requestedOutput.TryGetProperty("format", out var requestedFormat) && requestedFormat.ValueKind == JsonValueKind.String &&
                requestedFormat.GetString() is "text" or "objectJsonSchema" or "jsonStringCarrier") format = requestedFormat.GetString()!;
            var prepared = Prepare(value, true); rendered = prepared.RenderedFingerprint; format = prepared.Format;
            // Structured values remain private until complete decode. Phase1 has no engine public projection.
            var sink = format == "text" && value.GetProperty("fragmentMode").GetString() == "private" ? dependencies!.PrivateFragmentSink : null;
            var max = value.GetProperty("maximumOutputUtf8Bytes").GetInt32();
            visible = new(format == "jsonStringCarrier" ? Math.Min(786_432, max * 8 + 1024) : max, callId, sink);
            if (!await slots.WaitAsync(0, token)) throw new ProviderFailure("rateLimited");
            entered = true;
            using var message = Request(GenerateUri, prepared.Rendered.GenerationBody, await Credential(token));
            token.ThrowIfCancellationRequested();
            sent = true; // SendAsync invoked: a transport exception can no longer prove no provider effect.
            using var response = await client!.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            requestId = HeaderId(response, "request-id") ?? HeaderId(response, "x-request-id");
            if (!response.IsSuccessStatusCode)
            {
                result.FailureCode = HttpFailure(response.StatusCode);
                // Redirects and server errors do not prove the model's terminal state.
                if ((int)response.StatusCode is >= 400 and < 500)
                { result.Outcome = "failed"; result.Confirmed = true; }
            }
            else
            {
                if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw new InvalidDataException("SSE media type is required.");
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                await ReadStream(stream, visible, result, token);
                if (result.Outcome == "completed" && format == "objectJsonSchema" &&
                    ProviderJson.Parse(visible.Text).ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("The completed structured value must be an object.");
                if (result.Outcome == "completed" && format == "jsonStringCarrier")
                {
                    var decoded = JsonStringCarrier.Decode(visible.Text, max);
                    visible = new(max, callId, null); visible.Append(decoded);
                }
            }
        }
        catch (ProviderFailure e)
        { result.FailureCode = e.Code; result.Outcome = sent ? "unknown" : "failed"; result.Confirmed = !sent; }
        catch (OperationCanceledException)
        { result.FailureCode = sent ? "uncertainOutcome" : "cancelledBeforeDispatch"; result.Outcome = sent ? "unknown" : "cancelled"; result.Confirmed = !sent; }
        catch (Exception e) when (ProtocolException(e))
        { result.FailureCode = "protocolMalformed"; result.Outcome = sent ? "unknown" : "failed"; result.Confirmed = !sent; }
        catch (HttpRequestException)
        { result.FailureCode = sent ? "uncertainOutcome" : "providerUnavailable"; result.Outcome = sent ? "unknown" : "failed"; result.Confirmed = !sent; }
        catch (Exception)
        { result.FailureCode = sent ? "uncertainOutcome" : "providerUnavailable"; result.Outcome = sent ? "unknown" : "failed"; result.Confirmed = !sent; }
        finally { if (entered) slots.Release(); }
        // Carrier partials are not decoded values. Only a confirmed completed carrier can be consumed.
        var text = format == "jsonStringCarrier" && result.Outcome != "completed" ? "" : visible?.Text ?? "";
        var guarantee = result.Outcome == "completed" ? format switch
        { "objectJsonSchema" => "supportedObjectSchema", "jsonStringCarrier" => "carrierOnly", _ => "none" } : "none";
        var terminal = Terminal(callId, fingerprint, rendered, format, sent, result, text, guarantee, requestId);
        if (ProviderJson.Utf8.GetByteCount(terminal.GetRawText()) > 131_072)
        {
            result.Outcome = sent ? "unknown" : "failed"; result.FailureCode = "outputLimit";
            terminal = Terminal(callId, fingerprint, rendered, format, sent, result, "", "none", requestId);
        }
        return terminal;
    }
    private static JsonElement Terminal(string callId, string fingerprint, string? rendered, string format, bool sent,
        StreamResult result, string text, string guarantee, string? requestId)
        => JsonSerializer.SerializeToElement(new { schemaVersion = 1, callId, requestFingerprint = fingerprint,
            providerRequestFingerprint = rendered, outcome = result.Outcome, requestSent = sent, terminalConfirmed = result.Confirmed,
            cancellationConfirmed = !sent && result.Outcome == "cancelled", text, schemaGuarantee = guarantee, usage = result.Usage,
            receipt = new { requestId, responseId = result.ResponseId, actualModel = result.ActualModel },
            failureCode = result.FailureCode, outputFormat = format });
    private JsonElement Reconcile(JsonElement value)
    {
        ProviderJson.Validate(value, 140_000); ProviderJson.Fields(value, "schemaVersion", "callId", "binding", "previousCallId", "previousTerminal");
        ValueContract.Version(value); var id = ValueContract.Hex(value, "callId", 32); _ = ValueContract.Hex(value, "previousCallId", 32);
        var previous = value.GetProperty("previousTerminal");
        ValueContract.Terminal(previous); var oldId = ValueContract.Hex(previous, "callId", 32);
        if (oldId != value.GetProperty("previousCallId").GetString()) throw new InvalidDataException("The previous call identity does not match.");
        var fingerprint = ValueContract.Hex(previous, "requestFingerprint", 64);
        var format = ValueContract.Enum(previous, "outputFormat", "text", "objectJsonSchema", "jsonStringCarrier");
        _ = Binding(value.GetProperty("binding"));
        // Synchronous/store:false APIs cannot recover an uncertain remote terminal. Never resubmit.
        return Terminal(id, fingerprint, null, format, ValueContract.Flag(previous, "requestSent"), new StreamResult(), "", "none", null);
    }
    private HttpRequestMessage Request(Uri uri, JsonElement body, string secret)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, uri)
        { Content = new ByteArrayContent(ProviderJson.Canonical(body)) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(uri == GenerateUri ? "text/event-stream" : "application/json"));
        try { Authenticate(message, secret); return message; } catch { message.Dispose(); throw; }
    }
    private static string SafeIdentity(JsonElement value, string name, int length)
        => ValueContract.Hex(value, name, length); // Malformed envelope is rejected before any HTTP call.
    private static bool ProtocolException(Exception e) => e is JsonException or InvalidDataException or InvalidOperationException or
        KeyNotFoundException or FormatException or OverflowException or DecoderFallbackException or IOException or ArgumentException;
    private static string HttpFailure(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests ? "rateLimited" : "providerUnavailable";
    private static void CheckJsonMediaType(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json") throw new InvalidDataException("JSON media type is required.");
    }
    private static string? HeaderId(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;
        var all = values.Take(2).ToArray();
        return all.Length == 1 && all[0].Length is >= 1 and <= 256 && !all[0].Any(char.IsControl) ? all[0] : null;
    }
    private static async Task<byte[]> BoundedBody(HttpContent content, int max, CancellationToken token)
    {
        if (content.Headers.ContentLength > max) throw new InvalidDataException("The provider body exceeds its byte bound.");
        await using var stream = await content.ReadAsStreamAsync(token);
        using var result = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token); if (read == 0) return result.ToArray();
            if (read > max - result.Length) throw new InvalidDataException("The provider body exceeds its byte bound.");
            result.Write(buffer, 0, read);
        }
    }
    internal static int? Counter(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var counter) || counter.ValueKind == JsonValueKind.Null) return null;
        if (counter.ValueKind != JsonValueKind.Number || !counter.TryGetInt32(out var number) || number < 0)
            throw new InvalidDataException("The provider usage counter is invalid.");
        return number;
    }
    internal static string? ReceiptText(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: >= 1 and <= 256 } text || text.Any(char.IsControl))
            throw new InvalidDataException("The provider receipt identity is invalid.");
        return text;
    }
    private void Cleanup()
    {
        if (!disposed || activeCalls != 0 || cleaned) return;
        cleaned = true; client?.Dispose(); slots.Dispose(); lifetime.Dispose();
        // The injected handler and private sink are caller-owned; no other component state is disposed.
    }
    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed) return;
            disposed = true;
            // Completion cannot destroy these sources until cancellation has finished.
            activeCalls++;
        }
        try { lifetime.Cancel(); }
        finally { lock (lifecycleGate) { activeCalls--; Cleanup(); } }
    }
}
