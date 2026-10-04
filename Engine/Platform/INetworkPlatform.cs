using System.Net;

namespace KillerScan.Engine
{
    /// <summary>
    /// The operating-system calls the scanner cannot make portably: the neighbor (ARP) cache, a
    /// single-host ARP resolution, the local DNS cache flush, and the route lookup used by the
    /// connection checks. Everything else the engine does goes through System.Net.
    /// </summary>
    public interface INetworkPlatform
    {
        /// <summary>The current neighbor cache as IPv4 address to MAC (XX:XX:XX:XX:XX:XX).</summary>
        IReadOnlyDictionary<string, string> ReadNeighborCache();

        /// <summary>Resolves one host's MAC on the local segment, or an empty string.</summary>
        string ResolveMac(IPAddress address);

        /// <summary>Clears the local DNS resolver cache so reverse lookups are fresh.</summary>
        void FlushDnsCache();

        /// <summary>The interface address and next hop the OS would use for a destination.</summary>
        (string Interface, string NextHop)? BestRoute(IPAddress address);
    }

    /// <summary>Selects the platform implementation for the running OS.</summary>
    public static class NetworkPlatform
    {
        private static INetworkPlatform? _current;

        /// <summary>
        /// The platform the engine uses. Defaults to the Windows implementation on Windows and to
        /// <see cref="UnsupportedNetworkPlatform"/> elsewhere. Hosts and tests may replace it.
        /// </summary>
        public static INetworkPlatform Current
        {
            get => _current ??= CreateDefault();
            set => _current = value ?? throw new ArgumentNullException(nameof(value));
        }

        private static INetworkPlatform CreateDefault()
        {
#if NET
            if (OperatingSystem.IsWindows()) return new WindowsNetworkPlatform();
            return new UnsupportedNetworkPlatform();
#else
            return Environment.OSVersion.Platform == PlatformID.Win32NT
                ? new WindowsNetworkPlatform()
                : new UnsupportedNetworkPlatform();
#endif
        }
    }

    /// <summary>
    /// Used where no native implementation exists yet. Discovery falls back to ping and TCP
    /// liveness, and hosts are reported without MAC addresses.
    /// </summary>
    public sealed class UnsupportedNetworkPlatform : INetworkPlatform
    {
        public IReadOnlyDictionary<string, string> ReadNeighborCache() => new Dictionary<string, string>();
        public string ResolveMac(IPAddress address) => string.Empty;
        public void FlushDnsCache() { }
        public (string Interface, string NextHop)? BestRoute(IPAddress address) => null;
    }
}
