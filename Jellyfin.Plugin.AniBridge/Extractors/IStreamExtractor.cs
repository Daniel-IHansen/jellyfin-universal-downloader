using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AniBridge.Extractors;

/// <summary>
/// Interface for stream extractors that resolve provider embed URLs to direct video links.
/// </summary>
public interface IStreamExtractor
{
    /// <summary>
    /// Gets the provider name this extractor handles.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Gets the Referer header value ffmpeg must send when fetching the resolved stream URL
    /// (and its HLS segments), if the provider's CDN checks it. Most providers don't need this;
    /// returns <c>null</c> by default.
    /// </summary>
    string? RequiredReferer => null;

    /// <summary>
    /// Extracts the direct video link from a provider embed URL.
    /// </summary>
    /// <param name="embedUrl">The provider embed URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The direct video URL (HLS/MP4), or null if extraction fails.</returns>
    Task<string?> GetDirectLinkAsync(string embedUrl, CancellationToken cancellationToken = default);
}
