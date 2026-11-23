using System.Globalization;

namespace Cerebral.Core;

public static class InternetContractValidation
{
    public static InternetLimits Validate(InternetLimits value)
    {
        if (value is null || !double.IsFinite(value.RequestsPerSecond) || value.RequestsPerSecond is < 0.1 or > 100 ||
            value.Burst is < 1 or > 100 || value.MaximumConcurrentRequests is < 1 or > 32 ||
            value.RequestTimeoutSeconds is < 1 or > 120 || value.MaximumResponseBytes is < 1024 or > 8388608 ||
            value.MaximumRedirects is < 0 or > 10) throw new ArgumentException("Internet limits exceed the supported bounds.");
        ValidateDomains(value.AllowedDomains); ValidateDomains(value.BlockedDomains);
        return value;
    }
    public static string Domain(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253 || value.Contains('*') ||
            value != value.Trim() || value.EndsWith('.') || value.Contains('/') || value.Contains(':') ||
            Uri.CheckHostName(value) == UriHostNameType.Unknown)
            throw new ArgumentException("Use exact canonical DNS domain names, without wildcards, ports or paths.");
        return new IdnMapping().GetAscii(value).ToLowerInvariant();
    }
    private static void ValidateDomains(IReadOnlyList<string>? values)
    {
        if (values is null) return;
        if (values.Count > 64 || values.Distinct(StringComparer.Ordinal).Count() != values.Count ||
            values.Any(value => Domain(value) != value))
            throw new ArgumentException("Use at most 64 distinct lowercase ASCII domain names.");
    }
    public static bool Hex(string value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
