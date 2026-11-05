using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using KillerScan.Features.About;
using KillerScan.Services;

/// <summary>
/// WinVerifyTrust checks revocation over the network, and a firewall that silently drops that
/// traffic stalls it for many seconds. Opening About must not freeze the window while it waits:
/// the card comes up at once with placeholders and the publisher lands when the check finishes.
/// </summary>
internal static class AboutSignatureTests
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Body(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
        Console.WriteLine("PASS: About opens at once while the signature check waits, then shows the publisher.");
    }

    private static void Body()
    {
        // The app's theme resources use pack URIs; reading the scheme registers it.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var original = AboutController.ReadSigner;
        using var release = new ManualResetEventSlim();
        int uiThread = Environment.CurrentManagedThreadId, lookupThread = 0;
        const string subject = "Open Source Developer Stephen Riley";
        AboutController.ReadSigner = () =>
        {
            lookupThread = Environment.CurrentManagedThreadId;
            release.Wait(TimeSpan.FromSeconds(20));
            return (true, subject, "E478E4940DFD3547DAF2199494B399214FB3E0FD");
        };
        try
        {
            var host = new StubHost();
            var clock = Stopwatch.StartNew();
            new AboutController(host).Show();
            Require(clock.Elapsed < TimeSpan.FromSeconds(2), "About opens without waiting on the signature check.");
            Require(host.Shown, "The card is shown while the signature check is still running.");
            Require(host.Publisher == "Str_About_Computing" && host.Thumbprint == "Str_About_Computing",
                "The publisher and thumbprint say they are still being read.");
            Require(!host.AliasVisible, "The alias stays hidden until the signature has verified.");

            release.Set();
            PumpUntil(() => host.Publisher == subject, TimeSpan.FromSeconds(10));
            Require(host.Publisher == subject, "The publisher appears once the signature check finishes.");
            Require(host.Thumbprint == "E478E4940DFD3547DAF2199494B399214FB3E0FD", "The thumbprint appears with it.");
            Require(host.AliasVisible, "A verified signature from the publisher shows the alias.");
            Require(lookupThread != 0 && lookupThread != uiThread, "The signature check runs off the UI thread.");
        }
        finally
        {
            release.Set();
            AboutController.ReadSigner = original;
        }
    }

    private static void PumpUntil(Func<bool> done, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed < limit)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private sealed class StubHost : IAboutHost
    {
        private Window? _window;
        public Window Window => _window ??= new Window();
        public string Loc(string key) => key;
        public void SetStatus(string text) { }
        public string Version { set { } }
        public string ReleaseDate { set { } }
        public string Publisher { get; set; } = "";
        public string Alias { set { } }
        public bool AliasVisible { get; set; }
        public string Thumbprint { get; set; } = "";
        public string Sha256 { set { } }
        public string UpdateText { set { } }
        public bool UpdateVisible { set { } }
        public bool UpdateEnabled { set { } }
        public bool StartupUpdateCheck { set { } }
        public bool Shown { get; private set; }
        public void ShowCard() => Shown = true;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
