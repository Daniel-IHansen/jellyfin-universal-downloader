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
/// Service for interacting with Anikoto (anikoto.net), a HiAnime/Zoro-style anime streaming
/// site. anikoto.net itself sits behind anti-bot protection and exposes no documented public
/// API, so this adapter talks to a community-run wrapper API
/// (<see href="https://github.com/Shineii86/AniKotoAPI"/>) that mirrors its catalog and
/// resolves playable stream URLs directly — no embed-page extractor is needed
/// (<see cref="ResolvesDirectStreamUrls"/>). Anikoto offers both English Sub and English Dub
/// natively (no German track exists), so nothing is filtered out here.
/// <br/><br/>
/// This integration targets a schema documented by the community wrapper's README rather than
/// a response captured directly from live traffic (anikoto.net returned HTTP 403 to automated
/// fetches during development). If searches/episodes come back empty, check the logs for the
/// raw JSON shape and adjust <see cref="ParseSearchResults"/>/<see cref="ParseEpisodeList"/>
/// accordingly — the parsing here is written defensively (tries several plausible property
/// names) but has not been verified against a live response.
/// </summary>
public class AnikotoService : StreamingSiteService
{
    private const string SiteBaseUrl = "https://anikoto.net";
    private const string ApiBaseUrl = "https://anikototvapi.vercel.app/api";

    private static readonly IReadOnlyDictionary<string, string> AnikotoLanguageMap =
        new Dictionary<string, string> { ["sub"] = "sub", ["dub"] = "dub" };

    /// <summary>
    /// Initializes a new instance of the <see cref="AnikotoService"/> class.
    /// </summary>
    public AnikotoService(IHttpClientFactory httpClientFactory, ILogger<AnikotoService> logger)
        : base(httpClientFactory.CreateClient("Anikoto"), logger)
    {
    }

    /// <inheritdoc />
    public override string SourceName => "anikoto";

    /// <inheritdoc />
    public override string DisplayName => "Anikoto";

    /// <inheritdoc />
    public override string BaseUrl => SiteBaseUrl;

    /// <inheritdoc />
    public override bool ResolvesDirectStreamUrls => true;

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> LanguageMap => AnikotoLanguageMap;

    /// <inheritdoc />
    public override async Task<List<SearchResult>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        using var doc = await FetchJsonAsync($"{ApiBaseUrl}/search?keyword={Uri.EscapeDataString(keyword)}", cancellationToken).ConfigureAwait(false);
        return ParseSearchResults(doc.RootElement);
    }

    /// <inheritdoc />
    public override async Task<SeriesInfo> GetSeriesInfoAsync(string seriesUrl, CancellationToken cancellationToken = default)
    {
        var slug = ExtractSlug(seriesUrl);
        using var doc = await FetchJsonAsync($"{ApiBaseUrl}/anime/{slug}", cancellationToken).ConfigureAwait(false);
        var root = UnwrapResults(doc.RootElement);

        var title = GetString(root, "title", "name") ?? slug;
        var cover = GetString(root, "poster", "image", "cover") ?? string.Empty;
        var description = GetString(root, "description", "synopsis", "overview") ?? string.Empty;

        var genres = new List<string>();
        if (root.TryGetProperty("genres", out var genresEl) && genresEl.ValueKind == JsonValueKind.Array)
        {
            genres = genresEl.EnumerateArray()
                .Select(g => g.ValueKind == JsonValueKind.String ? g.GetString() : GetString(g, "name"))
                .Where(g => !string.IsNullOrEmpty(g))
                .Select(g => g!)
                .ToList();
        }

        // Anikoto (like most HiAnime-style sites) has no season concept — episodes are a
        // single flat list per title, so we expose one synthetic "season" pointing at the
        // series URL itself.
        var seasons = new List<SeasonRef> { new() { Url = seriesUrl, Number = 1 } };

        return new SeriesInfo
        {
            Title = DecodeHtml(title),
            Url = seriesUrl,
            CoverImageUrl = cover,
            Description = DecodeHtml(description),
            Genres = genres,
            Seasons = seasons,
            HasMovies = false,
        };
    }

    /// <inheritdoc />
    public override async Task<List<EpisodeRef>> GetEpisodesAsync(string seasonUrl, CancellationToken cancellationToken = default)
    {
        var slug = ExtractSlug(seasonUrl);
        using var doc = await FetchJsonAsync($"{ApiBaseUrl}/episodes/{slug}", cancellationToken).ConfigureAwait(false);
        return ParseEpisodeList(doc.RootElement, slug);
    }

    /// <inheritdoc />
    public override async Task<EpisodeDetails> GetEpisodeDetailsAsync(string episodeUrl, CancellationToken cancellationToken = default)
    {
        var (slug, episodeId) = ParseEpisodeUrl(episodeUrl);

        using var episodesDoc = await FetchJsonAsync($"{ApiBaseUrl}/episodes/{slug}", cancellationToken).ConfigureAwait(false);
        var episodeEntry = FindEpisodeEntry(episodesDoc.RootElement, episodeId);

        var providers = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var episodeTitle = string.Empty;

        if (episodeEntry.HasValue)
        {
            episodeTitle = GetString(episodeEntry.Value, "title", "name") ?? string.Empty;

            var serverIds = ExtractServerIds(episodeEntry.Value);
            if (serverIds.Count > 0)
            {
                var idsParam = string.Join(",", serverIds);
                using var serversDoc = await FetchJsonAsync($"{ApiBaseUrl}/servers?ids={Uri.EscapeDataString(idsParam)}", cancellationToken).ConfigureAwait(false);
                var serversRoot = UnwrapResults(serversDoc.RootElement);

                if (serversRoot.ValueKind == JsonValueKind.Array)
                {
                    foreach (var server in serversRoot.EnumerateArray())
                    {
                        var type = GetString(server, "type", "language")?.ToLowerInvariant() ?? "sub";
                        var name = GetString(server, "name", "server") ?? "Server";
                        var linkId = GetString(server, "link_id", "linkId", "id");

                        if (string.IsNullOrEmpty(linkId))
                        {
                            continue;
                        }

                        var langKey = type.Contains("dub", StringComparison.OrdinalIgnoreCase) ? "dub" : "sub";
                        if (!providers.TryGetValue(langKey, out var bucket))
                        {
                            bucket = new Dictionary<string, string>();
                            providers[langKey] = bucket;
                        }

                        // Store our own resolvable API URL as the "redirect" — ResolveRedirectAsync
                        // turns this into the final playable stream URL.
                        bucket[name] = $"{ApiBaseUrl}/stream?id={Uri.EscapeDataString(linkId)}";
                    }
                }
            }
        }

        return new EpisodeDetails
        {
            Url = episodeUrl,
            TitleEn = DecodeHtml(episodeTitle),
            ProvidersByLanguage = providers,
        };
    }

    /// <inheritdoc />
    public override async Task<string> ResolveRedirectAsync(string redirectUrl, CancellationToken cancellationToken = default)
    {
        using var doc = await FetchJsonAsync(redirectUrl, cancellationToken).ConfigureAwait(false);
        var root = UnwrapResults(doc.RootElement);
        return GetString(root, "url", "link", "file", "source") ?? redirectUrl;
    }

    /// <inheritdoc />
    public override async Task<List<BrowseItem>> GetPopularAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await FetchJsonAsync($"{ApiBaseUrl}/top-ten?period=week", cancellationToken).ConfigureAwait(false);
        return ParseBrowseItems(doc.RootElement);
    }

    /// <inheritdoc />
    public override async Task<List<BrowseItem>> GetNewReleasesAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await FetchJsonAsync(ApiBaseUrl, cancellationToken).ConfigureAwait(false);
        var root = UnwrapResults(doc.RootElement);

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var propName in new[] { "recentEpisodes", "latestEpisodes", "recent", "latest" })
            {
                if (root.TryGetProperty(propName, out var section))
                {
                    return ParseBrowseItems(section);
                }
            }
        }

        return new List<BrowseItem>();
    }

    // ── Parsing helpers ──────────────────────────────────────────

    private List<SearchResult> ParseSearchResults(JsonElement root)
    {
        var results = new List<SearchResult>();
        var arr = UnwrapResults(root);

        if (arr.ValueKind == JsonValueKind.Object)
        {
            foreach (var propName in new[] { "animes", "results", "data" })
            {
                if (arr.TryGetProperty(propName, out var nested) && nested.ValueKind == JsonValueKind.Array)
                {
                    arr = nested;
                    break;
                }
            }
        }

        if (arr.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in arr.EnumerateArray())
        {
            var title = GetString(item, "title", "name");
            var slug = GetString(item, "id", "slug");
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(slug))
            {
                continue;
            }

            results.Add(new SearchResult
            {
                Title = DecodeHtml(title),
                Url = $"{SiteBaseUrl}/{slug}",
                Description = DecodeHtml(GetString(item, "description", "synopsis") ?? string.Empty),
                Source = SourceName,
            });
        }

        return results;
    }

    private List<EpisodeRef> ParseEpisodeList(JsonElement root, string slug)
    {
        var episodes = new List<EpisodeRef>();
        var arr = UnwrapResults(root);

        if (arr.ValueKind == JsonValueKind.Object)
        {
            foreach (var propName in new[] { "episodes", "data" })
            {
                if (arr.TryGetProperty(propName, out var nested) && nested.ValueKind == JsonValueKind.Array)
                {
                    arr = nested;
                    break;
                }
            }
        }

        if (arr.ValueKind != JsonValueKind.Array)
        {
            return episodes;
        }

        foreach (var item in arr.EnumerateArray())
        {
            var idStr = GetString(item, "id", "episodeId", "episode_id");
            var numberStr = GetString(item, "number", "episode");
            if (string.IsNullOrEmpty(idStr))
            {
                continue;
            }

            int.TryParse(numberStr, out var number);

            episodes.Add(new EpisodeRef
            {
                Url = $"{SiteBaseUrl}/{slug}?ep={Uri.EscapeDataString(idStr)}",
                Number = number,
                IsMovie = false,
            });
        }

        return episodes.OrderBy(e => e.Number).ToList();
    }

    private JsonElement? FindEpisodeEntry(JsonElement root, string episodeId)
    {
        var arr = UnwrapResults(root);

        if (arr.ValueKind == JsonValueKind.Object)
        {
            foreach (var propName in new[] { "episodes", "data" })
            {
                if (arr.TryGetProperty(propName, out var nested) && nested.ValueKind == JsonValueKind.Array)
                {
                    arr = nested;
                    break;
                }
            }
        }

        if (arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in arr.EnumerateArray())
        {
            var idStr = GetString(item, "id", "episodeId", "episode_id");
            if (string.Equals(idStr, episodeId, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    private static List<string> ExtractServerIds(JsonElement episodeEntry)
    {
        foreach (var propName in new[] { "server_ids", "serverIds", "servers" })
        {
            if (episodeEntry.TryGetProperty(propName, out var el) && el.ValueKind == JsonValueKind.Array)
            {
                return el.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(s => s!)
                    .ToList();
            }
        }

        return new List<string>();
    }

    private List<BrowseItem> ParseBrowseItems(JsonElement root)
    {
        var items = new List<BrowseItem>();
        var arr = UnwrapResults(root);

        if (arr.ValueKind == JsonValueKind.Object)
        {
            foreach (var propName in new[] { "animes", "results", "data" })
            {
                if (arr.TryGetProperty(propName, out var nested) && nested.ValueKind == JsonValueKind.Array)
                {
                    arr = nested;
                    break;
                }
            }
        }

        if (arr.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var item in arr.EnumerateArray())
        {
            var title = GetString(item, "title", "name");
            var slug = GetString(item, "id", "slug");
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(slug))
            {
                continue;
            }

            items.Add(new BrowseItem
            {
                Title = DecodeHtml(title),
                Url = $"{SiteBaseUrl}/{slug}",
                CoverImageUrl = GetString(item, "poster", "image", "cover") ?? string.Empty,
                Genre = string.Empty,
                Source = SourceName,
            });
        }

        return items;
    }

    /// <summary>
    /// Unwraps the common <c>{ "success": true, "results": {...} }</c> envelope this API
    /// family typically uses, falling back to the root element if not present.
    /// </summary>
    private static JsonElement UnwrapResults(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var results))
        {
            return results;
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
        {
            return data;
        }

        return root;
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

    private static (string Slug, string EpisodeId) ParseEpisodeUrl(string episodeUrl)
    {
        if (!Uri.TryCreate(episodeUrl, UriKind.Absolute, out var uri))
        {
            return (episodeUrl, string.Empty);
        }

        var slug = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault() ?? string.Empty;
        var episodeId = string.Empty;

        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], "ep", StringComparison.OrdinalIgnoreCase))
            {
                episodeId = Uri.UnescapeDataString(parts[1]);
                break;
            }
        }

        return (slug, episodeId);
    }
}
