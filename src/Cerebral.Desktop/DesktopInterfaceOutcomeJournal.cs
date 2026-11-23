using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop;

public sealed record DesktopInterfaceCompletion(string Id, string ClaimId, bool Success, string? Error,
    string? Message, string? ResourceRequestId, InterfacePluginReceipt? Plugin, bool ControlTestsAllowed,
    InterfaceFailureStage? FailureStage, InterfaceResourceReceipt? Resource = null)
{
    // Resource is the observed coordinate used to verify acknowledgement, not a caller-supplied
    // replacement for the Worker's resourceRequestId resolution.
    public object CommandArguments() => new
    {
        id = Id, claimId = ClaimId, success = Success, error = Error, message = Message,
        resourceRequestId = ResourceRequestId, plugin = Plugin,
        controlTestsAllowed = ControlTestsAllowed, failureStage = FailureStage
    };
}

public sealed record DesktopInterfaceOutcome(int SchemaVersion, string ServiceKey, string Id, string ClaimId,
    string SessionId, string JobId, string ProfileId, string Source, string Path, string PackageFingerprint,
    DateTimeOffset CreatedAt, bool ControlTestsAllowed, DesktopInterfaceCompletion? Completion = null);

/// <summary>Bounded owner-desktop delivery state. It never installs, claims, loads or invokes a plugin.
/// Unclassified intents remain uncertain; only a retained classified outcome may be delivered.</summary>
public sealed class DesktopInterfaceOutcomeJournal(string directory)
{
    public const int MaximumEntries = 128;
    public const int MaximumDocumentBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
    {
        WriteIndented = false, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 20
    };
    private readonly string root = System.IO.Path.GetFullPath(directory);

    public static string ServiceKey(HostInfo host)
    {
        if (host.Product != "Pegasus Cerebral" || host.Protocol != 1 ||
            string.IsNullOrWhiteSpace(host.DataDirectory) || !System.IO.Path.IsPathFullyQualified(host.DataDirectory) ||
            host.DataDirectory.Contains('\0') || host.InstallationId?.Length > 128)
            throw new InvalidDataException("A verified local Cerebral host identity is required for outcome delivery.");
        // Process IDs, transient pipe endpoints, credentials and version numbers do not identify the
        // retained database. A restarted Worker using the same installation/data root keeps this key.
        return Hash(JsonSerializer.Serialize(new
        {
            host.Product, host.Protocol, host.InstallationId,
            data = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(host.DataDirectory)).ToUpperInvariant()
        }, Json));
    }

    public bool Reserve(InterfaceRequest request, string claimId, string serviceKey, bool controlTestsAllowed)
    {
        if (request.Kind != "plugin" || request.Status != "pending")
            throw new InvalidOperationException("Reserve a pending owner-reviewed plugin before claiming or executing it.");
        var value = new DesktopInterfaceOutcome(1, serviceKey, request.Id, claimId, request.SessionId,
            request.JobId, request.ProfileId, request.Source, request.Path, request.PackageFingerprint ?? "",
            request.CreatedAt, controlTestsAllowed);
        Validate(value);
        using var lease = Acquire();
        CleanupStages();
        if (ReadUnderLease(serviceKey, request.Id) is { } existing)
        {
            if (!SameIdentity(existing, value))
                throw new InvalidOperationException("A different claim or reviewed package already has retained delivery state; inspect it without replay.");
            return false;
        }
        if (Files().Count >= MaximumEntries)
            throw new IOException("The desktop has 128 unresolved plugin outcomes. Confirm their receipts before starting another installation.");
        Write(value);
        return true;
    }

    public DesktopInterfaceOutcome? Read(string serviceKey, string id)
    {
        ValidateKey(serviceKey); ValidateId(id);
        if (!Directory.Exists(root)) return null;
        using var lease = Acquire();
        return ReadUnderLease(serviceKey, id);
    }

    public IReadOnlyList<DesktopInterfaceOutcome> List(string serviceKey, string sessionId, int limit = 8)
    {
        ValidateKey(serviceKey); ValidateText(sessionId, 128);
        if (limit is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!Directory.Exists(root)) return [];
        using var lease = Acquire();
        var values = new List<DesktopInterfaceOutcome>();
        foreach (var file in Files())
        {
            if (!System.IO.Path.GetFileName(file).StartsWith(serviceKey + ".", StringComparison.Ordinal)) continue;
            var value = ReadFile(file);
            if (value.ServiceKey != serviceKey) throw new InvalidDataException("Outcome service identity does not match its file.");
            if (value.SessionId == sessionId) values.Add(value);
        }
        return values.OrderBy(value => value.CreatedAt).ThenBy(value => value.Id, StringComparer.Ordinal).Take(limit).ToArray();
    }

    public DesktopInterfaceOutcome SaveCompletion(DesktopInterfaceOutcome expected, DesktopInterfaceCompletion completion)
    {
        using var lease = Acquire();
        var current = RequireCurrent(expected);
        var candidate = current with { Completion = completion };
        Validate(candidate);
        if (current.Completion is not null)
        {
            if (Serialize(current) != Serialize(candidate))
                throw new InvalidOperationException("A recorded desktop outcome cannot be replaced with another result.");
            return current;
        }
        Write(candidate);
        return ReadUnderLease(candidate.ServiceKey, candidate.Id)!;
    }

    public DesktopInterfaceOutcome RequireSavedCompletion(DesktopInterfaceOutcome expected)
    {
        using var lease = Acquire();
        var current = RequireCurrent(expected);
        if (current.Completion is null || expected.Completion is null || Serialize(current) != Serialize(expected))
            throw new InvalidOperationException("The exact classified desktop outcome is not retained; do not deliver guessed or changed evidence.");
        return current;
    }

    public void DiscardUnstarted(DesktopInterfaceOutcome expected)
    {
        using var lease = Acquire();
        var current = RequireCurrent(expected);
        if (current.Completion is not null) throw new InvalidOperationException("A classified outcome requires confirmed receipt recovery.");
        File.Delete(FilePath(current.ServiceKey, current.Id));
    }

    public void Confirm(DesktopInterfaceOutcome expected, InterfaceRequest confirmed)
    {
        using var lease = Acquire();
        var current = RequireCurrent(expected);
        RequireMatchingRequest(current, confirmed);
        RequireConfirmedOutcome(current, confirmed);
        File.Delete(FilePath(current.ServiceKey, current.Id));
    }

    public static void RequireMatchingRequest(DesktopInterfaceOutcome value, InterfaceRequest request)
    {
        Validate(value);
        if (request.Kind != "plugin" || request.Id != value.Id || request.ClaimId != value.ClaimId ||
            request.SessionId != value.SessionId || request.JobId != value.JobId ||
            request.ProfileId != value.ProfileId || request.Source != value.Source ||
            request.Path != value.Path || request.PackageFingerprint != value.PackageFingerprint ||
            request.CreatedAt != value.CreatedAt || request.Status is not ("claimed" or "completed" or "failed"))
            throw new InvalidOperationException("The exact retained plugin request/claim/binding does not match. No installation or receipt was replayed.");
    }

    public static void RequireConfirmedOutcome(DesktopInterfaceOutcome value, InterfaceRequest request)
    {
        var outcome = value.Completion ?? throw new InvalidOperationException("The desktop outcome was not classified; inspect it without replaying native work.");
        if (request.Status != (outcome.Success ? "completed" : "failed") || request.Error != outcome.Error ||
            request.Result != outcome.Message || request.FailureStage != outcome.FailureStage ||
            request.Resource != outcome.Resource ||
            JsonSerializer.Serialize(request.Plugin, Json) != JsonSerializer.Serialize(outcome.Plugin, Json))
            throw new InvalidOperationException("The retained Worker outcome differs from this desktop's classified receipt. Keep the delivery evidence for inspection.");
    }

    private DesktopInterfaceOutcome RequireCurrent(DesktopInterfaceOutcome expected)
    {
        Validate(expected);
        var current = ReadUnderLease(expected.ServiceKey, expected.Id) ??
            throw new InvalidOperationException("The retained desktop outcome is unavailable.");
        if (!SameIdentity(current, expected))
            throw new InvalidOperationException("The retained desktop outcome changed; do not overwrite another claim.");
        return current;
    }

    private DesktopInterfaceOutcome? ReadUnderLease(string serviceKey, string id)
    {
        var file = FilePath(serviceKey, id);
        if (!File.Exists(file)) return null;
        var value = ReadFile(file);
        if (value.ServiceKey != serviceKey || value.Id != id)
            throw new InvalidDataException("Outcome identity does not match its exact file.");
        return value;
    }

    private DesktopInterfaceOutcome ReadFile(string file)
    {
        CerebralPluginPackage.RejectLinks(file);
        using var input = File.OpenRead(file);
        if (input.Length is < 2 or > MaximumDocumentBytes) throw new InvalidDataException("Desktop outcome exceeded its bounded document size.");
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 20 });
        RejectDuplicateFields(document.RootElement);
        var value = document.RootElement.Deserialize<DesktopInterfaceOutcome>(Json) ??
            throw new InvalidDataException("Desktop outcome is empty.");
        Validate(value);
        if (file != FilePath(value.ServiceKey, value.Id)) throw new InvalidDataException("Desktop outcome was moved to a different identity.");
        return value;
    }

    private static void RejectDuplicateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate desktop outcome fields are not valid evidence.");
                RejectDuplicateFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateFields(item);
    }

    private IReadOnlyList<string> Files()
    {
        var files = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).Take(MaximumEntries + 1).ToArray();
        if (files.Length > MaximumEntries) throw new InvalidDataException("Desktop outcome entry quota exceeded; unresolved evidence was preserved.");
        return files;
    }

    private FileStream Acquire()
    {
        CerebralPluginPackage.RejectLinks(root);
        Directory.CreateDirectory(root);
        CerebralPluginPackage.RejectLinks(root);
        var file = System.IO.Path.Combine(root, ".delivery.lock");
        CerebralPluginPackage.RejectLinks(file);
        var lease = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            CerebralPluginPackage.RejectLinks(file);
            if (lease.Length != 0) throw new InvalidDataException("The desktop delivery lock must be empty; file presence is not ownership.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private void Write(DesktopInterfaceOutcome value)
    {
        CleanupStages(); // Every writer cleans its bounded orphan stages while holding the OS lease.
        var contents = Encoding.UTF8.GetBytes(Serialize(value));
        if (contents.Length > MaximumDocumentBytes) throw new InvalidDataException("Desktop outcome exceeded its bounded document size.");
        var file = FilePath(value.ServiceKey, value.Id);
        var stage = System.IO.Path.Combine(root, ".outcome-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            CerebralPluginPackage.RejectLinks(file);
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(contents); output.Flush(flushToDisk: true); }
            CerebralPluginPackage.RejectLinks(file);
            File.Move(stage, file, overwrite: true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    private void CleanupStages()
    {
        var files = Directory.EnumerateFiles(root, ".outcome-stage-*", SearchOption.TopDirectoryOnly).Take(MaximumEntries + 1).ToArray();
        if (files.Length > MaximumEntries) throw new InvalidDataException("Too many interrupted outcome stages; no native work was started.");
        foreach (var file in files)
        {
            if (!Guid.TryParseExact(System.IO.Path.GetFileName(file)[".outcome-stage-".Length..], "N", out _))
                throw new InvalidDataException("Unexpected outcome stage name.");
            CerebralPluginPackage.RejectLinks(file);
            if (new FileInfo(file).Length > MaximumDocumentBytes) throw new InvalidDataException("Interrupted outcome stage exceeded its quota.");
            File.Delete(file);
        }
    }

    private string FilePath(string key, string id) => System.IO.Path.Combine(root, key + "." + id + ".json");
    private static string Serialize(DesktopInterfaceOutcome value) => JsonSerializer.Serialize(value, Json);
    private static bool SameIdentity(DesktopInterfaceOutcome a, DesktopInterfaceOutcome b) =>
        Serialize(a with { Completion = null }) == Serialize(b with { Completion = null });
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void ValidateKey(string? value)
    { if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new ArgumentException("Use the exact bounded service/package key."); }
    private static void ValidateId(string? value)
    { if (!Guid.TryParseExact(value, "N", out var id) || id == Guid.Empty) throw new ArgumentException("Use the exact request ID."); }
    private static void ValidateText(string? value, int maximum)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)) throw new ArgumentException("Use a bounded nonempty outcome identity."); }

    private static void Validate(DesktopInterfaceOutcome value)
    {
        if (value.SchemaVersion != 1) throw new InvalidDataException("Unsupported desktop outcome schema.");
        ValidateKey(value.ServiceKey); ValidateKey(value.PackageFingerprint); ValidateId(value.Id);
        ValidateText(value.ClaimId, 128); ValidateText(value.SessionId, 128);
        ValidateText(value.JobId, 128); ValidateText(value.ProfileId, 128);
        if (value.Source is not ("legacy" or "workflow") || string.IsNullOrWhiteSpace(value.Path) || value.Path.Length > 32768 ||
            !System.IO.Path.IsPathFullyQualified(value.Path) || value.Path.Contains('\0') ||
            value.Path != System.IO.Path.GetFullPath(value.Path))
            throw new InvalidDataException("Desktop outcome requires the original canonical plugin source.");
        if (value.Completion is not { } completion) return;
        if (completion.Id != value.Id || completion.ClaimId != value.ClaimId ||
            completion.ControlTestsAllowed != value.ControlTestsAllowed ||
            completion.Message?.Length > 2000 || completion.Error?.Length > 2000 ||
            completion.Message?.Contains('\0') == true || completion.Error?.Contains('\0') == true ||
            completion.FailureStage is { } stage && !Enum.IsDefined(stage) ||
            completion.Success && (completion.Error is not null || completion.FailureStage is not null || completion.Plugin is null) ||
            !completion.Success && (string.IsNullOrWhiteSpace(completion.Error) || completion.Plugin is not null || completion.Resource is not null || completion.ResourceRequestId is not null) ||
            (completion.ResourceRequestId is null) != (completion.Resource is null) ||
            completion.ResourceRequestId is { } resourceId && resourceId != "interface-" + value.Id ||
            completion.Plugin is { } plugin && (plugin.SchemaVersion != 1 || plugin.ControlTestsAllowed != value.ControlTestsAllowed ||
                plugin.Mounts is null || plugin.Mounts.Count > 72))
            throw new InvalidDataException("Desktop completion must retain one bounded, internally consistent classified outcome.");
    }
}

/// <summary>Recovery receives only exact read and completion-delivery callbacks; it has no native effect callback.</summary>
public static class DesktopInterfaceOutcomeDelivery
{
    public static async Task<InterfaceRequest> RecoverAsync(DesktopInterfaceOutcomeJournal journal,
        DesktopInterfaceOutcome entry, Func<string, CancellationToken, Task<InterfaceRequest>> read,
        Func<DesktopInterfaceCompletion, CancellationToken, Task<InterfaceRequest>> deliver,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        entry = journal.RequireSavedCompletion(entry);
        var request = await read(entry.Id, cancellationToken);
        DesktopInterfaceOutcomeJournal.RequireMatchingRequest(entry, request);
        if (request.Status == "claimed")
        {
            cancellationToken.ThrowIfCancellationRequested();
            request = await deliver(entry.Completion!, cancellationToken);
        }
        journal.Confirm(entry, request);
        return request;
    }
}
