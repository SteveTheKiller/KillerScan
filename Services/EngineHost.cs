namespace KillerScan.Services
{
    /// <summary>
    /// Connects KillerScan.Engine to the app's saved device data and display text. Runs once at
    /// startup, before the window, the CLI or the terminal can start a scan.
    /// </summary>
    internal static class EngineHost
    {
        internal static void Configure()
        {
            NetworkScanner.ManualTypeLookup = DeviceOverrides.Get;
            NetworkScanner.DeviceCompleted = DevicePreferences.Apply;
            NetworkDevice.DeviceTypeDisplayResolver = Controls.DeviceTypeConverter.Display;
        }
    }
}
