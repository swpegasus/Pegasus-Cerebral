using Cerebral.Core;

namespace Cerebral.Worker;

internal static class InternetPolicyRules
{
    public static InternetLimits Validate(InternetLimits value) => InternetContractValidation.Validate(value);
    public static bool Hex(string value) => InternetContractValidation.Hex(value);
    public static bool Allows(InternetLimits limits, Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0 ||
            url.AbsoluteUri.Length > 4096) return false;
        var host = url.IdnHost.ToLowerInvariant().TrimEnd('.');
        return !(limits.BlockedDomains ?? []).Contains(host, StringComparer.Ordinal) &&
            (limits.AllowedDomains is not { } allowed || allowed.Contains(host, StringComparer.Ordinal));
    }
    public static InternetLimits Narrow(InternetLimits ceiling, InternetLimits? restriction)
    {
        Validate(ceiling);
        if (restriction is null) return ceiling;
        Validate(restriction);
        if (restriction.RequestsPerSecond > ceiling.RequestsPerSecond || restriction.Burst > ceiling.Burst ||
            restriction.MaximumConcurrentRequests > ceiling.MaximumConcurrentRequests ||
            restriction.RequestTimeoutSeconds > ceiling.RequestTimeoutSeconds ||
            restriction.MaximumResponseBytes > ceiling.MaximumResponseBytes || restriction.MaximumRedirects > ceiling.MaximumRedirects ||
            ceiling.AllowedDomains is { } globalDomains &&
                (restriction.AllowedDomains is not { } ownDomains || ownDomains.Except(globalDomains, StringComparer.Ordinal).Any()) ||
            (ceiling.BlockedDomains ?? []).Except(restriction.BlockedDomains ?? [], StringComparer.Ordinal).Any())
            throw new ArgumentException("A Main Internet restriction cannot exceed its global ceiling.");
        return restriction;
    }
    // Runtime intersection also narrows an older Main record after a new global restriction.
    public static InternetLimits Intersect(InternetLimits ceiling, InternetLimits? saved)
    {
        Validate(ceiling); if (saved is null) return ceiling; Validate(saved);
        IReadOnlyList<string>? domains = ceiling.AllowedDomains is { } global
            ? saved.AllowedDomains is { } own ? global.Intersect(own, StringComparer.Ordinal).ToArray() : global
            : saved.AllowedDomains;
        // null means unrestricted; an explicit empty allow-set means deny every destination.
        return new(Math.Min(ceiling.RequestsPerSecond, saved.RequestsPerSecond), Math.Min(ceiling.Burst, saved.Burst),
            Math.Min(ceiling.MaximumConcurrentRequests, saved.MaximumConcurrentRequests),
            Math.Min(ceiling.RequestTimeoutSeconds, saved.RequestTimeoutSeconds),
            Math.Min(ceiling.MaximumResponseBytes, saved.MaximumResponseBytes), Math.Min(ceiling.MaximumRedirects, saved.MaximumRedirects),
            domains, (ceiling.BlockedDomains ?? []).Union(saved.BlockedDomains ?? [], StringComparer.Ordinal).ToArray());
    }
}
