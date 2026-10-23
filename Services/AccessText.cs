using System.Globalization;
using System.Windows;
using KillerScan.Engine;

namespace KillerScan.Services;

internal static class AccessText
{
    internal static string L(string key) => Application.Current?.TryFindResource("Str_Access_" + key) as string ?? key switch
    {
        "Title" => "Access", "None" => "No access endpoints are available.", "Authentication" => "Authentication",
        "Unknown" => "Unknown", "Required" => "Server requested authentication", "LoginPage" => "Password form detected",
        "HttpResponse" => "HTTP response received at this address", "SshBanner" => "SSH banner received",
        "TcpOpen" => "TCP port open; protocol suggested by port", "PortHint" => "Address suggested by port; no stored verification",
        "Redirect" => "Redirect response; destination was not followed", "NoTime" => "Observation time unavailable",
        "Note" => "Opening makes a new connection. HTTP 200 does not prove access without login. HTTPS certificates were not validated. Login evidence applies only to the requested address.",
        _ => key
    };
    internal static string Describe(AccessEndpoint endpoint) => L(endpoint.Verification) + Environment.NewLine
        + L("Authentication") + ": " + L(endpoint.Authentication)
        + (endpoint.HttpStatus.HasValue ? " · HTTP " + endpoint.HttpStatus.Value.ToString(CultureInfo.InvariantCulture) : "")
        + (endpoint.Challenge == null ? "" : " · WWW-Authenticate: " + endpoint.Challenge)
        + (endpoint.Redirect ? Environment.NewLine + L("Redirect") : "") + Environment.NewLine
        + (endpoint.ObservedAtUtc?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
            ?? L("NoTime"));
    internal static string DescribeDevice(NetworkDevice device) => string.Join(Environment.NewLine + Environment.NewLine,
        DeviceAccess.ForDevice(device).Select(endpoint => DeviceAccess.Address(device.IpAddress, endpoint) + Environment.NewLine + Describe(endpoint)));
}
