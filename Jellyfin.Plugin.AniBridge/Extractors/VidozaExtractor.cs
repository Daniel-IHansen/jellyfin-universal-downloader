using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniBridge.Extractors;

/// <summary>
/// Extracts direct video URLs from Vidoza embeds.
/// </summary>
public class VidozaExtractor : IStreamExtractor
{
    private static readonly Regex SourcePattern = new(
        @"sourcesCode\s*:\s*\[\s*\{[^}]*src:\s*['""](?<url>[^'""]+)['""]",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex SourceAltPattern = new(
        @"<source\s+src=['""](?<url>https?://[^'""]+\.mp4[^'""]*)['""]",
        RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<VidozaExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VidozaExtractor"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger instance.</param>
    public VidozaExtractor(IHttpClientFactory httpClientFactory, ILogger<VidozaExtractor> logger)
    {
        _httpClient = httpClientFactory.CreateClient("AniWorld");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => "Vidoza";

    /// <inheritdoc />
    public async Task<string?> GetDirectLinkAsync(string embedUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Extracting Vidoza direct link from: {Url}", embedUrl);

            var response = await _httpClient.GetAsync(embedUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Try sourcesCode pattern first
            var match = SourcePattern.Match(html);
            if (match.Success)
            {
                return match.Groups["url"].Value;
            }

            // Fallback to <source> tag
            match = SourceAltPattern.Match(html);
            if (match.Success)
            {
                return match.Groups["url"].Value;
            }

            _logger.LogWarning("No video source found in Vidoza page");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract Vidoza direct link from {Url}", embedUrl);
            return null;
        }
    }
}
