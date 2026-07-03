using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniBridge.Services;

/// <summary>
/// Service for interacting with Anime Nexus (anime.nexus). English Sub and Dub only.
/// <br/><br/>
/// <b>Experimental / unverified.</b> anime.nexus is a modern SPA whose backend API is not
/// publicly documented, and its <c>robots.txt</c> explicitly disallows automated fetch tools
/// (including the one used to research this feature), so this adapter could not be built or
/// tested against real responses. The endpoints below follow common conventions for this
/// class of site (Next.js anime catalogs with a REST API under <c>/api</c>) but are best-effort
/// guesses. This adapter is disabled by default (<c>Sites</c> config entry "animenexus",
/// <c>Enabled = false</c>) — enable it only if you're willing to debug it, and please check the
/// plugin logs for the actual HTTP responses if searches/episodes come back empty; the endpoint
/// paths and JSON property names below are the first thing to fix.
/// </summary>
public class AnimeNexusService : StreamingSiteService
{
    private const string SiteBaseUrl = "https://anime.nexus";
    private const string ApiBaseUrl = "https://anime.nexus/api";

    private static readonly IReadOnlyDictionary<string, string> AnimeNexusLanguageMap =
        new Dictionary<string, string> { ["sub"] = "sub", ["dub"] = "dub" };

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeNexusService"/> class.
    /// </summary>
    public AnimeNexusService(IHttpClientFactory httpClientFactory, ILogger<AnimeNexusService> logger)
        : base(httpClientFactory.CreateClient("AnimeNexus"), logger)
    {
    }

    /// <inheritdoc />
    public override string SourceName => "animenexus";

    /// <inheritdoc />
    public override string DisplayName => "Anime Nexus";

    /// <inheritdoc />
    public override string BaseUrl => SiteBaseUrl;

    /// <inheritdoc />
    public override bool ResolvesDirectStreamUrls => true;

    /// <inheritdoc />
    public override bool IsExperimental => true;

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> LanguageMap => AnimeNexusLanguageMap;

    /// <inheritdoc />
    public override async Task<List<SearchResult>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        using var doc = await FetchJsonAsync($"{ApiBaseUrl}/search?q={Uri.EscapeDataString(keyword)}", cancellationToken).ConfigureAwait(false);
        var arr = UnwrapArray(doc.RootElement, "results", "data", "items");

        var results = new List<SearchResult>();
        foreach (var item in arr)
        {
            var title = GetString(item, "title", "name");
            var slug = GetString(item, "slug", "id");
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(slug))
            {
                continue;
            }

            results.Add(new SearchResult
            {
                Title = DecodeHtml(title),
                Url = $"{SiteBaseUrl}/anime/{slug}",
                Description = DecodeHtml(GetString(item, "description", "synopsis") ?? string.Empty),
                Source = SourceName,
            });
        }

        return results;
    }

    /// <inheritdoc />
    public override async Task<SeriesInfo> GetSeriesInfoAsync(string seriesUrl, CancellationToken cancellationToken = default)
    {
        var slug = ExtractSlug(seriesUrl);
        using var doc = await FetchJsonAsync($"{ApiBaseUrl}/anime/{slug}", cancellationToken).ConfigureAwait(false);
        var root = Unwrap(doc.RootElement);

        var genres = new List<string>();
        if (root.TryGetProperty("genres", out var genresEl) && genresEl.ValueKind == JsonValueKind.Array)
        {
            genres = genresEl.EnumerateArray()
                .Select(g => g.ValueKind == JsonValueKind.String ? g.GetString() : GetString(g, "name"))
                .Where(g => !string.IsNullOrEmpty(g))
                .Select(g => g!)
                .ToList();
        }

        return new SeriesInfo
        {
            Title = DecodeHtml(GetString(root, "title", "name") ?? slug),
            Url = seriesUrl,
            CoverImageUrl = GetString(root, "poster", "cover", "image") ?? string.Empty,
            Description = DecodeHtml(GetString(root, "description", "synopsis") ?? string.Empty),
            Genres = genres,
            Seasons = new List<SeasonRef> { new() { Url = seriesUrl, Number = 1 } },
            HasMovies = false,
        };
    }

    /// <inheritdoc />
    public override async Task<List<EpisodeRef>> GetEpisodesAsync(string seasonUrl, CancellationToken cancellationToken = default)
    {
        var slug = ExtractSlug(seasonUrl);
        using var doc = await FetchJsonAsync($"{ApiBaseUrl}/anime/{slug}/episodes", cancellationToken).ConfigureAwait(false);
        var arr = UnwrapArray(doc.RootElement, "episodes", "results", "data");

        var episodes = new List<EpisodeRef>();
        foreach (var item in arr)
        {
            var idStr = GetString(item, "id", "episodeId");
            var numberStr = GetString(item, "number", "episode");
            if (string.IsNullOrEmpty(idStr))
            {
                continue;
            }

            int.TryParse(numberStr, out var number);
            episodes.Add(new EpisodeRef
            {
                Url = $"{SiteBaseUrl}/anime/{slug}?ep={Uri.EscapeDataString(idStr)}",
                Number = number,
                IsMovie = false,
            });
        }

        return episodes.OrderBy(e => e.Number).ToList();
    }

    /// <inheritdoc />
    public override async Task<EpisodeDetails> GetEpisodeDetailsAsync(string episodeUrl, CancellationToken cancellationToken = default)
    {
        var episodeId = ExtractEpisodeId(episodeUrl);
        using var doc = await FetchJsonAsync($"{ApiBaseUrl}/episode/{Uri.EscapeDataString(episodeId)}/servers", cancellationToken).ConfigureAwait(false);
        var arr = UnwrapArray(doc.RootElement, "servers", "results", "data");

        var providers = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in arr)
        {
            var type = GetString(server, "type", "language")?.ToLowerInvariant() ?? "sub";
            var name = GetString(server, "name", "server") ?? "Server";
            var streamUrl = GetString(server, "url", "link", "streamUrl");

            if (string.IsNullOrEmpty(streamUrl))
            {
                continue;
            }

            var langKey = type.Contains("dub", StringComparison.OrdinalIgnoreCase) ? "dub" : "sub";
            if (!providers.TryGetValue(langKey, out var bucket))
            {
                bucket = new Dictionary<string, string>();
                providers[langKey] = bucket;
            }

            bucket[name] = streamUrl;
        }

        return new EpisodeDetails
        {
            Url = episodeUrl,
            ProvidersByLanguage = providers,
        };
    }

    /// <inheritdoc />
    public override Task<string> ResolveRedirectAsync(string redirectUrl, CancellationToken cancellationToken = default)
    {
        // GetEpisodeDetailsAsync already resolves servers to final playable URLs above.
        return Task.FromResult(redirectUrl);
    }

    /// <inheritdoc />
    public override async Task<List<BrowseItem>> GetPopularAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await FetchJsonAsync($"{ApiBaseUrl}/discover?sort=popular", cancellationToken).ConfigureAwait(false);
        return ParseBrowseItems(UnwrapArray(doc.RootElement, "results", "data", "items"));
    }

    /// <inheritdoc />
    public override async Task<List<BrowseItem>> GetNewReleasesAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await FetchJsonAsync($"{ApiBaseUrl}/latest", cancellationToken).ConfigureAwait(false);
        return ParseBrowseItems(UnwrapArray(doc.RootElement, "results", "data", "items"));
    }

    private List<BrowseItem> ParseBrowseItems(IEnumerable<JsonElement> items)
    {
        var result = new List<BrowseItem>();
        foreach (var item in items)
        {
            var title = GetString(item, "title", "name");
            var slug = GetString(item, "slug", "id");
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(slug))
            {
                continue;
            }

            result.Add(new BrowseItem
            {
                Title = DecodeHtml(title),
                Url = $"{SiteBaseUrl}/anime/{slug}",
                CoverImageUrl = GetString(item, "poster", "cover", "image") ?? string.Empty,
                Genre = string.Empty,
                Source = SourceName,
            });
        }

        return result;
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
        {
            return data;
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var results))
        {
            return results;
        }

        return root;
    }

    private static IEnumerable<JsonElement> UnwrapArray(JsonElement root, params string[] propertyNames)
    {
        var el = Unwrap(root);

        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in propertyNames)
            {
                if (el.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Array)
                {
                    return nested.EnumerateArray().ToList();
                }
            }
        }

        return el.ValueKind == JsonValueKind.Array ? el.EnumerateArray().ToList() : Enumerable.Empty<JsonElement>();
    }

    private static string? GetString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }

                if (value.ValueKind == JsonValueKind.Number)
                {
                    return value.ToString();
                }
            }
        }

        return null;
    }

    private static string ExtractSlug(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath.Trim('/').Split('/').LastOrDefault() ?? string.Empty;
        }

        return url.Trim('/').Split('/').LastOrDefault() ?? url;
    }

    private static string ExtractEpisodeId(string episodeUrl)
    {
        if (!Uri.TryCreate(episodeUrl, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], "ep", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return string.Empty;
    }
}
