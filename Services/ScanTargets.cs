using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace KillerScan.Services
{
    /// <summary>Why a target string could not be turned into an address list.</summary>
    internal enum TargetError { None, Empty, Invalid, TooLarge }

    /// <summary>Result of parsing the subnet box. Never throws - a bad token comes back as
    /// <see cref="Error"/> plus the offending text in <see cref="Detail"/>, so the UI can say
    /// which piece is wrong instead of surfacing a raw exception.</summary>
    internal sealed class ScanTargetResult
    {
        public List<IPAddress> Addresses { get; } = [];
        /// <summary>Cleaned target tokens, in the order given, e.g. ["192.168.9.0/24", "192.168.10.10-50"].</summary>
        public List<string> Targets { get; } = [];
        public TargetError Error { get; set; }
        public string Detail { get; set; } = "";
        public bool Ok => Error == TargetError.None;

        /// <summary>Short caption for the tab and status bar: "192.168.9.0/24 +2".</summary>
        public string Summary =>
            Targets.Count == 0 ? "" :
            Targets.Count == 1 ? Targets[0] : $"{Targets[0]} +{Targets.Count - 1}";
    }

    /// <summary>
    /// Turns whatever is typed in the subnet box into a de-duplicated address list.
    ///
    /// Accepts several targets separated by commas (semicolons and newlines work too), each of
    /// which may be a CIDR block (192.168.9.0/24), a single host (192.168.1.7), a full range
    /// (192.168.1.10-192.168.1.50), or a short range whose right side is just the last octet
    /// (192.168.1.10-50).
    ///
    /// Deliberately forgiving about spacing: all whitespace inside a token is stripped before
    /// parsing, so "192.168.9.0 /24 , 192.168.10.10 - 50" is read the same as the tight form.
    /// No IPv4 target has a meaningful space in it, so nothing is lost by doing that. Empty
    /// tokens are skipped, which makes a trailing or doubled comma harmless.
    /// </summary>
    internal static class ScanTargets
    {
        /// <summary>Combined ceiling across every target - one /16 worth of addresses. A
        /// fat-fingered /8 is 16.7M and would lock the app up, so it is refused with a count
        /// rather than attempted.</summary>
        public const int MaxAddresses = 65536;

        private static readonly char[] Separators = [',', ';', '\n', '\r', '\t'];

        public static ScanTargetResult Parse(string? input)
        {
            var res = new ScanTargetResult();
            if (string.IsNullOrWhiteSpace(input)) { res.Error = TargetError.Empty; return res; }

            // (start, end) inclusive spans, collected before enumerating so the size ceiling can
            // be checked without ever materialising a huge list.
            var spans = new List<(uint Start, uint End)>();

            foreach (string rawToken in input!.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = Regex.Replace(rawToken, @"\s+", "");
                if (token.Length == 0) continue;

                if (!TryParseTarget(token, out uint start, out uint end))
                {
                    res.Error = TargetError.Invalid;
                    res.Detail = rawToken.Trim();
                    return res;
                }
                spans.Add((start, end));
                res.Targets.Add(token);
            }

            if (spans.Count == 0) { res.Error = TargetError.Empty; return res; }

            long total = 0;
            foreach (var (s, e) in spans) total += (long)e - s + 1;
            if (total > MaxAddresses)
            {
                res.Error = TargetError.TooLarge;
                res.Detail = total.ToString("N0");
                return res;
            }

            // Overlapping targets collapse; first-seen order is preserved.
            var seen = new HashSet<uint>();
            foreach (var (s, e) in spans)
                for (uint a = s; ; a++)
                {
                    if (seen.Add(a)) res.Addresses.Add(FromUInt(a));
                    if (a == e) break;      // guards the a == uint.MaxValue wrap
                }

            return res;
        }

        /// <summary>One cleaned token -> an inclusive address span.</summary>
        private static bool TryParseTarget(string token, out uint start, out uint end)
        {
            start = end = 0;

            int slash = token.IndexOf('/');
            if (slash >= 0)
            {
                if (!TryParseIPv4(token.Substring(0, slash), out uint ip)) return false;
                if (!int.TryParse(token.Substring(slash + 1), out int bits) || bits < 0 || bits > 32)
                    return false;

                uint mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
                uint network = ip & mask;
                uint broadcast = network | ~mask;

                // /32 is a single host and /31 is a two-host point-to-point link; neither has a
                // network or broadcast address to skip, and the usual network+1..broadcast-1 walk
                // would return nothing for both.
                if (bits >= 31) { start = network; end = broadcast; return true; }

                start = network + 1; end = broadcast - 1;
                return true;
            }

            int dash = token.IndexOf('-');
            if (dash > 0)
            {
                string left = token.Substring(0, dash), right = token.Substring(dash + 1);
                if (!TryParseIPv4(left, out start)) return false;

                if (TryParseIPv4(right, out end))
                {
                    // full form: 192.168.1.10-192.168.1.50
                }
                else if (byte.TryParse(right, out byte lastOctet))
                {
                    // short form: 192.168.1.10-50 - the right side replaces the last octet only
                    end = (start & 0xFFFFFF00u) | lastOctet;
                }
                else return false;

                return end >= start;
            }

            if (!TryParseIPv4(token, out uint single)) return false;
            start = end = single;
            return true;
        }

        /// <summary>Strict dotted-quad parse. Deliberately stricter than IPAddress.TryParse,
        /// which happily reads "192.168.1" as 192.168.0.1 and "7" as 0.0.0.7 - shorthand that
        /// would silently scan something the user never typed.</summary>
        private static bool TryParseIPv4(string s, out uint value)
        {
            value = 0;
            string[] parts = s.Split('.');
            if (parts.Length != 4) return false;
            foreach (string p in parts)
            {
                if (p.Length == 0 || p.Length > 3) return false;
                foreach (char c in p) if (c < '0' || c > '9') return false;
                if (!byte.TryParse(p, out byte b)) return false;
                value = (value << 8) | b;
            }
            return true;
        }

        private static IPAddress FromUInt(uint a) =>
            new IPAddress([(byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a]);
    }
}
