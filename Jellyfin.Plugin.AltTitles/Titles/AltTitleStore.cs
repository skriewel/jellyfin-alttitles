using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AltTitles.Titles;

/// <summary>One alternative title and where it came from ("tmdb:en", "anidb:x-jat", "hangul" …).</summary>
public sealed record AltTitle(string Title, string Source);

/// <summary>The alternative titles of one movie or series.</summary>
public sealed class TitleEntry
{
    public List<AltTitle> Titles { get; set; } = new();

    public DateTime FetchedAt { get; set; }

    /// <summary>Gets or sets a short note on what the last collection found ("anidb 17305", "no tmdb id" …).</summary>
    public string? Note { get; set; }
}

/// <summary>
/// The collected titles (<c>titles.json</c> in the plugin's data folder) and an in-memory search index.
/// The index is a flat list of normalised titles: a linear scan over tens of thousands of short
/// strings takes a few milliseconds, which is fast enough for a search box.
/// </summary>
public sealed class AltTitleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ILogger<AltTitleStore> _logger;
    private readonly object _lock = new();
    private Dictionary<Guid, TitleEntry> _entries = new();
    private IndexRow[] _index = Array.Empty<IndexRow>();
    private bool _loaded;
    private Timer? _saveTimer;

    public AltTitleStore(ILogger<AltTitleStore> logger)
    {
        _logger = logger;
    }

    private static string FilePath
    {
        get
        {
            var dir = Plugin.Instance?.DataFolderPath ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "titles.json");
        }
    }

    public int Count
    {
        get
        {
            EnsureLoaded();
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public int TitleCount
    {
        get
        {
            EnsureLoaded();
            return _index.Length;
        }
    }

    public TitleEntry? Get(Guid itemId)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _entries.TryGetValue(itemId, out var e) ? e : null;
        }
    }

    public void Set(Guid itemId, TitleEntry entry)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _entries[itemId] = entry;
            RebuildIndexLocked();
        }

        ScheduleSave();
    }

    /// <summary>Forgets items that are no longer in the library.</summary>
    public int Prune(ISet<Guid> existing)
    {
        EnsureLoaded();
        int removed;
        lock (_lock)
        {
            var gone = _entries.Keys.Where(k => !existing.Contains(k)).ToList();
            foreach (var id in gone)
            {
                _entries.Remove(id);
            }

            removed = gone.Count;
            if (removed > 0)
            {
                RebuildIndexLocked();
            }
        }

        if (removed > 0)
        {
            ScheduleSave();
        }

        return removed;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries = new Dictionary<Guid, TitleEntry>();
            _index = Array.Empty<IndexRow>();
            _loaded = true;
        }

        Flush();
    }

    /// <summary>Items whose alternative titles match the search term, with a score (exact 95 … contains 45).</summary>
    public List<(Guid ItemId, float Score)> Search(string term, int limit)
    {
        EnsureLoaded();
        var q = Normalizer.Normalize(term);
        if (q.Length == 0)
        {
            return new List<(Guid, float)>();
        }

        var qCompact = Normalizer.Compact(q);
        var best = new Dictionary<Guid, float>();
        foreach (var row in _index)
        {
            float score;
            if (row.Text == q || row.Compact == qCompact)
            {
                score = 95f;
            }
            else if (row.Text.StartsWith(q, StringComparison.Ordinal))
            {
                score = 75f;
            }
            else if (row.Text.Contains(" " + q, StringComparison.Ordinal))
            {
                score = 70f;
            }
            else if (row.Text.Contains(q, StringComparison.Ordinal))
            {
                score = 45f;
            }
            else if (qCompact.Length >= 4 && row.Compact.Contains(qCompact, StringComparison.Ordinal))
            {
                score = 40f; // "kiminonawa", "spiderman"
            }
            else
            {
                continue;
            }

            if (!best.TryGetValue(row.ItemId, out var s) || s < score)
            {
                best[row.ItemId] = score;
            }
        }

        return best.OrderByDescending(kv => kv.Value).Take(limit).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public void Flush()
    {
        _saveTimer?.Dispose();
        _saveTimer = null;
        Dictionary<Guid, TitleEntry> copy;
        lock (_lock)
        {
            copy = new Dictionary<Guid, TitleEntry>(_entries);
        }

        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(copy, JsonOptions));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Alternative Titles: could not save titles.json");
        }
    }

    private void ScheduleSave()
    {
        lock (_lock)
        {
            _saveTimer?.Dispose();
            _saveTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_lock)
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                if (File.Exists(FilePath))
                {
                    _entries = JsonSerializer.Deserialize<Dictionary<Guid, TitleEntry>>(File.ReadAllText(FilePath), JsonOptions) ?? new();
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(ex, "Alternative Titles: titles.json unreadable, starting empty");
                _entries = new Dictionary<Guid, TitleEntry>();
            }

            RebuildIndexLocked();
            _loaded = true;
        }
    }

    private void RebuildIndexLocked()
    {
        var rows = new List<IndexRow>();
        foreach (var (id, entry) in _entries)
        {
            foreach (var t in entry.Titles)
            {
                var n = Normalizer.Normalize(t.Title);
                if (n.Length > 0)
                {
                    rows.Add(new IndexRow(id, n, Normalizer.Compact(n)));
                }
            }
        }

        _index = rows.ToArray();
    }

    private readonly record struct IndexRow(Guid ItemId, string Text, string Compact);
}
