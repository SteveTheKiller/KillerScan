using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KillerScan.Engine;
using KillerScan.Features.Cli;

/// <summary>
/// killerscan network and vendor answer in JSON for scripts and KillerMCP, with the same values the text form prints.
/// </summary>
internal static class CliJsonTests
{
    internal static void Run()
    {
        Network();
        Vendor();
        Console.WriteLine("PASS: network and vendor /json carry the same values as their text output.");
    }

    private static void Network()
    {
        var (code, text, _) = Call(CliRunner.RunNetwork);
        var (jsonCode, json, jsonErr) = Call(CliRunner.RunNetwork, "/json");
        if (code != jsonCode) throw new Exception($"network exits {code} in text and {jsonCode} with /json.");
        var bad = Call(CliRunner.RunNetwork, "/bogus");
        if (bad.Code != 2) throw new Exception("network still rejects an unknown option, exit " + bad.Code + ".");
        if (code != 0) { Console.WriteLine("SKIP: no active IPv4 network to compare. " + jsonErr); return; }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Field(string name) => root.TryGetProperty(name, out var value) ? value.GetString() ?? ""
            : throw new Exception("network /json has no " + name + " field: " + json);
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        string Line(string label)
        {
            string value = lines.Single(line => line.StartsWith(label, StringComparison.Ordinal)).Substring(11);
            return value == "-" ? "" : value;
        }
        if (Field("localIp") != Line("LOCAL IP") || Field("subnet") != Line("SUBNET") || Field("gateway") != Line("GATEWAY")
            || Field("dns") != Line("DNS") || Field("interface") != Line("INTERFACE"))
            throw new Exception("network /json disagrees with the text form.\n" + text + "\n" + json);
        if (!root.TryGetProperty("adapter", out _) || !root.TryGetProperty("wireless", out _) || !root.TryGetProperty("linkSpeed", out _))
            throw new Exception("network /json carries the adapter, wireless and link speed: " + json);
    }

    private static void Vendor()
    {
        OuiLookup.Load();
        string known = "00:00:0C:12:34:56";
        string expected = OuiLookup.GetVendor(known);
        if (expected.Length == 0) throw new Exception("The bundled OUI list has no entry for 00:00:0C.");
        var (code, json, _) = Call(CliRunner.RunVendor, "00-00-0c-12-34-56", "/json");
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            if (code != 0 || root.GetProperty("mac").GetString() != known || root.GetProperty("vendor").GetString() != expected
                || !root.GetProperty("known").GetBoolean())
                throw new Exception("vendor /json for a listed prefix: exit " + code + ", " + json);
        }
        var text = Call(CliRunner.RunVendor, known);
        if (text.Code != 0 || text.Out.Trim() != expected) throw new Exception("vendor text output changed: " + text.Out);

        string unknown = "02:00:5E:00:00:01";
        if (OuiLookup.GetVendor(unknown).Length != 0) throw new Exception("Pick another unlisted prefix for the test.");
        var (missCode, missJson, _) = Call(CliRunner.RunVendor, unknown, "/json");
        using (var doc = JsonDocument.Parse(missJson))
        {
            var root = doc.RootElement;
            if (missCode != 3 || root.GetProperty("vendor").GetString() != "" || root.GetProperty("known").GetBoolean())
                throw new Exception("vendor /json for an unlisted prefix keeps exit 3: exit " + missCode + ", " + missJson);
        }
        if (Call(CliRunner.RunVendor, "zz", "/json").Code != 2) throw new Exception("vendor /json still rejects a bad MAC.");
    }

    private static (int Code, string Out, string Err) Call(
        Func<List<string>, Dictionary<string, string>, TextWriter, TextWriter, int> run, params string[] words)
    {
        var options = words.Where(word => word.StartsWith("/", StringComparison.Ordinal))
            .ToDictionary(word => word, _ => "", StringComparer.OrdinalIgnoreCase);
        var @out = new StringWriter();
        var err = new StringWriter();
        int code = run(words.Where(word => !word.StartsWith("/", StringComparison.Ordinal)).ToList(), options, @out, err);
        return (code, @out.ToString().Trim(), err.ToString().Trim());
    }
}
