using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>
/// Owns paired-capacity credentials and the deliberately small peer command surface. The installation owner key,
/// private prompts, paths, plugin configuration and unrestricted command dispatcher never cross this boundary.
/// </summary>
public sealed class PeerAccessControl(StateStore store, Configuration configuration, JobScheduler scheduler)
{
    public const string HeaderName = "X-Cerebral-Peer";
    public const string UnavailableAssignmentMessage = "Peer assignment is unavailable.";
    private const string TokenPrefix = "cpg1";

    public IReadOnlyList<PeerGrantSummary> List() => store.List<PeerAccessGrant>("peer-grant")
        .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id, StringComparer.Ordinal)
        .Select(Summary).ToList();

    public PeerGrantToken Create(JsonElement args)
    {
        var name = RequiredText(args, "name", 200);
        var profiles = ProfileIds(args);
        var maximum = Number(args, "maxConcurrentJobs", 1, 1, configuration.Get().MaxConcurrentJobs);
        var expires = OptionalDate(args, "expiresAt");
        if (expires is { } value && value <= DateTimeOffset.UtcNow) throw new ArgumentException("Peer grant expiry must be in the future.");
        ValidateSharedProfiles(profiles);
        var id = Guid.NewGuid().ToString("N");
        var secret = RandomNumberGenerator.GetBytes(32);
        var grant = new PeerAccessGrant
        {
            Id = id, Name = name, SecretDigest = Digest(secret), SharedProfileIds = profiles,
            MaxConcurrentJobs = maximum, ExpiresAt = expires
        };
        store.Put("peer-grant", id, grant);
        return new(id, Token(id, secret));
    }

    public PeerGrantToken Rotate(string id)
    {
        Configuration.ValidateId(id);
        var secret = RandomNumberGenerator.GetBytes(32);
        store.Locked(() =>
        {
            var grant = Get(id);
            if (grant.RevokedAt is not null) throw new InvalidOperationException("A revoked peer grant cannot be rotated. Create a new grant.");
            grant.SecretDigest = Digest(secret); grant.RotatedAt = DateTimeOffset.UtcNow; grant.Revision++;
            store.Put("peer-grant", grant.Id, grant); return true;
        });
        return new(id, Token(id, secret));
    }

    public PeerGrantSummary Drain(string id, bool draining) => store.Locked(() =>
    {
        var grant = Get(id);
        if (grant.RevokedAt is not null) throw new InvalidOperationException("Peer grant is revoked.");
        if (grant.Draining != draining) { grant.Draining = draining; grant.Revision++; store.Put("peer-grant", id, grant); }
        return Summary(grant);
    });

    public PeerGrantSummary Revoke(string id)
    {
        var summary = store.Locked(() =>
        {
            var grant = Get(id);
            if (grant.RevokedAt is null)
            {
                grant.RevokedAt = DateTimeOffset.UtcNow; grant.Draining = true; grant.Revision++;
                store.Put("peer-grant", id, grant);
            }
            return Summary(grant);
        });
        foreach (var job in store.ActiveJobs().Where(x => x.PeerGrantId == id).ToList()) scheduler.Cancel(job.Id);
        return summary;
    }

    public bool TryAuthenticate(string? token, out PeerAccessGrant? grant)
    {
        grant = null;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024) return false;
        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != TokenPrefix) return false;
        try { Configuration.ValidateId(parts[1]); }
        catch (ArgumentException) { return false; }
        byte[] secret;
        try { secret = Decode(parts[2]); }
        catch (FormatException) { return false; }
        if (secret.Length != 32) return false;
        var candidate = store.Get<PeerAccessGrant>("peer-grant", parts[1]);
        if (candidate is null || candidate.RevokedAt is not null || candidate.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow) return false;
        byte[] saved;
        try { saved = Decode(candidate.SecretDigest); }
        catch (FormatException) { return false; }
        var actual = SHA256.HashData(secret);
        if (saved.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(saved, actual)) return false;
        grant = candidate; return true;
    }

    public async Task<CommandResult> ExecuteAsync(PeerAccessGrant authenticatedGrant, CommandRequest request, CancellationToken token)
    {
        try
        {
            if (request.Arguments.ValueKind != JsonValueKind.Object || request.ProfileId is not null)
                throw new UnauthorizedAccessException("Peer commands require a structured body and cannot impersonate an owner agent.");
            // Re-read mutable state after authentication so drain/revoke/profile changes take effect at dispatch.
            var grant = Get(authenticatedGrant.Id);
            if (grant.RevokedAt is not null || grant.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
                throw new UnauthorizedAccessException("Peer grant is unavailable.");
            object result = request.Operation switch
            {
                "health" => SafeHost(),
                "peer.metrics" or "resources" => await MetricsAsync(token),
                "peer.catalog" => Catalog(grant),
                "jobs.submit" => Submit(grant, request.Arguments),
                "jobs.get" => OwnedJob(grant, Id(request.Arguments)),
                "jobs.cancel" => scheduler.Cancel(OwnedJob(grant, Id(request.Arguments)).Id),
                "jobs.steer" => scheduler.Steer(OwnedJob(grant, Id(request.Arguments)).Id,
                    RequiredText(request.Arguments, "prompt", 1_000_000), OptionalText(request.Arguments, "requestId", 200)),
                "questions.list" => Questions(grant, RequiredText(request.Arguments, "sessionId", 200)),
                "questions.answer" => Answer(grant, request.Arguments),
                _ => throw new UnauthorizedAccessException("Command is not available to paired Workers.")
            };
            return CommandResult.Ok(result);
        }
        catch (OperationCanceledException) { return CommandResult.Fail("Peer command cancelled or timed out."); }
        catch (Exception ex) { return CommandResult.Fail(ex.Message); }
    }

    public async Task<PeerWorkerMetrics> MetricsAsync(CancellationToken token = default)
    {
        var resources = await HardwareProbe.ReadAsync(token);
        var safe = new ResourceInfo(resources.ProcessMemoryMb, resources.SystemAvailableMb,
            resources.Gpus.Select(gpu => new GpuInfo(gpu.Name, gpu.TotalMb, gpu.FreeMb, gpu.Unified,
                gpu.Error is null ? null : "unavailable", gpu.UtilizationPercent) { UsedMb = gpu.UsedMb }).ToList())
        {
            SystemTotalMb = resources.SystemTotalMb,
            CpuUtilizationPercent = resources.CpuUtilizationPercent
        };
        return new(SafeHost(), safe, scheduler.QueueMetrics(), DateTimeOffset.UtcNow);
    }

    private AgentJob Submit(PeerAccessGrant grant, JsonElement args)
    {
        if (grant.Draining) throw new InvalidOperationException("Peer grant is draining and does not accept new work.");
        var request = CerebralJson.Read<SubmitJobRequest>(args);
        if (request.ParentJobId is not null) throw new UnauthorizedAccessException("A paired installation may submit only root assignments.");
        if (request.DelegatedProfile is null) throw new UnauthorizedAccessException("Peer submission requires a bounded delegated profile snapshot.");
        request.DelegatedProfile.AllowedRoots = [];
        request.DelegatedProfile.WorkerId = "local";
        request.DelegatedProfile.ModelWorkerId = "local";
        request.DelegatedProfile.RemoteProfileId = null;
        request.PeerGrantId = null;
        return scheduler.SubmitPeer(request, grant);
    }

    private AgentJob OwnedJob(PeerAccessGrant grant, string id)
    {
        try
        {
            var job = scheduler.Get(id);
            if (job.PeerGrantId == grant.Id) return job;
        }
        catch (ArgumentException) { }
        throw new UnauthorizedAccessException(UnavailableAssignmentMessage);
    }

    private IReadOnlyList<UserQuestion> Questions(PeerAccessGrant grant, string sessionId) =>
        store.List<UserQuestion>("question").Where(question => question.SessionId == sessionId &&
            store.Get<AgentJob>("job", question.JobId)?.PeerGrantId == grant.Id).Take(100).ToList();

    private UserQuestion Answer(PeerAccessGrant grant, JsonElement args)
    {
        var id = Id(args); var question = store.Get<UserQuestion>("question", id) ?? throw new ArgumentException("Question not found.");
        OwnedJob(grant, question.JobId);
        return scheduler.Answer(id, RequiredText(args, "answer", 100_000));
    }

    private PeerCatalog Catalog(PeerAccessGrant grant)
    {
        var agents = grant.SharedProfileIds.Select(id => store.Get<AgentProfile>("agent", id))
            .Where(profile => profile is { WorkerId: "local", ModelWorkerId: "local" }).Cast<AgentProfile>().ToList();
        var modelIds = agents.Select(x => x.ModelId).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var catalogAgents = agents.Select(x => new PeerAgentCatalogEntry(x.Id, x.Revision, x.Name, x.Purpose, x.Role,
            x.ModelId, x.Capabilities.ToList(), x.RequiredModelCapabilities.ToList())).ToList();
        var models = store.List<ModelRecord>("model").Where(x => modelIds.Contains(x.Id))
            .Select(x => new PeerModelCatalogEntry(x.Id, x.Name, x.State, x.Architecture, x.Quantization,
                x.ContextTokens, x.CapabilityStatus.ToList())).ToList();
        return new(catalogAgents, models, grant.Revision);
    }

    private PeerHostInfo SafeHost() => new("Pegasus Cerebral",
        typeof(PeerAccessControl).Assembly.GetName().Version?.ToString(3) ?? "0.1.0", 1,
        configuration.Get().Name, RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.OSDescription,
        configuration.Get().Coordinator);

    private PeerAccessGrant Get(string id)
    {
        Configuration.ValidateId(id);
        return store.Get<PeerAccessGrant>("peer-grant", id) ?? throw new ArgumentException("Peer grant not found.");
    }

    private void ValidateSharedProfiles(IReadOnlyList<string> ids)
    {
        if (ids.Count is < 1 or > 64) throw new ArgumentException("Select between 1 and 64 receiver-owned profiles.");
        foreach (var id in ids)
        {
            Configuration.ValidateId(id);
            var profile = store.Get<AgentProfile>("agent", id) ?? throw new ArgumentException("Shared profile not found: " + id);
            if (profile.WorkerId != "local" || profile.ModelWorkerId != "local")
                throw new ArgumentException("Only receiver-owned local profiles can be shared with a peer.");
        }
    }

    private static List<string> ProfileIds(JsonElement args)
    {
        if (!args.TryGetProperty("profileIds", out var value) || value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("profileIds must be an array.");
        var ids = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .Select(item => item is { Length: > 0 } ? item : throw new ArgumentException("profileIds contains an invalid value."))
            .Distinct(StringComparer.Ordinal).Cast<string>().ToList();
        return ids;
    }

    private static string Id(JsonElement args) => RequiredText(args, "id", 200);
    private static string RequiredText(JsonElement args, string name, int maximum) =>
        OptionalText(args, name, maximum) is { Length: > 0 } value ? value : throw new ArgumentException("Missing or invalid field: " + name);
    private static string? OptionalText(JsonElement args, string name, int maximum)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("Invalid field: " + name);
        var text = value.GetString();
        return text is not null && text.Length <= maximum && !text.Contains('\0') ? text : throw new ArgumentException("Invalid field: " + name);
    }
    private static int Number(JsonElement args, string name, int fallback, int minimum, int maximum)
    {
        var number = args.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
        return number >= minimum && number <= maximum ? number : throw new ArgumentException("Out of range: " + name);
    }
    private static DateTimeOffset? OptionalDate(JsonElement args, string name)
    {
        var text = OptionalText(args, name, 100);
        return text is null ? null : DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var value) ? value.ToUniversalTime() : throw new ArgumentException("Invalid timestamp: " + name);
    }
    private static PeerGrantSummary Summary(PeerAccessGrant grant) => new(grant.Id, grant.Name, grant.Revision,
        grant.SharedProfileIds.ToList(), grant.MaxConcurrentJobs, grant.Draining, grant.CreatedAt,
        grant.ExpiresAt, grant.RevokedAt, grant.RotatedAt);
    private static string Token(string id, byte[] secret) => TokenPrefix + "." + id + "." + Encode(secret);
    private static string Digest(byte[] secret) => Encode(SHA256.HashData(secret));
    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        text += new string('=', (4 - text.Length % 4) % 4);
        var decoded = Convert.FromBase64String(text);
        if (!string.Equals(Encode(decoded), value, StringComparison.Ordinal))
            throw new FormatException("The token secret is not canonical base64url.");
        return decoded;
    }
}
