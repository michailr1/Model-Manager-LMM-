using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace LocalModelManager.Infrastructure.Network;

public sealed record LanCandidate(string Name, string Description, NetworkInterfaceType Type,
    bool IsUp, bool HasGateway, IPAddress Address, int Index);

public static class NetworkEndpoints
{
    public static LanCandidate? FindLanAddress()
    {
        var candidates = new List<LanCandidate>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = adapter.GetIPProperties();
            var gateway = properties.GatewayAddresses.Any(g => IsUsableIPv4(g.Address));
            foreach (var address in properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                candidates.Add(new(adapter.Name, adapter.Description, adapter.NetworkInterfaceType,
                    adapter.OperationalStatus == OperationalStatus.Up, gateway, address.Address,
                    properties.GetIPv4Properties()?.Index ?? int.MaxValue));
        }
        return SelectLanAddress(candidates);
    }
    public static LanCandidate? SelectLanAddress(IEnumerable<LanCandidate> candidates) => candidates
        .Where(c => c.IsUp && c.HasGateway && IsUsableIPv4(c.Address) &&
            c.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
            !Regex.IsMatch(c.Name + " " + c.Description,
                @"vpn|tunnel|\btun\d*\b|\btap\b|warp|cloudflare|wireguard|wintun|virtual|hyper-v|vethernet|tailscale|zerotier|amnezia|hiddify|sing-tun|loopback",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        .OrderBy(c => c.Type == NetworkInterfaceType.Ethernet ? 0 : 1)
        .ThenBy(c => c.Index).ThenBy(c => c.Address.ToString(), StringComparer.Ordinal).FirstOrDefault();
    public static bool IsUsableIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] is > 0 and < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }
    public static bool IsListeningOnAllIPv4(int port) => IPGlobalProperties.GetIPGlobalProperties()
        .GetActiveTcpListeners().Any(endpoint => endpoint.Port == port && endpoint.Address.Equals(IPAddress.Any));
}