using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniBridge.Extractors;

/// <summary>
/// Extracts direct video URLs from Vidmoly embeds.
/// </summary>
public class VidmolyExtractor : IStreamExtractor
{
    private static readonly Regex SourcePattern = new(
        @"sources\s*:\s*\[\s*\{[^}]*file:\s*['""](?<url>[^'""]+)['""]",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HlsPattern = new(
        @"['""](?<url>https?://[^'""]+\.m3u8[^'""]*)['""]",
        RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<VidmolyExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VidmolyExtractor"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger instance.</param>
    public VidmolyExtractor(IHttpClientFactory httpClientFactory, ILogger<VidmolyExtractor> logger)
    {
        _httpClient = httpClientFactory.CreateClient("AniWorld");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => "Vidmoly";

    /// <inheritdoc />
    public async Task<string?> GetDirectLinkAsync(string embedUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Extracting Vidmoly direct link from: {Url}", embedUrl);

            var response = await _httpClient.GetAsync(embedUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Try sources array pattern
            var match = SourcePattern.Match(html);
            if (match.Success)
            {
                return match.Groups["url"].Value;
            }

            // Fallback to any m3u8 URL
            match = HlsPattern.Match(html);
            if (match.Success)
            {
                return match.Groups["url"].Value;
            }

            _logger.LogWarning("No video source found in Vidmoly page");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract Vidmoly direct link from {Url}", embedUrl);
            return null;
        }
    }
}
