using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Cerebral.Integrator;

// This is local routing-table evidence, not a worker probe, throughput test, or connection policy.
internal static class WorkerNetworkRoute
{
    internal sealed record Route(string InterfaceName, string Subnet, long LinkSpeed, bool OnLink);
    internal sealed record Endpoint(string Display, IPAddress? Address, string? Diagnostic);

    internal static Endpoint ParseEndpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new("Invalid worker endpoint", null, "Edit the worker connection to set an HTTP or HTTPS address.");
        // A registration can contain credentials or query data; neither belongs in a selector or diagnostic.
        var display = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        if (!IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address))
            return new(display, null, "Hostname route not inspected. A numeric worker address makes the subnet explicit.");
        if (IPAddress.IsLoopback(address))
            return new(display, null, "Loopback connection on this machine.");
        return new(display, address, null);
    }

    internal static string Describe(string url)
    {
        var endpoint = ParseEndpoint(url);
        if (endpoint.Diagnostic is { } diagnostic) return diagnostic;
        var destination = endpoint.Address!;
        try
        {
            var error = GetBestInterfaceEx(SocketAddressBytes(destination), out var index);
            if (error != 0) return $"Windows route unavailable (code {error}). Route information does not verify worker reachability.";
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    var properties = adapter.GetIPProperties();
                    var adapterIndex = destination.AddressFamily == AddressFamily.InterNetwork
                        ? properties.GetIPv4Properties()?.Index : properties.GetIPv6Properties()?.Index;
                    if (adapterIndex != index) continue;
                    var addresses = properties.UnicastAddresses
                        .Where(item => item.Address.AddressFamily == destination.AddressFamily).ToArray();
                    var onLink = addresses.Where(item => Contains(item.Address, item.PrefixLength, destination))
                        .OrderByDescending(item => item.PrefixLength).FirstOrDefault();
                    var local = onLink ?? addresses.FirstOrDefault();
                    if (local is null)
                        return $"Windows route: {adapter.Name}. Local subnet is unavailable; route information does not verify worker reachability.";
                    return Format(new(adapter.Name, Subnet(local.Address, local.PrefixLength), adapter.Speed, onLink is not null));
                }
                catch (NetworkInformationException) { /* Another adapter may have changed while enumerating. */ }
            }
            return "Windows route interface is unavailable. Route information does not verify worker reachability.";
        }
        catch (Exception error) when (error is NetworkInformationException or SocketException or
                                      DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            return "Windows route information is unavailable. Route information does not verify worker reachability.";
        }
    }

    internal static string Format(Route route)
    {
        var speed = route.LinkSpeed > 0
            ? " · " + (route.LinkSpeed >= 1_000_000_000
                ? (route.LinkSpeed / 1_000_000_000d).ToString("0.##", CultureInfo.CurrentCulture) + " Gbit/s"
                : (route.LinkSpeed / 1_000_000d).ToString("0.##", CultureInfo.CurrentCulture) + " Mbit/s") + " adapter link"
            : "";
        return $"Windows route: {route.InterfaceName} · local subnet {route.Subnet}{speed}" +
               (route.OnLink ? " · on-link destination." : " · routed destination; the worker may be on another subnet.") +
               " Route information does not verify worker reachability.";
    }

    internal static string Subnet(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        if (prefix < 0 || prefix > bytes.Length * 8) throw new ArgumentOutOfRangeException(nameof(prefix));
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xff << (8 - bits));
        }
        return new IPAddress(bytes) + "/" + prefix;
    }

    internal static bool Contains(IPAddress local, int prefix, IPAddress destination) =>
        local.AddressFamily == destination.AddressFamily && Subnet(local, prefix) == Subnet(destination, prefix);

    internal static byte[] SocketAddressBytes(IPAddress address)
    {
        var ipv4 = address.AddressFamily == AddressFamily.InterNetwork;
        var result = new byte[ipv4 ? 16 : 28];
        BitConverter.GetBytes((ushort)address.AddressFamily).CopyTo(result, 0);
        address.GetAddressBytes().CopyTo(result, ipv4 ? 4 : 8);
        if (!ipv4) BitConverter.GetBytes(checked((uint)address.ScopeId)).CopyTo(result, 24);
        return result;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterfaceEx([In] byte[] destination, out uint interfaceIndex);
}
