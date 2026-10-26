using System.Net;
using System.Text.Json;
using KillerScan.Engine;
using Server = HttpRedirectTests.Server;

// Access evidence records what each web endpoint actually answered, and never more: a 401 asks
// for a login, a password form is a sign-in page, and anything else stays Unknown. Every fixture
// is a loopback server, and no credentials are ever sent.
internal static class AccessTests
{
    internal static async Task Run()
    {
        using (var server = new Server(IPAddress.Loopback, _ => Response("401 Unauthorized", "", "WWW-Authenticate: Basic realm=\"secret-realm\"\r\n")))
        {
            var entry = Single(await Probe(server.Port), server.Port);
            Require(entry.Evidence == AccessEvidence.HttpResponse && entry.HttpStatus == 401 && entry.Login == AccessLogin.Requested && entry.AuthScheme == "Basic",
                "A 401 is recorded as a login request with its scheme only.");
            Require(entry.ObservedAt is { } seen && (DateTimeOffset.UtcNow - seen).TotalMinutes < 1, "A web response records when it was seen.");
            Require(!JsonSerializer.Serialize(entry).Contains("secret-realm"), "The realm is not kept.");
        }
        using (var server = new Server(IPAddress.Loopback, _ => Response("200 OK", "<title>Panel</title><form action=/login><input name=u><input type=\"password\" name=p></form>")))
            Require(Single(await Probe(server.Port), server.Port).Login == AccessLogin.LoginForm, "A form with a password field is a sign-in page.");
        using (var server = new Server(IPAddress.Loopback, _ => Response("200 OK",
                   "<title>Status</title><!-- <form><input type=password></form> --><script>var f='<form><input type=password></form>';</script>")))
            Require(Single(await Probe(server.Port), server.Port).Login == AccessLogin.Unknown, "Password fields in comments or scripts do not count.");
        using (var server = new Server(IPAddress.Loopback, _ => Response("200 OK", "<title>Open page</title>")))
        {
            var entry = Single(await Probe(server.Port), server.Port);
            Require(entry.HttpStatus == 200 && entry.Login == AccessLogin.Unknown, "A plain 200 says nothing about logins.");
        }
        using (var server = new Server(IPAddress.Loopback, _ => Response("403 Forbidden", "")))
            Require(Single(await Probe(server.Port), server.Port).Login == AccessLogin.Unknown, "A 403 is not a login request.");

        // A same-address redirect: the first port records the redirect, the second the page.
        using (var panel = new Server(IPAddress.Loopback, _ => Response("200 OK", "<title>Local panel</title><form><input type=password></form>")))
        using (var front = new Server(IPAddress.Loopback, _ => "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:" + panel.Port + "/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"))
        {
            var device = await Probe(front.Port);
            Require(Single(device, front.Port).HttpStatus == 302 && Single(device, front.Port).Login == AccessLogin.Unknown,
                "The redirecting port records the redirect, not the page it led to.");
            Require(Single(device, panel.Port).HttpStatus == 200 && Single(device, panel.Port).Login == AccessLogin.LoginForm,
                "The page is recorded under the port that served it.");
        }

        // A same-address redirect to a raw print port is not followed: a GET there would print.
        System.Net.Sockets.TcpListener? printer = null;
        try { printer = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 9100); printer.Start(); }
        catch (System.Net.Sockets.SocketException) { printer = null; }
        try
        {
            using var front = new Server(IPAddress.Loopback, _ => "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:9100/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            var device = await Probe(front.Port);
            Require(Single(device, front.Port).HttpStatus == 302 && device.Access!.All(e => e.Port != 9100),
                "A redirect to a raw print port is recorded and not followed.");
            if (printer != null) Require(!printer.Pending(), "Nothing connects to the raw print port.");
        }
        finally { printer?.Stop(); }

        // A redirect whose next hop fails is still an answer at the first endpoint.
        var closed = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        using (var front = new Server(IPAddress.Loopback, _ => "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:" + closedPort + "/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"))
            Require(Single(await Probe(front.Port), front.Port).HttpStatus == 302, "A redirect is recorded even when the page it points to does not answer.");

        // Normal scans stop at the first useful page; deep scans check every web port.
        using (var first = new Server(IPAddress.Loopback, _ => Response("200 OK", "<title>First</title>")))
        using (var second = new Server(IPAddress.Loopback, _ => Response("401 Unauthorized", "", "WWW-Authenticate: NTLM\r\n")))
        {
            var normal = new NetworkDevice { IpAddress = "127.0.0.1", OpenPorts = [first.Port, second.Port] };
            await NetworkScanner.ProbeHttpAsync(normal, IPAddress.Loopback, deep: true);
            Require(normal.Access!.Count == 1 && second.Requests == 0, "A normal web probe stops after the first useful page.");
            var deep = new NetworkDevice { IpAddress = "127.0.0.1", OpenPorts = [first.Port, second.Port] };
            await NetworkScanner.ProbeHttpAsync(deep, IPAddress.Loopback, deep: true, everyPort: true, CancellationToken.None);
            Require(deep.Access!.Count == 2 && Single(deep, second.Port).AuthScheme == "NTLM", "A deep probe records every web port.");
            Require(deep.HttpTitle == "First", "Classification still reads the first useful page.");
        }

        // The access list adds every other open port, labeled by what is known about it.
        var host = new NetworkDevice
        {
            IpAddress = "192.0.2.10", OpenPorts = [22, 443, 3389, 9100], SshBanner = "SSH-2.0-OpenSSH_9.6",
            Access = [new AccessEndpoint { Protocol = "http", Port = 80, Evidence = AccessEvidence.HttpResponse, HttpStatus = 200 }],
        };
        var list = DeviceAccess.ForDevice(host);
        Require(list.Select(e => e.Protocol + ":" + e.Port).SequenceEqual(["ssh:22", "http:80", "https:443", "rdp:3389", "tcp:9100"]),
            "Every endpoint is listed once, by port: " + string.Join(", ", list.Select(e => e.Protocol + ":" + e.Port)));
        Require(list[0].Evidence == AccessEvidence.SshBanner && list[2].Evidence == AccessEvidence.PortOpen && list[2].ObservedAt == null,
            "Port-only endpoints say so and invent no time.");
        Require(DeviceAccess.BestWeb(host)!.Port == 80, "The default web address is one that answered, even over a guessed HTTPS port.");
        host.Access = null;
        Require(DeviceAccess.BestWeb(host)!.Protocol == "https", "With no answers, the guessed HTTPS port comes first.");
        Require(DeviceAccess.ForDevice(new NetworkDevice { IpAddress = "printer.local", OpenPorts = [80] }).Count == 0, "Only numeric addresses get endpoints.");

        // Addresses are built from the device's numeric address and the endpoint alone.
        Require(DeviceAccess.Address("192.0.2.10", new AccessEndpoint { Protocol = "https", Port = 8443 }) == "https://192.0.2.10:8443/", "Web addresses use the scheme and port.");
        Require(DeviceAccess.Address("192.0.2.10", new AccessEndpoint { Protocol = "ssh", Port = 2222 }) == "192.0.2.10:2222", "Other endpoints are host and port.");
        Require(DeviceAccess.Address("192.0.2.10", new AccessEndpoint { Protocol = "javascript", Port = 80 }) == null &&
                DeviceAccess.Address("192.0.2.10", new AccessEndpoint { Protocol = "http", Port = 0 }) == null &&
                DeviceAccess.Address("evil.example", new AccessEndpoint { Protocol = "http", Port = 80 }) == null,
            "Unknown schemes, bad ports and names are refused.");

        // A bound scan reads its adapter's table with arp -a -N, which adds an Interface header.
        var perAdapter = ArpOutput.Parse("\r\nInterface: 192.168.8.10 --- 0x15\r\n  Internet Address      Physical Address      Type\r\n  192.168.8.1           94-83-c4-a4-78-82     dynamic\r\n");
        Require(perAdapter.Count == 1 && perAdapter["192.168.8.1"] == "94:83:C4:A4:78:82", "A single adapter's ARP table parses like the full one.");

        // arp.exe is named by its System32 path, so a copy beside a portable exe is never started.
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
#pragma warning disable CA1416 // The engine runner only executes on Windows.
        var arp = WindowsNetworkPlatform.ArpStart("-a");
#pragma warning restore CA1416
        Require(string.Equals(arp.FileName, Path.Combine(system, "arp.exe"), StringComparison.OrdinalIgnoreCase)
                && string.Equals(arp.WorkingDirectory, system, StringComparison.OrdinalIgnoreCase) && !arp.UseShellExecute,
            "arp.exe starts from System32 by full path: " + arp.FileName);

        string json = JsonSerializer.Serialize(new AccessEndpoint { Protocol = "https", Port = 443, Evidence = AccessEvidence.HttpResponse, Login = AccessLogin.LoginForm });
        Require(json.Contains("\"HttpResponse\"") && json.Contains("\"LoginForm\""), "JSON uses readable evidence and login names: " + json);

        Console.WriteLine("PASS: access records what each endpoint answered and claims no login it did not see.");
    }

    private static async Task<NetworkDevice> Probe(int port)
    {
        var device = new NetworkDevice { IpAddress = "127.0.0.1", OpenPorts = [port] };
        await NetworkScanner.ProbeHttpAsync(device, IPAddress.Loopback, deep: true);
        return device;
    }

    private static AccessEndpoint Single(NetworkDevice device, int port) =>
        device.Access?.SingleOrDefault(e => e.Port == port) ?? throw new InvalidOperationException("No access entry for port " + port + ".");

    private static string Response(string status, string body, string headers = "") =>
        "HTTP/1.1 " + status + "\r\n" + headers + "Content-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
