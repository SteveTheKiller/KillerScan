using System.Net;

namespace KillerScan.Engine;

/// <summary>One observed or suggested endpoint. Authentication applies only to the requested resource.</summary>
public sealed record AccessEndpoint
{
    public string Protocol { get; init; } = "tcp";
    public int Port { get; init; }
    public string Verification { get; init; } = "PortHint";
    public string Authentication { get; init; } = "Unknown";
    public int? HttpStatus { get; init; }
    public string? Challenge { get; init; }
    public bool Redirect { get; init; }
    public DateTimeOffset? ObservedAtUtc { get; init; }
}

public static class DeviceAccess
{
    public static string? SuggestedWebProtocol(int port) => port switch
    {
        443 or 5001 or 8006 or 8443 => "https",
        80 or 5000 or 5357 or 8080 or 8123 or 32400 => "http",
        _ => null
    };

    /// <summary>No I/O. Old records have suggestions, never retroactively verified observations.</summary>
    public static IReadOnlyList<AccessEndpoint> ForDevice(NetworkDevice device)
    {
        if (!IPAddress.TryParse(device.IpAddress, out _)) return [];
        var result = (device.Access ?? []).Where(item => Address(device.IpAddress, item) != null)
            .GroupBy(item => (item.Protocol, item.Port)).Select(group => group.Last()).ToList();
        foreach (int port in device.OpenPorts.Distinct().OrderBy(port => port))
        {
            if (port is < 1 or > 65535 || result.Any(item => item.Port == port)) continue;
            string protocol = SuggestedWebProtocol(port) ?? (port == 22 ? "ssh" : port == 3389 ? "rdp" : "tcp");
            bool ssh = port == 22 && device.SshBanner.StartsWith("SSH-", StringComparison.Ordinal);
            result.Add(new AccessEndpoint { Protocol = protocol, Port = port,
                Verification = ssh ? "SshBanner" : device.ObservationCompletedAtUtc != null ? "TcpOpen" : "PortHint",
                ObservedAtUtc = device.ObservationCompletedAtUtc });
        }
        return result.OrderBy(item => item.Port).ThenBy(item => item.Protocol, StringComparer.Ordinal).ToArray();
    }

    public static string? Address(string ip, AccessEndpoint endpoint)
    {
        if (!IPAddress.TryParse(ip, out var address) || endpoint.Port is < 1 or > 65535 ||
            endpoint.Protocol is not ("http" or "https" or "ssh" or "rdp" or "tcp")) return null;
        string host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "[" + address + "]" : address.ToString();
        return endpoint.Protocol + "://" + host + ":" + endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + (endpoint.Protocol is "http" or "https" ? "/" : "");
    }

    public static AccessEndpoint? Web(NetworkDevice device, int? port = null) => ForDevice(device)
        .Where(item => item.Protocol is "http" or "https" && (!port.HasValue || item.Port == port.Value))
        .OrderByDescending(item => item.Verification == "HttpResponse").ThenByDescending(item => item.Protocol == "https").FirstOrDefault();
}
