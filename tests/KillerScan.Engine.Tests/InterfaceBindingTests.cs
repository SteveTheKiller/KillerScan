using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using KillerScan.Engine;

internal static class InterfaceBindingTests
{
    private sealed class Listener(IPAddress address, int port) : TcpListener(address, port), IDisposable
    { void IDisposable.Dispose() => Stop(); }
    internal static LocalNet Loopback(string subnet = "127.0.0.0/8")
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
        return new LocalNet { AdapterId = adapter.Id, InterfaceIndex = adapter.GetIPProperties().GetIPv4Properties()!.Index,
            LocalIp = "127.0.0.1", Subnet = subnet, Dns = "127.0.0.1", ConnectionIdentity = "fixture" };
    }
    private static ScanBinding Binding(string subnet = "127.0.0.0/8") => new(Loopback(subnet), () => true);
    private static NetworkScanner.ScanProbes Probes(ScanRun run) => (NetworkScanner.ScanProbes)typeof(NetworkScanner)
        .GetMethod("ProbesFor", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(new NetworkScanner(run.Binding!), new object[] { run })!;

    public static async Task ScopeAndDisconnect()
    {
        var net = Loopback("127.0.0.1/32");
        bool connected = true;
        var binding = new ScanBinding(net, () => connected);
        Require(binding.Contains(IPAddress.Loopback) && !binding.Contains(IPAddress.Parse("127.0.0.2")), "Binding enforces the subnet, even for an overlapping interface address.");
        using var run = new ScanRun(binding, CancellationToken.None);
        using var socket = run.Tcp();
        try { run.CheckTarget(IPAddress.Parse("192.0.2.10")); throw new Exception("Expected outside-scope refusal"); }
        catch (NetworkBindingException) { }
        connected = false;
        try { run.Check(force: true); throw new Exception("Expected network-change refusal"); }
        catch (NetworkBindingException) { }
        Require(run.Token.IsCancellationRequested && run.ConnectionChanged, "A lost/replaced link cancels its own run.");
        try { socket.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0)); throw new Exception("Expected disposed socket"); }
        catch (ObjectDisposedException) { }
        var none = new ScanOptions().Without(string.Join(",", ScanOptions.Features));
        foreach (bool quick in new[] { false, true })
        {
            try { await new NetworkScanner(binding).ScanSubnetAsync("127.0.0.1", CancellationToken.None, none, !quick); throw new Exception("Expected stale binding refusal"); }
            catch (NetworkBindingException) { }
        }
        try { await new NetworkScanner(binding).DeepProbeHostAsync("127.0.0.1", CancellationToken.None, none); throw new Exception("Expected stale deep binding refusal"); }
        catch (NetworkBindingException) { }
        using var valid = new ScanRun(Binding(), CancellationToken.None);
        Require(!valid.Token.IsCancellationRequested, "Canceling one interface run cannot cancel a different run.");
        bool present = true;
        using var watched = new ScanRun(new ScanBinding(net, () => present), CancellationToken.None);
        present = false;
        await Task.WhenAny(Task.Delay(2000), Task.Delay(Timeout.Infinite, watched.Token));
        Require(watched.ConnectionChanged && watched.Token.IsCancellationRequested, "The link watcher cancels without another probe check.");
    }

    public static async Task PinnedSockets()
    {
        using var run = new ScanRun(Binding(), CancellationToken.None);
        using var tcp = run.Tcp();
        Require(((IPEndPoint)tcp.Client.LocalEndPoint!).Address.Equals(IPAddress.Loopback), "TCP is bound to the selected source address.");
        Require((int)tcp.Client.GetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31)! == run.Binding!.InterfaceIndex,
            "Windows IP_UNICAST_IF pins TCP routing to the selected index.");
        using var udp = run.Udp();
        Require((int)udp.Client.GetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31)! == run.Binding.InterfaceIndex, "UDP also pins routing.");
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] request = [1, 2, 3];
        await udp.SendAsync(request, request.Length, (IPEndPoint)server.Client.LocalEndPoint!);
        var received = await server.ReceiveAsync();
        Require(received.RemoteEndPoint.Address.Equals(IPAddress.Loopback), "Real UDP traffic uses the selected source.");
        await server.SendAsync(request, request.Length, received.RemoteEndPoint);
        var reply = await MulticastDiscovery.ReceivePinned(udp, run);
        Require(reply != null && reply.Value.Data.SequenceEqual(request), "Packet-information receive accepts the selected interface.");
        using var limited = new ScanRun(Binding("127.0.0.1/32"), CancellationToken.None);
        using var receiver = limited.Udp();
        using var otherSource = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        await otherSource.SendAsync(request, request.Length, (IPEndPoint)receiver.Client.LocalEndPoint!);
        Require(await MulticastDiscovery.ReceivePinned(receiver, limited) == null, "A foreign-scope multicast/unicast reply is excluded.");
    }

    public static Task BoundIcmpAndClassification()
    {
        using var run = new ScanRun(Binding(), CancellationToken.None);
        int? ttl = BoundWindowsNetwork.Ping(IPAddress.Loopback, 500, run);
        using var reference = new Ping();
        Require(ttl == reference.Send(IPAddress.Loopback, 500).Options?.Ttl, "Native ICMP TTL matches the framework reply layout on this architecture.");
        string gateway = NetworkScanner.GatewayIp, dns = NetworkScanner.DnsIp;
        try
        {
            NetworkScanner.GatewayIp = "192.0.2.1"; NetworkScanner.DnsIp = "192.0.2.2";
            var net = Loopback(); net.Gateway = "127.0.0.1"; net.Dns = "127.0.0.1";
            var scanner = new NetworkScanner(new ScanBinding(net, () => true));
            string type = (string)typeof(NetworkScanner).GetMethod("ClassifyForScan", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(scanner, new object[] { new NetworkDevice { IpAddress = "127.0.0.1" } })!;
            Require(type == "Router/DNS", "An explicitly bound scanner uses its own gateway/DNS, not another tab's globals.");
        }
        finally { NetworkScanner.GatewayIp = gateway; NetworkScanner.DnsIp = dns; }
        return Task.CompletedTask;
    }

    public static async Task BoundHttp()
    {
        using var run = new ScanRun(Binding(), CancellationToken.None);
        using var listener = new Listener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var trap = new Listener(IPAddress.Parse("127.0.0.2"), 0);
        trap.Start();
        int trapPort = ((IPEndPoint)trap.LocalEndpoint).Port;
        async Task Serve(string response)
        {
            using var client = await listener.AcceptTcpClientAsync();
            Require(((IPEndPoint)client.Client.RemoteEndPoint!).Address.Equals(IPAddress.Loopback), "HTTP uses the selected source address.");
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, false, 4096, leaveOpen: true);
            while (await reader.ReadLineAsync() is { Length: > 0 }) { }
            byte[] bytes = Encoding.UTF8.GetBytes(response);
            await client.GetStream().WriteAsync(bytes, 0, bytes.Length);
        }
        var serve = Serve("HTTP/1.1 200 OK\r\nServer: Bound-Fixture\r\nConnection: close\r\n\r\n<title>Bound title</title>");
        var device = new NetworkDevice { IpAddress = "127.0.0.1", OpenPorts = [port] };
        await Probes(run).Http(device, IPAddress.Loopback, true);
        Require(await Task.WhenAny(serve, Task.Delay(2000)) == serve, "HTTP fixture received the request."); await serve;
        Require(device.HttpTitle == "Bound title" && device.HttpServer == "Bound-Fixture", "Bound HTTP retains title and server fingerprints.");
        serve = Serve("HTTP/1.1 302 Found\r\nServer: Redirect-Fixture\r\nLocation: http://127.0.0.2:" + trapPort + "/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await Probes(run).Http(new NetworkDevice { OpenPorts = [port] }, IPAddress.Loopback, true);
        Require(await Task.WhenAny(serve, Task.Delay(2000)) == serve, "Redirect fixture received the request."); await serve;
        Require(!trap.Pending(), "Bound fingerprinting never follows redirects to another endpoint.");
    }

    public static async Task BoundDnsRoundTrip()
    {
        var network = Loopback("127.0.0.1/32");
        network.Dns = "127.0.0.2";
        using var run = new ScanRun(new ScanBinding(network, () => true), CancellationToken.None);
        using var dns = new UdpClient(new IPEndPoint(IPAddress.Parse(network.Dns), 0));
        async Task Server()
        {
            for (int n = 0; n < 2; n++)
            {
                var packet = await dns.ReceiveAsync();
                Require(packet.RemoteEndPoint.Address.Equals(IPAddress.Loopback), "DNS uses the selected source address.");
                byte[] question = packet.Buffer;
                ushort type = (ushort)((question[question.Length - 4] << 8) | question[question.Length - 3]);
                byte[] payload = type == 12 ? BoundDns.BuildQuery("fixture.example", 1, [0, 0]).Skip(12).TakeWhileWithTerminator() : [127, 0, 0, 1];
                var answer = question.ToList();
                answer[2] = 0x81; answer[3] = 0x80; answer[7] = 1;
                answer.AddRange(new byte[] { 0xC0, 12, 0, (byte)type, 0, 1, 0, 0, 0, 0, 0, (byte)payload.Length });
                answer.AddRange(payload);
                await dns.SendAsync(answer.ToArray(), answer.Count, packet.RemoteEndPoint);
            }
        }
        var server = Server();
        string hostname = await BoundDns.Hostname(IPAddress.Loopback, run, ((IPEndPoint)dns.Client.LocalEndPoint!).Port);
        Require(await Task.WhenAny(server, Task.Delay(2000)) == server, "DNS fixture completed both requests."); await server;
        Require(hostname == "fixture.example", "Pinned DNS verifies replies on the selected interface even when the configured DNS server is outside the target subnet.");
        var query = BoundDns.BuildQuery("fixture.example", 1, [1, 2]);
        Require(BoundDns.ReadAnswers(query, [2, 1], "fixture.example", 1).Count == 0, "A mismatched transaction cannot supply a name.");
    }

    private static byte[] TakeWhileWithTerminator(this IEnumerable<byte> bytes)
    {
        var result = new List<byte>();
        foreach (byte value in bytes) { result.Add(value); if (value == 0) break; }
        return result.ToArray();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
