using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;

namespace KillerScan.Engine
{
    /// <summary>What a scan actually saw at an endpoint. None of these means a login would work.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AccessEvidence
    {
        /// <summary>The port accepted a connection. The protocol is a guess from the port number.</summary>
        PortOpen,
        /// <summary>A web server answered at the endpoint's root address.</summary>
        HttpResponse,
        /// <summary>An SSH server sent its banner. No login was attempted.</summary>
        SshBanner,
    }

    /// <summary>What the endpoint said about signing in, for the root address only.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AccessLogin
    {
        /// <summary>Nothing either way. A 200 page or an open port does not mean no login is needed.</summary>
        Unknown,
        /// <summary>The server answered 401 and asked for credentials.</summary>
        Requested,
        /// <summary>The page has a form with a password field.</summary>
        LoginForm,
    }

    /// <summary>One way into a device: a protocol and port, and the evidence for it.</summary>
    public sealed class AccessEndpoint
    {
        /// <summary>http, https, ssh, rdp or tcp.</summary>
        public string Protocol { get; set; } = "tcp";
        public int Port { get; set; }
        public AccessEvidence Evidence { get; set; }
        public int? HttpStatus { get; set; }
        public AccessLogin Login { get; set; }
        /// <summary>The scheme a 401 asked for, such as Basic or NTLM. Realms and other values are not kept.</summary>
        public string AuthScheme { get; set; } = string.Empty;
        /// <summary>When the response was seen, in UTC. Null for endpoints known only from an open port.</summary>
        public DateTimeOffset? ObservedAt { get; set; }
    }

    /// <summary>
    /// Builds a device's access list from what a scan recorded. Reading it sends nothing: the
    /// list is the recorded web responses plus every other open port, labeled by what is known.
    /// </summary>
    public static class DeviceAccess
    {
        /// <summary>The web protocol usually served on a port, or null when there is none.</summary>
        public static string? SuggestedWebProtocol(int port) => port switch
        {
            443 or 5001 or 8006 or 8443 => "https",
            80 or 5000 or 5357 or 8080 or 8123 or 32400 => "http",
            _ => null,
        };

        private static readonly string[] Protocols = ["http", "https", "ssh", "rdp", "tcp"];

        /// <summary>Every endpoint for the device, ordered by port. Empty when its address is not numeric.</summary>
        public static IReadOnlyList<AccessEndpoint> ForDevice(NetworkDevice device)
        {
            if (!IPAddress.TryParse(device.IpAddress, out _)) return [];
            var list = (device.Access ?? [])
                .Where(e => e.Port is >= 1 and <= 65535 && Protocols.Contains(e.Protocol))
                .GroupBy(e => (e.Protocol, e.Port)).Select(g => g.Last()).ToList();
            foreach (int port in device.OpenPorts.Distinct())
            {
                if (port is < 1 or > 65535 || list.Any(e => e.Port == port)) continue;
                string protocol = SuggestedWebProtocol(port) ?? (port == 22 ? "ssh" : port == 3389 ? "rdp" : "tcp");
                bool banner = port == 22 && device.SshBanner.StartsWith("SSH-", StringComparison.Ordinal);
                list.Add(new AccessEndpoint { Protocol = protocol, Port = port, Evidence = banner ? AccessEvidence.SshBanner : AccessEvidence.PortOpen });
            }
            return [.. list.OrderBy(e => e.Port).ThenBy(e => e.Protocol, StringComparer.Ordinal)];
        }

        /// <summary>
        /// The address to open or copy, such as https://192.0.2.10:8443/. Built only from the
        /// device's numeric address and the endpoint, never from anything the device sent.
        /// </summary>
        public static string? Address(string ip, AccessEndpoint endpoint)
        {
            if (!IPAddress.TryParse(ip, out var address) || endpoint.Port is < 1 or > 65535 || !Protocols.Contains(endpoint.Protocol))
                return null;
            string host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();
            string port = endpoint.Port.ToString(CultureInfo.InvariantCulture);
            return endpoint.Protocol is "http" or "https" ? endpoint.Protocol + "://" + host + ":" + port + "/" : host + ":" + port;
        }

        /// <summary>
        /// The web endpoint to open by default: one that answered, HTTPS first, then a web port
        /// guessed from its number. Null when the device has no web port at all.
        /// </summary>
        public static AccessEndpoint? BestWeb(NetworkDevice device, int? port = null) => ForDevice(device)
            .Where(e => e.Protocol is "http" or "https" && (port == null || e.Port == port))
            .OrderByDescending(e => e.Evidence == AccessEvidence.HttpResponse)
            .ThenByDescending(e => e.Protocol == "https")
            .ThenBy(e => e.Port)
            .FirstOrDefault();
    }
}
