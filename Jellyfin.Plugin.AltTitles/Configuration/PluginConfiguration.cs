using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AltTitles.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether the search also finds items by their alternative titles.</summary>
    public bool EnableSearch { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the details page lists the alternative titles ("Also known as").</summary>
    public bool ShowOnDetails { get; set; } = true;

    /// <summary>Gets or sets the languages whose titles are looked up through Jellyfin's TMDB provider ("en, de").</summary>
    public string Languages { get; set; } = "en, de";

    /// <summary>Gets or sets a value indicating whether anime get their AniDB titles (romaji, official, synonyms).</summary>
    public bool UseAniDb { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether Korean titles get a romanized form (Revised Romanization).</summary>
    public bool RomanizeKorean { get; set; } = true;

    /// <summary>
    /// Gets or sets an own TMDB API key (v3 key or v4 read access token). Optional: with it, every
    /// alternative title TMDB knows is added, including romanized titles of non-anime films and dramas.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets after how many days the titles of an item are collected again.</summary>
    public int RefreshDays { get; set; } = 30;

    /// <summary>Gets or sets a value that changes on every save (part of the client cache key).</summary>
    public long Revision { get; set; }
}
