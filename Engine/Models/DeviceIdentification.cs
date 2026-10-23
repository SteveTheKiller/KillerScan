using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KillerScan.Engine;

public sealed record RecognitionEvidence
{
    public string Source { get; init; } = "";
    public string Value { get; init; } = "";
    public string Origin { get; init; } = "";
    public DateTimeOffset? ObservedAtUtc { get; init; }
    public bool Reused { get; init; }
}

public sealed record RecognitionRule(string Candidate, int Weight, string[] Sources);
public sealed record RecognitionCandidate(string Type, int Score, string[] Sources);

/// <summary>Heuristic assessment, not a calibrated probability or authenticated hardware identity.</summary>
public sealed record DeviceIdentification
{
    public int Version { get; init; } = 1;
    public string AutomaticType { get; init; } = "Unknown";
    public string Confidence { get; init; } = "None";
    public string? ManualType { get; init; }
    public string? ReportedModel { get; init; }
    public bool ModelConflict { get; init; }
    public bool ConflictingSignals { get; init; }
    public DateTimeOffset AssessedAtUtc { get; init; }
    public DateTimeOffset? ObservationStartedAtUtc { get; init; }
    public DateTimeOffset? ObservationCompletedAtUtc { get; init; }
    public string Scope { get; init; } = "external";
    public string[] DisabledFeatures { get; init; } = [];
    public RecognitionEvidence[] Evidence { get; init; } = [];
    public RecognitionRule[] Rules { get; init; } = [];
    public RecognitionCandidate[] Candidates { get; init; } = [];
    [JsonIgnore] public string EffectiveConfidence => ManualType == null ? Confidence : "Manual";

    public DeviceIdentification Snapshot() => this with
    {
        DisabledFeatures = [.. DisabledFeatures], Evidence = [.. Evidence],
        Rules = Rules.Select(rule => rule with { Sources = [.. rule.Sources] }).ToArray(),
        Candidates = Candidates.Select(candidate => candidate with { Sources = [.. candidate.Sources] }).ToArray()
    };

    public static void ApplyManualType(NetworkDevice device, string? type)
    {
        var result = device.Identification ?? new DeviceIdentification { Scope = "manual", AssessedAtUtc = DateTimeOffset.UtcNow };
        device.Identification = result with { ManualType = type };
        device.DeviceType = type ?? result.AutomaticType;
    }
}

internal sealed class RecognitionAssessment
{
    private readonly NetworkDevice _device;
    private readonly string? _manual;
    private readonly List<RecognitionEvidence> _evidence = [];
    private readonly List<RecognitionRule> _rules = [];
    internal RecognitionAssessment(NetworkDevice device, string? manual)
    {
        _device = device; _manual = manual;
        AddFact("Name", device.Hostname, device.HostnameSource == "mDNS" ? device.MdnsObservedAtUtc : device.ObservationCompletedAtUtc,
            device.HostnameSource == "mDNS" && device.RecognitionScope == "deep", device.HostnameSource);
        AddFact("OUI", device.Vendor, null, true); // A database lookup, not a live model announcement.
        AddFact("TCP", string.Join(", ", device.OpenPorts.OrderBy(port => port)));
        if (!string.IsNullOrWhiteSpace(device.HttpTitle)) AddFact("HTTP", "title=" + Bounded(device.HttpTitle));
        if (!string.IsNullOrWhiteSpace(device.HttpServer)) AddFact("HTTP", "Server=" + Bounded(device.HttpServer));
        AddFact("TLS", device.TlsSubject); AddFact("SSH", device.SshBanner);
        AddFact("SNMP", device.SnmpDescr); AddFact("NetBIOS", device.NetbiosName);
        foreach (string service in device.MdnsServices.Take(16).Distinct())
            AddFact("mDNS", Bounded(service), device.MdnsObservedAtUtc, device.RecognitionScope == "deep");
        AddFact("SSDP", device.SsdpServer, device.SsdpObservedAtUtc, device.RecognitionScope == "deep");
        AddFact("ICMP", device.Ttl?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }

    internal static string Bounded(string? value) => new((value ?? "").Take(480).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    private void AddFact(string source, string value) => AddFact(source, value, _device.ObservationCompletedAtUtc, false);
    private void AddFact(string source, string value, DateTimeOffset? observed, bool reused, string origin = "")
    {
        if (!string.IsNullOrWhiteSpace(value)) _evidence.Add(new RecognitionEvidence
        { Source = source, Origin = origin, Value = new string(value.Take(512).Select(c => char.IsControl(c) ? ' ' : c).ToArray()), ObservedAtUtc = observed, Reused = reused });
    }
    internal void Add(string type, int weight, params string[] sources)
    {
        type = type == "Network" ? "Switch/AP" : type;
        var present = sources.Distinct().Where(source => _evidence.Any(fact => fact.Source == source)).ToArray();
        if (present.Length != 0) _rules.Add(new RecognitionRule(type, weight, present));
    }
    internal string Finish(string? routeRole = null)
    {
        // Repeated tests of the same protocol/vendor do not create independent corroboration.
        var candidates = _rules.GroupBy(rule => rule.Candidate).Select(group =>
        {
            var bySource = group.SelectMany(rule => rule.Sources.Select(source => (Source: source, rule.Weight)))
                .GroupBy(item => item.Source).ToArray();
            return new RecognitionCandidate(group.Key, bySource.Sum(items => Math.Min(15, items.Max(item => item.Weight))),
                bySource.Select(items => items.Key).ToArray());
        }).OrderByDescending(item => item.Score).ThenBy(item => item.Type, StringComparer.Ordinal).ToArray();
        var winner = candidates.FirstOrDefault(item => item.Score >= 6);
        bool conflict = winner != null && candidates.Skip(1).Any(item => item.Score >= 6 &&
            (winner.Score - item.Score <= 5 || _rules.Any(rule => rule.Candidate == item.Type && rule.Weight >= 10 &&
                rule.Sources.Any(source => source is "HTTP" or "TLS" or "SSH" or "SNMP" or "mDNS" or "SSDP"))));
        string automatic = routeRole ?? winner?.Type ?? "Unknown";
        string confidence = "None";
        if (routeRole != null) confidence = "Medium"; // Configured role, not hardware identification.
        else if (winner != null)
        {
            int protocols = winner.Sources.Count(source => (source is "HTTP" or "TLS" or "SSH" or "SNMP" or "mDNS" or "SSDP") &&
                _evidence.Any(fact => fact.Source == source && !fact.Reused));
            bool specific = _rules.Any(rule => rule.Candidate == winner.Type && rule.Weight >= 10 &&
                rule.Sources.Any(source => source is "HTTP" or "TLS" or "SSH" or "SNMP" or "mDNS" or "SSDP"));
            confidence = !conflict && specific ? protocols >= 2 ? "High" : "Medium" : "Low";
        }
        // Accept explicit model declarations only. Never guess a model from an OUI or port.
        var models = _evidence.Where(fact => fact.Source is "SNMP" or "HTTP" or "SSDP")
            .SelectMany(fact => Regex.Matches(fact.Value, @"\bmodel(?:name)?\s*[:=]\s*([\p{L}\p{N}][^|;,]{0,63})(?=[|;,]|$)", RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100)).Cast<Match>().Select(match => match.Groups[1].Value.Trim()))
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _device.Identification = new DeviceIdentification
        {
            AutomaticType = automatic, Confidence = confidence, ManualType = _manual,
            ReportedModel = models.Length == 1 ? models[0] : null, ModelConflict = models.Length > 1,
            ConflictingSignals = conflict, AssessedAtUtc = DateTimeOffset.UtcNow,
            ObservationStartedAtUtc = _device.ObservationStartedAtUtc, ObservationCompletedAtUtc = _device.ObservationCompletedAtUtc,
            Scope = _device.RecognitionScope, DisabledFeatures = _device.RecognitionDisabledFeatures,
            Evidence = _evidence.ToArray(), Rules = _rules.ToArray(), Candidates = candidates
        };
        return _manual ?? automatic;
    }
    internal void Route(string gateway, string dns) => AddFact("Route", gateway + " / DNS " + dns);
    internal void Vendor(string vendor)
    {
        _evidence.RemoveAll(fact => fact.Source == "OUI");
        AddFact("OUI", vendor, null, true);
    }
}
