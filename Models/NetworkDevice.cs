using System.Net;

namespace KillerScan.Models
{
    public class NetworkDevice
    {
        public string IpAddress { get; set; } = string.Empty;
        public string Hostname { get; set; } = string.Empty;
        public string MacAddress { get; set; } = string.Empty;
        public string Vendor { get; set; } = string.Empty;
        public string DeviceType { get; set; } = "Unknown";
        public List<int> OpenPorts { get; set; } = [];

        // -- Active fingerprint fields (populated by scanner) --
        public int? Ttl { get; set; }
        public string HttpTitle { get; set; } = string.Empty;
        public string HttpServer { get; set; } = string.Empty;
        public string SshBanner { get; set; } = string.Empty;
        public string TlsSubject { get; set; } = string.Empty;
        public string SmbOs { get; set; } = string.Empty;
        public string SnmpDescr { get; set; } = string.Empty;
        public string NetbiosName { get; set; } = string.Empty;
        public List<string> MdnsServices { get; set; } = [];
        public string SsdpServer { get; set; } = string.Empty;

        public string OpenPortsDisplay =>
            OpenPorts.Count > 0 ? string.Join(", ", OpenPorts) : "-";

        /// <summary>
        /// Numeric IP value for proper sorting.
        /// </summary>
        public uint IpSortKey
        {
            get
            {
                if (IPAddress.TryParse(IpAddress, out var addr))
                {
                    var bytes = addr.GetAddressBytes();
                    return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
                }
                return 0;
            }
        }
    }
}
