using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace MOIRAI.Services;

public sealed class UpdateChecker
{
    public enum Status { UpToDate, UpdateAvailable, NotConfigured, CouldNotCheck }

    public sealed record Result(
        Status Status, string CurrentVersion, string? LatestVersion, string? Url, string? Notes,
        IReadOnlyList<string> Changelog, string? Message);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private const string ConfigFile = "update-config.json";

    public string CurrentVersion { get; } = ReadVersion();

    private Source? _winSource;
    private Manifest? _winManifest;

    public async Task<Result> CheckAsync(CancellationToken ct = default)
    {
        var cfg = LoadConfig();
        if (cfg is null || cfg.Sources.Count == 0)
            return Fail(Status.NotConfigured, $"No {ConfigFile} found next to the application.");

        string? lastError = null;
        foreach (var src in cfg.Sources)
        {
            try
            {
                var m = await FetchManifestAsync(src, cfg.ManifestPath, ct);
                if (m?.Version is null || !TryParseVersion(m.Version, out var latest)) continue;

                _winSource = src;
                _winManifest = m;
                var current = TryParseVersion(CurrentVersion, out var c) ? c : new Version(0, 0, 0);
                var status = latest > current ? Status.UpdateAvailable : Status.UpToDate;
                return new(status, CurrentVersion, m.Version, m.Url, m.Notes,
                    m.Changelog ?? Array.Empty<string>(), null);
            }
            catch (Exception ex) { lastError = ex.Message; }
        }
        return Fail(Status.CouldNotCheck, lastError ?? "No source responded.");
    }

    public async Task<string> DownloadAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        if (_winSource is not { } src || _winManifest is not { } m)
            throw new InvalidOperationException("No update available to download.");

        var url = m.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url) && string.Equals(src.Kind, "github", StringComparison.OrdinalIgnoreCase))
            url = await ResolveGitHubAssetUrlAsync(src, m.Version!, ct);
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("No download URL for the update.");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("MOIRAI-app");
        if (string.Equals(src.Kind, "github", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.Accept.ParseAdd("application/octet-stream");
            if (!string.IsNullOrWhiteSpace(src.Token)) req.Headers.Authorization = new("Bearer", src.Token);
        }
        else if (!string.IsNullOrWhiteSpace(src.Token))
        {
            req.Headers.Add("PRIVATE-TOKEN", src.Token);
        }

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var zipPath = Path.Combine(Path.GetTempPath(), $"MOIRAI-update-{Guid.NewGuid():N}.zip");
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using (var src2 = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(zipPath))
        {
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src2.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }
        progress?.Report(1.0);
        return zipPath;
    }

    private static async Task<Manifest?> FetchManifestAsync(Source src, string manifestPath, CancellationToken ct)
    {
        switch (src.Kind?.ToLowerInvariant())
        {
            case "github":
            {
                using var req = GitHubContents(src, manifestPath);
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;
                return JsonSerializer.Deserialize<Manifest>(await resp.Content.ReadAsStringAsync(ct), JsonOpts);
            }
            case "gitlab":
                return await GitLabLatestReleaseAsync(src, ct);
            default:
                return null;
        }
    }

    private static HttpRequestMessage GitHubContents(Source s, string path)
    {
        var url = $"https://api.github.com/repos/{s.Owner}/{s.Repo}/contents/{path}?ref={s.Ref ?? "main"}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("MOIRAI-app");
        req.Headers.Accept.ParseAdd("application/vnd.github.raw");
        if (!string.IsNullOrWhiteSpace(s.Token)) req.Headers.Authorization = new("Bearer", s.Token);
        return req;
    }

    private static async Task<Manifest?> GitLabLatestReleaseAsync(Source s, CancellationToken ct)
    {
        var url = $"{s.BaseUrl?.TrimEnd('/')}/api/v4/projects/{s.ProjectId}/releases?per_page=1";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("MOIRAI-app");
        if (!string.IsNullOrWhiteSpace(s.Token)) req.Headers.Add("PRIVATE-TOKEN", s.Token);
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;
        var rel = doc.RootElement[0];

        string? tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag)) return null;

        string? name = rel.TryGetProperty("name", out var nm) ? nm.GetString() : null;
        string? desc = rel.TryGetProperty("description", out var d) ? d.GetString() : null;
        string? web = rel.TryGetProperty("_links", out var lk) && lk.TryGetProperty("self", out var self)
            ? self.GetString() : null;

        string? zip = null;
        if (rel.TryGetProperty("assets", out var assets) && assets.TryGetProperty("links", out var links)
            && links.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in links.EnumerateArray())
            {
                var aName = a.TryGetProperty("name", out var an) ? an.GetString() : null;
                var aUrl = a.TryGetProperty("direct_asset_url", out var du) ? du.GetString()
                         : a.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(aUrl)) continue;
                bool isZip = (aName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ?? false)
                           || aUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                if (isZip) { zip = aUrl; break; }
                zip ??= aUrl;
            }
        }

        var changelog = desc is null ? null
            : desc.Replace("\r\n", "\n").Split('\n')
                  .Select(l => l.Trim().TrimStart('-', '*', ' ').Trim())
                  .Where(l => l.Length > 0).ToArray();

        return new Manifest
        {
            Version = tag,
            Url = web,
            Notes = name,
            DownloadUrl = zip,
            Changelog = changelog,
        };
    }

    private static async Task<string?> ResolveGitHubAssetUrlAsync(Source s, string version, CancellationToken ct)
    {
        foreach (var tag in new[] { $"v{version}", version })
        {
            var url = $"https://api.github.com/repos/{s.Owner}/{s.Repo}/releases/tags/{tag}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("MOIRAI-app");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(s.Token)) req.Headers.Authorization = new("Bearer", s.Token);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) continue;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("assets", out var assets)) continue;
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is not null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && a.TryGetProperty("url", out var u))
                    return u.GetString();
            }
        }
        return null;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private Result Fail(Status s, string msg) =>
        new(s, CurrentVersion, null, null, null, Array.Empty<string>(), msg);

    private static Config? LoadConfig()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, ConfigFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<Config>(File.ReadAllText(path), JsonOpts) : null;
        }
        catch { return null; }
    }

    private static string ReadVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version ?? typeof(UpdateChecker).Assembly.GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static bool TryParseVersion(string text, out Version version)
    {
        var s = text.Trim().TrimStart('v', 'V');
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out version!);
    }

    private sealed class Config
    {
        public string ManifestPath { get; set; } = "latest-version.json";
        public List<Source> Sources { get; set; } = new();
    }

    private sealed class Source
    {
        public string? Kind { get; set; }
        public string? Owner { get; set; }
        public string? Repo { get; set; }
        public string? BaseUrl { get; set; }
        public string? ProjectId { get; set; }
        public string? Ref { get; set; }
        public string? Token { get; set; }
    }

    private sealed class Manifest
    {
        public string? Version { get; set; }
        public string? Url { get; set; }
        public string? Notes { get; set; }
        public string? DownloadUrl { get; set; }
        public string[]? Changelog { get; set; }
    }
}
