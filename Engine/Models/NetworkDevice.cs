using System.Net;

namespace KillerScan.Engine
{
    public class NetworkDevice
    {
        public string IpAddress { get; set; } = string.Empty;
        public string Hostname { get; set; } = string.Empty;
        public string MacAddress { get; set; } = string.Empty;
        public string Vendor { get; set; } = string.Empty;
        public string DeviceType { get; set; } = "Unknown";

        /// <summary>
        /// The type as it appears on screen. Sorting the Type column reads this rather than the
        /// English key behind it, so the order the grid shows is alphabetical in the language the
        /// reader is actually looking at. Resolved on each read, not cached, because a locale
        /// change rewrites every one of these without touching the devices themselves.
        /// The host supplies the translation through <see cref="DeviceTypeDisplayResolver"/>;
        /// without one the English key is shown.
        /// </summary>
        public string DeviceTypeDisplay => DeviceTypeDisplayResolver?.Invoke(DeviceType) ?? DeviceType;

        /// <summary>Turns a stored device type key into display text. Set once by the host.</summary>
        public static Func<string, string>? DeviceTypeDisplayResolver { get; set; }
        public List<int> OpenPorts { get; set; } = [];

        /// <summary>
        /// Why the device got its type, set with <see cref="DeviceType"/> by the scanner.
        /// Null for devices that were never assessed, such as older saved results.
        /// </summary>
        public DeviceRecognition? Recognition { get; set; }

        /// <summary>
        /// Web endpoints that answered during the scan. Other open ports are added by
        /// <see cref="DeviceAccess.ForDevice"/>. Null when no web response was recorded.
        /// </summary>
        public List<AccessEndpoint>? Access { get; set; }

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
