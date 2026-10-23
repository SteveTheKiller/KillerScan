using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using KillerScan.Engine;

internal static class AccessTests
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static Task Evidence()
    {
        var device = new NetworkDevice { IpAddress = "192.0.2.40", OpenPorts = [80, 443, 22, 3389, 53] };
        var old = DeviceAccess.ForDevice(device);
        Require(old.All(item => item.Verification == "PortHint" && item.Authentication == "Unknown" && item.ObservedAtUtc == null), "Legacy ports are not retroactive checks or login claims.");
        HttpAccessObservation.Record(device, 9187, false, 200, "", "<title>Fixture</title><form><input name='user'><input type='password'></form>");
        var form = device.Access!.Single();
        Require(form.Authentication == "LoginPage" && DeviceAccess.Address(device.IpAddress, form) == "http://192.0.2.40:9187/", "A nonstandard endpoint keeps its actual URL and password-form evidence.");
        HttpAccessObservation.Record(device, 9187, false, 200, "", "<form><input data-type='password'></form><!-- <form><input type='password'></form> --><script>var x = \"<form><input type='password'></form>\";</script>");
        Require(device.Access!.Single().Authentication == "Unknown", "200, data attributes and inactive HTML cannot prove login state.");
        HttpAccessObservation.Record(device, 9187, false, 401, "WWW-Authenticate: Basic realm=fixture-secret\r\n", "");
        Require(device.Access!.Single().Authentication == "Required" && device.Access!.Single().Challenge == "Basic" && !JsonSerializer.Serialize(device.Access).Contains("fixture-secret"), "The challenge scheme is evidence; realm/secrets are not retained.");
        HttpAccessObservation.Record(device, 9187, false, 403, "", "");
        Require(device.Access!.Single().Authentication == "Unknown", "Forbidden alone does not establish an authentication method.");
        HttpAccessObservation.Record(device, 9187, false, 200, "WWW-Authenticate: Basic", "");
        Require(device.Access!.Single().Authentication == "Unknown", "An optional challenge on HTTP 200 does not establish a login requirement.");
        HttpAccessObservation.Record(device, 9187, false, 302, "Location: http://other.invalid/?token=not-for-storage", "");
        Require(device.Access!.Single().Redirect && !JsonSerializer.Serialize(device.Access).Contains("not-for-storage"), "Redirects remain evidence without storing target tokens.");
        var endpoint = device.Access!.Single();
        Require(DeviceAccess.Address("192.0.2.40;bad", endpoint) == null && DeviceAccess.Address(device.IpAddress, endpoint with { Protocol = "file" }) == null
            && DeviceAccess.Address(device.IpAddress, endpoint with { Port = -1 }) == null, "Open addresses accept only numeric hosts, valid ports and allowed schemes.");
        Require(DeviceAccess.Address("2001:db8::40", endpoint) == "http://[2001:db8::40]:9187/", "IPv6 URLs retain brackets and port.");
        Require(DeviceAccess.ForDevice(new NetworkDevice { IpAddress = "invalid", OpenPorts = [80] }).Count == 0,
            "Invalid hosts cannot produce inferred actionable endpoints.");
        var restored = JsonSerializer.Deserialize<NetworkDevice>(JsonSerializer.Serialize(device))!;
        Require(restored.Access!.Single() == endpoint && DeviceAccess.Web(restored)?.Port == 9187, "JSON and browser selection retain verified nonstandard endpoints.");
        Require(!JsonSerializer.Serialize(new NetworkDevice()).Contains("Access"), "Old/unassessed devices retain the optional contract.");
        return Task.CompletedTask;
    }

    internal static async Task HttpRoundTrip()
    {
        using var trap = new Fixture(false, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var challenge = new Fixture(false, "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Digest realm=fixture\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var redirect = new Fixture(false, "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:" + trap.Port + "/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var device = new NetworkDevice { IpAddress = "127.0.0.1", OpenPorts = [challenge.Port, redirect.Port] };
        await Probe(device);
        Require(device.Access?.Count == 2 && device.Access!.Any(item => item.Port == challenge.Port && item.HttpStatus == 401 && item.Authentication == "Required"), "The real probe retains title-less 401 and multiple endpoint results.");
        Require(device.Access!.Single(item => item.Port == redirect.Port).Redirect && trap.Requests == 0, "Unbound HTTP never follows a redirect or authenticates automatically.");
        Require(challenge.Requests == 1 && redirect.Requests == 1, "A captured HTTP response needs no second protocol attempt or login retry.");
    }

    internal static async Task HttpsRoundTrip()
    {
        const string body = "<html><title>Secure fixture</title><form><input type=password></form></html>";
        using var fixture = new Fixture(true, "HTTP/1.1 200 OK\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body);
        var device = new NetworkDevice { IpAddress = "127.0.0.1", OpenPorts = [fixture.Port] };
        await Probe(device);
        Require(device.Access?.Single() is { Protocol: "https", Authentication: "LoginPage", Verification: "HttpResponse" }, "Deep HTTP detects HTTPS at a nonstandard port with an untrusted fixture certificate. Fixture: " + fixture.LastError + "; access=" + JsonSerializer.Serialize(device.Access));
        Require(DeviceAccess.Address(device.IpAddress, device.Access!.Single()) == "https://127.0.0.1:" + fixture.Port + "/", "The captured scheme and port drive the address.");
    }

    internal static async Task StopRequests()
    {
        using var stalled = new Fixture(false, "", stall: true);
        using var next = new Fixture(false, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var cancel = new CancellationTokenSource();
        var task = NetworkScanner.ProbeHttpWithCancellationAsync(new NetworkDevice { IpAddress = "127.0.0.1", OpenPorts = [stalled.Port, next.Port] }, IPAddress.Loopback, true, cancel.Token);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (stalled.Requests == 0 && wait.ElapsedMilliseconds < 3000) await Task.Delay(10);
        Require(stalled.Requests == 1, "The controlled stalled HTTP request started."); cancel.Cancel();
        Require(await Task.WhenAny(task, Task.Delay(1500)) == task, "Stop interrupts the active request promptly.");
        try { await task; throw new InvalidOperationException("Stop was ignored."); } catch (OperationCanceledException) { }
        Require(next.Requests == 0, "Stop prevents all later endpoint attempts.");
    }

    private static Task Probe(NetworkDevice device) => (Task)typeof(NetworkScanner)
        .GetMethod("ProbeHttpAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { device, IPAddress.Loopback, true })!;

    private sealed class Fixture : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly System.Collections.Concurrent.ConcurrentBag<TcpClient> _clients = [];
        private readonly X509Certificate2? _certificate;
        private readonly string _response;
        private readonly bool _stall;
        internal int Port { get; }
        internal int Requests;
        internal string? LastError;
        internal Fixture(bool tls, string response, bool stall = false)
        {
            _response = response;
            _stall = stall;
            if (tls)
            {
                using var rsa = RSA.Create(); rsa.KeySize = 2048;
                var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
#if NET10_0_OR_GREATER
                _certificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
#else
                _certificate = new X509Certificate2(certificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.DefaultKeySet);
#endif
            }
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _ = Accept();
        }
        private async Task Accept()
        {
            try { while (!_stop.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(); _clients.Add(client); _ = Serve(client); } }
            catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
        }
        private async Task Serve(TcpClient client)
        {
            using (client)
            try
            {
                System.IO.Stream stream = client.GetStream();
                using var ssl = _certificate == null ? null : new SslStream(stream);
                if (ssl != null) { await ssl.AuthenticateAsServerAsync(_certificate!, false, SslProtocols.Tls12, false); stream = ssl; }
                var buffer = new byte[4096]; int used = 0;
                while (used < buffer.Length)
                {
                    int count = await stream.ReadAsync(buffer, used, buffer.Length - used, _stop.Token);
                    if (count == 0) return; used += count;
                    if (Encoding.ASCII.GetString(buffer, 0, used).Contains("\r\n\r\n")) break;
                }
                string request = Encoding.ASCII.GetString(buffer, 0, used);
                if (!request.StartsWith("GET / HTTP/", StringComparison.Ordinal)) return;
                Require(!request.Contains("Authorization:") && !request.Contains("Cookie:"), "Fixtures receive no credentials/cookies.");
                Interlocked.Increment(ref Requests);
                if (_stall) { await Task.Delay(Timeout.Infinite, _stop.Token); return; }
                byte[] bytes = Encoding.UTF8.GetBytes(_response); await stream.WriteAsync(bytes, 0, bytes.Length, _stop.Token);
            }
            catch (Exception error) when (error is System.IO.IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException) { LastError = error.ToString(); }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); foreach (var client in _clients) client.Dispose(); _certificate?.Dispose(); _stop.Dispose(); }
    }
}
