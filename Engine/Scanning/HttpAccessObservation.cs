using System.Net;
using System.Text.RegularExpressions;

namespace KillerScan.Engine;

internal static class HttpAccessObservation
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);
    internal static async Task<string> ReadBody(System.Net.Http.HttpContent content, int timeout, CancellationToken token)
    {
        using var stream = await content.ReadAsStreamAsync();
        using var bytes = new System.IO.MemoryStream();
        var buffer = new byte[4096];
        var deadline = Task.Delay(timeout, token);
        while (bytes.Length < 65536)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, 65536 - bytes.Length), token);
            if (await Task.WhenAny(read, deadline) != read)
            {
                _ = read.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                token.ThrowIfCancellationRequested();
                break;
            }
            int count = await read;
            if (count == 0) break;
            bytes.Write(buffer, 0, count);
        }
        // Incomplete forms deliberately remain Unknown.
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
    internal static void RecordRaw(NetworkDevice device, int port, bool tls, string response, bool headersOnly = false)
    {
        int separator = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator < 0) return;
        var status = Regex.Match(response, @"\AHTTP/1\.[01] ([1-5][0-9]{2})(?:[ \r\n])", RegexOptions.None, Limit);
        if (!status.Success) return;
        Record(device, port, tls, int.Parse(status.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            response[..separator], headersOnly ? "" : response[(separator + 4)..]);
    }
    internal static void Record(NetworkDevice device, int port, bool tls, int status, string headers, string body)
    {
        if (status is < 100 or > 599) return;
        headers = headers.Length > 16384 ? headers[..16384] : headers;
        body = body.Length > 65536 ? body[..65536] : body;
        var challenge = Regex.Match(headers, @"(?im)^WWW-Authenticate:\s*([A-Za-z][A-Za-z0-9_-]{0,31})", RegexOptions.None, Limit);
        body = Regex.Replace(body, @"(?is)<!--.*?-->|<(script|style)\b[^>]*>.*?</\1\s*>", "", RegexOptions.None, Limit);
        // A password input inside a form is evidence of a login UI, not of successful authentication.
        bool login = Regex.Matches(body, @"(?is)<form\b[^>]*>(.*?)</form\s*>", RegexOptions.None, Limit).Cast<Match>()
            .Any(form => Regex.IsMatch(form.Groups[1].Value, @"(?is)<input\s[^>]*?\stype\s*=\s*(?:""password""|'password'|password(?=[\s/>]))|<input\s+type\s*=\s*(?:""password""|'password'|password(?=[\s/>]))", RegexOptions.None, Limit));
        var endpoint = new AccessEndpoint { Protocol = tls ? "https" : "http", Port = port, Verification = "HttpResponse",
            Authentication = status == 401 ? "Required" : login ? "LoginPage" : "Unknown",
            Challenge = challenge.Success ? challenge.Groups[1].Value : null, HttpStatus = status,
            Redirect = status is >= 300 and <= 399, ObservedAtUtc = DateTimeOffset.UtcNow };
        device.Access ??= [];
        device.Access.RemoveAll(item => item.Protocol == endpoint.Protocol && item.Port == port);
        device.Access.Add(endpoint);
        // Preserve the first useful legacy fingerprint, while keeping endpoint-specific facts separately.
        var server = Regex.Match(headers, @"(?im)^Server:\s*([^\r\n]*)", RegexOptions.None, Limit);
        if (device.HttpServer.Length == 0 && server.Success) device.HttpServer = RecognitionAssessment.Bounded(server.Groups[1].Value.Trim());
        var title = Regex.Match(body, @"(?is)<title[^>]*>(.*?)</title>", RegexOptions.None, Limit);
        if (device.HttpTitle.Length == 0 && title.Success)
            device.HttpTitle = RecognitionAssessment.Bounded(WebUtility.HtmlDecode(Regex.Replace(title.Groups[1].Value, "<[^>]+>", "")).Trim());
    }
}
