using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cerebral.BrowserPlugin;

internal sealed record BrowserPageRequestArguments(string RequestId, string Url, string? TabId = null,
    int MaximumBytes = 65536, int MaximumTextBytes = 12000)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(RequestId) || RequestId.Length > 128 ||
            RequestId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')) ||
            MaximumBytes is < 1024 or > 262144 || MaximumTextBytes is < 256 or > 16384 ||
            TabId is not null && !Guid.TryParseExact(TabId, "N", out _))
            throw new ArgumentException("Page requests require one bounded request ID, byte limits and optional owned tab ID.");
    }
}

internal sealed class BrowserPageRequestException(string code, string message, bool dispatched) : Exception(message)
{
    internal string Code { get; } = code;
    internal bool Dispatched { get; } = dispatched;
}

internal static class BrowserNetworkAddressPolicy
{
    // Resolve once inside ConnectCallback and connect to that exact address. No second DNS lookup can rebind it.
    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, BrowserPolicy policy,
        CancellationToken token)
    {
        var uri = context.InitialRequestMessage.RequestUri ?? throw new BrowserRejectedException("invalid-address", "A request address is required.");
        policy.RequireUrl(uri.AbsoluteUri);
        var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
            ? new[] { literal } : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        if (addresses.Length is < 1 or > 16 || addresses.Any(address => !Allows(address, uri.IdnHost, policy)))
            throw new BrowserRejectedException("address-policy-denied", "The domain resolves to an unavailable or ungranted local address.");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("No allowed address accepted the connection.");
    }

    internal static bool Allows(IPAddress address, string host, BrowserPolicy policy)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast) || address.IsIPv6Multicast || address.IsIPv6LinkLocal ||
            address.AddressFamily == AddressFamily.InterNetwork && (bytes[0] >= 224 || bytes[0] == 0 || bytes[0] == 169 && bytes[1] == 254))
            return false;
        // Conservative special-purpose fence: DNS does not silently grant shared, translation, documentation or benchmark networks.
        // Numeric profile grants may name these explicitly; link-local, unspecified and multicast remain unconditionally denied above.
        var local = IPAddress.IsLoopback(address) || address.IsIPv6SiteLocal ||
            address.AddressFamily == AddressFamily.InterNetworkV6 && ((bytes[0] & 0xe0) != 0x20 ||
                bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] < 2 || bytes[2] == 0x0d && bytes[3] == 0xb8) ||
                bytes[0] == 0x3f && (bytes[1] & 0xf0) == 0xf0) ||
            address.AddressFamily == AddressFamily.InterNetwork && (bytes[0] == 10 ||
                bytes[0] == 100 && bytes[1] is >= 64 and <= 127 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2 || bytes[1] == 88 && bytes[2] == 99) ||
                bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100) ||
                bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        return !local || IPAddress.TryParse(host, out var explicitAddress) && explicitAddress.Equals(address) ||
            policy.AllowedDomains.Any(domain => IPAddress.TryParse(domain, out var allowed) && allowed.Equals(address));
    }

    internal static BrowserRejectedException? Rejection(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is BrowserRejectedException rejected) return rejected;
        return null;
    }
}

internal static class BrowserPageText
{
    internal static Encoding EncodingFor(string? charset) => charset?.Trim().Trim('"').ToLowerInvariant() switch
    {
        null or "" or "utf-8" or "utf8" => new UTF8Encoding(false, true),
        "us-ascii" or "ascii" => Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        "iso-8859-1" or "latin1" => Encoding.Latin1,
        _ => throw new InvalidDataException("The response character encoding is unsupported.")
    };
    internal static bool AllowsMedia(string? media) => media is not null && media.Length <= 128 &&
        (media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
         media.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
         media.Equals("application/xml", StringComparison.OrdinalIgnoreCase) ||
         media.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));
    internal static string Clip(string text, int maximum, out bool truncated)
    {
        var builder = new StringBuilder(); var bytes = 0; truncated = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximum) { truncated = true; break; }
            builder.Append(rune); bytes += rune.Utf8SequenceLength;
        }
        return builder.ToString();
    }
}
