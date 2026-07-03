using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniBridge.Extractors;

/// <summary>
/// Extracts direct video URLs from DoodStream-family embeds, used by AniWatch's "DGHG" server
/// (currently served from <c>playmogo.com</c>, a whitelabelled DoodStream instance — the exact
/// hostname has rotated before and may again, but the player markup and API below are the
/// standard DoodStream template shared across all of its mirrors/clones).
/// <br/><br/>
/// Verified live against a real embed page: the page's inline script calls
/// <c>$.get('/pass_md5/{hash}/{token}', ...)</c>; that endpoint's plain-text response is a base
/// CDN URL which the page then completes with <c>makePlay()</c> — a random 10-character
/// alphanumeric string plus <c>?token={token}&amp;expiry={nowMs}</c>. The CDN redirect-loops
/// without a Referer matching the embed's own origin, so that's required on every request here
/// (and passed through to ffmpeg for the final fetch via <see cref="RequiredReferer"/>).
/// </summary>
public class DoodStreamExtractor : IStreamExtractor
{
    private const string RefererOrigin = "https://playmogo.com/";
    private const string RandomCharPool = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private static readonly Regex PassMd5Pattern = new(
        @"/pass_md5/(?<hash>[^/'""]+)/(?<token>[a-zA-Z0-9]+)",
        RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<DoodStreamExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DoodStreamExtractor"/> class.
    /// </summary>
    public DoodStreamExtractor(IHttpClientFactory httpClientFactory, ILogger<DoodStreamExtractor> logger)
    {
        _httpClient = httpClientFactory.CreateClient("AniWatch");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => "DoodStream";

    /// <inheritdoc />
    public string? RequiredReferer => RefererOrigin;

    /// <inheritdoc />
    public bool IsProgressiveStream => true;

    /// <inheritdoc />
    public async Task<StreamResolveResult?> GetDirectLinkAsync(string embedUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Extracting DoodStream direct link from: {Url}", embedUrl);

            using var embedRequest = new HttpRequestMessage(HttpMethod.Get, embedUrl);
            embedRequest.Headers.Referrer = new Uri(RefererOrigin);
            var embedResponse = await _httpClient.SendAsync(embedRequest, cancellationToken).ConfigureAwait(false);
            embedResponse.EnsureSuccessStatusCode();
            var html = await embedResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var match = PassMd5Pattern.Match(html);
            if (!match.Success)
            {
                _logger.LogWarning("Could not find pass_md5 path on DoodStream embed page: {Url}", embedUrl);
                return null;
            }

            var hash = match.Groups["hash"].Value;
            var token = match.Groups["token"].Value;

            var origin = new Uri(embedUrl).GetLeftPart(UriPartial.Authority);
            var passMd5Url = $"{origin}/pass_md5/{hash}/{token}";

            using var passRequest = new HttpRequestMessage(HttpMethod.Get, passMd5Url);
            passRequest.Headers.Referrer = new Uri(embedUrl);
            var passResponse = await _httpClient.SendAsync(passRequest, cancellationToken).ConfigureAwait(false);
            passResponse.EnsureSuccessStatusCode();
            var baseUrl = (await passResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();

            if (string.IsNullOrEmpty(baseUrl) || baseUrl.Equals("RELOAD", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("DoodStream pass_md5 returned no usable base URL for {Url}", embedUrl);
                return null;
            }

            var videoUrl = $"{baseUrl}{GenerateRandomString(10)}?token={token}&expiry={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

            _logger.LogDebug("DoodStream source extracted for {Url}", embedUrl);
            return new StreamResolveResult { VideoUrl = videoUrl };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract DoodStream direct link from {Url}", embedUrl);
            return null;
        }
    }

    private static string GenerateRandomString(int length)
    {
        var chars = new char[length];
        Span<byte> buffer = stackalloc byte[length];
        RandomNumberGenerator.Fill(buffer);

        for (var i = 0; i < length; i++)
        {
            chars[i] = RandomCharPool[buffer[i] % RandomCharPool.Length];
        }

        return new string(chars);
    }
}
