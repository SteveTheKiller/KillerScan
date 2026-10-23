namespace KillerScan.Engine
{
    /// <summary>
    /// Immutable choices for one scan. Defaults preserve the existing scan behavior. Turning off
    /// a probe prevents that application-level operation; other enabled traffic can still cause
    /// the operating system to resolve neighbors. This is not an interface or routing policy.
    /// </summary>
    public sealed record ScanOptions
    {
        public bool NeighborCache { get; init; } = true;
        public bool Arp { get; init; } = true;
        public bool Icmp { get; init; } = true;
        public bool TcpLiveness { get; init; } = true;
        public bool ReverseDns { get; init; } = true;
        public bool Mdns { get; init; } = true;
        public bool Ssdp { get; init; } = true;
        public bool Ports { get; init; } = true;
        public bool Http { get; init; } = true;
        public bool Tls { get; init; } = true;
        public bool Ssh { get; init; } = true;
        public bool Snmp { get; init; } = true;
        public bool Netbios { get; init; } = true;

        /// <summary>Stable, language-independent names shared by the GUI and command line.</summary>
        public static IReadOnlyList<string> Features { get; } = Array.AsReadOnly(new[]
        {
            "neighbor-cache", "arp", "icmp", "tcp-liveness", "dns", "mdns", "ssdp",
            "ports", "http", "tls", "ssh", "snmp", "netbios"
        });

        public bool IsEnabled(string feature) => feature switch
        {
            "neighbor-cache" => NeighborCache, "arp" => Arp, "icmp" => Icmp,
            "tcp-liveness" => TcpLiveness, "dns" => ReverseDns, "mdns" => Mdns,
            "ssdp" => Ssdp, "ports" => Ports, "http" => Http, "tls" => Tls,
            "ssh" => Ssh, "snmp" => Snmp, "netbios" => Netbios,
            _ => throw new ArgumentException("Unknown scan feature: " + feature, nameof(feature))
        };

        public ScanOptions WithFeature(string feature, bool enabled) => feature switch
        {
            "neighbor-cache" => this with { NeighborCache = enabled },
            "arp" => this with { Arp = enabled }, "icmp" => this with { Icmp = enabled },
            "tcp-liveness" => this with { TcpLiveness = enabled },
            "dns" => this with { ReverseDns = enabled }, "mdns" => this with { Mdns = enabled },
            "ssdp" => this with { Ssdp = enabled }, "ports" => this with { Ports = enabled },
            "http" => this with { Http = enabled }, "tls" => this with { Tls = enabled },
            "ssh" => this with { Ssh = enabled }, "snmp" => this with { Snmp = enabled },
            "netbios" => this with { Netbios = enabled },
            _ => throw new ArgumentException("Unknown scan feature: " + feature, nameof(feature))
        };

        /// <summary>Apply a CLI-style list. Unknown or empty names fail before starting a scan.</summary>
        public ScanOptions Without(string features)
        {
            if (features == null) throw new ArgumentNullException(nameof(features));
            var result = this;
            foreach (string name in features.Split(','))
                result = result.WithFeature(name.Trim().ToLowerInvariant(), false);
            return result;
        }

        /// <summary>Disabled features, suitable for the CLI /skip value; empty means defaults.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string DisabledFeatures => string.Join(",", Features.Where(feature => !IsEnabled(feature)));
    }
}
