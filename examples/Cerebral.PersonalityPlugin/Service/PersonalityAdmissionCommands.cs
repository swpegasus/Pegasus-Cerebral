using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;

namespace Cerebral.PersonalityServicePlugin;

/// <summary>
/// Package-owned value bridge over the existing seed codec. All identities and persisted values must
/// be authored/extracted by the authenticated host. These commands neither authenticate JSON claims
/// nor supply grants, package bindings, migration readiness, instructions or model tools.
/// </summary>
internal static class PersonalityAdmissionCommands
{
    internal const string CaptureOperation = "personality.admission.capture";
    internal const string RestoreOperation = "personality.admission.restore";
    internal const int Version = 1;
    internal const int MaximumCaptureInputBytes = 8192;
    internal const int MaximumRestoreInputBytes = 16384;
    internal const int MaximumOutputBytes = 8192;

    internal static async Task<object?> ExecuteAsync(PersonalityServicePlugin plugin, string operation,
        JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var restore = operation == RestoreOperation;
            Boundary(arguments, restore ? MaximumRestoreInputBytes : MaximumCaptureInputBytes);
            PersonalityServicePlugin.Fields(arguments, restore
                ? ["schemaVersion", "acceptedTarget", "consumer", "caller", "parentRunId", "seed", "seedSha256", "contextRevision", "exclusionRevision"]
                : ["schemaVersion", "identity", "consumer", "caller", "parentRunId"]);
            if (Number(arguments, "schemaVersion") != Version)
                throw new ArgumentException();
            var submission = Identity(arguments.GetProperty(restore ? "acceptedTarget" : "identity"), arguments, restore);
            var consumer = Consumer(arguments.GetProperty("consumer"));
            if (!restore)
            {
                var captured = await PersonalityAdmissionEngine.CaptureAsync(plugin, submission, consumer, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                JsonElement? seed = captured.Admission?.ToSnapshot();
                return Output(new
                {
                    schemaVersion = Version,
                    status = JsonNamingPolicy.CamelCase.ConvertName(captured.Status.ToString()),
                    reason = captured.Reason, seed,
                    seedSha256 = seed is null ? null : SeedHash(seed.Value)
                });
            }

            // Restore is pure codec work. Lifecycle is checked without opening or reading the store.
            plugin.RequireAdmissionAvailability();
            var preserved = arguments.GetProperty("seed");
            var expectedHash = String(arguments, "seedSha256");
            PersonalityAdmissionEngine.RequireHash(expectedHash);
            var admission = PersonalityAdmission.Restore(preserved, submission, consumer);
            var canonical = admission.ToSnapshot();
            if (SeedHash(canonical) != expectedHash)
                throw new InvalidDataException();
            var binding = admission.BindAccepted(submission, consumer, preserved);
            var request = binding.CreateRequest(submission, consumer,
                Number(arguments, "contextRevision"), Number(arguments, "exclusionRevision"));
            cancellationToken.ThrowIfCancellationRequested();
            return Output(new { schemaVersion = Version, request });
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or
            JsonException or KeyNotFoundException or FormatException or OverflowException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Fixed refusal: accepted-state corruption must not become optional omission or live recapture.
            throw new InvalidDataException("The personality admission command payload is invalid, mismatched or unavailable.");
        }
    }

    private static string SeedHash(JsonElement seed) =>
        PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(seed.GetRawText()));

    private static JsonElement Output(object value)
    {
        var result = PersonalityContract.Element(value);
        Boundary(result, MaximumOutputBytes);
        return result;
    }

    private static void Boundary(JsonElement value, int maximumBytes)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            PersonalityContract.Utf8.GetByteCount(value.GetRawText()) > maximumBytes)
            throw new ArgumentException();
    }

    private static WorkflowSubmission Identity(JsonElement value, JsonElement envelope, bool accepted)
    {
        RequireObject(value);
        PersonalityServicePlugin.Fields(value, accepted
            ? ["requestId", "conversationId", "profileId", "definitionId", "definitionVersion", "projectId", "conversationSequence"]
            : ["requestId", "conversationId", "profileId", "definitionId", "definitionVersion"]);
        var actorValue = envelope.GetProperty("caller");
        EventActor? caller = null;
        if (actorValue.ValueKind != JsonValueKind.Null)
        {
            RequireObject(actorValue);
            PersonalityServicePlugin.Fields(actorValue, ["kind", "id"]);
            caller = new(Kind<EventActorKind>(actorValue, "kind"), NullableIdentifier(actorValue, "id", 256));
        }
        // This skeletal submission is internal codec input only, never a workflow to submit or model context.
        var submission = new WorkflowSubmission(Identifier(value, "requestId", 256),
            Identifier(value, "conversationId", 256), Identifier(value, "profileId", 128),
            Identifier(value, "definitionId", 128), Identifier(value, "definitionVersion", 64),
            string.Empty, null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, Caller: caller)
        {
            ParentRunId = NullableIdentifier(envelope, "parentRunId", 256),
            ProjectId = accepted ? NullableIdentifier(value, "projectId", 128) : null,
            ConversationSequence = accepted ? Number(value, "conversationSequence") : 0
        };
        if (accepted) PersonalityAdmissionEngine.ValidateAcceptedTarget(submission);
        else PersonalityAdmissionEngine.ValidateIdentity(submission);
        return submission;
    }

    private static PersonalityConsumer Consumer(JsonElement value)
    {
        RequireObject(value);
        PersonalityServicePlugin.Fields(value, ["profileId", "role", "kind"]);
        return new(Identifier(value, "profileId", 128), Identifier(value, "role", 64),
            Kind<PersonalityConsumerKind>(value, "kind"));
    }

    private static T Kind<T>(JsonElement value, string name) where T : struct, Enum
    {
        var text = String(value, name);
        foreach (var kind in Enum.GetValues<T>())
            if (text == JsonNamingPolicy.CamelCase.ConvertName(kind.ToString())) return kind;
        throw new ArgumentException();
    }

    private static string Identifier(JsonElement value, string name, int maximumLength)
    {
        var result = String(value, name);
        if (result.Length > maximumLength || result.Any(char.IsControl) || result.Any(char.IsWhiteSpace))
            throw new ArgumentException();
        _ = PersonalityContract.Utf8.GetByteCount(result); // Reject unpaired surrogate identifiers before canonicalization.
        return result;
    }

    private static string? NullableIdentifier(JsonElement value, string name, int maximumLength) =>
        value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Identifier(value, name, maximumLength);

    private static string String(JsonElement value, string name)
    {
        var item = value.GetProperty(name);
        if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } text)
            throw new ArgumentException();
        return text;
    }

    private static long Number(JsonElement value, string name)
    {
        var item = value.GetProperty(name);
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var result) || result < 0)
            throw new ArgumentException();
        return result;
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException();
    }
}
