namespace KillerScan.Services
{
    /// <summary>
    /// Connects KillerScan.Engine to the app's display text. Runs once at startup, before the
    /// window, the CLI or the terminal can start a scan. Saved device data is not connected here:
    /// it belongs to a Location, so each scanner gets its own hooks from the tab or command
    /// that runs it.
    /// </summary>
    internal static class EngineHost
    {
        internal static void Configure()
        {
            NetworkDevice.DeviceTypeDisplayResolver = Controls.DeviceTypeConverter.Display;
        }
    }
}
