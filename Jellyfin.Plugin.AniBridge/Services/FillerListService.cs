using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniBridge.Services;

/// <summary>
/// Best-effort lookup of filler episode numbers for a series, scraped from
/// animefillerlist.com, to power the "Canon only" toggle in the episode browser. A show that
/// isn't listed there (confirmed via a plain HTTP 404 on an unrecognized slug), or whose page
/// doesn't parse, just means the toggle isn't offered — this never blocks browsing or
/// downloading.
/// </summary>
public class FillerListService
{
    private const string BaseUrl = "https://www.animefillerlist.com/shows/";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

    // animefillerlist.com row markup, verified live (e.g. /shows/naruto):
    // <tr class="manga_canon odd" id="eps-1"><td class="Number">1</td>...
    // <tr class="filler even" id="eps-26"><td class="Number">26</td>...
    // <tr class="mixed_canon/filler even" id="eps-98"><td class="Number">98</td>...
    // A row counts as filler for the "Canon only" toggle if its class contains "filler" at
    // all — that covers both pure filler and mixed canon/filler rows.
    private static readonly Regex EpisodeRowPattern = new(
        @"<tr\s+class=""(?<cls>[^""]*)""\s+id=""eps-\d+""><td\s+class=""Number"">(?<num>\d+)</td>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NonSlugChars = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<FillerListService> _logger;
    private readonly ConcurrentDictionary<string, (HashSet<int>? Filler, DateTime FetchedAt)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="FillerListService"/> class.
    /// </summary>
    public FillerListService(IHttpClientFactory httpClientFactory, ILogger<FillerListService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Anikoto");
        _logger = logger;
    }

    /// <summary>
    /// Returns the set of filler episode numbers for a series title, or null if the series
    /// isn't listed on animefillerlist.com or its page couldn't be parsed.
    /// </summary>
    public async Task<HashSet<int>?> GetFillerEpisodesAsync(string seriesTitle, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            return null;
        }

        var slug = ToSlug(seriesTitle);
        if (string.IsNullOrEmpty(slug))
        {
            return null;
        }

        if (_cache.TryGetValue(slug, out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheLifetime)
        {
            return cached.Filler;
        }

        HashSet<int>? filler = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + slug);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                filler = ParseFillerEpisodes(html);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to fetch filler list for {Title}", seriesTitle);
        }

        _cache[slug] = (filler, DateTime.UtcNow);
        return filler;
    }

    private static HashSet<int>? ParseFillerEpisodes(string html)
    {
        var matches = EpisodeRowPattern.Matches(html);
        if (matches.Count == 0)
        {
            return null;
        }

        var filler = new HashSet<int>();
        foreach (Match match in matches)
        {
            if (match.Groups["cls"].Value.Contains("filler", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(match.Groups["num"].Value, out var number))
            {
                filler.Add(number);
            }
        }

        return filler;
    }

    private static string ToSlug(string title)
    {
        return NonSlugChars.Replace(title.ToLowerInvariant(), "-").Trim('-');
    }
}
