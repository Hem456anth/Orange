using System.Net.Http.Headers;
using System.Text.Json;

namespace Orange.Core.Updates;

public sealed record UpdateInfo(string Version, string Notes, string Url, bool IsNewer);

/// <summary>
/// Opt-in update check (Settings → Updates, off by default so Orange makes no network request unless asked).
/// One GET to the GitHub releases API for this repository; no identifiers, no telemetry, nothing downloaded.
/// </summary>
public sealed class UpdateChecker(HttpMessageHandler? handler = null)
{
    public const string ReleasesApi = "https://api.github.com/repos/Hem456anth/Orange/releases/latest";

    public async Task<UpdateInfo> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(15);
        using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Orange", current.ToString(3)));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new OrangeException($"The update check failed ({(int)resp.StatusCode}).");
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (json.Length > 2_000_000) throw new OrangeException("The update server sent an unexpected answer.");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
        if (!url.StartsWith("https://github.com/Hem456anth/Orange/", StringComparison.Ordinal)) url = "https://github.com/Hem456anth/Orange/releases";
        var v = Parse(tag);
        return new UpdateInfo(tag.TrimStart('v'), notes.Length > 20_000 ? notes[..20_000] + "…" : notes, url, v is not null && v > current);
    }

    public static Version? Parse(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOf('-'); if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? v : null;
    }
}
