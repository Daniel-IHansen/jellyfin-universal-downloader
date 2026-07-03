using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniBridge.Extractors;

/// <summary>
/// Extracts direct video URLs from megaplay.buzz embeds, used by Anikoto's "HD-*" server.
/// Ported from the working flow in
/// <see href="https://github.com/testingbetaversion/anikoto/blob/main/anikoto/anikoto.py"/>:
/// fetch the embed page, pull the numeric <c>data-id</c> attribute out of its HTML, then call
/// megaplay's own <c>getSources</c> AJAX endpoint with that id to get the real HLS file URL.
/// </summary>
public class MegaplayExtractor : IStreamExtractor
{
    private const string RefererOrigin = "https://anikoto.net/";

    // The CDN serving the actual HLS manifest/segments validates Referer against megaplay.buzz's
    // own origin (the page hosting the player), not the site that embedded it — this is what
    // ffmpeg needs to send when it fetches the stream, separate from RefererOrigin above (used
    // only when this extractor itself fetches the embed page).
    private const string StreamReferer = "https://megaplay.buzz/";

    private static readonly Regex DataIdPattern = new(
        @" data-id=""(?<id>\d+)""",
        RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<MegaplayExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MegaplayExtractor"/> class.
    /// </summary>
    public MegaplayExtractor(IHttpClientFactory httpClientFactory, ILogger<MegaplayExtractor> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Anikoto");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => "Megaplay";

    /// <summary>
    /// megaplay.buzz's CDN checks Referer on the HLS manifest/segment requests themselves, not
    /// just the API calls made while resolving them — ffmpeg needs this passed explicitly since
    /// it otherwise sends no Referer at all, which the CDN rejects.
    /// </summary>
    public string? RequiredReferer => StreamReferer;

    /// <inheritdoc />
    public async Task<string?> GetDirectLinkAsync(string embedUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Extracting Megaplay direct link from: {Url}", embedUrl);

            using var embedRequest = new HttpRequestMessage(HttpMethod.Get, embedUrl);
            embedRequest.Headers.Referrer = new Uri(RefererOrigin);
            var embedResponse = await _httpClient.SendAsync(embedRequest, cancellationToken).ConfigureAwait(false);
            embedResponse.EnsureSuccessStatusCode();
            var html = await embedResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var idMatch = DataIdPattern.Match(html);
            if (!idMatch.Success)
            {
                _logger.LogWarning("Could not find data-id on Megaplay embed page: {Url}", embedUrl);
                return null;
            }

            var dataId = idMatch.Groups["id"].Value;
            var sourcesUrl = $"https://megaplay.buzz/stream/getSources?id={Uri.EscapeDataString(dataId)}";

            using var sourcesRequest = new HttpRequestMessage(HttpMethod.Get, sourcesUrl);
            sourcesRequest.Headers.Referrer = new Uri(embedUrl);
            sourcesRequest.Headers.Add("x-requested-with", "XMLHttpRequest");
            var sourcesResponse = await _httpClient.SendAsync(sourcesRequest, cancellationToken).ConfigureAwait(false);
            sourcesResponse.EnsureSuccessStatusCode();
            var json = await sourcesResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sources", out var sources) &&
                sources.TryGetProperty("file", out var file))
            {
                var streamUrl = file.GetString();
                _logger.LogDebug("Megaplay source extracted: {Found}", !string.IsNullOrEmpty(streamUrl));
                return streamUrl;
            }

            _logger.LogWarning("Megaplay getSources response had no sources.file: {Json}", json);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract Megaplay direct link from {Url}", embedUrl);
            return null;
        }
    }
}
