using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AltTitles.Titles;

/// <summary>
/// Anime titles from AniDB: the Anime-Lists mapping finds the AniDB entries of a TMDB/TVDB series or
/// movie, AniDB's daily title dump gives their titles (romaji main title, official titles in many
/// languages, synonyms). Both files are downloaded at most once a day (AniDB allows no more) and cached.
/// </summary>
public sealed class AniDbTitles
{
    private const string MappingUrl = "https://raw.githubusercontent.com/Anime-Lists/anime-lists/master/anime-list-master.xml";
    private const string TitlesUrl = "https://anidb.net/api/anime-titles.xml.gz";
    private static readonly XNamespace XmlNs = "http://www.w3.org/XML/1998/namespace";
    private static readonly HashSet<string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        // romanized Japanese/Chinese/Korean, and the languages people search in
        "x-jat", "x-zht", "x-kot", "en", "de", "ja", "ko", "zh-Hans", "zh-Hant", "zh"
    };

    private readonly IHttpClientFactory _http;
    private readonly ILogger<AniDbTitles> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, List<string>>? _byTmdbTv;
    private Dictionary<string, List<string>>? _byTmdbMovie;
    private Dictionary<string, List<string>>? _byTvdb;
    private Dictionary<string, List<AltTitle>>? _titles;
    private DateTime _loadedAt;

    public AniDbTitles(IHttpClientFactory http, ILogger<AniDbTitles> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>AniDB ids for a series (all its seasons) or movie.</summary>
    public async Task<List<string>> FindAsync(string? tmdbTv, string? tmdbMovie, string? tvdb, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var ids = new List<string>();
        void Add(Dictionary<string, List<string>>? map, string? key)
        {
            if (map is not null && !string.IsNullOrEmpty(key) && map.TryGetValue(key, out var list))
            {
                ids.AddRange(list.Where(i => !ids.Contains(i)));
            }
        }

        Add(_byTmdbTv, tmdbTv);
        Add(_byTmdbMovie, tmdbMovie);
        if (ids.Count == 0)
        {
            Add(_byTvdb, tvdb);
        }

        return ids;
    }

    public IEnumerable<AltTitle> TitlesOf(string aid)
    {
        return _titles is not null && _titles.TryGetValue(aid, out var list) ? list : Enumerable.Empty<AltTitle>();
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_titles is not null && DateTime.UtcNow - _loadedAt < TimeSpan.FromHours(24))
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_titles is not null && DateTime.UtcNow - _loadedAt < TimeSpan.FromHours(24))
            {
                return;
            }

            var mapping = XDocument.Load(await CachedAsync(MappingUrl, "anime-list-master.xml", ct).ConfigureAwait(false));
            var byTv = new Dictionary<string, List<string>>();
            var byMovie = new Dictionary<string, List<string>>();
            var byTvdb = new Dictionary<string, List<string>>();
            foreach (var a in mapping.Root?.Elements("anime") ?? Enumerable.Empty<XElement>())
            {
                var aid = (string?)a.Attribute("anidbid");
                if (string.IsNullOrEmpty(aid))
                {
                    continue;
                }

                AddTo(byTv, (string?)a.Attribute("tmdbtv"), aid);
                foreach (var m in ((string?)a.Attribute("tmdbid") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    AddTo(byMovie, m, aid);
                }

                var tvdb = (string?)a.Attribute("tvdbid");
                if (tvdb is not null && tvdb.All(char.IsDigit))
                {
                    AddTo(byTvdb, tvdb, aid);
                }
            }

            var titles = new Dictionary<string, List<AltTitle>>();
            await using (var gz = new GZipStream(await CachedAsync(TitlesUrl, "anime-titles.xml.gz", ct).ConfigureAwait(false), CompressionMode.Decompress))
            {
                var doc = XDocument.Load(gz);
                foreach (var anime in doc.Root?.Elements("anime") ?? Enumerable.Empty<XElement>())
                {
                    var aid = (string?)anime.Attribute("aid");
                    if (aid is null)
                    {
                        continue;
                    }

                    var list = new List<AltTitle>();
                    foreach (var t in anime.Elements("title"))
                    {
                        var type = (string?)t.Attribute("type");
                        var lang = (string?)t.Attribute(XmlNs + "lang") ?? string.Empty;
                        if (type is "main" || (type is "official" or "syn" && Languages.Contains(lang)))
                        {
                            list.Add(new AltTitle(t.Value.Trim(), $"anidb:{lang}"));
                        }
                    }

                    titles[aid] = list;
                }
            }

            (_byTmdbTv, _byTmdbMovie, _byTvdb, _titles, _loadedAt) = (byTv, byMovie, byTvdb, titles, DateTime.UtcNow);
        }
        finally
        {
            _gate.Release();
        }

        static void AddTo(Dictionary<string, List<string>> map, string? key, string aid)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = new List<string>();
            }

            list.Add(aid);
        }
    }

    /// <summary>A local copy no older than a day; on a failed download an older copy is used.</summary>
    private async Task<Stream> CachedAsync(string url, string name, CancellationToken ct)
    {
        var dir = Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "cache");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        var fresh = File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(24);
        if (!fresh)
        {
            try
            {
                var client = _http.CreateClient("AltTitles");
                client.Timeout = TimeSpan.FromSeconds(60);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("jellyfin-plugin-alttitles/1.0");
                using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(path + ".tmp", bytes, ct).ConfigureAwait(false);
                File.Move(path + ".tmp", path, true);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && File.Exists(path))
            {
                _logger.LogWarning("Alternative Titles: {Url} not downloaded ({Message}), using the cached copy", url, ex.Message);
            }
        }

        return File.OpenRead(path);
    }
}
