using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniBridge.Services;

/// <summary>
/// Service for interacting with AniWatch (aniwatch.one), a HiAnime-style anime streaming site.
/// Unlike Anikoto/Anime Nexus, aniwatch.one itself is directly fetchable (no anti-bot layer) and
/// serves everything — search results, the full episode list, and both sub/dub provider embed
/// URLs — as plain server-rendered HTML on a single page per anime, with no AJAX calls needed
/// (verified against shows with 500+ episodes: every episode across every paginated tab is
/// already present in the initial response, just split across several hidden
/// <c>id="episodes-page-N"</c> containers).
/// <br/><br/>
/// Each episode's watch page exposes provider servers directly as
/// <c>&lt;div class="item server-item" data-type="sub|dub" data-url="https://host/e/id"&gt;</c> —
/// no separate redirect/ajax step needed, so <see cref="ResolveRedirectAsync"/> is a no-op.
/// Three servers are offered site-wide (verified across multiple titles): a MegaCloud/rabbitstream
/// embed (<c>megacloudx.net</c>), a custom SPA player (<c>weneverbeenfree.com</c>), and a
/// whitelabelled DoodStream instance (<c>playmogo.com</c>) — only the last has a working
/// extractor (<see cref="Extractors.DoodStreamExtractor"/>) so far, so only that one is surfaced
/// here; add the other two once their embeds have been decoded.
/// </summary>
public class AniWatchService : StreamingSiteService
{
    private const string SiteBaseUrl = "https://aniwatch.one";

    private static readonly IReadOnlyDictionary<string, string> AniWatchLanguageMap =
        new Dictionary<string, string> { ["sub"] = "sub", ["dub"] = "dub" };

    // Maps a provider embed host to a friendly display name. Only hosts with a registered
    // IStreamExtractor are listed — unrecognized hosts are silently skipped (see class doc)
    // rather than surfaced as a dead "no extractor available" error.
    private static readonly IReadOnlyDictionary<string, string> KnownProviderHosts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["playmogo.com"] = "DoodStream",
        };

    private static readonly Regex SearchResultPattern = new(
        @"<div[^>]*class=""flw-item""[^>]*>[\s\S]*?" +
        @"<img[^>]*(?:data-src|src)=""(?<cover>[^""]+)""[^>]*>[\s\S]*?" +
        @"<h3[^>]*class=""film-name""[^>]*><a[^>]*href=""(?<url>/watch/[^""]+)""[^>]*>(?<title>[^<]+)</a>",
        RegexOptions.Compiled);

    private static readonly Regex TitlePattern = new(
        @"<h2[^>]*class=""film-name""[^>]*><a[^>]*>(?<title>[^<]+)</a>",
        RegexOptions.Compiled);

    private static readonly Regex CoverImagePattern = new(
        @"<img(?=[^>]*class=""film-poster-img"")[^>]*(?:data-src|src)=""(?<src>[^""]+)""[^>]*>",
        RegexOptions.Compiled);

    private static readonly Regex DescriptionPattern = new(
        @"<div[^>]*class=""film-description[^""]*""[^>]*>\s*<div[^>]*class=""text""[^>]*>(?<desc>[\s\S]*?)</div>",
        RegexOptions.Compiled);

    private static readonly Regex GenrePattern = new(
        @"Genres:</strong>\s*(?<genres>(?:<a[^>]*href=""/genre/[^""]+""[^>]*>[^<]+</a>,?\s*)+)",
        RegexOptions.Compiled);

    private static readonly Regex GenreLinkPattern = new(@"<a[^>]*>(?<name>[^<]+)</a>", RegexOptions.Compiled);

    private static readonly Regex EpisodeItemPattern = new(
        @"<a[^>]*class=""ssl-item ep-item[^""]*""[^>]*data-number=""(?<num>\d+)""[^>]*href=""(?<url>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex ServerItemPattern = new(
        @"<div[^>]*class=""item server-item""[^>]*data-type=""(?<type>sub|dub)""[^>]*data-url=""(?<url>[^""]+)""[^>]*>\s*<a[^>]*class=""btn[^""]*"">(?<label>[^<]*)</a>",
        RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the <see cref="AniWatchService"/> class.
    /// </summary>
    public AniWatchService(IHttpClientFactory httpClientFactory, ILogger<AniWatchService> logger)
        : base(httpClientFactory.CreateClient("AniWatch"), logger)
    {
    }

    /// <inheritdoc />
    public override string SourceName => "aniwatch";

    /// <inheritdoc />
    public override string DisplayName => "AniWatch";

    /// <inheritdoc />
    public override string BaseUrl => SiteBaseUrl;

    /// <inheritdoc />
    public override bool ResolvesDirectStreamUrls => false;

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> LanguageMap => AniWatchLanguageMap;

    /// <inheritdoc />
    public override async Task<List<SearchResult>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync($"{SiteBaseUrl}/search?keyword={Uri.EscapeDataString(keyword)}", cancellationToken).ConfigureAwait(false);

        var results = new List<SearchResult>();
        foreach (Match match in SearchResultPattern.Matches(html))
        {
            results.Add(new SearchResult
            {
                Title = DecodeHtml(match.Groups["title"].Value.Trim()),
                Url = ResolveUrl(match.Groups["url"].Value),
                Description = string.Empty,
                Source = SourceName,
            });
        }

        return results;
    }

    /// <inheritdoc />
    public override async Task<SeriesInfo> GetSeriesInfoAsync(string seriesUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(seriesUrl, cancellationToken).ConfigureAwait(false);

        var titleMatch = TitlePattern.Match(html);
        var title = titleMatch.Success ? DecodeHtml(titleMatch.Groups["title"].Value.Trim()) : "Unknown";

        var coverMatch = CoverImagePattern.Match(html);
        var coverUrl = coverMatch.Success ? coverMatch.Groups["src"].Value : string.Empty;

        var descMatch = DescriptionPattern.Match(html);
        var description = descMatch.Success
            ? StripHtml(descMatch.Groups["desc"].Value)
            : string.Empty;

        var genres = new List<string>();
        var genreMatch = GenrePattern.Match(html);
        if (genreMatch.Success)
        {
            foreach (Match link in GenreLinkPattern.Matches(genreMatch.Groups["genres"].Value))
            {
                genres.Add(DecodeHtml(link.Groups["name"].Value.Trim()));
            }
        }

        return new SeriesInfo
        {
            Title = title,
            Url = seriesUrl,
            CoverImageUrl = coverUrl,
            Description = description,
            Genres = genres,
            // Flat episode list, no season concept — same "one synthetic season" model as Anikoto.
            Seasons = new List<SeasonRef> { new() { Url = seriesUrl, Number = 1 } },
            HasMovies = false,
        };
    }

    /// <inheritdoc />
    public override async Task<List<EpisodeRef>> GetEpisodesAsync(string seasonUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(seasonUrl, cancellationToken).ConfigureAwait(false);

        var episodes = new List<EpisodeRef>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in EpisodeItemPattern.Matches(html))
        {
            var url = ResolveUrl(match.Groups["url"].Value);
            if (!seenUrls.Add(url))
            {
                continue;
            }

            episodes.Add(new EpisodeRef
            {
                Url = url,
                Number = int.Parse(match.Groups["num"].Value),
                IsMovie = false,
            });
        }

        return episodes.OrderBy(e => e.Number).ToList();
    }

    /// <inheritdoc />
    public override async Task<EpisodeDetails> GetEpisodeDetailsAsync(string episodeUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(episodeUrl, cancellationToken).ConfigureAwait(false);

        var providers = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ServerItemPattern.Matches(html))
        {
            var dataUrl = match.Groups["url"].Value;
            var providerName = ResolveProviderName(dataUrl);
            if (providerName == null)
            {
                // No extractor for this embed host yet (see class doc) — skip rather than
                // surface a download option that can only ever fail.
                continue;
            }

            var langKey = match.Groups["type"].Value;
            if (!providers.TryGetValue(langKey, out var bucket))
            {
                bucket = new Dictionary<string, string>();
                providers[langKey] = bucket;
            }

            bucket[providerName] = dataUrl;
        }

        // The site doesn't reliably expose a per-episode subtitle distinct from the anime's own
        // title (its "data-jname" field was found stale/inconsistent across episodes during
        // testing), so leave it null — DownloadService falls back to "Episode N" via PathHelper.
        return new EpisodeDetails
        {
            Url = episodeUrl,
            TitleEn = null,
            ProvidersByLanguage = providers,
        };
    }

    /// <inheritdoc />
    public override async Task<List<BrowseItem>> GetPopularAsync(CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync($"{SiteBaseUrl}/most-popular", cancellationToken).ConfigureAwait(false);
        return ParseBrowseItems(html);
    }

    /// <inheritdoc />
    public override async Task<List<BrowseItem>> GetNewReleasesAsync(CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync($"{SiteBaseUrl}/recently-updated", cancellationToken).ConfigureAwait(false);
        return ParseBrowseItems(html);
    }

    /// <inheritdoc />
    public override Task<string> ResolveRedirectAsync(string redirectUrl, CancellationToken cancellationToken = default)
    {
        // Provider server URLs are already direct embed pages (see class doc) — nothing to resolve.
        return Task.FromResult(redirectUrl);
    }

    private List<BrowseItem> ParseBrowseItems(string html)
    {
        var items = new List<BrowseItem>();
        foreach (Match match in SearchResultPattern.Matches(html))
        {
            items.Add(new BrowseItem
            {
                Title = DecodeHtml(match.Groups["title"].Value.Trim()),
                Url = ResolveUrl(match.Groups["url"].Value),
                CoverImageUrl = match.Groups["cover"].Value,
                Genre = string.Empty,
                Source = SourceName,
            });
        }

        return items;
    }

    private static string? ResolveProviderName(string dataUrl)
    {
        if (!Uri.TryCreate(dataUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return KnownProviderHosts.TryGetValue(uri.Host, out var name) ? name : null;
    }

    private static string ResolveUrl(string path)
    {
        return path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : $"{SiteBaseUrl}{path}";
    }
}
