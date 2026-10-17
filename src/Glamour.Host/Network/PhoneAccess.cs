using System.Net;
using System.Net.Sockets;

namespace Glamour.Host.Network;

/// <summary>
/// Whether phones can reach this host. <see cref="Requested"/> is the user's choice at startup ("Allow phone access" or Glamour:AllowLan);
/// <see cref="Blocked"/> is set when another program already holds the phone port, so Glamour still starts and says so instead of failing.
/// </summary>
public sealed class PhoneAccess(bool requested)
{
    public bool Requested { get; } = requested;
    public volatile bool Blocked;
    public bool Active => Requested && !Blocked;

    /// <summary>True when nothing is listening on the port on any address (a brief bind attempt).</summary>
    public static bool PortFree(int port)
    {
        try
        {
            using var l = new TcpListener(IPAddress.Any, port);
            l.Start();
            return true;
        }
        catch (SocketException) { return false; }
    }
}
