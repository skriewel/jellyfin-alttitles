using System.Net.Http.Headers;
using System.Text.Json;
using Jellyfin.Plugin.AltTitles.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AltTitles.Titles;

/// <summary>
/// Gathers the alternative titles of a movie or series:
/// <list type="bullet">
/// <item>the title in each configured language, through Jellyfin's own TMDB provider (no key of our own);</item>
/// <item>for anime: AniDB's romaji, official and synonym titles (via the Anime-Lists mapping);</item>
/// <item>for Korean titles: a romanized form;</item>
/// <item>with an own TMDB key: every alternative title and translation TMDB knows.</item>
/// </list>
/// Titles equal to the name or original title Jellyfin already searches are left out.
/// </summary>
public sealed class TitleCollector
{
    private readonly IProviderManager _providers;
    private readonly AniDbTitles _anidb;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<TitleCollector> _logger;

    public TitleCollector(IProviderManager providers, AniDbTitles anidb, IHttpClientFactory http, ILogger<TitleCollector> logger)
    {
        _providers = providers;
        _anidb = anidb;
        _http = http;
        _logger = logger;
    }

    public async Task<TitleEntry> CollectAsync(BaseItem item, PluginConfiguration cfg, CancellationToken ct)
    {
        var titles = new List<AltTitle>();
        var notes = new List<string>();
        item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdb);
        item.TryGetProviderId(MetadataProvider.Tvdb, out var tvdb);

        // 1. the title in other languages (Jellyfin's TMDB provider)
        if (!string.IsNullOrEmpty(tmdb))
        {
            foreach (var lang in (cfg.Languages ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = await LanguageTitleAsync(item, tmdb!, lang.Trim().ToLowerInvariant(), ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(t))
                {
                    titles.Add(new AltTitle(t!, "tmdb:" + lang.Trim().ToLowerInvariant()));
                }
            }
        }
        else
        {
            notes.Add("no TMDB id");
        }

        // 2. AniDB for anime
        if (cfg.UseAniDb)
        {
            try
            {
                var aids = await _anidb.FindAsync(item is Series ? tmdb : null, item is Movie ? tmdb : null, item is Series ? tvdb : null, ct).ConfigureAwait(false);
                // the first entry is the series (or its first season); later seasons ("Frieren 2", "Sousou no Frieren (2026)")
                // are searchable too, but not listed on the details page
                for (var i = 0; i < aids.Count; i++)
                {
                    titles.AddRange(i == 0 ? _anidb.TitlesOf(aids[i]) : _anidb.TitlesOf(aids[i]).Select(a => a with { Source = a.Source.Replace("anidb:", "anidb-season:", StringComparison.Ordinal) }));
                }

                if (aids.Count > 0)
                {
                    notes.Add("anidb " + string.Join(",", aids));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Xml.XmlException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                notes.Add("anidb unavailable");
                _logger.LogWarning("Alternative Titles: AniDB titles unavailable ({Message})", ex.Message);
            }
        }

        // 3. an own TMDB key: all alternative titles and translations
        if (!string.IsNullOrWhiteSpace(cfg.TmdbApiKey) && !string.IsNullOrEmpty(tmdb) && item is Movie or Series)
        {
            try
            {
                titles.AddRange(await TmdbAlternativesAsync(item is Series ? "tv" : "movie", tmdb!, cfg.TmdbApiKey.Trim(), ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                notes.Add("tmdb alternative titles failed: " + ex.Message);
            }
        }

        // 4. Korean: romanized form of the Korean original title (and Korean titles from TMDB), not of the
        //    Korean translations AniDB lists for Japanese anime
        if (cfg.RomanizeKorean)
        {
            var korean = titles.Where(t => t.Source.StartsWith("tmdb", StringComparison.Ordinal)).Select(t => t.Title)
                .Append(item.OriginalTitle).Append(item.Name).ToList();
            foreach (var t in korean)
            {
                if (Hangul.Romanize(t) is { } r)
                {
                    titles.Add(new AltTitle(r, "hangul"));
                }
            }
        }

        // drop what Jellyfin searches anyway, and duplicates
        var known = new HashSet<string>(new[] { item.Name, item.OriginalTitle }.Select(Normalizer.Normalize).Where(s => s.Length > 0));
        var seen = new HashSet<string>(known);
        var result = new List<AltTitle>();
        foreach (var t in titles)
        {
            var n = Normalizer.Normalize(t.Title);
            if (n.Length >= 2 && seen.Add(n))
            {
                result.Add(t with { Title = t.Title.Trim() });
            }
        }

        return new TitleEntry { Titles = result, FetchedAt = DateTime.UtcNow, Note = notes.Count > 0 ? string.Join("; ", notes) : null };
    }

    private async Task<string?> LanguageTitleAsync(BaseItem item, string tmdb, string language, CancellationToken ct)
    {
        var country = language switch
        {
            "en" => "US", "de" => "DE", "fr" => "FR", "es" => "ES", "it" => "IT", "ja" => "JP", "ko" => "KR", "zh" => "CN", "pt" => "BR", "nl" => "NL",
            _ => language.ToUpperInvariant()
        };
        try
        {
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [MetadataProvider.Tmdb.ToString()] = tmdb };
            IEnumerable<RemoteSearchResult> results = item switch
            {
                Series => await _providers.GetRemoteSearchResults<Series, SeriesInfo>(
                    new RemoteSearchQuery<SeriesInfo>
                    {
                        SearchInfo = new SeriesInfo { Name = item.Name, Year = item.ProductionYear, ProviderIds = ids, MetadataLanguage = language, MetadataCountryCode = country },
                        SearchProviderName = "TheMovieDb",
                        IncludeDisabledProviders = true
                    },
                    ct).ConfigureAwait(false),
                Movie => await _providers.GetRemoteSearchResults<Movie, MovieInfo>(
                    new RemoteSearchQuery<MovieInfo>
                    {
                        SearchInfo = new MovieInfo { Name = item.Name, Year = item.ProductionYear, ProviderIds = ids, MetadataLanguage = language, MetadataCountryCode = country },
                        SearchProviderName = "TheMovieDb",
                        IncludeDisabledProviders = true
                    },
                    ct).ConfigureAwait(false),
                _ => Enumerable.Empty<RemoteSearchResult>()
            };

            var list = results.ToList();
            return (list.FirstOrDefault(r => r.ProviderIds is not null && r.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var id) && id == tmdb)
                ?? list.FirstOrDefault())?.Name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Alternative Titles: no {Language} title for {Name}", language, item.Name);
            return null;
        }
    }

    /// <summary>TMDB /alternative_titles and /translations, with the user's own key (v3 key or v4 read token).</summary>
    private async Task<List<AltTitle>> TmdbAlternativesAsync(string kind, string tmdb, string key, CancellationToken ct)
    {
        var bearer = key.Length > 40; // v4 read access tokens are long JWTs, v3 keys 32 hex characters
        var url = $"https://api.themoviedb.org/3/{kind}/{Uri.EscapeDataString(tmdb)}?append_to_response=alternative_titles,translations" + (bearer ? string.Empty : "&api_key=" + Uri.EscapeDataString(key));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        var client = _http.CreateClient("AltTitles");
        client.Timeout = TimeSpan.FromSeconds(20);
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var list = new List<AltTitle>();

        // alternative_titles: movies { titles: [...] }, series { results: [...] }
        if (root.TryGetProperty("alternative_titles", out var alt))
        {
            var arr = alt.TryGetProperty("titles", out var t1) ? t1 : alt.TryGetProperty("results", out var t2) ? t2 : default;
            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    if (a.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } s)
                    {
                        var country = a.TryGetProperty("iso_3166_1", out var c) ? c.GetString() : null;
                        var type = a.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                        list.Add(new AltTitle(s, $"tmdb-alt:{country}{(string.IsNullOrEmpty(type) ? string.Empty : "/" + type)}"));
                    }
                }
            }
        }

        // translations: { translations: [ { iso_639_1, data: { title | name } } ] }
        if (root.TryGetProperty("translations", out var tr) && tr.TryGetProperty("translations", out var trs) && trs.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in trs.EnumerateArray())
            {
                if (t.TryGetProperty("data", out var data))
                {
                    var name = data.TryGetProperty("title", out var n1) ? n1.GetString() : data.TryGetProperty("name", out var n2) ? n2.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var lang = t.TryGetProperty("iso_639_1", out var l) ? l.GetString() : null;
                        list.Add(new AltTitle(name!, "tmdb-tr:" + lang));
                    }
                }
            }
        }

        return list;
    }
}
