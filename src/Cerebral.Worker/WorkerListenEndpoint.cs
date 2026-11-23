using System.Net;
using System.Net.Sockets;

namespace Cerebral.Worker;

internal static class WorkerListenEndpoint
{
    internal const int MaximumPortAttempts = 256;
    internal const string ReceiptFileName = "worker-endpoint.txt";

    internal static string Resolve(string configuredUrl, bool selectAvailablePort,
        Func<IPAddress, int, bool>? isAvailable = null)
    {
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var configured) ||
            configured.Scheme != Uri.UriSchemeHttp || configured.UserInfo.Length != 0 ||
            configured.Query.Length != 0 || configured.Fragment.Length != 0 ||
            configured.AbsolutePath.Trim('/').Length != 0 || configured.Port is < 1 or > 65535)
            throw new ArgumentException("Worker listen URL must be an HTTP origin with an explicit port.");

        if (!selectAvailablePort) return configured.GetLeftPart(UriPartial.Authority);

        var address = configured.Host switch
        {
            "127.0.0.1" or "localhost" => IPAddress.Loopback,
            "0.0.0.0" => IPAddress.Any,
            _ => throw new ArgumentException("Automatic Worker port selection requires a loopback or IPv4 wildcard listener.")
        };
        isAvailable ??= CanBind;
        var last = Math.Min(65535, configured.Port + MaximumPortAttempts - 1);
        for (var port = configured.Port; port <= last; port++)
            if (isAvailable(address, port))
                return $"http://{configured.Host}:{port}";
        throw new IOException($"No available Worker port was found from {configured.Port} through {last}.");
    }

    internal static void WriteReceipt(string dataDirectory, string listenUrl)
    {
        var path = Path.Combine(dataDirectory, ReceiptFileName);
        var pending = path + ".pending";
        File.WriteAllText(pending, listenUrl + Environment.NewLine);
        File.Move(pending, path, true);
    }

    private static bool CanBind(IPAddress address, int port)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = true
            };
            socket.Bind(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
