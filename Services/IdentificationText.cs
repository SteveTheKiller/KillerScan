using System.Text;
using System.Windows;
using KillerScan.Engine;

namespace KillerScan.Services;

internal static class IdentificationText
{
    internal static string L(string key) => Application.Current?.TryFindResource("Str_Evidence_" + key) as string ?? key switch
    {
        "Title" => "Recognition evidence", "Model" => "Reported model (unverified)", "Time" => "Observation window (UTC)",
        "Automatic" => "Automatic type", "Disabled" => "Disabled probes", "External" => "Imported / unspecified scope",
        "Candidates" => "Other candidates", "Cached" => "Earlier / cached", "None" => "Not available",
        "Note" => "Heuristic estimate, not a probability. Protocol values are self-reported. MAC vendor does not identify a model; missing responses do not prove absence.",
        "Conflict" => "Conflicting signals; check the individual sources.", _ => key
    };
    internal static string Confidence(DeviceIdentification? result) => L(result?.EffectiveConfidence is "Low" or "Medium" or "High" or "Manual"
        ? result.EffectiveConfidence : "None");
    internal static string Describe(NetworkDevice device)
    {
        var text = new StringBuilder();
        var result = device.Identification;
        text.AppendLine(device.IpAddress + "  " + device.Hostname);
        text.AppendLine(Controls.DeviceTypeConverter.Display(device.DeviceType) + " - " + L("Confidence") + ": " + Confidence(result));
        if (result == null) return text.AppendLine(L("None")).AppendLine(L("Note")).ToString();
        var winner = result.Candidates.FirstOrDefault(candidate => candidate.Type == result.AutomaticType);
        text.AppendLine(L("Automatic") + ": " + Controls.DeviceTypeConverter.Display(result.AutomaticType) + " - " + L(result.Confidence) +
            " (" + string.Join(", ", winner?.Sources.Select(SourceName) ?? result.Evidence.Where(fact => fact.Source == "Route").Select(fact => SourceName(fact.Source))) + ")");
        text.AppendLine(L("Model") + ": " + (result.ReportedModel ?? L("None")));
        text.AppendLine(L("Time") + ": " + Time(result.ObservationStartedAtUtc) + " / " + Time(result.ObservationCompletedAtUtc));
        text.AppendLine(L(result.Scope switch { "quick" => "Quick", "full" => "Full", "deep" => "Deep", _ => "External" }) + "; " + L("Disabled") + ": " + result.DisabledFeatures.Length);
        if (result.ConflictingSignals || result.ModelConflict) text.AppendLine(L("Conflict"));
        text.AppendLine().AppendLine(L("Note")).AppendLine().AppendLine(L("Source") + " / " + L("Value"));
        foreach (var fact in result.Evidence)
        {
            string source = fact.Source == "Name" && fact.Origin.Length > 0 ? fact.Origin : SourceName(fact.Source);
            text.AppendLine(source + "  " + Time(fact.ObservedAtUtc) + (fact.Reused ? "  [" + L("Cached") + "]" : ""));
            text.AppendLine("  " + fact.Value);
        }
        if (result.Candidates.Any(candidate => candidate.Type != result.AutomaticType && candidate.Score >= 6))
        {
            text.AppendLine().AppendLine(L("Candidates"));
            foreach (var candidate in result.Candidates.Where(candidate => candidate.Type != result.AutomaticType && candidate.Score >= 6))
                text.AppendLine(Controls.DeviceTypeConverter.Display(candidate.Type) + " (" + string.Join(", ", candidate.Sources.Select(SourceName)) + ")");
        }
        return text.ToString();
    }
    private static string Time(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? L("None");
    private static string SourceName(string source) => source switch
    {
        "Name" => Application.Current?.TryFindResource("Str_Col_Host") as string ?? "Hostname",
        "Route" => Application.Current?.TryFindResource("Str_Lbl_Gateway") as string ?? "Gateway/DNS",
        "OUI" => "MAC/OUI", _ => source
    };
}
