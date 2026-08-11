using System.Diagnostics;

namespace KillerScan.Services
{
    /// <summary>Hands a URL to the default browser.</summary>
    internal static class WebLink
    {
        internal static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser available */ }
        }
    }
}
