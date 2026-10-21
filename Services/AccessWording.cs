using System.Globalization;
using System.Text;
using System.Windows;

namespace KillerScan.Services
{
    /// <summary>
    /// Puts a device's access endpoints into words for the Access menu, the HTML report and CSV.
    /// The wording never claims more than was seen: a 200 page, an SSH banner or an open port is
    /// reported as exactly that, and nothing says a login works.
    /// </summary>
    internal static class AccessWording
    {
        /// <summary>The menu line, such as "HTTPS 443: sign-in page".</summary>
        internal static string Item(AccessEndpoint endpoint) =>
            string.Format(Loc("Str_Access_Item", "{0} {1}: {2}"), endpoint.Protocol.ToUpperInvariant(),
                endpoint.Port.ToString(CultureInfo.InvariantCulture), Evidence(endpoint));

        private static string Evidence(AccessEndpoint endpoint) => endpoint.Evidence switch
        {
            AccessEvidence.HttpResponse => endpoint.Login switch
            {
                AccessLogin.Requested => endpoint.AuthScheme.Length > 0
                    ? string.Format(Loc("Str_Access_LoginRequestedScheme", "asks for a login ({0})"), endpoint.AuthScheme)
                    : Loc("Str_Access_LoginRequested", "asks for a login"),
                AccessLogin.LoginForm => Loc("Str_Access_LoginForm", "sign-in page"),
                _ => string.Format(Loc("Str_Access_Answered", "answered {0}"), endpoint.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "?"),
            },
            AccessEvidence.SshBanner => Loc("Str_Access_Banner", "banner received"),
            _ => Loc("Str_Access_PortOpen", "port open"),
        };

        /// <summary>The menu item's tooltip: what the evidence means, when it was seen, and what clicking does.</summary>
        internal static string Tooltip(AccessEndpoint endpoint, bool opens)
        {
            var sb = new StringBuilder();
            switch (endpoint.Evidence)
            {
                case AccessEvidence.HttpResponse:
                    sb.Append(string.Format(Loc("Str_Access_Tip_Http", "The web server answered HTTP {0} at this address."),
                        endpoint.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "?"));
                    sb.AppendLine().Append(endpoint.Login switch
                    {
                        AccessLogin.Requested => Loc("Str_Access_Tip_Requested", "It asked for credentials. KillerScan sent none."),
                        AccessLogin.LoginForm => Loc("Str_Access_Tip_LoginForm", "The page has a password field, so it is a sign-in page."),
                        _ => Loc("Str_Access_Tip_Unknown", "Whether it needs a login is not known."),
                    });
                    break;
                case AccessEvidence.SshBanner:
                    sb.Append(Loc("Str_Access_Tip_Banner", "The SSH server sent its banner. No login was attempted."));
                    break;
                default:
                    sb.Append(Loc("Str_Access_Tip_PortOpen", "The port accepted a connection. The protocol is guessed from the port number."));
                    break;
            }
            if (endpoint.ObservedAt is { } seen)
                sb.AppendLine().Append(string.Format(Loc("Str_Access_Tip_Seen", "Seen {0}"), seen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
            if (!opens) sb.AppendLine().Append(Loc("Str_Access_Tip_Copy", "No app opens this protocol, so clicking copies the address."));
            return sb.ToString();
        }

        /// <summary>A plain English line for CSV, such as "https:443 401 Basic; ssh:22 banner".</summary>
        internal static string Csv(NetworkDevice device) => string.Join("; ", DeviceAccess.ForDevice(device).Select(e =>
            e.Protocol + ":" + e.Port.ToString(CultureInfo.InvariantCulture) + " " + e.Evidence switch
            {
                AccessEvidence.HttpResponse => (e.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "?") + e.Login switch
                {
                    AccessLogin.Requested => " login-requested" + (e.AuthScheme.Length > 0 ? " " + e.AuthScheme : ""),
                    AccessLogin.LoginForm => " login-form",
                    _ => "",
                },
                AccessEvidence.SshBanner => "banner",
                _ => "open",
            }));

        private static string Loc(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
