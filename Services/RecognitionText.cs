using System.Text;
using System.Windows;
using KillerScan.Controls;

namespace KillerScan.Services
{
    /// <summary>
    /// Puts a device's recognition evidence into words: the short tooltip on the type cell, the
    /// full "Why this type?" text, and the plain English line written to CSV. Only the source
    /// names and rule sentences are translated; observed values are shown as the device sent them.
    /// </summary>
    internal static class RecognitionText
    {
        private const int TooltipSignals = 3;

        internal static string Confidence(RecognitionConfidence confidence) =>
            Loc("Str_Rec_Conf_" + confidence, confidence.ToString());

        /// <summary>One line of evidence, such as "Open ports: 9100" or the rule sentence for a guess.</summary>
        internal static string Signal(RecognitionSignal signal) =>
            signal.Source is RecognitionSources.Fallback or RecognitionSources.Gateway
                ? Loc("Str_RecRule_" + signal.Detail, signal.Detail)
                : Loc("Str_RecSrc_" + signal.Source, signal.Source) + ": " + signal.Detail;

        /// <summary>The type cell's tooltip: the confidence and the strongest few signals.</summary>
        internal static string Tooltip(DeviceRecognition? recognition)
        {
            if (recognition == null) return Loc("Str_Rec_None", "No evidence was recorded for this device.");
            var sb = new StringBuilder(Confidence(recognition.Confidence));
            if (recognition.Confidence == RecognitionConfidence.Manual)
                sb.AppendLine().Append(Automatic(recognition));
            foreach (var signal in recognition.Signals.Take(TooltipSignals))
                sb.AppendLine().Append(Signal(signal));
            int more = recognition.Signals.Count - TooltipSignals;
            if (more > 0) sb.AppendLine().Append(string.Format(Loc("Str_Rec_More", "and {0} more"), more));
            if (recognition.Conflict) sb.AppendLine().Append(Loc("Str_Rec_Conflict", "Another type scored close to this one."));
            return sb.ToString();
        }

        /// <summary>The heading of the "Why this type?" window, such as "Printer (High confidence)".</summary>
        internal static string Heading(NetworkDevice device) =>
            string.Format(Loc("Str_Rec_Heading", "{0} ({1})"), DeviceTypeConverter.Display(device.DeviceType),
                device.Recognition == null ? Loc("Str_Rec_Conf_None", "no evidence") : Confidence(device.Recognition.Confidence));

        /// <summary>The body of the "Why this type?" window.</summary>
        internal static string Detail(NetworkDevice device)
        {
            var recognition = device.Recognition;
            if (recognition == null) return Loc("Str_Rec_None", "No evidence was recorded for this device.");
            var sb = new StringBuilder();
            if (recognition.Confidence == RecognitionConfidence.Manual)
                sb.AppendLine(Automatic(recognition)).AppendLine();
            sb.AppendLine(Loc("Str_Rec_Evidence", "Evidence"));
            foreach (var signal in recognition.Signals) sb.Append("• ").AppendLine(Signal(signal));
            if (recognition.Alternatives.Count > 0)
            {
                sb.AppendLine().AppendLine(Loc("Str_Rec_Alternatives", "Also possible"));
                foreach (var other in recognition.Alternatives) sb.Append("• ").AppendLine(DeviceTypeConverter.Display(other.Type));
            }
            if (recognition.Conflict) sb.AppendLine().AppendLine(Loc("Str_Rec_Conflict", "Another type scored close to this one."));
            sb.AppendLine().Append(Loc("Str_Rec_Caveat", "Confidence grades how well the evidence fits the type."));
            return sb.ToString();
        }

        /// <summary>A plain English evidence line for CSV, independent of the app language.</summary>
        internal static string Csv(DeviceRecognition? recognition) =>
            recognition == null ? string.Empty
                : string.Join("; ", recognition.Signals.Select(s => s.Source + ": " + s.Detail));

        private static string Automatic(DeviceRecognition recognition) =>
            string.Format(Loc("Str_Rec_Automatic", "Automatic result: {0} ({1})"),
                DeviceTypeConverter.Display(recognition.AutomaticType), Confidence(recognition.AutomaticConfidence));

        private static string Loc(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
