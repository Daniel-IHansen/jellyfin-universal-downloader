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
/// Abstract base for a streaming site adapter. Implementations plug a new site into the
/// plugin without requiring any changes to the controller, config, or UI: register the
/// concrete type in <see cref="PluginServiceRegistrator"/> and everything else (search,
/// browse, download routing, config UI) discovers it automatically through this contract.
/// English Sub/Dub only — sites that offer other languages should simply not map them
/// in <see cref="LanguageMap"/> (see <see cref="RemapLanguages"/>).
/// </summary>
public abstract class StreamingSiteService
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingSiteService"/> class.
    /// </summary>
    protected StreamingSiteService(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        Logger = logger;
    }

    /// <summary>Gets the HTTP client for derived classes.</summary>
    protected HttpClient HttpClient => _httpClient;

    /// <summary>Logger instance for derived classes.</summary>
    protected ILogger Logger { get; }

    /// <summary>Gets the source identifier (e.g. "anikoto", "aniwatch").</summary>
    public abstract string SourceName { get; }

    /// <summary>Gets the human-readable display name shown in the UI (e.g. "Anikoto").</summary>
    public abstract string DisplayName { get; }

    /// <summary>Gets the base URL of the site (e.g. "https://anikoto.net").</summary>
    public abstract string BaseUrl { get; }

    /// <summary>Gets the user agent string used for outbound requests.</summary>
    protected virtual string UserAgent => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>
    /// Gets whether this experimental/best-effort adapter should be surfaced to users.
    /// Sites without a verified, stable integration should default this to <c>false</c>
    /// so they don't appear as if fully supported until confirmed working.
    /// </summary>
    public virtual bool IsExperimental => false;

    /// <summary>
    /// Gets whether <see cref="ResolveRedirectAsync"/> already returns a final, directly
    /// downloadable stream URL (HLS/MP4) for this site, so no <see cref="Jellyfin.Plugin.AniBridge.Extractors.IStreamExtractor"/>
    /// lookup by provider name is needed. A hypothetical HTML-embed site would need an extractor
    /// to decode the provider's embed page; API-driven sites like Anikoto typically already hand
    /// back a playable URL and should set this to <c>true</c>.
    /// </summary>
    public virtual bool ResolvesDirectStreamUrls => false;

    /// <summary>
    /// Gets whether non-HTTPS (plain HTTP) URLs are allowed for this site. Only relevant for
    /// user-configurable custom base URLs; defaults to HTTPS-only for SSRF safety.
    /// </summary>
    public virtual bool AllowInsecureHttp => false;

    /// <summary>
    /// Gets the hostnames this service is allowed to talk to (used for SSRF protection by
    /// <see cref="Jellyfin.Plugin.AniBridge.Helpers.UrlValidator"/>). Defaults to the host of <see cref="BaseUrl"/> plus
    /// its "www." variant; override to add mirrors/CDN hosts.
    /// </summary>
    public virtual IEnumerable<string> AllowedHosts
    {
        get
        {
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri))
            {
                hosts.Add(baseUri.Host);
                if (!baseUri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                {
                    hosts.Add("www." + baseUri.Host);
                }
            }

            return hosts;
        }
    }

    /// <summary>
    /// Gets the map from this site's raw/native language identifiers to the plugin's two
    /// canonical language keys, <c>"sub"</c> (English Sub) and <c>"dub"</c> (English Dub).
    /// Any raw key not present here (e.g. a German track) is silently dropped by
    /// <see cref="RemapLanguages"/> and never surfaced to users.
    /// </summary>
    protected abstract IReadOnlyDictionary<string, string> LanguageMap { get; }

    /// <summary>Searches for series on the site.</summary>
    public abstract Task<List<SearchResult>> SearchAsync(string keyword, CancellationToken cancellationToken = default);

    /// <summary>Gets detailed information about a series.</summary>
    public abstract Task<SeriesInfo> GetSeriesInfoAsync(string seriesUrl, CancellationToken cancellationToken = default);

    /// <summary>Gets episodes for a given season.</summary>
    public abstract Task<List<EpisodeRef>> GetEpisodesAsync(string seasonUrl, CancellationToken cancellationToken = default);

    /// <summary>Gets provider links (English Sub/Dub only) for an episode.</summary>
    public abstract Task<EpisodeDetails> GetEpisodeDetailsAsync(string episodeUrl, CancellationToken cancellationToken = default);

    /// <summary>Gets the popular series list.</summary>
    public abstract Task<List<BrowseItem>> GetPopularAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets newly added series.</summary>
    public abstract Task<List<BrowseItem>> GetNewReleasesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a provider redirect/reference URL to the URL that should be handed to the
    /// download pipeline next. For HTML-embed sites this is the provider's embed page (which
    /// an <see cref="Jellyfin.Plugin.AniBridge.Extractors.IStreamExtractor"/> then decodes). For API-driven sites where
    /// <see cref="ResolvesDirectStreamUrls"/> is <c>true</c>, this should return the final
    /// playable stream URL directly.
    /// </summary>
    public abstract Task<string> ResolveRedirectAsync(string redirectUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Filters and remaps a raw provider-by-language dictionary through <see cref="LanguageMap"/>,
    /// dropping any language not explicitly mapped (e.g. German tracks).
    /// </summary>
    protected Dictionary<string, Dictionary<string, string>> RemapLanguages(
        Dictionary<string, Dictionary<string, string>> raw)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rawKey, providers) in raw)
        {
            if (!LanguageMap.TryGetValue(rawKey, out var canonicalKey))
            {
                continue;
            }

            if (!result.TryGetValue(canonicalKey, out var bucket))
            {
                bucket = new Dictionary<string, string>();
                result[canonicalKey] = bucket;
            }

            foreach (var (provider, url) in providers)
            {
                bucket[provider] = url;
            }
        }

        return result;
    }

    /// <summary>
    /// Fetches a page/resource as a string.
    /// </summary>
    protected async Task<string> FetchPageAsync(string url, CancellationToken cancellationToken)
    {
        Logger.LogDebug("Fetching page: {Url}", url);
        var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches a URL and parses the response body as JSON. Used by API-driven site adapters
    /// (see <see cref="AnikotoService"/>) instead of the HTML-scraping <see cref="FetchPageAsync"/>.
    /// </summary>
    protected async Task<JsonDocument> FetchJsonAsync(string url, CancellationToken cancellationToken)
    {
        Logger.LogDebug("Fetching JSON: {Url}", url);
        var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Strips HTML tags and decodes entities.
    /// </summary>
    protected static string StripHtml(string input)
    {
        var stripped = System.Text.RegularExpressions.Regex.Replace(input, "<.*?>", string.Empty).Trim();
        return DecodeHtml(stripped);
    }

    /// <summary>
    /// Decodes HTML entities, handling double/triple-encoded content.
    /// </summary>
    protected static string DecodeHtml(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var decoded = input;
        for (int i = 0; i < 5; i++)
        {
            var next = System.Net.WebUtility.HtmlDecode(decoded);
            if (next == decoded)
            {
                break;
            }

            decoded = next;
        }

        return decoded;
    }
}

// ── DTO classes ─────────────────────────────────────────────────

/// <summary>
/// Raw search result from a site's search API.
/// </summary>
public class SearchResultRaw
{
    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the link.</summary>
    public string? Link { get; set; }

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// Search result.
/// </summary>
public class SearchResult
{
    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the source site identifier.</summary>
    public string Source { get; set; } = "anikoto";
}

/// <summary>
/// Series information.
/// </summary>
public class SeriesInfo
{
    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the cover image URL.</summary>
    public string CoverImageUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the genres.</summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>Gets or sets the seasons.</summary>
    public List<SeasonRef> Seasons { get; set; } = new();

    /// <summary>Gets or sets whether the series has movies.</summary>
    public bool HasMovies { get; set; }
}

/// <summary>
/// Season reference.
/// </summary>
public class SeasonRef
{
    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the season number.</summary>
    public int Number { get; set; }
}

/// <summary>
/// Episode reference.
/// </summary>
public class EpisodeRef
{
    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode number.</summary>
    public int Number { get; set; }

    /// <summary>Gets or sets whether this is a movie.</summary>
    public bool IsMovie { get; set; }
}

/// <summary>
/// A series item from the browse (popular/new) lists.
/// </summary>
public class BrowseItem
{
    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the cover image URL.</summary>
    public string CoverImageUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the genre label.</summary>
    public string Genre { get; set; } = string.Empty;

    /// <summary>Gets or sets the source site identifier.</summary>
    public string Source { get; set; } = "anikoto";
}

/// <summary>
/// Episode details with provider links.
/// </summary>
public class EpisodeDetails
{
    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the German title, if the site exposes one (kept for filename purposes only; never shown as a language option).</summary>
    public string? TitleDe { get; set; }

    /// <summary>Gets or sets the English title.</summary>
    public string? TitleEn { get; set; }

    /// <summary>
    /// Gets or sets the providers grouped by canonical language key: <c>"sub"</c> (English Sub)
    /// or <c>"dub"</c> (English Dub). Value: dictionary of provider name to redirect URL.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ProvidersByLanguage { get; set; } = new();
}
