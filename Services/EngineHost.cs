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

        internal static NetworkScanner CreateScanner(ScanBinding? binding = null)
        {
            var scanner = binding == null ? new NetworkScanner() : new NetworkScanner(binding);
            CapturePreferences(scanner);
            return scanner;
        }

        internal static void CapturePreferences(NetworkScanner scanner)
        {
            // Freeze the location's data for this scanner, including the empty case. A later
            // location choice cannot redirect an in-flight completion into another store.
            scanner.ManualTypeResolver = DeviceOverrides.CaptureLookup();
            var apply = DevicePreferences.CaptureApply();
            var location = Locations.Current;
            scanner.DeviceFinalizer = device =>
            {
                apply(device); device.LocationId = location.Id; device.LocationName = location.Name;
            };
        }
    }
}
