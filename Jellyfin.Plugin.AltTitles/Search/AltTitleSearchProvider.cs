using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AltTitles.Titles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Plugin.AltTitles.Search;

/// <summary>
/// Adds items found by their alternative titles to Jellyfin's search. As an internal provider its results
/// are merged with the database search (best score per item), so the normal results stay and items
/// found only by another title are added. Works for /Items?searchTerm= and /Search/Hints, i.e. in the web
/// client and in the apps.
/// <para>
/// Internal providers must respect user access themselves: an item is only returned if the user may see
/// it (library access, parental rating, blocked tags) and it matches the requested types and parent.
/// </para>
/// </summary>
public sealed class AltTitleSearchProvider : IInternalSearchProvider
{
    private readonly AltTitleStore _store;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;

    public AltTitleSearchProvider(AltTitleStore store, ILibraryManager library, IUserManager users)
    {
        _store = store;
        _library = library;
        _users = users;
    }

    public string Name => "Alternative Titles";

    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    // the database provider has 100; order does not matter for internal providers, their results are merged
    public int Priority => 90;

    public bool CanSearch(SearchProviderQuery query)
    {
        if (Plugin.Instance?.Configuration.EnableSearch != true || string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            return false;
        }

        // only movies and series carry alternative titles
        return query.IncludeItemTypes.Length == 0
            || query.IncludeItemTypes.Contains(BaseItemKind.Movie)
            || query.IncludeItemTypes.Contains(BaseItemKind.Series);
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchProviderQuery query, CancellationToken cancellationToken)
    {
        var limit = query.Limit ?? 100;
        var hits = _store.Search(query.SearchTerm, limit * 3);
        if (hits.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
        }

        var user = query.UserId is { } uid && uid != Guid.Empty ? _users.GetUserById(uid) : null;
        var results = new List<SearchResult>();
        foreach (var (id, score) in hits)
        {
            var item = _library.GetItemById(id);
            if (item is null || item.IsVirtualItem)
            {
                continue;
            }

            var kind = item.GetBaseItemKind();
            if (query.IncludeItemTypes.Length > 0 && !query.IncludeItemTypes.Contains(kind))
            {
                continue;
            }

            if (query.ExcludeItemTypes.Contains(kind))
            {
                continue;
            }

            if (query.MediaTypes.Length > 0 && !query.MediaTypes.Contains(item.MediaType))
            {
                continue;
            }

            if (query.ParentId is { } pid && pid != Guid.Empty && !item.GetParents().Any(p => p.Id == pid))
            {
                continue;
            }

            if (user is not null && !item.IsVisibleStandalone(user))
            {
                continue;
            }

            results.Add(new SearchResult(id, score));
            if (results.Count >= limit)
            {
                break;
            }
        }

        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }
}
