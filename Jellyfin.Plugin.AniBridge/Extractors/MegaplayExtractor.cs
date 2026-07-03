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

    // Matches each variant entry in an HLS *master* playlist: an EXT-X-STREAM-INF tag (with its
    // BANDWIDTH attribute) followed by the media playlist URI on the next line.
    private static readonly Regex VariantPattern = new(
        @"#EXT-X-STREAM-INF:(?<attrs>[^\r\n]*)\r?\n(?<uri>[^\r\n]+)",
        RegexOptions.Compiled);

    private static readonly Regex BandwidthPattern = new(
        @"BANDWIDTH=(?<bw>\d+)",
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
    public async Task<StreamResolveResult?> GetDirectLinkAsync(string embedUrl, CancellationToken cancellationToken = default)
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

                if (string.IsNullOrEmpty(streamUrl))
                {
                    return null;
                }

                var videoUrl = await ResolveBestVariantAsync(streamUrl, cancellationToken).ConfigureAwait(false);
                var subtitleUrl = ExtractSubtitleUrl(doc.RootElement);

                return new StreamResolveResult
                {
                    VideoUrl = videoUrl,
                    SubtitleUrl = subtitleUrl,
                    SubtitleLanguage = subtitleUrl != null ? "eng" : null,
                };
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

    /// <summary>
    /// megaplay.buzz's getSources response carries an optional top-level "tracks" array —
    /// separate WebVTT subtitle files alongside the video (e.g.
    /// <c>"tracks":[{"file":"...vtt","label":"English","kind":"captions","default":true}]</c>).
    /// Picks the entry marked <c>default</c>, falling back to the first "captions"/"subtitles"
    /// entry if none is marked default.
    /// </summary>
    private static string? ExtractSubtitleUrl(JsonElement root)
    {
        if (!root.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? firstCaption = null;
        foreach (var track in tracks.EnumerateArray())
        {
            if (!track.TryGetProperty("kind", out var kindEl))
            {
                continue;
            }

            var kind = kindEl.GetString();
            if (!string.Equals(kind, "captions", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(kind, "subtitles", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!track.TryGetProperty("file", out var fileEl))
            {
                continue;
            }

            var fileUrl = fileEl.GetString();
            if (string.IsNullOrEmpty(fileUrl))
            {
                continue;
            }

            firstCaption ??= fileUrl;

            if (track.TryGetProperty("default", out var defaultEl) &&
                defaultEl.ValueKind == JsonValueKind.True)
            {
                return fileUrl;
            }
        }

        return firstCaption;
    }

    /// <summary>
    /// megaplay.buzz's "file" URL is an HLS *master* playlist listing multiple bitrate variants,
    /// each of which ffmpeg treats as its own "program". Feeding the master playlist straight to
    /// ffmpeg makes its automatic stream selection unreliable across those programs (seen as
    /// "Output file does not contain any stream" / "Stream map matches no streams" failures).
    /// Resolving to a single variant's own media playlist up front sidesteps that entirely — the
    /// media playlist has just one program, so ffmpeg's default stream selection works normally.
    /// </summary>
    private async Task<string> ResolveBestVariantAsync(string masterPlaylistUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, masterPlaylistUrl);
            request.Headers.Referrer = new Uri(StreamReferer);
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var playlist = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            string? bestUri = null;
            var bestBandwidth = -1L;

            foreach (Match match in VariantPattern.Matches(playlist))
            {
                var bwMatch = BandwidthPattern.Match(match.Groups["attrs"].Value);
                var bandwidth = bwMatch.Success && long.TryParse(bwMatch.Groups["bw"].Value, out var bw) ? bw : 0;

                if (bandwidth > bestBandwidth)
                {
                    bestBandwidth = bandwidth;
                    bestUri = match.Groups["uri"].Value.Trim();
                }
            }

            if (string.IsNullOrEmpty(bestUri))
            {
                // Not a master playlist (no variants found) — already a single-program media
                // playlist, safe to use as-is.
                return masterPlaylistUrl;
            }

            var resolvedUri = new Uri(new Uri(masterPlaylistUrl), bestUri);
            _logger.LogDebug("Megaplay resolved master playlist to variant (bandwidth {Bandwidth}): {Url}", bestBandwidth, resolvedUri);
            return resolvedUri.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve Megaplay master playlist variant, falling back to master URL: {Url}", masterPlaylistUrl);
            return masterPlaylistUrl;
        }
    }
}
