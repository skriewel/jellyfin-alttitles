using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.AltTitles.Titles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AltTitles.Api;

[ApiController]
[Route("AltTitles")]
public class AltTitlesController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AltTitleStore _store;
    private readonly TitleCollector _collector;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;

    public AltTitlesController(AltTitleStore store, TitleCollector collector, ILibraryManager library, IUserManager users)
    {
        _store = store;
        _collector = collector;
        _library = library;
        _users = users;
    }

    // ------------------------------------------------------------------ client assets (anonymous: loaded before login)

    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        var clientConfig = new { Enabled = plugin.Configuration.ShowOnDetails, Version = plugin.CacheKey };
        var script = "window.AltTitlesConfig = " + JsonSerializer.Serialize(clientConfig, JsonOptions) + ";\n"
            + ReadResource("Web.alttitles.js");
        Response.Headers.CacheControl = "public, max-age=86400";
        return Content(script, "application/javascript; charset=utf-8");
    }

    [HttpGet("ClientStyle")]
    [AllowAnonymous]
    [Produces("text/css")]
    public ActionResult GetClientStyle()
    {
        Response.Headers.CacheControl = "public, max-age=86400";
        return Content(ReadResource("Web.alttitles.css"), "text/css; charset=utf-8");
    }

    // ------------------------------------------------------------------ details page

    /// <summary>The alternative titles of a movie or series (seasons and episodes: their series).</summary>
    [HttpGet("Item/{itemId}")]
    [Authorize]
    public ActionResult GetItem([FromRoute] Guid itemId)
    {
        var item = _library.GetItemById(itemId);
        var user = CurrentUser();
        if (item is null || (user is not null && !item.IsVisible(user)))
        {
            return NotFound();
        }

        BaseItem? target = item switch
        {
            Episode e => e.Series,
            Season s => s.Series,
            Movie or Series => item,
            _ => null
        };
        var entry = target is null ? null : _store.Get(target.Id);
        return Json(new
        {
            titles = (entry?.Titles ?? new List<AltTitle>())
                .Where(t => !t.Source.StartsWith("anidb-season:", StringComparison.Ordinal))
                .Select(t => new { title = t.Title, source = t.Source })
        });
    }

    // ------------------------------------------------------------------ admin

    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetStatus()
    {
        return Json(new { items = _store.Count, titles = _store.TitleCount });
    }

    /// <summary>What the search finds for a term, with the matching titles (for checking).</summary>
    [HttpGet("Search")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult Search([FromQuery] string q)
    {
        var hits = _store.Search(q ?? string.Empty, 50);
        var nq = Normalizer.Normalize(q);
        return Json(hits.Select(h =>
        {
            var item = _library.GetItemById(h.ItemId);
            var matched = _store.Get(h.ItemId)?.Titles
                .Where(t => Normalizer.Normalize(t.Title).Contains(nq, StringComparison.Ordinal)
                    || Normalizer.Compact(Normalizer.Normalize(t.Title)).Contains(Normalizer.Compact(nq), StringComparison.Ordinal))
                .Select(t => t.Title + " (" + t.Source + ")");
            return new { id = h.ItemId, name = item?.Name, score = h.Score, matched };
        }));
    }

    /// <summary>Collects the titles of one item now and returns them.</summary>
    [HttpPost("Refresh/{itemId}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Refresh([FromRoute] Guid itemId, CancellationToken ct)
    {
        var item = _library.GetItemById(itemId);
        if (item is not (Movie or Series))
        {
            return NotFound();
        }

        var entry = await _collector.CollectAsync(item, Plugin.Instance!.Configuration, ct).ConfigureAwait(false);
        _store.Set(item.Id, entry);
        return Json(new { name = item.Name, originalTitle = item.OriginalTitle, entry.Note, titles = entry.Titles });
    }

    [HttpPost("Clear")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult Clear()
    {
        _store.Clear();
        return NoContent();
    }

    private Jellyfin.Database.Implementations.Entities.User? CurrentUser()
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var userId) ? _users.GetUserById(userId) : null;
    }

    private ContentResult Json(object value)
    {
        Response.Headers.CacheControl = "no-store";
        return Content(JsonSerializer.Serialize(value, JsonOptions), "application/json; charset=utf-8");
    }

    private static string ReadResource(string relativeName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = $"{typeof(Plugin).Namespace}.{relativeName}";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
