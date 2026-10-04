namespace KillerScan.Services
{
    /// <summary>
    /// Turns the engine's scan stages into the app's localized status text, and into the
    /// key|argument lines the in-app terminal reads from a CLI scan.
    /// </summary>
    internal static class ScanStatusText
    {
        internal static string Key(ScanStage stage) => stage switch
        {
            ScanStage.Discovering    => "Str_St_Discovering",
            ScanStage.ResolvingMacs  => "Str_St_ResolvingMacs",
            ScanStage.Probing        => "Str_St_Probing",
            ScanStage.ResolvingHosts => "Str_St_ResolvingHosts",
            _                        => "Str_St_ScanComplete",
        };

        /// <summary>The value the status string's {0} placeholder shows.</summary>
        internal static object Argument(ScanStatus status) =>
            status.Stage == ScanStage.Discovering ? status.Label : status.Count;

        internal static string Format(ScanStatus status, Func<string, string?> localize)
        {
            string? format = localize(Key(status.Stage));
            return string.IsNullOrEmpty(format) ? status.ToString() : string.Format(format, Argument(status));
        }

        internal static string Protocol(ScanStatus status) => Key(status.Stage) + "|" + Argument(status);
    }
}
