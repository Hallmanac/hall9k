using System.Net;
using System.Net.Sockets;

namespace Hall9k.Connectors.Processes;

/// <summary>
/// A free TCP port on this machine, for a local launch that has somewhere in its command to put
/// one (idea b9b09779, piece 5). The operating system's own answer, taken by binding port 0 and
/// reading back what it assigned, rather than a number this platform picked and hoped about: a
/// reviewer's machine already has their own work running on it, and a review launch that seized a
/// project's default port would take down whatever they were already looking at.
/// <para>
/// There is a race here and it is not closable: the listener is released before the product binds,
/// so something else on the machine could take the port in between. It is the same race every
/// port-0 allocation in every tool has, and the alternative — holding the socket open and handing
/// the child a port it then cannot bind — is strictly worse.
/// </para>
/// </summary>
public static class EphemeralPort
{
    /// <summary>A port nothing was listening on a moment ago.</summary>
    public static int Free()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
