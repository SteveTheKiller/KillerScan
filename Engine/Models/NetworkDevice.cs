using System.Net;

namespace KillerScan.Engine
{
    public class NetworkDevice
    {
        public string IpAddress { get; set; } = string.Empty;

        // Every value below that a device can choose is length-bounded by DeviceText, whether it
        // arrives from a probe or from saved history.
        private string _hostname = string.Empty, _httpTitle = string.Empty, _httpServer = string.Empty,
            _sshBanner = string.Empty, _tlsSubject = string.Empty, _smbOs = string.Empty, _snmpDescr = string.Empty,
            _netbiosName = string.Empty, _ssdpServer = string.Empty;

        public string Hostname { get => _hostname; set => _hostname = DeviceText.Clean(value, DeviceText.NameLength); }
        public string MacAddress { get; set; } = string.Empty;
        // Never null, whatever a saved or imported file holds: the classifier and filters read them.
        private string _vendor = string.Empty;
        private List<int> _openPorts = [];
        private List<string> _mdnsServices = [];
        public string Vendor { get => _vendor; set => _vendor = value ?? string.Empty; }
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
        public List<int> OpenPorts { get => _openPorts; set => _openPorts = value ?? []; }

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
        public string HttpTitle { get => _httpTitle; set => _httpTitle = DeviceText.Clean(value, DeviceText.TextLength); }
        public string HttpServer { get => _httpServer; set => _httpServer = DeviceText.Clean(value, DeviceText.TextLength); }
        public string SshBanner { get => _sshBanner; set => _sshBanner = DeviceText.Clean(value, DeviceText.TextLength); }
        public string TlsSubject { get => _tlsSubject; set => _tlsSubject = DeviceText.Clean(value, DeviceText.TextLength); }
        public string SmbOs { get => _smbOs; set => _smbOs = DeviceText.Clean(value, DeviceText.TextLength); }
        public string SnmpDescr { get => _snmpDescr; set => _snmpDescr = DeviceText.Clean(value, DeviceText.TextLength); }
        public string NetbiosName { get => _netbiosName; set => _netbiosName = DeviceText.Clean(value, DeviceText.NameLength); }
        public List<string> MdnsServices { get => _mdnsServices; set => _mdnsServices = value ?? []; }
        public string SsdpServer { get => _ssdpServer; set => _ssdpServer = DeviceText.Clean(value, DeviceText.TextLength); }

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
