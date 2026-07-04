using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniBridge.Extractors;

/// <summary>
/// Extracts direct video URLs from AniWatch's "MGCLD" server, a MegaCloud/rabbitstream-style
/// embed currently served from <c>megacloudx.net</c>.
/// <br/><br/>
/// Verified live against a real embed page: unlike the typical MegaCloud/rabbitstream template
/// (which hides its source behind an encrypted <c>getSources</c> AJAX call), this whitelabelled
/// instance renders the final signed HLS master playlist URL straight into the page as a plain
/// JS variable — <c>var HLS = "https://.../master.m3u8?...";</c> — alongside an (always empty so
/// far, but present in the template as <c>var TRACKS = [{file,label,default?}];</c>) sidecar
/// subtitle array. No decryption, token exchange, or Referer is needed for either the embed page
/// or the resulting CDN URLs.
/// </summary>
public class MegaCloudExtractor : IStreamExtractor
{
    private const string RefererOrigin = "https://aniwatch.one/";

    private static readonly Regex HlsVarPattern = new(
        @"var\s+HLS\s*=\s*""(?<url>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex TracksVarPattern = new(
        @"var\s+TRACKS\s*=\s*(?<arr>\[[\s\S]*?\])\s*;",
        RegexOptions.Compiled);

    private static readonly Regex TrackEntryPattern = new(
        @"\{[^{}]*\}",
        RegexOptions.Compiled);

    private static readonly Regex TrackFilePattern = new(@"file\s*:\s*""(?<file>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex TrackDefaultPattern = new(@"default\s*:\s*true", RegexOptions.Compiled);

    // Matches each variant entry in an HLS *master* playlist: an EXT-X-STREAM-INF tag (with its
    // BANDWIDTH attribute) followed by the media playlist URI on the next line. Needed because
    // this master playlist also carries an EXT-X-I-FRAME-STREAM-INF entry, which — like
    // Megaplay's multi-bitrate masters — ffmpeg counts as its own "program" and can make its
    // automatic stream selection unreliable if fed the master directly.
    private static readonly Regex VariantPattern = new(
        @"#EXT-X-STREAM-INF:(?<attrs>[^\r\n]*)\r?\n(?<uri>[^\r\n]+)",
        RegexOptions.Compiled);

    private static readonly Regex BandwidthPattern = new(@"BANDWIDTH=(?<bw>\d+)", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<MegaCloudExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MegaCloudExtractor"/> class.
    /// </summary>
    public MegaCloudExtractor(IHttpClientFactory httpClientFactory, ILogger<MegaCloudExtractor> logger)
    {
        _httpClient = httpClientFactory.CreateClient("AniWatch");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => "MegaCloud";

    /// <inheritdoc />
    public async Task<StreamResolveResult?> GetDirectLinkAsync(string embedUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Extracting MegaCloud direct link from: {Url}", embedUrl);

            using var embedRequest = new HttpRequestMessage(HttpMethod.Get, embedUrl);
            embedRequest.Headers.Referrer = new Uri(RefererOrigin);
            var embedResponse = await _httpClient.SendAsync(embedRequest, cancellationToken).ConfigureAwait(false);
            embedResponse.EnsureSuccessStatusCode();
            var html = await embedResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var hlsMatch = HlsVarPattern.Match(html);
            if (!hlsMatch.Success)
            {
                _logger.LogWarning("Could not find HLS variable on MegaCloud embed page: {Url}", embedUrl);
                return null;
            }

            var masterPlaylistUrl = hlsMatch.Groups["url"].Value;
            var videoUrl = await ResolveBestVariantAsync(masterPlaylistUrl, cancellationToken).ConfigureAwait(false);
            var subtitleUrl = ExtractSubtitleUrl(html);

            _logger.LogDebug("MegaCloud source extracted for {Url}", embedUrl);
            return new StreamResolveResult
            {
                VideoUrl = videoUrl,
                SubtitleUrl = subtitleUrl,
                SubtitleLanguage = subtitleUrl != null ? "eng" : null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract MegaCloud direct link from {Url}", embedUrl);
            return null;
        }
    }

    /// <summary>
    /// Parses the embed page's <c>TRACKS</c> array for a sidecar subtitle file, preferring the
    /// entry marked <c>default</c>. Returns <c>null</c> when the array is empty (the common case
    /// so far — this server appears to hardsub instead), matching how <see cref="MegaplayExtractor"/>
    /// handles its own optional "tracks" field.
    /// </summary>
    private static string? ExtractSubtitleUrl(string html)
    {
        var arrMatch = TracksVarPattern.Match(html);
        if (!arrMatch.Success)
        {
            return null;
        }

        string? firstFile = null;
        foreach (Match entry in TrackEntryPattern.Matches(arrMatch.Groups["arr"].Value))
        {
            var fileMatch = TrackFilePattern.Match(entry.Value);
            if (!fileMatch.Success || string.IsNullOrEmpty(fileMatch.Groups["file"].Value))
            {
                continue;
            }

            firstFile ??= fileMatch.Groups["file"].Value;

            if (TrackDefaultPattern.IsMatch(entry.Value))
            {
                return fileMatch.Groups["file"].Value;
            }
        }

        return firstFile;
    }

    /// <summary>
    /// Resolves the master playlist to a single variant's media playlist so ffmpeg's automatic
    /// stream selection doesn't get confused by the extra "program" the I-FRAME stream adds
    /// (same failure mode documented on <see cref="MegaplayExtractor.ResolveBestVariantAsync"/>).
    /// </summary>
    private async Task<string> ResolveBestVariantAsync(string masterPlaylistUrl, CancellationToken cancellationToken)
    {
        try
        {
            var playlist = await _httpClient.GetStringAsync(new Uri(masterPlaylistUrl), cancellationToken).ConfigureAwait(false);

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
            _logger.LogDebug("MegaCloud resolved master playlist to variant (bandwidth {Bandwidth}): {Url}", bestBandwidth, resolvedUri);
            return resolvedUri.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve MegaCloud master playlist variant, falling back to master URL: {Url}", masterPlaylistUrl);
            return masterPlaylistUrl;
        }
    }
}
