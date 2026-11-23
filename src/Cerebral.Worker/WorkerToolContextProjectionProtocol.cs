using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Context;

namespace Cerebral.Worker;

// Pure wire/normalization mechanics. Callers separately prove accepted package ownership,
// current grants, exact durable intents/receipts and the authoritative consumer ledger.
internal static class WorkerToolContextProjectionProtocol
{
    internal const string CommandId = "cerebral.host:tool-context-project";
    internal const int CommandVersion = 1;
    private const int MaximumDepth = 64;
    private const int MaximumNodes = 65_536;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = MaximumDepth
    };

    internal static WorkflowToolContextProjectionCommandRequest ReadCommandRequest(JsonElement value)
    {
        var result = Read<WorkflowToolContextProjectionCommandRequest>(value,
            WorkflowToolContextProjectionContract.MaximumCommandRequestUtf8Bytes);
        Version(result.SchemaVersion);
        Id(result.ContributionId); Id(result.ToolEffectId);
        OptionalId(result.PreviousProjectionEffectId);
        Slot(result.Slot);
        Revisions(result.ContextRevision, result.ExclusionRevision);
        State(result.PriorState);
        if (result.PreviousProjectionEffectId == result.ToolEffectId)
            throw Invalid("A source tool cannot be the previous projection.");
        return result with { Slot = result.Slot with { }, PriorState = Clone(result.PriorState) };
    }

    // This validates the closed wrapper, not its origin or its outer WorkflowCommandResult.State.
    // Historical callers must also use ValidateResult with their independently proven coordinates.
    internal static WorkflowToolContextProjectionResult ReadResult(JsonElement value)
    {
        var result = Read<WorkflowToolContextProjectionResult>(value,
            WorkflowToolContextProjectionContract.MaximumCommandResultUtf8Bytes);
        Version(result.SchemaVersion);
        Link(result.Link);
        Outcome(result.Disposition, result.NextState, result.Reason,
            result.ProviderResult is not null, result.SlotUpdate is not null, normalized: true);
        ContextProviderResult? published = null;
        if (result.ProviderResult is { } provider)
        {
            Wire(value.GetProperty("providerResult"),
                WorkflowToolContextProjectionContract.MaximumPublicationUtf8Bytes, requireObject: true);
            if (provider.Window is null || provider.Window.Checkpoint is not null ||
                provider.Window.Contributions is not { Count: 1 } ||
                result.SlotUpdate is not { } update)
                throw Invalid("Publication requires exactly one own slot and no checkpoint.");
            Id(provider.ProviderId);
            if (provider.ProviderVersion < 1) throw Invalid("Invalid provider version.");
            var source = provider.Window.Contributions[0];
            if (source is null || update.Id != result.Link.Slot.Id ||
                update.SourceRevision != NextRevision(result.Link.Slot) ||
                source.Id != update.Id || source.SourceRevision != update.SourceRevision ||
                source.Owner != provider.ProviderId || string.IsNullOrWhiteSpace(source.ExactReadHandle))
                throw Invalid("Published source and own-slot update disagree.");
            ContextValidation.ValidateWindow(provider.Window);
            Bound(provider, WorkflowToolContextProjectionContract.MaximumPublicationUtf8Bytes);
            published = FreezeProviderResult(provider);
        }
        return result with
        {
            Link = CopyLink(result.Link), NextState = Clone(result.NextState),
            SlotUpdate = result.SlotUpdate is { } slot ? slot with { } : null,
            ProviderResult = published
        };
    }

    internal static ContextProviderRequest DerivePublicationRequest(ContextProviderRequest admittedTemplate,
        WorkflowToolContextProjectionCommandRequest command)
    {
        ArgumentNullException.ThrowIfNull(admittedTemplate);
        command = ReadCommandRequest(Element(command,
            WorkflowToolContextProjectionContract.MaximumCommandRequestUtf8Bytes));
        var template = FreezeProviderRequest(admittedTemplate);
        if (template.References is not { Count: 1 } ||
            template.References[0].Id != command.Slot.Id ||
            template.References[0].ExpectedRevision is not null)
            throw Invalid("Publication must use the admitted fixed own-slot template.");
        return template with
        {
            ContextRevision = command.ContextRevision,
            ExclusionRevision = command.ExclusionRevision,
            References = Array.AsReadOnly(new[]
            {
                template.References[0] with { ExpectedRevision = NextRevision(command.Slot) }
            })
        };
    }

    internal static WorkflowToolContextProjectionRequest FreezeRequest(WorkflowToolContextProjectionRequest value)
    {
        var request = Read<WorkflowToolContextProjectionRequest>(Element(value,
            WorkflowToolContextProjectionContract.MaximumCallbackInputUtf8Bytes),
            WorkflowToolContextProjectionContract.MaximumCallbackInputUtf8Bytes);
        Version(request.SchemaVersion);
        Link(request.Link);
        SavedEffect(request.SavedEffect);
        Id(request.ToolName);
        State(request.PriorState);
        if (request.Facts is null || request.Facts.Accepted is null ||
            request.Facts.Accepted.Identity is not { } identity || request.Selection is null ||
            identity.RequestId != request.Link.RunId ||
            request.SavedEffect.RunId != request.Link.RunId ||
            request.SavedEffect.EffectId != request.Link.ToolEffectId ||
            request.Link.SavedEffectSha256 != SavedEffectDigest(request.SavedEffect))
            throw Invalid("Callback facts, source receipt and link disagree.");
        if (request.SavedEffect.CommandId != WorkerWorkflowEffects.ToolCommand ||
            request.SavedEffect.Arguments.ValueKind != JsonValueKind.Object ||
            !request.SavedEffect.Arguments.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String || name.GetString() != request.ToolName)
            throw Invalid("Callback semantic tool identity differs from the saved tool intent.");
        State(request.Selection.Payload);
        var publication = FreezeProviderRequest(request.PublicationRequest);
        PublicationRequest(publication, request.Link);
        if (request.Facts.ContextRevision != publication.ContextRevision ||
            request.Facts.ExclusionRevision != publication.ExclusionRevision)
            throw Invalid("Callback context revisions disagree.");
        var selection = request.Selection with
        {
            Payload = Clone(request.Selection.Payload),
            Request = request.Selection.Request is { } accepted ? FreezeProviderRequest(accepted) : null
        };
        return request with
        {
            Link = CopyLink(request.Link), PriorState = Clone(request.PriorState),
            Selection = selection, PublicationRequest = publication,
            SavedEffect = request.SavedEffect with
            {
                Arguments = request.SavedEffect.Arguments.Clone(),
                Data = Clone(request.SavedEffect.Data)
            }
        };
    }

    internal static WorkflowToolContextProjectionResolution ValidateRaw(
        WorkflowToolContextProjectionRequest request, WorkflowToolContextProjectionResolution resolution)
    {
        request = FreezeRequest(request);
        return Raw(request, resolution);
    }

    private static WorkflowToolContextProjectionResolution Raw(
        WorkflowToolContextProjectionRequest request, WorkflowToolContextProjectionResolution value)
    {
        var result = Read<WorkflowToolContextProjectionResolution>(Element(value,
            WorkflowToolContextProjectionContract.MaximumCommandResultUtf8Bytes),
            WorkflowToolContextProjectionContract.MaximumCommandResultUtf8Bytes);
        Version(result.SchemaVersion);
        Link(result.Link);
        SameLink(request.Link, result.Link);
        Outcome(result.Disposition, result.NextState, result.Reason,
            result.Resolution is not null, hasSlot: false, normalized: false);
        ContextProviderResolution? publication = null;
        if (result.Resolution is { } raw)
        {
            if (raw.Sources is not { Count: 1 } || raw.Sources[0] is null ||
                raw.ProviderId != request.PublicationRequest.ProviderId ||
                raw.ProviderVersion != request.PublicationRequest.ProviderVersion)
                throw Invalid("Raw publication must name the exact requested provider and own slot.");
            var expected = request.PublicationRequest.References[0];
            var source = raw.Sources[0];
            if (source.Id != expected.Id || source.Kind != expected.Kind ||
                source.SourceRevision != expected.ExpectedRevision ||
                source.Requirement != expected.Requirement || source.Priority != expected.Priority ||
                source.BeforeCurrentInput != expected.BeforeCurrentInput)
                throw Invalid("Raw publication changed the admitted own-slot policy or revision.");
            Bound(raw, Math.Min(WorkflowToolContextProjectionContract.MaximumPublicationUtf8Bytes,
                request.PublicationRequest.MaximumUtf8Bytes));
            ContextValidation.ValidateWindow(new(request.PublicationRequest.ContextRevision,
                request.PublicationRequest.ExclusionRevision,
                [new(source.Id, raw.ProviderId, source.Kind, source.SourceRevision,
                    source.Requirement, source.Messages, Complete: source.Complete,
                    Priority: source.Priority, BeforeCurrentInput: source.BeforeCurrentInput)]));
            publication = raw with
            {
                Sources = Array.AsReadOnly(new[] { source with { Messages = FreezeMessages(source.Messages) } })
            };
        }
        return result with
        {
            Link = CopyLink(result.Link), NextState = Clone(result.NextState), Resolution = publication
        };
    }

    internal static WorkflowToolContextProjectionResult MakeResult(ContextProviderDescriptor admittedProvider,
        WorkflowToolContextProjectionRequest request, WorkflowToolContextProjectionResolution resolution,
        string projectionReceiptId)
    {
        request = FreezeRequest(request);
        var raw = Raw(request, resolution);
        var publication = CheckedPublicationRequest(admittedProvider, request.PublicationRequest, request.Link);
        RequireReceipt(request.Link.ProjectionEffectId, projectionReceiptId);
        ContextProviderResult? normalized = null;
        WorkflowToolContextSlotUpdate? update = null;
        if (raw.Resolution is { } sources)
        {
            normalized = ContextProviderNormalizer.Normalize(admittedProvider, publication, sources,
                request.Link.ProjectionEffectId, projectionReceiptId);
            Bound(normalized, Math.Min(WorkflowToolContextProjectionContract.MaximumPublicationUtf8Bytes,
                Math.Min(admittedProvider.MaxUtf8Bytes, publication.MaximumUtf8Bytes)));
            update = new(request.Link.Slot.Id, NextRevision(request.Link.Slot));
        }
        return ReadResult(Element(new WorkflowToolContextProjectionResult(
            WorkflowToolContextProjectionContract.SchemaVersion, CopyLink(request.Link), raw.Disposition,
            Clone(raw.NextState), update, normalized, raw.Reason),
            WorkflowToolContextProjectionContract.MaximumCommandResultUtf8Bytes));
    }

    // Pure historical proof: callers supply the accepted descriptor, derived request and proven link.
    // Reconstructing raw sources and re-normalizing verifies every host-minted handle and policy field.
    internal static WorkflowToolContextProjectionResult ValidateResult(ContextProviderDescriptor admittedProvider,
        ContextProviderRequest exactDerivedRequest, WorkflowToolContextProjectionLink expectedLink,
        WorkflowToolContextProjectionResult value, string projectionReceiptId)
    {
        Link(expectedLink);
        var result = ReadResult(Element(value,
            WorkflowToolContextProjectionContract.MaximumCommandResultUtf8Bytes));
        SameLink(expectedLink, result.Link);
        var request = CheckedPublicationRequest(admittedProvider, exactDerivedRequest, expectedLink);
        RequireReceipt(expectedLink.ProjectionEffectId, projectionReceiptId);
        if (result.ProviderResult is { } normalized)
        {
            var raw = new ContextProviderResolution(normalized.ProviderId, normalized.ProviderVersion,
                Array.AsReadOnly(normalized.Window.Contributions.Select(source => new ContextProviderSource(
                    source.Id, source.Category, source.SourceRevision, source.Requirement,
                    FreezeMessages(source.Messages), source.Complete, source.Priority,
                    source.BeforeCurrentInput)).ToArray()));
            Bound(raw, Math.Min(WorkflowToolContextProjectionContract.MaximumPublicationUtf8Bytes,
                Math.Min(admittedProvider.MaxUtf8Bytes, request.MaximumUtf8Bytes)));
            var verified = ContextProviderNormalizer.Normalize(admittedProvider, request, raw,
                expectedLink.ProjectionEffectId, projectionReceiptId);
            Bound(verified, Math.Min(WorkflowToolContextProjectionContract.MaximumPublicationUtf8Bytes,
                Math.Min(admittedProvider.MaxUtf8Bytes, request.MaximumUtf8Bytes)));
            if (!JsonElement.DeepEquals(Element(verified,
                    WorkflowToolContextProjectionContract.MaximumPublicationUtf8Bytes),
                Element(normalized, WorkflowToolContextProjectionContract.MaximumPublicationUtf8Bytes)))
                throw Invalid("Published source does not match its exact host-normalization proof.");
        }
        return result;
    }

    internal static string ReceiptId(string projectionEffectId)
    {
        Id(projectionEffectId);
        var receipt = "tool-context:" + projectionEffectId;
        Receipt(receipt);
        return receipt;
    }

    private static void RequireReceipt(string effectId, string receipt)
    {
        if (receipt != ReceiptId(effectId))
            throw Invalid("Projection receipt identity differs from its host-owned effect.");
    }

    internal static string SavedEffectDigest(WorkflowToolContextSavedEffect value)
    {
        SavedEffect(value);
        using var bytes = new LimitedStream(WorkflowToolContextProjectionContract.MaximumCallbackInputUtf8Bytes);
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", value.SchemaVersion);
            writer.WriteString("runId", value.RunId);
            writer.WriteString("effectId", value.EffectId);
            writer.WriteString("commandId", value.CommandId);
            writer.WriteNumber("commandVersion", value.CommandVersion);
            writer.WritePropertyName("arguments"); value.Arguments.WriteTo(writer);
            writer.WriteString("state", value.State switch
            {
                WorkflowEffectState.Succeeded => "Succeeded",
                WorkflowEffectState.Failed => "Failed",
                WorkflowEffectState.Cancelled => "Cancelled",
                _ => throw Invalid("A source receipt must have a known terminal outcome.")
            });
            writer.WritePropertyName("data");
            if (value.Data is { } data) data.WriteTo(writer); else writer.WriteNullValue();
            writer.WritePropertyName("error"); writer.WriteStringValue(value.Error);
            writer.WritePropertyName("providerReceiptId"); writer.WriteStringValue(value.ProviderReceiptId);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }

    private static void SavedEffect(WorkflowToolContextSavedEffect value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Version(value.SchemaVersion);
        Id(value.RunId); Id(value.EffectId); Id(value.CommandId);
        if (value.CommandVersion < 1 || value.State is not (WorkflowEffectState.Succeeded or
            WorkflowEffectState.Failed or WorkflowEffectState.Cancelled) ||
            value.Arguments.ValueKind != JsonValueKind.Object)
            throw Invalid("Invalid saved terminal effect.");
        Wire(value.Arguments, WorkflowContract.MaximumStateBytes, requireObject: true);
        if (value.Data is { } data) Wire(data, WorkflowContract.MaximumStateBytes, requireObject: false);
        Text(value.Error, 8192);
        Text(value.ProviderReceiptId, 512);
    }

    private static ContextProviderRequest CheckedPublicationRequest(ContextProviderDescriptor provider,
        ContextProviderRequest request, WorkflowToolContextProjectionLink link)
    {
        ArgumentNullException.ThrowIfNull(provider);
        request = FreezeProviderRequest(request);
        PublicationRequest(request, link);
        // The already-admitted descriptor stays owned by registration, not this helper.
        return ContextProviderNormalizer.ReadRequest(provider, Element(request,
            WorkflowToolContextProjectionContract.MaximumCommandRequestUtf8Bytes));
    }

    private static void PublicationRequest(ContextProviderRequest request, WorkflowToolContextProjectionLink link)
    {
        Id(request.ProviderId);
        if (request.ProviderVersion < 1 || request.MaximumUtf8Bytes < 1 ||
            request.MaximumUtf8Bytes > ContextProviderContract.MaximumUtf8Bytes ||
            request.References is not { Count: 1 } || request.References[0] is not { } reference ||
            reference.Id != link.Slot.Id || reference.ExpectedRevision != NextRevision(link.Slot))
            throw Invalid("Publication request differs from the expected single own slot.");
        Revisions(request.ContextRevision, request.ExclusionRevision);
        Id(reference.Id); Id(reference.Kind); Id(reference.SourceId);
        if (!Enum.IsDefined(reference.Requirement) || reference.Priority is < 0 or > 1_000_000)
            throw Invalid("Invalid publication reference.");
    }

    private static ContextProviderRequest FreezeProviderRequest(ContextProviderRequest value)
    {
        var request = Read<ContextProviderRequest>(Element(value,
            ContextProviderContract.MaximumUtf8Bytes), ContextProviderContract.MaximumUtf8Bytes);
        return request with
        {
            References = Array.AsReadOnly(request.References.Select(reference =>
                reference is null ? throw Invalid("A context reference is absent.") : reference with { }).ToArray())
        };
    }

    private static ContextProviderResult FreezeProviderResult(ContextProviderResult value) => value with
    {
        Window = value.Window with
        {
            Contributions = Array.AsReadOnly(value.Window.Contributions.Select(source => source with
                { Messages = FreezeMessages(source.Messages) }).ToArray())
        }
    };

    private static IReadOnlyList<ModelInputMessage> FreezeMessages(IReadOnlyList<ModelInputMessage> messages)
    {
        if (messages is null || messages.Count is < 1 or > 1000)
            throw Invalid("Publication requires bounded messages.");
        return Array.AsReadOnly(messages.Select(message =>
        {
            if (message is null || message.Content is null || message.Content.Count is < 1 or > 256)
                throw Invalid("Publication requires bounded content.");
            return message with { Content = Array.AsReadOnly(message.Content.Select(FreezePart).ToArray()) };
        }).ToArray());
    }

    private static ContentPart FreezePart(ContentPart part) => part switch
    {
        TextContentPart text => text with { },
        StructuredContentPart structured => structured with { Value = structured.Value.Clone() },
        FileContentPart file => file with { Attachment = FreezeAttachment(file.Attachment) },
        ImageContentPart image => image with { Attachment = FreezeAttachment(image.Attachment) },
        AudioContentPart audio => audio with { Attachment = FreezeAttachment(audio.Attachment) },
        ResourceContentPart resource => resource with { Resource = resource.Resource with { } },
        _ => throw Invalid("Unsupported publication content.")
    };

    private static AttachmentReference FreezeAttachment(AttachmentReference value) => value with
    {
        Metadata = value.Metadata is null ? null : new ReadOnlyDictionary<string, string>(
            value.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))
    };

    private static void Outcome(WorkflowToolContextProjectionDisposition disposition, JsonElement? state,
        string? reason, bool hasPublication, bool hasSlot, bool normalized)
    {
        State(state);
        switch (disposition)
        {
            case WorkflowToolContextProjectionDisposition.StateOnly:
                if (hasPublication || hasSlot || reason is not null)
                    throw Invalid("State-only cannot replace or clear a publication.");
                break;
            case WorkflowToolContextProjectionDisposition.Publish:
                if (!hasPublication || normalized != hasSlot || reason is not null)
                    throw Invalid("Publish requires exactly one publication and its host slot update.");
                break;
            case WorkflowToolContextProjectionDisposition.Blocked:
                if (hasPublication || hasSlot || state is not null || string.IsNullOrWhiteSpace(reason))
                    throw Invalid("Blocked cannot adopt state or sources.");
                Text(reason, WorkflowToolContextProjectionContract.MaximumReasonCharacters);
                break;
            default:
                throw Invalid("Projection disposition must be stateOnly, publish or blocked.");
        }
    }

    private static void Link(WorkflowToolContextProjectionLink value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Version(value.SchemaVersion);
        Id(value.ContributionId); Id(value.RunId); Id(value.ProjectionEffectId); Id(value.ToolEffectId);
        OptionalId(value.PreviousProjectionEffectId);
        Slot(value.Slot);
        if (value.SavedEffectSha256 is not { Length: 64 } digest ||
            digest.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            value.ProjectionEffectId == value.ToolEffectId ||
            value.PreviousProjectionEffectId == value.ProjectionEffectId ||
            value.PreviousProjectionEffectId == value.ToolEffectId)
            throw Invalid("Invalid projection linkage or saved-effect digest.");
    }

    private static void SameLink(WorkflowToolContextProjectionLink expected,
        WorkflowToolContextProjectionLink actual)
    {
        if (expected != actual) throw Invalid("Projection did not echo the exact input linkage.");
    }

    private static WorkflowToolContextProjectionLink CopyLink(WorkflowToolContextProjectionLink value) =>
        value with { Slot = value.Slot with { } };

    private static void Slot(WorkflowToolContextSlotExpectation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Id(value.Id);
        if (value.CurrentSourceRevision is < 1) throw Invalid("Invalid own-slot source revision.");
    }

    private static long NextRevision(WorkflowToolContextSlotExpectation slot)
    {
        Slot(slot);
        if (slot.CurrentSourceRevision == long.MaxValue) throw Invalid("Own-slot source revision is exhausted.");
        return slot.CurrentSourceRevision is { } revision ? revision + 1 : 1;
    }

    private static void Revisions(long context, long exclusion)
    {
        if (context < 0 || exclusion < 0) throw Invalid("Context revisions cannot be negative.");
    }

    private static void State(JsonElement? value)
    {
        if (value is { } state)
        {
            if (state.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw Invalid("Use nullable null for absent opaque state.");
            Wire(state, WorkflowToolContextProjectionContract.MaximumProviderStateUtf8Bytes, requireObject: false);
            Bound(state, WorkflowToolContextProjectionContract.MaximumProviderStateUtf8Bytes);
        }
    }

    private static void Receipt(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Invalid("A projection receipt identity is required.");
        Text(value, 512);
        if (Utf8.GetByteCount(value) > 512) throw Invalid("Projection receipt identity exceeds its UTF-8 bound.");
    }

    private static void Version(int version)
    {
        if (version != WorkflowToolContextProjectionContract.SchemaVersion)
            throw Invalid("Unsupported projection schema version.");
    }

    private static void Id(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw Invalid("Invalid bounded projection identity.");
    }

    private static void OptionalId(string? value) { if (value is not null) Id(value); }

    private static void Text(string? value, int maximum)
    {
        if (value is not null && (value.Length > maximum || value.Contains('\0')))
            throw Invalid("Projection metadata exceeds its bound or contains NUL.");
    }

    private static JsonElement? Clone(JsonElement? value) => value is { } element ? element.Clone() : null;

    private static T Read<T>(JsonElement value, int maximum) where T : class
    {
        Wire(value, maximum, requireObject: true);
        try
        {
            var result = value.Deserialize<T>(Json) ?? throw Invalid("Projection value is absent.");
            // Constructor-required/nullability checks plus exact round-trip reject omitted defaulted
            // nested fields, numeric/case-variant enums and unknown or differently cased members.
            if (!JsonElement.DeepEquals(value, Element(result, maximum)))
                throw Invalid("Projection value is not the complete exact closed wire shape.");
            return result;
        }
        catch (JsonException error) { throw Invalid("Projection wire value is invalid.", error); }
    }

    private static void Wire(JsonElement value, int maximum, bool requireObject)
    {
        if (value.ValueKind == JsonValueKind.Undefined ||
            requireObject && value.ValueKind != JsonValueKind.Object)
            throw Invalid("Projection wire value has an invalid kind.");
        var raw = value.GetRawText();
        if (raw.Length > maximum || Utf8.GetByteCount(raw) > maximum)
            throw Invalid("Projection wire value exceeds its UTF-8 byte bound.");
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                MaxDepth = MaximumDepth, AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        }
        catch (JsonException error) { throw Invalid("Projection JSON is not a strict bounded document.", error); }
        var nodes = 0;
        Visit(value, 0, ref nodes);
    }

    private static void Visit(JsonElement value, int depth, ref int nodes)
    {
        if (depth > MaximumDepth || ++nodes > MaximumNodes)
            throw Invalid("Projection JSON exceeds its depth or node bound.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid("Projection JSON contains duplicate member names.");
                Visit(property.Value, depth + 1, ref nodes);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Visit(item, depth + 1, ref nodes);
    }

    private static void Bound<T>(T value, int maximum) => _ = Bytes(value, maximum);

    private static JsonElement Element<T>(T value, int maximum)
    {
        using var document = JsonDocument.Parse(Bytes(value, maximum), new JsonDocumentOptions { MaxDepth = MaximumDepth });
        return document.RootElement.Clone();
    }

    private static byte[] Bytes<T>(T value, int maximum)
    {
        if (maximum < 1) throw Invalid("Invalid projection byte bound.");
        using var stream = new LimitedStream(maximum);
        try { JsonSerializer.Serialize(stream, value, Json); }
        catch (JsonException error) { throw Invalid("Projection value cannot be serialized safely.", error); }
        return stream.ToArray();
    }

    private sealed class LimitedStream(int maximum) : MemoryStream
    {
        private void Check(int count)
        {
            if (count < 0 || Position > maximum - (long)count)
                throw Invalid("Projection serialized value exceeds its UTF-8 byte bound.");
        }
        public override void Write(byte[] buffer, int offset, int count)
        { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value)
        { Check(1); base.WriteByte(value); }
    }

    private static InvalidDataException Invalid(string message, Exception? inner = null) => new(message, inner);
}
