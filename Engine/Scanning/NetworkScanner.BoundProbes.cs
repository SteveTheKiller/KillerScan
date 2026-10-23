using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

namespace KillerScan.Engine
{
    public partial class NetworkScanner
    {
        private readonly ScanBinding? _binding;
        public ScanBinding? Binding => _binding;
        public NetworkScanner(ScanBinding binding) : this(new ScanProbes()) => _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        private ScanProbes ProbesFor(ScanRun run) => _binding == null ? _probes : new BoundProbes(run);
        private string ClassifyForScan(NetworkDevice device, ScanOptions options, string scope)
        {
            device.ObservationCompletedAtUtc = DateTimeOffset.UtcNow;
            device.RecognitionScope = scope;
            device.RecognitionDisabledFeatures = ScanOptions.Features.Where(feature => !options.IsEnabled(feature)).ToArray();
            return ClassifyDeviceForNetwork(device, _binding?.Gateway ?? GatewayIp, _binding?.DnsServer ?? DnsIp,
                ManualTypeResolver ?? ManualTypeLookup);
        }

        private static async Task<UdpReceiveResult> ReceiveProbeAsync(UdpClient udp, IPAddress target, int port, ScanRun? run)
        {
            if (run == null) return await udp.ReceiveAsync();
            try
            {
                while (true)
                {
                    var message = await MulticastDiscovery.ReceivePinned(udp, run);
                    if (message != null && message.Value.Sender.Address.Equals(target) && message.Value.Sender.Port == port)
                        return new UdpReceiveResult(message.Value.Data, message.Value.Sender);
                }
            }
            catch (ObjectDisposedException) { run.Token.ThrowIfCancellationRequested(); return new UdpReceiveResult([], new IPEndPoint(target, port)); }
            catch (SocketException) { run.Token.ThrowIfCancellationRequested(); return new UdpReceiveResult([], new IPEndPoint(target, port)); }
        }

        private sealed class BoundProbes(ScanRun run) : ScanProbes
        {
            internal override IReadOnlyDictionary<string, string> ReadNeighbors() => BoundWindowsNetwork.Neighbors(run);
            internal override string ResolveMac(IPAddress address) => BoundWindowsNetwork.Mac(address, run);
            internal override void FlushDns() { run.Check(); } // Direct adapter DNS queries do not use the OS resolver cache.
            internal override Task<string> Hostname(IPAddress address) => BoundDns.Hostname(address, run);
            internal override Task<int?> Ping(IPAddress address, int timeout) => Task.Run(() => BoundWindowsNetwork.Ping(address, timeout, run), run.Token);
            internal override async Task<bool> TcpAlive(IPAddress address) =>
                (await Task.WhenAll(LivenessPorts.Select(port => Connect(address, port, 300)))).Any(alive => alive);
            internal override async Task<bool> Connect(IPAddress address, int port, int timeout)
            {
                run.CheckTarget(address);
                using var client = run.Tcp(); // Pinning errors propagate; never create an unbound fallback.
                try { await run.Wait(client.ConnectAsync(address, port), timeout); return client.Connected; }
                catch (NetworkBindingException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch { return false; }
            }
            internal override Task<Dictionary<string, MulticastDiscovery.MdnsInfo>> Mdns(CancellationToken ct) => MulticastDiscovery.CollectBoundMdns(run);
            internal override Task<Dictionary<string, string>> Ssdp(CancellationToken ct) => MulticastDiscovery.CollectBoundSsdp(run);
            internal override Task Snmp(NetworkDevice device, IPAddress address) => ProbeSnmpAsync(device, address, run);
            internal override Task Netbios(NetworkDevice device, IPAddress address) => ProbeNetbiosAsync(device, address, run);

            internal override async Task Ssh(NetworkDevice device, IPAddress address)
            {
                run.CheckTarget(address);
                using var client = run.Tcp();
                try
                {
                    await run.Wait(client.ConnectAsync(address, 22), 1000);
                    var bytes = new byte[256];
                    var read = client.GetStream().ReadAsync(bytes, 0, bytes.Length, run.Token);
                    await run.Wait(read, 1500);
                    string banner = Encoding.ASCII.GetString(bytes, 0, read.Result).Trim();
                    if (banner.StartsWith("SSH-")) device.SshBanner = banner.Split(['\r', '\n'])[0];
                }
                catch (NetworkBindingException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            internal override async Task Http(NetworkDevice device, IPAddress address, bool deep)
            {
                (int Port, bool Tls)[] standard = [(80, false), (8080, false), (5000, false), (8123, false), (443, true), (8443, true), (8006, true), (5001, true)];
                var candidates = standard.Where(p => device.OpenPorts.Contains(p.Port)).ToList();
                if (deep) candidates.AddRange(device.OpenPorts.Where(p => !standard.Any(s => s.Port == p)).Take(12).Select(p => (p, TlsLikelyPorts.Contains(p))));
                foreach (var candidate in candidates)
                {
                    run.CheckTarget(address);
                    using var client = run.Tcp();
                    try
                    {
                        await run.Wait(client.ConnectAsync(address, candidate.Port), 1000);
                        Stream stream = client.GetStream();
                        using var ssl = candidate.Tls ? new SslStream(stream, false, (_, _, _, _) => true) : null;
                        if (ssl != null)
                        {
                            await run.Wait(ssl.AuthenticateAsClientAsync(address.ToString(), null, SslProtocols.None, false), 1500);
                            stream = ssl;
                        }
                        // Numeric host, no proxy, no redirect following, no secondary OS DNS lookup.
                        byte[] request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: " + address + ":" + candidate.Port + "\r\nConnection: close\r\nAccept-Encoding: identity\r\n\r\n");
                        await run.Wait(stream.WriteAsync(request, 0, request.Length, run.Token), 1000);
                        using var body = new MemoryStream();
                        var buffer = new byte[4096];
                        while (body.Length < 65536)
                        {
                            var read = stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, 65536 - body.Length), run.Token);
                            await run.Wait(read, 1500);
                            if (read.Result == 0) break;
                            body.Write(buffer, 0, read.Result);
                            if (Encoding.UTF8.GetString(body.ToArray()).IndexOf("</title>", StringComparison.OrdinalIgnoreCase) >= 0) break;
                        }
                        string text = Encoding.UTF8.GetString(body.ToArray());
                        if (!text.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) continue;
                        var server = Regex.Match(text.Split(new[] { "\r\n\r\n" }, 2, StringSplitOptions.None)[0], @"(?im)^Server:\s*([^\r\n]*)");
                        if (server.Success) device.HttpServer = server.Groups[1].Value.Trim();
                        var title = Regex.Match(text, @"(?is)<title[^>]*>(.*?)</title>");
                        if (title.Success) device.HttpTitle = WebUtility.HtmlDecode(Regex.Replace(title.Groups[1].Value, "<[^>]+>", "")).Trim();
                        if (device.HttpTitle.Length > 0 || device.HttpServer.Length > 0) return;
                    }
                    catch (NetworkBindingException) { throw; }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
            }
            internal override async Task Tls(NetworkDevice device, IPAddress address, bool deep)
            {
                int[] standard = [443, 8443, 8006, 902, 5001];
                var ports = standard.Where(device.OpenPorts.Contains).Concat(deep ? device.OpenPorts.Where(p => !standard.Contains(p)).Take(12) : []);
                foreach (int port in ports)
                {
                    run.CheckTarget(address);
                    using var client = run.Tcp();
                    try
                    {
                        await run.Wait(client.ConnectAsync(address, port), 1500);
                        using var stream = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
                        await run.Wait(stream.AuthenticateAsClientAsync(address.ToString(), null, SslProtocols.None, false), 1500);
                        if (stream.RemoteCertificate != null) { device.TlsSubject = stream.RemoteCertificate.Subject; return; }
                    }
                    catch (NetworkBindingException) { throw; }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
            }
        }
    }
}
