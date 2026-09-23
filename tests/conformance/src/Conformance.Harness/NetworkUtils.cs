using System.Net.Sockets;

namespace Conformance.Harness;

public static class NetworkUtils
{
    /// <summary>
    /// Reserves a free TCP port on loopback by binding then immediately releasing it. There is
    /// an inherent (tiny) TOCTOU race between release and the real bind by Kestrel/the Python
    /// process — acceptable for a local test harness; retried callers are not needed in
    /// practice because nothing else on a CI runner or dev box is racing for the same port.
    /// </summary>
    public static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
