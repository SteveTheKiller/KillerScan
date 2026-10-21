using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KillerScan.Engine;

internal static class HttpRedirectTests
{
    internal static async Task Run()
    {
        using var other = new Server(IPAddress.Parse("127.0.0.2"), _ => Final());
        using (var source = new Server(IPAddress.Loopback, _ => Redirect("http://127.0.0.2:" + other.Port + "/")))
        {
            var device = await Probe(source.Port);
            Require(other.Requests == 0 && device.HttpTitle.Length == 0 && device.HttpServer.Length == 0,
                "Off-target redirects must not send a request or attribute another device's title/header.");
        }
        using var same = new Server(IPAddress.Loopback, _ => Final());
        using (var source = new Server(IPAddress.Loopback, _ => Redirect("http://localhost:" + same.Port + "/")))
        {
            var device = await Probe(source.Port);
            Require(same.Requests == 0 && device.HttpTitle.Length == 0 && device.HttpServer.Length == 0,
                "A DNS redirect cannot widen the requested literal-IP boundary.");
        }
        using (var source = new Server(IPAddress.Loopback, _ => Redirect("http://127.0.0.1:" + same.Port + "/panel")))
        {
            var device = await Probe(source.Port);
            Require(same.Requests == 1 && device.HttpTitle == "Local panel" && device.HttpServer == "Local-Fixture",
                "Same-device port redirects retain the final fingerprint.");
        }
        using (var secure = new Server(IPAddress.Loopback, _ => Final(), tls: true))
        using (var source = new Server(IPAddress.Loopback, _ => Redirect("https://127.0.0.1:" + secure.Port + "/")))
        {
            var device = await Probe(source.Port);
            Require(secure.Requests == 1 && device.HttpTitle == "Local panel", "Same-IP HTTP-to-HTTPS redirects retain local panels. " + secure.Error);
        }
        using (var source = new Server(IPAddress.Loopback, request => request.StartsWith("GET /panel ", StringComparison.Ordinal) ? Final() : Redirect("/panel")))
        {
            var device = await Probe(source.Port);
            Require(source.Requests == 2 && device.HttpTitle == "Local panel", "Relative redirects remain on the scanned device.");
        }
        using (var source = new Server(IPAddress.Loopback, _ => Redirect("/loop")))
        {
            var device = await Probe(source.Port);
            Require(source.Requests == 3 && device.HttpTitle.Length == 0 && device.HttpServer.Length == 0,
                "Redirect loops stop after two hops without attributing redirect content.");
        }
        Console.WriteLine("PASS: redirects stay on the scanned IP and retain bounded local redirects.");
    }

    private static async Task<NetworkDevice> Probe(int port)
    {
        var device = new NetworkDevice { IpAddress = "127.0.0.1", OpenPorts = [port] };
        await NetworkScanner.ProbeHttpAsync(device, IPAddress.Loopback, deep: true);
        return device;
    }
    private static string Redirect(string location) => "HTTP/1.1 302 Found\r\nLocation: " + location + "\r\nServer: Redirect-Fixture\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
    private static string Final()
    {
        const string body = "<title>Local panel</title>";
        return "HTTP/1.1 200 OK\r\nServer: Local-Fixture\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body;
    }
    // Shared with AccessTests, which needs the same loopback web fixtures.
    internal sealed class Server : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<TcpClient> _clients = new();
        private readonly Func<string, string> _response;
        private readonly X509Certificate2? _certificate;
        private int _requests;
        internal string? Error { get; private set; }
        internal int Requests => Volatile.Read(ref _requests);
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal Server(IPAddress address, Func<string, string> response, bool tls = false)
        {
            _response = response;
            if (tls)
            {
                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
#pragma warning disable SYSLIB0057
                _certificate = new X509Certificate2(certificate.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.UserKeySet);
#pragma warning restore SYSLIB0057
            }
            _listener = new TcpListener(address, 0); _listener.Start(); _ = Accept();
        }
        private async Task Accept()
        {
            while (!_stop.IsCancellationRequested)
                try { var client = await _listener.AcceptTcpClientAsync(); _clients.Add(client); _ = Respond(client); }
                catch when (_stop.IsCancellationRequested) { return; }
        }
        private async Task Respond(TcpClient client)
        {
            using (client)
                try
                {
                    using Stream stream = _certificate == null ? client.GetStream() : new SslStream(client.GetStream());
                    if (stream is SslStream ssl) await ssl.AuthenticateAsServerAsync(_certificate!, false, SslProtocols.Tls12, false);
                    var request = new StringBuilder();
                    var bytes = new byte[4096];
                    while (request.Length < 16384 && !request.ToString().Contains("\r\n\r\n"))
                    { int n = await stream.ReadAsync(bytes, 0, bytes.Length, _stop.Token); if (n == 0) return; request.Append(Encoding.ASCII.GetString(bytes, 0, n)); }
                    Interlocked.Increment(ref _requests);
                    byte[] output = Encoding.ASCII.GetBytes(_response(request.ToString()));
                    await stream.WriteAsync(output, 0, output.Length, _stop.Token);
                }
                catch (Exception ex) { Error = ex.ToString(); }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); foreach (var client in _clients) client.Dispose(); _certificate?.Dispose(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
