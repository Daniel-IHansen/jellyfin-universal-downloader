using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniBridge.Services;

/// <summary>
/// Base class for streaming sites that are scraped via regex over server-rendered HTML
/// (aniworld.to / s.to style). Sites whose data instead comes from a JSON API (e.g. Anikoto,
/// Anime Nexus) should inherit <see cref="StreamingSiteService"/> directly instead — see
/// <see cref="AnikotoService"/> for that pattern.
/// </summary>
public abstract class HtmlScrapingSiteService : StreamingSiteService
{
    private static readonly Regex TitlePattern = new(
        @"<h1[^>]*><span>(?<title>[^<]+)</span>",
        RegexOptions.Compiled);

    private static readonly Regex CoverImagePattern = new(
        @"<div[^>]*class=""seriesCoverBox""[^>]*>.*?data-src=""(?<src>[^""]+)""",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DescriptionPattern = new(
        @"<p[^>]*class=""seri_des""[^>]*data-full-description=""(?<desc>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex GermanTitlePattern = new(
        @"<span[^>]*class=""episodeGermanTitle""[^>]*>(?<title>[^<]*)",
        RegexOptions.Compiled);

    private static readonly Regex EnglishTitlePattern = new(
        @"<small[^>]*class=""episodeEnglishTitle""[^>]*>(?<title>[^<]*)",
        RegexOptions.Compiled);

    private static readonly Regex GenrePattern = new(
        @"<a[^>]*href=""/genre/[^""]+""[^>]*class=""genreButton[^""]*""[^>]*>(?<genre>[^<]+)</a>",
        RegexOptions.Compiled);

    private static readonly Regex MovieListPattern = new(
        @"<a[^>]*href=""(/(?:anime/stream|serie)/[^""]+/filme/film-\d+)""[^>]*>",
        RegexOptions.Compiled);

    private static readonly Regex MovieSectionLinkPattern = new(
        @"href=""/(?:anime/stream|serie)/[^""]+/filme(?:[""?#/]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the <see cref="HtmlScrapingSiteService"/> class.
    /// </summary>
    protected HtmlScrapingSiteService(HttpClient httpClient, ILogger logger)
        : base(httpClient, logger)
    {
    }

    /// <summary>Gets the search URL.</summary>
    protected abstract string SearchUrl { get; }

    /// <summary>Gets the series path prefix (e.g. "/anime/stream/" or "/serie/").</summary>
    protected abstract string SeriesPathPrefix { get; }

    /// <summary>Gets the popular page path (e.g. "/beliebte-animes").</summary>
    protected abstract string PopularPath { get; }

    /// <summary>Gets the heading text for new releases section (e.g. "Neue Animes").</summary>
    protected abstract string NewSectionHeading { get; }

    /// <summary>Gets the season link pattern. Overridable for different path prefixes.</summary>
    protected abstract Regex SeasonLinkPattern { get; }

    /// <summary>Gets the episode list pattern. Overridable for different path prefixes.</summary>
    protected abstract Regex EpisodeListPattern { get; }

    /// <summary>Gets the search result URL filter pattern.</summary>
    protected abstract Regex SearchFilterPattern { get; }

    /// <summary>Gets the browse item pattern for this site.</summary>
    protected abstract Regex BrowseItemPattern { get; }

    /// <inheritdoc />
    public override async Task<List<SearchResult>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("keyword", keyword)
        });

        var response = await HttpClient.PostAsync(SearchUrl, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var results = JsonSerializer.Deserialize<List<SearchResultRaw>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (results == null)
        {
            return new List<SearchResult>();
        }

        return results
            .Where(r => !string.IsNullOrEmpty(r.Link) && SearchFilterPattern.IsMatch(r.Link))
            .Select(r => new SearchResult
            {
                Title = StripHtml(r.Title ?? string.Empty),
                Url = $"{BaseUrl}{r.Link}",
                Description = StripHtml(r.Description ?? string.Empty),
                Source = SourceName,
            })
            .ToList();
    }

    /// <inheritdoc />
    public override async Task<SeriesInfo> GetSeriesInfoAsync(string seriesUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(seriesUrl, cancellationToken).ConfigureAwait(false);

        var titleMatch = TitlePattern.Match(html);
        var coverMatch = CoverImagePattern.Match(html);
        var descMatch = DescriptionPattern.Match(html);

        var genres = GenrePattern.Matches(html)
            .Select(m => DecodeHtml(m.Groups["genre"].Value.Trim()))
            .Distinct()
            .ToList();

        // Extract seasons
        var seasons = SeasonLinkPattern.Matches(html)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Select(path =>
            {
                var numMatch = Regex.Match(path, @"staffel-(\d+)");
                return new SeasonRef
                {
                    Url = $"{BaseUrl}{path}",
                    Number = numMatch.Success ? int.Parse(numMatch.Groups[1].Value) : 0,
                };
            })
            .OrderBy(s => s.Number)
            .ToList();

        // Check for movies. Some pages only expose the /filme section link on the series page
        // and render concrete /filme/film-* entries on the movie subpage.
        var hasMovies = html.Contains("/filme/film-", StringComparison.OrdinalIgnoreCase)
            || MovieSectionLinkPattern.IsMatch(html);

        var coverUrl = coverMatch.Success ? coverMatch.Groups["src"].Value : string.Empty;
        if (!string.IsNullOrEmpty(coverUrl) && !coverUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            coverUrl = $"{BaseUrl}{coverUrl}";
        }

        return new SeriesInfo
        {
            Title = titleMatch.Success ? DecodeHtml(titleMatch.Groups["title"].Value.Trim()) : "Unknown",
            Url = seriesUrl,
            CoverImageUrl = coverUrl,
            Description = descMatch.Success ? DecodeHtml(descMatch.Groups["desc"].Value.Trim()) : string.Empty,
            Genres = genres,
            Seasons = seasons,
            HasMovies = hasMovies,
        };
    }

    /// <inheritdoc />
    public override async Task<List<EpisodeRef>> GetEpisodesAsync(string seasonUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(seasonUrl, cancellationToken).ConfigureAwait(false);

        var isMovies = seasonUrl.Contains("/filme", StringComparison.OrdinalIgnoreCase);
        var pattern = isMovies ? MovieListPattern : EpisodeListPattern;

        var episodes = pattern.Matches(html)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Select(path =>
            {
                var numMatch = Regex.Match(path, @"(?:episode|film)-(\d+)");
                return new EpisodeRef
                {
                    Url = $"{BaseUrl}{path}",
                    Number = numMatch.Success ? int.Parse(numMatch.Groups[1].Value) : 0,
                    IsMovie = isMovies,
                };
            })
            .OrderBy(e => e.Number)
            .ToList();

        return episodes;
    }

    /// <inheritdoc />
    public override async Task<EpisodeDetails> GetEpisodeDetailsAsync(string episodeUrl, CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(episodeUrl, cancellationToken).ConfigureAwait(false);

        var germanTitle = GermanTitlePattern.Match(html);
        var englishTitle = EnglishTitlePattern.Match(html);

        var providers = new Dictionary<string, Dictionary<string, string>>();

        var liPattern = new Regex(
            @"<li[^>]*data-lang-key=""(?<langKey>\d+)""[^>]*data-link-target=""(?<redirect>[^""]+)""[^>]*>.*?<h4>(?<provider>[^<]+)</h4>",
            RegexOptions.Singleline);

        foreach (Match match in liPattern.Matches(html))
        {
            var langKey = match.Groups["langKey"].Value;
            var redirect = match.Groups["redirect"].Value;
            var provider = match.Groups["provider"].Value.Trim();

            if (!providers.ContainsKey(langKey))
            {
                providers[langKey] = new Dictionary<string, string>();
            }

            var redirectUrl = redirect.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? redirect
                : $"{BaseUrl}{redirect}";

            providers[langKey][provider] = redirectUrl;
        }

        return new EpisodeDetails
        {
            Url = episodeUrl,
            TitleDe = germanTitle.Success ? DecodeHtml(germanTitle.Groups["title"].Value.Trim()) : null,
            TitleEn = englishTitle.Success ? DecodeHtml(englishTitle.Groups["title"].Value.Trim()) : null,
            ProvidersByLanguage = RemapLanguages(providers),
        };
    }

    /// <inheritdoc />
    public override async Task<List<BrowseItem>> GetPopularAsync(CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync($"{BaseUrl}{PopularPath}", cancellationToken).ConfigureAwait(false);
        return ParseBrowseItems(html);
    }

    /// <inheritdoc />
    public override async Task<List<BrowseItem>> GetNewReleasesAsync(CancellationToken cancellationToken = default)
    {
        var html = await FetchPageAsync(BaseUrl, cancellationToken).ConfigureAwait(false);

        var heading = $"<h2>{NewSectionHeading}</h2>";
        var newSectionIdx = html.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (newSectionIdx < 0)
        {
            // Fallback: try without h2 tags
            newSectionIdx = html.IndexOf($"{NewSectionHeading}</h2>", StringComparison.OrdinalIgnoreCase);
        }

        if (newSectionIdx < 0)
        {
            Logger.LogWarning("Could not find '{Heading}' heading on homepage", NewSectionHeading);
            return new List<BrowseItem>();
        }

        var sectionHtml = html[newSectionIdx..];
        var nextSection = sectionHtml.IndexOf("<div class=\"homeContentPromotionBox", 10, StringComparison.OrdinalIgnoreCase);
        if (nextSection < 0)
        {
            nextSection = sectionHtml.IndexOf("<footer", StringComparison.OrdinalIgnoreCase);
        }

        if (nextSection > 0)
        {
            sectionHtml = sectionHtml[..nextSection];
        }

        return ParseBrowseItems(sectionHtml);
    }

    /// <inheritdoc />
    public override async Task<string> ResolveRedirectAsync(string redirectUrl, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
        var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return response.RequestMessage?.RequestUri?.ToString() ?? redirectUrl;
    }

    /// <summary>
    /// Parses browse items (popular/new) from HTML.
    /// </summary>
    protected List<BrowseItem> ParseBrowseItems(string html)
    {
        var items = new List<BrowseItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in BrowseItemPattern.Matches(html))
        {
            var url = $"{BaseUrl}{match.Groups["url"].Value}";

            if (!seen.Add(url))
            {
                continue;
            }

            var coverPath = match.Groups["cover"].Value;
            var coverUrl = coverPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? coverPath
                : $"{BaseUrl}{coverPath}";

            var rawTitle = match.Groups["name"].Value.Trim();
            var cleanTitle = Regex.Replace(rawTitle, "<[^>]+>", string.Empty).Trim();

            items.Add(new BrowseItem
            {
                Title = DecodeHtml(cleanTitle),
                Url = url,
                CoverImageUrl = coverUrl,
                Genre = match.Groups["genre"].Success ? DecodeHtml(match.Groups["genre"].Value.Trim()) : string.Empty,
                Source = SourceName,
            });
        }

        return items;
    }
}
