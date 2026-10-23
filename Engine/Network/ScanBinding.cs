using System.Net;
using System.Net.Sockets;

namespace KillerScan.Engine
{
    public sealed class NetworkBindingException(string message) : InvalidOperationException(message);

    /// <summary>A fixed IPv4 interface/address/link snapshot. No fallback to another adapter.</summary>
    public sealed class ScanBinding
    {
        public string AdapterId { get; }
        private readonly string _sourceAddress;
        public IPAddress SourceAddress => IPAddress.Parse(_sourceAddress);
        public int InterfaceIndex { get; }
        public string Subnet { get; }
        public string Gateway { get; }
        public string DnsServer { get; }
        private readonly uint _network, _mask;
        private readonly Func<bool> _isCurrent;

        public ScanBinding(LocalNet network) : this(network, null) { }
        internal ScanBinding(LocalNet network, Func<bool>? isCurrent)
        {
            AdapterId = network.AdapterId;
            _sourceAddress = IPAddress.Parse(network.LocalIp).ToString();
            InterfaceIndex = network.InterfaceIndex;
            Subnet = network.Subnet;
            Gateway = network.Gateway;
            DnsServer = network.Dns;
            var parts = Subnet.Split('/');
            if (SourceAddress.AddressFamily != AddressFamily.InterNetwork || InterfaceIndex <= 0 || string.IsNullOrWhiteSpace(AdapterId) ||
                parts.Length != 2 || !int.TryParse(parts[1], out int prefix) || prefix < 0 || prefix > 32)
                throw new NetworkBindingException("The selected IPv4 interface is invalid.");
            _mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
            _network = Number(IPAddress.Parse(parts[0])) & _mask;
            if (!Contains(SourceAddress)) throw new NetworkBindingException("Source address is outside the selected interface subnet.");
            if (network.Wireless && network.ConnectionIdentity.Length == 0 && isCurrent == null)
                throw new NetworkBindingException("Wi-Fi network identity is unavailable. Explicit binding requires Windows access to the connected Wi-Fi identity.");
            string fingerprint = Fingerprint(network);
            _isCurrent = isCurrent ?? (() => LocalNetwork.Enumerate().Any(candidate => Fingerprint(candidate) == fingerprint));
        }

        public static ScanBinding Resolve(string adapterId, string sourceAddress)
        {
            var net = LocalNetwork.Enumerate().FirstOrDefault(n => n.AdapterId == adapterId && n.LocalIp == sourceAddress);
            return net == null ? throw new NetworkBindingException("The selected interface/address is unavailable. Choose an interface again.") : new ScanBinding(net);
        }
        public bool Contains(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork && (Number(address) & _mask) == _network;
        internal bool IsCurrent() => _isCurrent();
        private static string Fingerprint(LocalNet n) => string.Join("|", n.AdapterId, n.InterfaceIndex, n.LocalIp, n.Subnet, n.Gateway, n.Dns, n.ConnectionIdentity);
        private static uint Number(IPAddress address)
        {
            var b = address.GetAddressBytes();
            if (b.Length != 4) throw new NetworkBindingException("Only IPv4 is supported for explicit interface selection.");
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }
    }
}
