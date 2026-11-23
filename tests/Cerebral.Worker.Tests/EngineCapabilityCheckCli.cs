using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

/// <summary>Private, explicit Root-invoked A/B checker. No discovery, worker startup or automatic replay.</summary>
public static class EngineCapabilityCheckCli
{
    private const string Workspace = @"C:\Development\Pegasus-Cerebral";
    private const string TaskDirectory = @".tmp\engine-capability-check-sample-source-20261012";
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
    {
        WriteIndented = false, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict, RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true, MaxDepth = 32
    };
    private sealed record RuntimeFence(string Schema, string CheckId, string OwnerInstallationId, string DataDirectory,
        long Epoch, string ActiveSlot, string PackageSha256, string ConfigurationSha256, string EndpointFileSha256,
        DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc, bool ReviewedByRoot, bool KnownCallerWindowReviewed,
        bool LocalGpuReserved, bool ControlledModelWindowReviewed);
    private sealed class OwnerClient(CerebralClient client) : IEngineCapabilityCheckClient
    {
        public Task<T> GetAsync<T>(string operation, object arguments, CancellationToken token) =>
            client.GetAsync<T>(operation, arguments, cancellationToken: token);
    }

    public static async Task<int> Main(string[] args)
    {
        bool consumed = false;
        EngineCapabilityCheckPlan? plan = null;
        try
        {
            var options = Options(args);
            var mode = NeedOption(options, "--mode");
            if (mode is not ("validate" or "inspect" or "submit" or "read"))
                throw new ArgumentException("Unsupported check mode.");
            RequireOptions(options, mode);
            plan = Read<EngineCapabilityCheckPlan>(NeedOption(options, "--plan"), NeedOption(options, "--plan-sha"), 16384);
            EngineCapabilityCheck.Validate(plan);
            if (mode == "validate")
            {
                Print(new { schema = "cerebral.engine.capability-check-validation/1", plan.CheckId, plan.TargetSlot,
                    planSha256 = EngineCapabilityCheck.PlanSha256(plan), valid = true, ownerCalls = 0 });
                return 0;
            }
            var endpointSha = NeedOption(options, "--endpoint-sha");
            var endpoint = Read<LocalWorkerEndpoint>(NeedOption(options, "--endpoint"), endpointSha, 4096);
            endpoint.Validate(); // The invoker supplies a freshly reviewed exact instance; no Source discovery fallback.
            EngineCapabilityCheckRelease? release = null;
            if (mode == "submit")
            {
                release = Read<EngineCapabilityCheckRelease>(NeedOption(options, "--release"), NeedOption(options, "--release-sha"), 4096);
                var fenceSha = NeedOption(options, "--fence-sha");
                var fence = Read<RuntimeFence>(NeedOption(options, "--fence"), fenceSha, 16384);
                ValidateFence(fence, plan, release, endpointSha, fenceSha);
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var client = new CerebralClient(endpoint);
            var owner = new OwnerClient(client);
            if (mode == "inspect")
            {
                Print(await EngineCapabilityCheck.InspectAsync(owner, plan, deadline.Token));
                return 0;
            }
            if (mode == "read")
            {
                Print(await EngineCapabilityCheck.ReadRetainedAsync(owner, plan, NeedOption(options, "--run-id"), deadline.Token));
                return 0;
            }
            var receipt = await EngineCapabilityCheck.SubmitOnceAsync(owner, plan, release!, () =>
            {
                var directory = Path.Combine(Workspace, TaskDirectory, "attempts");
                EnsurePrivateDirectory(directory);
                WriteNew(Path.Combine(directory, plan.CheckId + ".consumed.json"), new
                {
                    schema = "cerebral.engine.capability-check-consumption/1", plan.CheckId, plan.RequestId,
                    plan.ConversationId, plan.TargetSlot, planSha256 = EngineCapabilityCheck.PlanSha256(plan),
                    consumedUtc = DateTimeOffset.UtcNow, state = "consumed-before-sole-submit"
                });
                consumed = true;
            }, deadline.Token);
            WriteNew(Path.Combine(Workspace, TaskDirectory, "attempts", plan.CheckId + ".receipt.json"), receipt);
            Print(receipt);
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or InvalidOperationException or
            UnauthorizedAccessException or JsonException or TimeoutException or OperationCanceledException or NotSupportedException)
        {
            // No raw owner body, profile, journal, exception message or credential is emitted.
            Print(new { schema = "cerebral.engine.capability-check-refusal/1", checkId = plan?.CheckId,
                state = consumed ? "submission-outcome-unconfirmed-query-retained-request" : "refused-before-submission",
                requestId = plan?.RequestId, conversationId = plan?.ConversationId, replayed = false,
                diagnostic = error is LocalWorkerCommandUncertainException ? "local-command-unconfirmed" : error.GetType().Name });
            return consumed ? 3 : 2;
        }
    }

    private static void ValidateFence(RuntimeFence f, EngineCapabilityCheckPlan p,
        EngineCapabilityCheckRelease release, string endpointSha, string fenceSha)
    {
        var now = DateTimeOffset.UtcNow;
        if (f.Schema != "cerebral.root.engine-capability-check-fence/1" || !f.ReviewedByRoot ||
            !f.KnownCallerWindowReviewed || !f.ControlledModelWindowReviewed ||
            f.CheckId != p.CheckId || f.OwnerInstallationId != p.ExpectedOwnerInstallationId ||
            !string.Equals(Path.GetFullPath(f.DataDirectory), Path.GetFullPath(p.ExpectedDataDirectory), StringComparison.OrdinalIgnoreCase) ||
            f.Epoch != p.ExpectedEpoch || f.ActiveSlot != p.TargetSlot || f.PackageSha256 != p.ExpectedPackageSha256 ||
            f.ConfigurationSha256 != p.ExpectedConfigurationSha256 || f.EndpointFileSha256 != endpointSha ||
            f.IssuedUtc > now.AddSeconds(5) || f.ExpiresUtc <= now || f.ExpiresUtc <= f.IssuedUtc ||
            f.ExpiresUtc - f.IssuedUtc > TimeSpan.FromMinutes(15) || release.RuntimeFenceSha256 != fenceSha ||
            release.IssuedUtc < f.IssuedUtc || release.ExpiresUtc > f.ExpiresUtc ||
            f.LocalGpuReserved && new[] { p.Lead, p.Alpha, p.Beta }.Any(x => x.ModelWorkerId == "local"))
            throw new UnauthorizedAccessException("The exact fresh Root-reviewed runtime/model window does not admit this check.");
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        if (args.Length is < 6 or > 20 || args.Length % 2 != 0) throw new ArgumentException("Use bounded named option/value pairs.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
            if (args[index].Length > 32 || args[index + 1].Length is < 1 or > 4096 ||
                !result.TryAdd(args[index], args[index + 1])) throw new ArgumentException("Duplicate or oversized option.");
        return result;
    }
    private static void RequireOptions(Dictionary<string, string> options, string mode)
    {
        var expected = new List<string> { "--mode", "--plan", "--plan-sha" };
        if (mode != "validate") expected.AddRange(["--endpoint", "--endpoint-sha"]);
        if (mode == "read") expected.Add("--run-id");
        if (mode == "submit") expected.AddRange(["--release", "--release-sha", "--fence", "--fence-sha"]);
        if (options.Count != expected.Count || expected.Any(x => !options.ContainsKey(x)))
            throw new ArgumentException("Only the exact mode-specific options are admitted.");
    }
    private static string NeedOption(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) ? value : throw new ArgumentException("Required option missing.");

    private static T Read<T>(string path, string sha, int maximum)
    {
        if (sha.Length != 64 || !sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("An exact lowercase SHA256 is required.");
        var full = WithinWorkspace(path);
        RejectReparseAncestors(full);
        using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 || input.Length > maximum) throw new InvalidDataException("Pinned input exceeded its bound.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha) throw new InvalidDataException("Pinned input changed.");
        using var document = OwnerSnapshotJson.Parse(bytes, maximum);
        Unique(document.RootElement);
        return document.RootElement.Deserialize<T>(Json) ?? throw new InvalidDataException("Pinned typed input missing.");
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var child in OwnerSnapshotJson.Fields(value).Values) Unique(child);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) Unique(child);
    }
    private static string WithinWorkspace(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute pinned input is required.");
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(Workspace) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Private checker files must remain in the selected workspace.");
        return full;
    }
    private static void RejectReparseAncestors(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Private checker paths cannot traverse a reparse point.");
    }
    private static void EnsurePrivateDirectory(string path)
    {
        WithinWorkspace(path); RejectReparseAncestors(path);
        Directory.CreateDirectory(path); RejectReparseAncestors(path);
    }
    private static void WriteNew(string path, object value)
    {
        WithinWorkspace(path); RejectReparseAncestors(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > 16384) throw new InvalidDataException("Private receipt exceeded its bound.");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        output.Write(bytes); output.Flush(flushToDisk: true);
    }
    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
}
