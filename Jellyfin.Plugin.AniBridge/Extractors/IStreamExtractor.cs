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
    /// Gets whether <see cref="GetDirectLinkAsync"/> resolves to a single progressive file
    /// (e.g. a direct MP4) rather than an HLS playlist. When <c>true</c> and
    /// <see cref="RequiredReferer"/> is set, the referer is passed straight to ffmpeg via its own
    /// "-headers" flag and the "aac_adtstoasc" bitstream filter (needed only for TS-sourced HLS
    /// audio) is skipped. When <c>false</c> (default), a <see cref="RequiredReferer"/> instead
    /// triggers pre-downloading the HLS segments to a local file — see the comment in
    /// <c>DownloadService.DownloadWithFfmpegAsync</c> for why that workaround exists for
    /// providers like Megaplay.
    /// </summary>
    bool IsProgressiveStream => false;

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
