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
    /// Extracts the direct video link (and, if the provider exposes one separately, a subtitle
    /// track) from a provider embed URL.
    /// </summary>
    /// <param name="embedUrl">The provider embed URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved stream info, or null if extraction fails.</returns>
    Task<StreamResolveResult?> GetDirectLinkAsync(string embedUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of resolving a provider embed URL to a playable stream.
/// </summary>
public class StreamResolveResult
{
    /// <summary>Gets the direct video URL (HLS/MP4).</summary>
    public required string VideoUrl { get; init; }

    /// <summary>Gets the direct URL of a subtitle file the provider serves separately from the video, if any.</summary>
    public string? SubtitleUrl { get; init; }

    /// <summary>Gets the ISO 639-2 language code for <see cref="SubtitleUrl"/> (e.g. "eng").</summary>
    public string? SubtitleLanguage { get; init; }
}
