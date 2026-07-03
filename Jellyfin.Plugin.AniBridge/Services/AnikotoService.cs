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
/// (<see href="https://github.com/Shineii86/AniKotoAPI"/>, deployed at
/// <c>anikototvapi.vercel.app</c>) that mirrors its catalog. Anikoto offers both English Sub and
/// English Dub natively (no German track exists), so nothing is filtered out here.
/// <br/><br/>
/// Endpoint shapes below were verified against live responses from the wrapper API. There is no
/// "anime info" endpoint in this particular deployment (an earlier version of this file called a
/// <c>/api/anime/:slug</c> path that belongs to a *different*, unrelated community project with a
/// different deployment — that's what caused "Failed to load series"). Series metadata (title,
/// poster, genres) is instead captured at search/browse time and carried in the series URL's
/// query string, since there's nowhere else to fetch it from afterwards.
/// <br/><br/>
/// The wrapper's <c>/api/stream</c> endpoint resolves a server's <c>link_id</c> to a third-party
/// embed page (verified: <c>megaplay.buzz/stream/s-.../.../sub|dub</c>), not a direct video file —
/// so <see cref="ResolvesDirectStreamUrls"/> is <c>false</c> and a dedicated
/// <see cref="Extractors.MegaplayExtractor"/> decodes the actual stream URL from that embed page.
/// Other server names returned by <c>/api/servers</c> (VidPlay, Vidstream, VidCloud) resolve
/// through different third-party hosts this plugin has no verified extractor for, so only the
/// "HD-*" server (megaplay-backed) is currently surfaced as a download option.
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
    public override bool ResolvesDirectStreamUrls => false;

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> LanguageMap => AnikotoLanguageMap;

    /// <inheritdoc />
    public override async Task<List<SearchResult>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        using var doc = await FetchJsonAsync($"{ApiBaseUrl}/search?keyword={Uri.EscapeDataString(keyword)}", cancellationToken).ConfigureAwait(false);
        var results = new List<SearchResult>();

        if (!doc.RootElement.TryGetProperty("results", out var resultsEl) ||
            !resultsEl.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in data.EnumerateArray())
        {
            var title = GetString(item, "title");
            var rawSlug = GetString(item, "slug");
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(rawSlug))
            {
                continue;
            }

            var slug = NormalizeSlug(rawSlug);
            var poster = GetString(item, "poster") ?? string.Empty;
            var genres = GetStringArray(item, "genres");

            results.Add(new SearchResult
            {
                Title = DecodeHtml(title),
                Url = BuildSeriesUrl(slug, title, poster, genres),
                Description = string.Empty,
                Source = SourceName,
            });
        }

        return results;
    }

    /// <inheritdoc />
    public override Task<SeriesInfo> GetSeriesInfoAsync(string seriesUrl, CancellationToken cancellationToken = default)
    {
        // No "anime info" endpoint exists on this API deployment — everything we can show was
        // already captured in the URL when the SearchResult/BrowseItem was built.
        var slug = ExtractSlug(seriesUrl);
        var query = ParseQuery(seriesUrl);

        query.TryGetValue("t", out var title);
        query.TryGetValue("p", out var poster);
        query.TryGetValue("g", out var genresRaw);

        var genres = string.IsNullOrEmpty(genresRaw)
            ? new List<string>()
            : genresRaw.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();

        var seasons = new List<SeasonRef> { new() { Url = seriesUrl, Number = 1 } };

        return Task.FromResult(new SeriesInfo
        {
            Title = DecodeHtml(string.IsNullOrEmpty(title) ? slug : title),
            Url = seriesUrl,
            CoverImageUrl = poster ?? string.Empty,
            Description = string.Empty,
            Genres = genres,
            Seasons = seasons,
            HasMovies = false,
        });
    }

    /// <inheritdoc />
    public override async Task<List<EpisodeRef>> GetEpisodesAsync(string seasonUrl, CancellationToken cancellationToken = default)
    {
        var slug = ExtractSlug(seasonUrl);
        using var doc = await FetchJsonAsync($"{ApiBaseUrl}/episodes/{slug}", cancellationToken).ConfigureAwait(false);

        var episodes = new List<EpisodeRef>();
        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            !results.TryGetProperty("episodes", out var episodesEl) ||
            episodesEl.ValueKind != JsonValueKind.Array)
        {
            return episodes;
        }

        foreach (var item in episodesEl.EnumerateArray())
        {
            var id = GetString(item, "id");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            int.TryParse(GetString(item, "episode_no"), out var number);

            // "ep" carries the opaque backend episode id (needed to look up servers); "n" carries
            // the human episode number so PathHelper can build a correct SxxExx filename and
            // duplicate-detection key (Anikoto has no season concept, so PathHelper treats every
            // "?n=" URL as Season 1 — see FlatEpisodeNumberFromUrl).
            episodes.Add(new EpisodeRef
            {
                Url = $"{SiteBaseUrl}/{slug}?ep={Uri.EscapeDataString(id)}&n={number}",
                Number = number,
                IsMovie = false,
            });
        }

        return episodes.OrderBy(e => e.Number).ToList();
    }

    /// <inheritdoc />
    public override async Task<EpisodeDetails> GetEpisodeDetailsAsync(string episodeUrl, CancellationToken cancellationToken = default)
    {
        var slug = ExtractSlug(episodeUrl);
        var query = ParseQuery(episodeUrl);
        query.TryGetValue("ep", out var episodeId);

        var providers = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(episodeId))
        {
            return new EpisodeDetails { Url = episodeUrl, ProvidersByLanguage = providers };
        }

        using var episodesDoc = await FetchJsonAsync($"{ApiBaseUrl}/episodes/{slug}", cancellationToken).ConfigureAwait(false);

        if (!episodesDoc.RootElement.TryGetProperty("results", out var results) ||
            !results.TryGetProperty("episodes", out var episodesEl) ||
            episodesEl.ValueKind != JsonValueKind.Array)
        {
            return new EpisodeDetails { Url = episodeUrl, ProvidersByLanguage = providers };
        }

        string? serverIds = null;
        string? episodeTitle = null;
        string? episodeNo = null;
        foreach (var item in episodesEl.EnumerateArray())
        {
            if (string.Equals(GetString(item, "id"), episodeId, StringComparison.OrdinalIgnoreCase))
            {
                serverIds = GetString(item, "server_ids");
                episodeTitle = GetString(item, "title");
                episodeNo = GetString(item, "episode_no");
                break;
            }
        }

        // Anikoto rarely populates a per-episode title, so fall back to "Episode N" rather than
        // leaving it blank in the UI.
        var displayTitle = !string.IsNullOrWhiteSpace(episodeTitle)
            ? episodeTitle
            : (episodeNo != null ? $"Episode {episodeNo}" : null);

        if (string.IsNullOrEmpty(serverIds))
        {
            return new EpisodeDetails { Url = episodeUrl, TitleEn = displayTitle, ProvidersByLanguage = providers };
        }

        using var serversDoc = await FetchJsonAsync($"{ApiBaseUrl}/servers?ids={Uri.EscapeDataString(serverIds)}", cancellationToken).ConfigureAwait(false);

        if (!serversDoc.RootElement.TryGetProperty("results", out var servers) || servers.ValueKind != JsonValueKind.Array)
        {
            return new EpisodeDetails { Url = episodeUrl, TitleEn = displayTitle, ProvidersByLanguage = providers };
        }

        foreach (var server in servers.EnumerateArray())
        {
            var name = GetString(server, "name") ?? string.Empty;

            // Only "HD-*" is verified to resolve through megaplay.buzz, which is the only embed
            // host this plugin currently has an extractor for (see MegaplayExtractor). VidPlay/
            // Vidstream/VidCloud resolve through other hosts we haven't built extractors for yet,
            // so surfacing them would just be a dead "no extractor available" error.
            if (!name.StartsWith("HD", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var type = GetString(server, "type") ?? "sub";
            var linkId = GetString(server, "link_id");
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

            // Store our own resolvable API URL — ResolveRedirectAsync turns this into the
            // megaplay.buzz embed page URL, which MegaplayExtractor then decodes.
            bucket["Megaplay"] = $"{ApiBaseUrl}/stream?id={Uri.EscapeDataString(linkId)}";
        }

        return new EpisodeDetails
        {
            Url = episodeUrl,
            TitleEn = displayTitle,
            ProvidersByLanguage = providers,
        };
    }

    /// <inheritdoc />
    public override async Task<string> ResolveRedirectAsync(string redirectUrl, CancellationToken cancellationToken = default)
    {
        using var doc = await FetchJsonAsync(redirectUrl, cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("results", out var results))
        {
            return GetString(results, "url") ?? redirectUrl;
        }

        return redirectUrl;
    }

    /// <inheritdoc />
    public override async Task<List<BrowseItem>> GetPopularAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await FetchJsonAsync(ApiBaseUrl, cancellationToken).ConfigureAwait(false);
        return ParseHomeSection(doc.RootElement, "topAiring");
    }

    /// <inheritdoc />
    public override async Task<List<BrowseItem>> GetNewReleasesAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await FetchJsonAsync(ApiBaseUrl, cancellationToken).ConfigureAwait(false);
        return ParseHomeSection(doc.RootElement, "trending");
    }

    // ── Parsing helpers ──────────────────────────────────────────

    private List<BrowseItem> ParseHomeSection(JsonElement root, string sectionName)
    {
        var items = new List<BrowseItem>();

        if (!root.TryGetProperty("results", out var results) ||
            !results.TryGetProperty(sectionName, out var section) ||
            section.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var item in section.EnumerateArray())
        {
            var title = GetString(item, "title");
            var rawSlug = GetString(item, "slug");
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(rawSlug))
            {
                continue;
            }

            var slug = NormalizeSlug(rawSlug);
            var poster = GetString(item, "poster") ?? string.Empty;

            items.Add(new BrowseItem
            {
                Title = DecodeHtml(title),
                Url = BuildSeriesUrl(slug, title, poster, new List<string>()),
                CoverImageUrl = poster,
                Genre = string.Empty,
                Source = SourceName,
            });
        }

        return items;
    }

    /// <summary>
    /// Builds a series URL that carries display metadata in its query string (title, poster,
    /// pipe-separated genres) so <see cref="GetSeriesInfoAsync"/> never needs to call a
    /// nonexistent detail endpoint.
    /// </summary>
    private static string BuildSeriesUrl(string slug, string title, string poster, List<string> genres)
    {
        var url = $"{SiteBaseUrl}/{slug}?t={Uri.EscapeDataString(title)}";
        if (!string.IsNullOrEmpty(poster))
        {
            url += $"&p={Uri.EscapeDataString(poster)}";
        }

        if (genres.Count > 0)
        {
            url += $"&g={Uri.EscapeDataString(string.Join('|', genres))}";
        }

        return url;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static List<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }

    /// <summary>
    /// Strips a trailing "/ep-N" episode reference some list endpoints (search, trending) embed
    /// in their "slug" field — the underlying anime slug never includes it.
    /// </summary>
    private static string NormalizeSlug(string slug)
    {
        var idx = slug.IndexOf("/ep-", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? slug[..idx] : slug;
    }

    private static string ExtractSlug(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath.Trim('/').Split('/').LastOrDefault() ?? string.Empty;
        }

        return url.Trim('/').Split('/').LastOrDefault() ?? url;
    }

    private static Dictionary<string, string> ParseQuery(string url)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return result;
        }

        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
            {
                result[parts[0]] = Uri.UnescapeDataString(parts[1]);
            }
        }

        return result;
    }
}
