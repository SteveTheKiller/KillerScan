using System.Net;
using System.Net.Sockets;

namespace KillerScan.Engine
{
    internal sealed class ScanRun : IDisposable
    {
        internal ScanBinding? Binding { get; }
        internal CancellationToken Token { get; }
        private readonly CancellationTokenSource? _stop;
        private readonly Timer? _watch;
        private readonly object _gate = new();
        private int _lastCheck;
        private bool _checked, _disposed;
        internal bool ConnectionChanged { get; private set; }

        internal ScanRun(ScanBinding? binding, CancellationToken token)
        {
            Binding = binding;
            if (binding == null) { Token = token; return; }
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new NetworkBindingException("Explicit interface binding currently requires Windows.");
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            Token = _stop.Token;
            try { Check(force: true); }
            catch { _stop.Dispose(); throw; }
            _watch = new Timer(_ =>
            {
                try { Check(force: true); } catch (OperationCanceledException) { } catch (NetworkBindingException) { }
            }, null, 250, 250);
        }

        internal void Check(bool force = false)
        {
            Token.ThrowIfCancellationRequested();
            if (Binding == null) return;
            lock (_gate)
            {
                if (_disposed) throw new OperationCanceledException(Token);
                int now = Environment.TickCount;
                if (!force && _checked && unchecked((uint)(now - _lastCheck)) < 100) return;
                bool current;
                try { current = Binding.IsCurrent(); } catch { current = false; }
                _checked = true; _lastCheck = now;
                if (current) return;
                ConnectionChanged = true;
                _stop!.Cancel();
                throw Changed();
            }
        }

        internal NetworkBindingException Changed() => new("The selected interface or network changed. Scan stopped; no fallback interface was selected.");
        internal void CheckTarget(IPAddress address)
        {
            Check();
            if (Binding != null && !Binding.Contains(address))
                throw new NetworkBindingException("Target " + address + " is outside the selected interface subnet " + Binding.Subnet + ".");
        }

        internal void Bind(Socket socket, int port = 0, bool multicastReceive = false)
        {
            Check();
            var binding = Binding ?? throw new InvalidOperationException("A binding is required.");
            try
            {
                socket.Bind(new IPEndPoint(multicastReceive ? IPAddress.Any : binding.SourceAddress, port));
                // Windows IP_UNICAST_IF is DWORD in network byte order. Bind alone does not pin routing.
                socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(binding.InterfaceIndex));
                if (socket.SocketType == SocketType.Dgram)
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            { socket.Dispose(); throw; }
            catch (Exception ex)
            {
                socket.Dispose();
                throw new NetworkBindingException("Could not pin the socket to the selected IPv4 interface: " + ex.Message);
            }
            Token.Register(() => { try { socket.Dispose(); } catch { } });
        }

        internal TcpClient Tcp()
        {
            var client = new TcpClient(AddressFamily.InterNetwork);
            Bind(client.Client);
            return client;
        }
        internal UdpClient Udp(int port = 0, bool multicastReceive = false)
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            if (multicastReceive) udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            Bind(udp.Client, port, multicastReceive);
            return udp;
        }
        internal async Task Wait(Task task, int timeout)
        {
            if (await Task.WhenAny(task, Task.Delay(timeout, Token)) != task)
            {
                Token.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }
            await task;
            Check();
        }
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _watch?.Dispose();
                _stop?.Cancel();
                _stop?.Dispose();
            }
        }
    }
}
