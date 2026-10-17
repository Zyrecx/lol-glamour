using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Glamour.Host.Auth;

/// <summary>The PC's private IPv4 addresses, best guess first. Used to build the QR address.</summary>
public static class LanAddress
{
    public static IReadOnlyList<IPAddress> Candidates()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && Rank(a.Address) < 9)
                    .Select(a => (a.Address, Rank: Rank(a.Address) + (IsVirtual(n) ? 5 : 0))))
                .OrderBy(x => x.Rank).Select(x => x.Address).Distinct().ToList();
        }
        catch (NetworkInformationException) { return []; }
    }

    /// <summary>
    /// The PC's mDNS name (<c>MYPC.local</c>), which does not change when DHCP hands out a new address. Windows answers for it natively and
    /// iOS/Android resolve it. Null when the machine name is not a valid DNS label. The session cookie belongs to this host, so using it
    /// in the QR keeps a paired phone signed in across IP changes.
    /// </summary>
    public static string? MdnsName()
    {
        try
        {
            var name = Dns.GetHostName();
            return name.Length is > 0 and <= 63 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && name[0] != '-' && name[^1] != '-'
                ? name.ToLowerInvariant() + ".local" : null;
        }
        catch (SocketException) { return null; }
    }

    /// <summary>Hosts a phone can try, best first: raw addresses (work everywhere), then the mDNS name (iOS, and Android 12+ per reports; older Android cannot resolve .local).</summary>
    public static IReadOnlyList<string> Hosts() =>
        [.. Candidates().Select(a => a.ToString()), .. (MdnsName() is { } n ? [n] : Array.Empty<string>())];

    // Home LAN ranges first, Tailscale (100.64/10) next, anything else is ignored.
    private static int Rank(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 192 && b[1] == 168 ? 0
            : b[0] == 10 ? 1
            : b[0] == 172 && b[1] is >= 16 and <= 31 ? 2
            : b[0] == 100 && b[1] is >= 64 and <= 127 ? 3
            : 9;
    }

    private static bool IsVirtual(NetworkInterface n) =>
        new[] { "vEthernet", "VirtualBox", "VMware", "Hyper-V", "WSL", "Docker" }.Any(s => n.Name.Contains(s, StringComparison.OrdinalIgnoreCase) || n.Description.Contains(s, StringComparison.OrdinalIgnoreCase));
}
