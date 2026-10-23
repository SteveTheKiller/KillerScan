using System.Net;
using System.Net.NetworkInformation;

namespace KillerScan.Engine
{
    public partial class NetworkScanner
    {
        private readonly ScanProbes _probes;
        public NetworkScanner() : this(new ScanProbes()) { }
        internal NetworkScanner(ScanProbes probes) => _probes = probes;

        // One internal boundary for probe I/O lets regression tests verify that a disabled
        // operation is never started, without sending discovery traffic to a real network.
        internal class ScanProbes
        {
            internal virtual IReadOnlyDictionary<string, string> ReadNeighbors() => GetArpCache();
            internal virtual string ResolveMac(IPAddress address) => GetMacAddress(address);
            internal virtual void FlushDns() => FlushLocalDnsCache();
            internal virtual Task<string> Hostname(IPAddress address) => ResolveVerifiedHostnameAsync(address);
            internal virtual async Task<int?> Ping(IPAddress address, int timeout)
            {
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(address, timeout);
                    return reply.Status == IPStatus.Success ? reply.Options?.Ttl ?? 0 : null;
                }
                catch { return null; }
            }
            internal virtual Task<bool> TcpAlive(IPAddress address) => TcpAliveAsync(address);
            internal virtual Task<bool> Connect(IPAddress address, int port, int timeout) => TryConnectAsync(address, port, timeout);
            internal virtual Task<Dictionary<string, MulticastDiscovery.MdnsInfo>> Mdns(CancellationToken ct) => MulticastDiscovery.CollectMdnsAsync(ct);
            internal virtual Task<Dictionary<string, string>> Ssdp(CancellationToken ct) => MulticastDiscovery.CollectSsdpAsync(ct);
            internal virtual Task Http(NetworkDevice device, IPAddress address, bool deep, CancellationToken token = default) => ProbeHttpWithCancellationAsync(device, address, deep, token);
            internal virtual Task Tls(NetworkDevice device, IPAddress address, bool deep) => ProbeTlsCertAsync(device, address, deep);
            internal virtual Task Ssh(NetworkDevice device, IPAddress address) => ProbeSshBannerAsync(device, address);
            internal virtual Task Snmp(NetworkDevice device, IPAddress address) => ProbeSnmpAsync(device, address);
            internal virtual Task Netbios(NetworkDevice device, IPAddress address) => ProbeNetbiosAsync(device, address);
        }
    }
}
