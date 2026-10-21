using System.Text.Json.Serialization;

namespace KillerScan.Engine
{
    /// <summary>
    /// How sure the classifier is of a device's type. A grade, not a probability, and never a
    /// claim that the device is authentic: every protocol string it reads is self-reported.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RecognitionConfidence
    {
        /// <summary>Only weak hints: a name, a MAC vendor, a port pattern, or a fallback guess.</summary>
        Low,
        /// <summary>One protocol answer supports the type, or several weak hints add up strongly.</summary>
        Medium,
        /// <summary>Two or more different protocols agree and nothing close disagrees.</summary>
        High,
        /// <summary>A technician set the type. The automatic result is kept alongside.</summary>
        Manual,
    }

    /// <summary>
    /// Why a device got its type: the signals behind it, other types it could be, and how sure
    /// the classifier is. Old saved scans have none, which reads as "no evidence recorded".
    /// </summary>
    public sealed class DeviceRecognition
    {
        /// <summary>The type shown for the device: the manual type when one is set.</summary>
        public string Type { get; set; } = "Unknown";
        public RecognitionConfidence Confidence { get; set; }

        /// <summary>What the evidence alone says, even when a manual type overrides it.</summary>
        public string AutomaticType { get; set; } = "Unknown";
        public RecognitionConfidence AutomaticConfidence { get; set; }

        /// <summary>Signals that support <see cref="AutomaticType"/>, strongest first.</summary>
        public List<RecognitionSignal> Signals { get; set; } = [];

        /// <summary>Other types the evidence pointed to, strongest first.</summary>
        public List<RecognitionCandidate> Alternatives { get; set; } = [];

        /// <summary>True when an unrelated type scored close to the winner.</summary>
        public bool Conflict { get; set; }
    }

    /// <summary>
    /// One observation that counted toward a type. <see cref="Source"/> is a stable,
    /// language-independent family name (see <see cref="RecognitionSources"/>), and
    /// <see cref="Detail"/> is the value seen, such as a port list or an HTTP title. For
    /// <see cref="RecognitionSources.Fallback"/> the detail is a stable rule key instead.
    /// </summary>
    public sealed class RecognitionSignal
    {
        public string Source { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public int Weight { get; set; }
    }

    public sealed class RecognitionCandidate
    {
        public string Type { get; set; } = string.Empty;
        public int Score { get; set; }
    }

    /// <summary>The source families a signal can come from.</summary>
    public static class RecognitionSources
    {
        public const string Manual = "manual";
        public const string Gateway = "gateway";
        public const string Hostname = "hostname";
        public const string Vendor = "vendor";
        public const string Ports = "ports";
        public const string Ttl = "ttl";
        public const string Http = "http";
        public const string Tls = "tls";
        public const string Ssh = "ssh";
        public const string Snmp = "snmp";
        public const string NetBios = "netbios";
        public const string Mdns = "mdns";
        public const string Ssdp = "ssdp";
        public const string Fallback = "fallback";

        /// <summary>
        /// Families where the device itself answered a protocol. Names, vendors, ports and TTLs
        /// are inferences from the outside and count as weak hints.
        /// </summary>
        public static bool IsProtocol(string source) =>
            source is Http or Tls or Ssh or Snmp or NetBios or Mdns or Ssdp;
    }

    /// <summary>Stable keys for the fallback rules used when no type scored high enough.</summary>
    public static class RecognitionRules
    {
        public const string PrivateMacFewPorts = "private-mac-few-ports";
        public const string SshPort = "ssh-port";
        public const string WindowsPorts = "windows-ports";
        public const string WebPort = "web-port";
        public const string PcVendorNoPorts = "pc-vendor-no-ports";
        public const string NoPortsWithMac = "no-ports-with-mac";
        public const string NoEvidence = "no-evidence";
        public const string OpenPortsOnly = "open-ports-only";
    }
}
