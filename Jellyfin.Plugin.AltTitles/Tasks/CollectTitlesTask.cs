using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AltTitles.Titles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AltTitles.Tasks;

/// <summary>
/// Collects the alternative titles of all movies and series: new ones first, then those older than the
/// refresh interval. Runs daily at night; the first run goes through the whole library.
/// </summary>
public sealed class CollectTitlesTask : IScheduledTask
{
    private readonly ILibraryManager _library;
    private readonly AltTitleStore _store;
    private readonly TitleCollector _collector;
    private readonly ILogger<CollectTitlesTask> _logger;

    public CollectTitlesTask(ILibraryManager library, AltTitleStore store, TitleCollector collector, ILogger<CollectTitlesTask> logger)
    {
        _library = library;
        _store = store;
        _collector = collector;
        _logger = logger;
    }

    public string Name => "Collect alternative titles";

    public string Key => "AltTitlesCollect";

    public string Description => "Looks up the other titles of movies and series (other languages, AniDB, romanization) so the search finds them.";

    public string Category => "Alternative Titles";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3.5).Ticks }
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var items = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true,
                IsVirtualItem = false
            })
            .ToList();

        var removed = _store.Prune(items.Select(i => i.Id).ToHashSet());
        var maxAge = TimeSpan.FromDays(Math.Max(1, cfg.RefreshDays));
        var todo = items
            .Select(i => (Item: i, Entry: _store.Get(i.Id)))
            .Where(x => x.Entry is null || DateTime.UtcNow - x.Entry.FetchedAt > maxAge)
            .OrderBy(x => x.Entry is null ? 0 : 1)
            .ThenBy(x => x.Entry?.FetchedAt ?? DateTime.MinValue)
            .Select(x => x.Item)
            .ToList();

        var withTitles = 0;
        for (var i = 0; i < todo.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = await _collector.CollectAsync(todo[i], cfg, cancellationToken).ConfigureAwait(false);
            _store.Set(todo[i].Id, entry);
            if (entry.Titles.Count > 0)
            {
                withTitles++;
            }

            progress.Report(100.0 * (i + 1) / Math.Max(1, todo.Count));
        }

        _store.Flush();
        _logger.LogInformation(
            "Alternative Titles: {Done} of {Total} titles looked up ({WithTitles} with alternative titles), {Removed} removed, {Count} in the index",
            todo.Count,
            items.Count,
            withTitles,
            removed,
            _store.TitleCount);
        progress.Report(100);
    }
}
