using KillerScan.Engine;

namespace KillerScan.Services
{
    /// <summary>
    /// The evidence for which network a run was on. An address range is not that evidence: two
    /// sites can both be 192.168.1.0/24. The gateway's MAC is that one router's, and it means the
    /// same on any PC. The adapter ID survives renaming the adapter and normally belongs to one PC,
    /// though PCs cloned from one image can share it, so it only covers a run with no gateway to
    /// read, such as one behind a VPN.
    /// </summary>
    internal sealed record ScanNetwork(string GatewayMac, string AdapterId)
    {
        internal static readonly ScanNetwork Unknown = new(string.Empty, string.Empty);

        /// <summary>
        /// Works out the network of targets scanned through the named adapter. The gateway is read
        /// only when every target sits directly on that adapter, because the PC's own router says
        /// nothing about a site at the far end of a route. It comes from
        /// <paramref name="results"/> (IP to MAC) when the scan found it, otherwise from the ARP
        /// entries Windows already has for that adapter, which sends no traffic. Tests supply
        /// their own adapter lookups.
        /// </summary>
        internal static ScanNetwork Identify(string targets, string adapterName, IReadOnlyDictionary<string, string> results,
            Func<string, ScanBinding?>? find = null, Func<ScanBinding, string>? gatewayOf = null, Func<ScanBinding, string>? idOf = null)
        {
            var adapter = adapterName.Length == 0 ? null : (find ?? ScanBinding.Find)(adapterName);
            if (adapter == null) return Unknown;
            string id = (idOf ?? (binding => binding.AdapterId()))(adapter);
            var parsed = ScanTargets.Parse(targets);
            string gateway = parsed.Ok && parsed.Addresses.Count > 0 && parsed.Addresses.All(adapter.Contains)
                ? (gatewayOf ?? (binding => binding.Gateway()))(adapter) : string.Empty;
            if (gateway.Length == 0) return new(string.Empty, id);
            string mac = results.TryGetValue(gateway, out var found) ? found
                : adapter.Neighbors().TryGetValue(gateway, out var cached) ? cached : string.Empty;
            return new(LocationInfo.NormalizeMac(mac), id);
        }

        /// <summary>The adapter Windows would route these targets through, by its name, or empty.</summary>
        internal static string RouteAdapter(string targets)
        {
            var parsed = ScanTargets.Parse(targets);
            if (!parsed.Ok || parsed.Addresses.Count == 0) return string.Empty;
            try { return NetworkPlatform.Current.BestRoute(parsed.Addresses[0])?.Interface ?? string.Empty; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return string.Empty; }
        }

        /// <summary>The IP-to-MAC map <see cref="Identify"/> takes, from a scan's results.</summary>
        internal static Dictionary<string, string> Macs(IEnumerable<NetworkDevice> devices) =>
            devices.Where(device => device.MacAddress.Length > 0).GroupBy(device => device.IpAddress)
                .ToDictionary(group => group.Key, group => group.First().MacAddress);
    }
}
