using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PhoneCam.Desktop.Hosting;

public sealed record NetworkCandidate(string InterfaceId, string InterfaceName, IPAddress Address, int Score, bool? IsPublicNetwork, bool IsIphoneUsb)
{
    public string Label => $"{Address}   ·   {(IsIphoneUsb ? "iPhone przez kabel USB" : InterfaceName)}";
}

/// <summary>
/// Picks the IPv4 address a phone is most likely to reach: an iPhone on the USB cable first (lowest, steadiest
/// latency), then the home Wi-Fi/Ethernet; virtual adapters rank last.
/// </summary>
internal static class NetworkCandidates
{
    private static readonly string[] VirtualMarkers =
        ["hyper-v", "vethernet", "virtualbox", "vmware", "wsl", "tap-", "vpn", "wireguard", "tailscale", "zerotier", "bluetooth", "npcap", "docker"];

    public static IReadOnlyList<NetworkCandidate> Discover()
    {
        Dictionary<Guid, bool> publicNetworks = NetworkCategories.ReadPublicFlags();
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(n =>
            {
                IPInterfaceProperties properties = n.GetIPProperties();
                bool hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                bool? isPublic = Guid.TryParse(n.Id, out Guid id) && publicNetworks.TryGetValue(id, out bool flag) ? flag : null;
                return properties.UnicastAddresses
                    .Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !IsLinkLocal(a))
                    .Select(a => new NetworkCandidate(n.Id, n.Name, a, Score(n, a, hasGateway), isPublic, IphoneUsb.IsAdapter(n)));
            })
            .OrderByDescending(c => c.Score)
            .ToList();
    }

    private static int Score(NetworkInterface nic, IPAddress address, bool hasGateway)
    {
        int score = 0;
        if (hasGateway) score += 50;
        if (IsPrivate(address)) score += 30;
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 20;
        else if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet) score += 15;
        string text = $"{nic.Name} {nic.Description}".ToLowerInvariant();
        if (VirtualMarkers.Any(text.Contains)) score -= 100;
        if (IphoneUsb.IsAdapter(nic)) score += 100;
        return score;
    }

    public static bool IsPrivate(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static bool IsLinkLocal(IPAddress address) => address.GetAddressBytes() is [169, 254, ..];
}

/// <summary>Reads the Windows network category (Public/Private) through the Network List Manager COM API.</summary>
internal static class NetworkCategories
{
    private const int CategoryPublic = 0;

    public static Dictionary<Guid, bool> ReadPublicFlags()
    {
        var result = new Dictionary<Guid, bool>();
        try
        {
            Type? type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type is null) return result;
            dynamic manager = Activator.CreateInstance(type)!;
            foreach (dynamic connection in manager.GetNetworkConnections())
            {
                Guid adapterId = connection.GetAdapterId();
                int category = connection.GetNetwork().GetCategory();
                result[adapterId] = category == CategoryPublic;
            }
        }
        catch (Exception)
        {
            // Category is advisory only; an unknown category simply hides the warning.
        }
        return result;
    }
}
