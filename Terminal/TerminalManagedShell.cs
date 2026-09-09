using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KillerScan.Terminal
{
    internal sealed partial class TerminalControl
    {
        private readonly string _bridgeName = "KillerScan-" + Guid.NewGuid().ToString("N");
        private NamedPipeServerStream? _bridge;
        private Task _bridgeWrites = Task.CompletedTask;

        // Output passes through the shell's console, so ConPTY owns its cursor and history.
        public string ManagedShellSetup =>
            "function global:Invoke-KillerScanSpeedTest { " +
            "$p = [System.IO.Pipes.NamedPipeClientStream]::new('.', '" + _bridgeName + "', [System.IO.Pipes.PipeDirection]::In); " +
            "try { $p.Connect(10000); $r = [System.IO.StreamReader]::new($p, [System.Text.Encoding]::UTF8); " +
            "while ($null -ne ($line = $r.ReadLine())) { [Console]::Write([System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line))) } " +
            "} finally { $p.Dispose() } }";

        public async Task BeginShellManagedSessionAsync(CancellationToken cancellation)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!_atPrompt)
            {
                cancellation.ThrowIfCancellationRequested();
                if (_closed || !HasShell || DateTime.UtcNow >= deadline)
                    throw new IOException("The terminal shell did not become ready.");
                await Task.Delay(20, cancellation);
            }
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
                PipeAccessRights.FullControl, AccessControlType.Allow));
            var pipe = _bridge = new NamedPipeServerStream(_bridgeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
            _bridgeWrites = Task.CompletedTask;
            Send("Invoke-KillerScanSpeedTest\r");
            IsManaged = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var registration = timeout.Token.Register(() => pipe.Dispose());
            try { await pipe.WaitForConnectionAsync(timeout.Token); }
            catch { pipe.Dispose(); _bridge = null; IsManaged = false; throw; }
        }

        private void WriteShellManaged(string text)
        {
            var pipe = _bridge!;
            var bytes = Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\n");
            _bridgeWrites = _bridgeWrites.ContinueWith(async previous =>
            {
                await previous.ConfigureAwait(false);
                await pipe.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await pipe.FlushAsync().ConfigureAwait(false);
            }, TaskScheduler.Default).Unwrap();
        }

        public async Task EndShellManagedSessionAsync()
        {
            try { await _bridgeWrites; }
            finally
            {
                _bridge?.Dispose();
                _bridge = null;
                IsManaged = false;
                ManagedInput = null;
                _cursorOn = true;
                InvalidateVisual();
            }
        }
    }
}
