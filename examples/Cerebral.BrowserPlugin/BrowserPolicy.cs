using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using Cerebral.Core;

namespace Cerebral.BrowserPlugin;

/// <summary>Profile-owned ordinary settings. No secrets, browser flags, profile paths or authority switches.</summary>
public sealed record BrowserPolicy
{
    public int MaximumJobs { get; init; } = 2;
    public int MaximumTabsPerJob { get; init; } = 3;
    public int MaximumTabs { get; init; } = 6;
    // This first provider deliberately serializes actions. Higher parallelism needs independent page lifetimes.
    public int MaximumActionsPerSession { get; init; } = 1;
    public int QueueDepth { get; init; } = 8;
    public int MinimumActionDelayMilliseconds { get; init; } = 350;
    public int NavigationTimeoutSeconds { get; init; } = 30;
    public int ActionTimeoutSeconds { get; init; } = 15;
    public int IdleTimeoutSeconds { get; init; } = 300;
    public int PageHeapLimitMiB { get; init; } = 128;
    public long DownloadFileBytes { get; init; } = 16 * 1024 * 1024;
    public long DownloadQuotaBytes { get; init; } = 64 * 1024 * 1024;
    public long ScreenshotQuotaBytes { get; init; } = 16 * 1024 * 1024;
    public int ScreenshotRetentionMinutes { get; init; } = 30;
    public string[] AllowedDomains { get; init; } = [];
    public string Downloads { get; init; } = "confirm";
    public string Uploads { get; init; } = "confirm";
    public string Clipboard { get; init; } = "deny";
    public string Authentication { get; init; } = "confirm";
    public string ExternalApplications { get; init; } = "deny";
    public string OutsideDomains { get; init; } = "confirm";

    public static BrowserPolicy For(AgentProfile profile)
    {
        var result = profile.PluginSettings.TryGetValue(BrowserServicePlugin.Id, out var raw)
            ? BrowserJson.Read<BrowserPolicy>(raw) : new BrowserPolicy();
        result.Validate(); return result;
    }

    public void Validate()
    {
        if (MaximumJobs is < 1 or > 4 || MaximumTabsPerJob is < 1 or > 8 || MaximumTabs is < 1 or > 16 ||
            MaximumActionsPerSession != 1 || QueueDepth is < 0 or > 16 ||
            MaximumTabsPerJob > MaximumTabs || MinimumActionDelayMilliseconds is < 100 or > 5000 ||
            NavigationTimeoutSeconds is < 2 or > 120 || ActionTimeoutSeconds is < 1 or > 60 ||
            IdleTimeoutSeconds is < 30 or > 1800 || PageHeapLimitMiB is < 16 or > 1024 ||
            DownloadFileBytes is < 1 or > 268435456 || DownloadQuotaBytes < DownloadFileBytes || DownloadQuotaBytes > 1073741824 ||
            ScreenshotQuotaBytes is < 262144 or > 67108864 || ScreenshotRetentionMinutes is < 1 or > 1440)
            throw new ArgumentException("Browser settings exceed their fixed limits.");
        if (AllowedDomains is null || AllowedDomains.Length > 32 || AllowedDomains.Distinct(StringComparer.OrdinalIgnoreCase).Count() != AllowedDomains.Length ||
            AllowedDomains.Any(domain => string.IsNullOrWhiteSpace(domain) || domain.Length > 253 || domain.Contains('*') ||
                Uri.CheckHostName(domain) == UriHostNameType.Unknown || domain != domain.Trim().TrimEnd('.')))
            throw new ArgumentException("Use up to 32 explicit DNS names or IP addresses; wildcards, ports and URL paths are not domains.");
        foreach (var value in new[] { Downloads, Uploads, Clipboard, Authentication, ExternalApplications, OutsideDomains })
            if (value is not ("deny" or "confirm")) throw new ArgumentException("A browser gate must be deny or confirm; confirmation never originates in model arguments.");
    }

    public bool Allows(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 &&
        AllowedDomains.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);

    public void RequireUrl(string url)
    {
        if (url.Length > 4096 || !Allows(url)) throw new BrowserRejectedException(
            OutsideDomains == "confirm" ? "outside-domain-confirmation-required" : "outside-domain-denied",
            "The address is outside this profile's explicit HTTP(S) domain policy. Review the profile domain settings; no navigation was authorized.");
    }
}

internal static class BrowserJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 16,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static T Read<T>(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || raw.GetRawText().Length > 65536)
            throw new ArgumentException("Browser arguments must be a bounded object.");
        Check(raw); return raw.Deserialize<T>(Options) ?? throw new ArgumentException("Browser arguments cannot be null.");
    }
    private static void Check(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw new ArgumentException("Duplicate browser argument fields are not allowed.");
                Check(field.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Check(item);
    }
    public static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Options);
    public static string Text(object value) => JsonSerializer.Serialize(value, Options);
}

public sealed class BrowserRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
