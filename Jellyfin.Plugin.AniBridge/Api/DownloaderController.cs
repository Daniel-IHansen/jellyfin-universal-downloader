using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniBridge.Helpers;
using Jellyfin.Plugin.AniBridge.Services;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniBridge.Api;

/// <summary>
/// REST API controller for AniBridge Downloader. Site-agnostic: every endpoint that takes a
/// <c>source</c> parameter resolves it against the set of registered
/// <see cref="StreamingSiteService"/> adapters, so adding a new site (see
/// <see cref="PluginServiceRegistrator"/>) requires no changes here.
/// </summary>
[ApiController]
[Route("AniBridge")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class DownloaderController : ControllerBase
{
    private readonly Dictionary<string, StreamingSiteService> _services;
    private readonly DownloadService _downloadService;
    private readonly DownloadHistoryService _historyService;
    private readonly IServerConfigurationManager _configManager;
    private readonly ILogger<DownloaderController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloaderController"/> class.
    /// </summary>
    public DownloaderController(
        IEnumerable<StreamingSiteService> services,
        DownloadService downloadService,
        DownloadHistoryService historyService,
        IServerConfigurationManager configManager,
        ILogger<DownloaderController> logger)
    {
        _services = services.ToDictionary(s => s.SourceName, StringComparer.OrdinalIgnoreCase);
        _downloadService = downloadService;
        _historyService = historyService;
        _configManager = configManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the correct streaming service for a source, falling back to the first registered
    /// site if the source is unknown or unspecified.
    /// </summary>
    private StreamingSiteService GetService(string? source)
    {
        if (!string.IsNullOrEmpty(source) && _services.TryGetValue(source, out var service))
        {
            return service;
        }

        return _services.TryGetValue("anikoto", out var defaultService)
            ? defaultService
            : _services.Values.First();
    }

    /// <summary>
    /// Resolves the source from an explicit parameter or URL auto-detection.
    /// </summary>
    private string ResolveSource(string? explicitSource, string? url = null)
    {
        if (!string.IsNullOrEmpty(explicitSource))
        {
            return explicitSource;
        }

        if (!string.IsNullOrEmpty(url))
        {
            return UrlValidator.DetectSource(url, _services.Values);
        }

        return "anikoto";
    }

    private bool IsValidUrl(string url) => UrlValidator.IsValidUrl(url, _services.Values);

    // ── Non-admin access endpoints ────────────────────────────────────

    /// <summary>
    /// Serves the injection script for non-admin sidebar access.
    /// </summary>
    [HttpGet("InjectionScript")]
    [AllowAnonymous]
    public ActionResult GetInjectionScript()
    {
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.AniBridge.Web.injection.js");
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, "application/javascript");
    }

    /// <summary>
    /// Serves the main plugin page HTML for non-admin rendering.
    /// </summary>
    [HttpGet("Page")]
    [AllowAnonymous]
    public ActionResult GetPage()
    {
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.AniBridge.Web.aniworld.html");
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, "text/html");
    }

    /// <summary>
    /// Serves the main plugin page JavaScript for non-admin rendering.
    /// </summary>
    [HttpGet("PageScript")]
    [AllowAnonymous]
    public ActionResult GetPageScript()
    {
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.AniBridge.Web.aniworld.js");
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, "application/javascript");
    }

    /// <summary>
    /// Returns which sources are enabled/available in the configuration, plus display metadata.
    /// </summary>
    [HttpGet("EnabledSources")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetEnabledSources()
    {
        var config = Plugin.Instance?.Configuration;

        var sources = _services.Values.Select(s => new
        {
            source = s.SourceName,
            displayName = s.DisplayName,
            enabled = config?.GetSiteConfig(s.SourceName).Enabled ?? false,
            experimental = s.IsExperimental,
        }).ToList();

        return Ok(new
        {
            sources,
            maintenanceMode = config?.MaintenanceMode ?? false,
            maintenanceMessage = config?.MaintenanceMessage ?? string.Empty,
        });
    }

    // ── Search & Browse ─────────────────────────────────────────────

    /// <summary>
    /// Search for series. Use source=all (or omit) to query every enabled site, or a specific
    /// source name (e.g. source=anikoto) for one site.
    /// </summary>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<SearchResult>>> Search(
        [Required] string query,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;

        if (string.Equals(source, "all", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(source))
        {
            var results = new List<SearchResult>();

            foreach (var service in _services.Values)
            {
                if (config?.GetSiteConfig(service.SourceName).Enabled != true)
                {
                    continue;
                }

                try
                {
                    var siteResults = await service.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                    results.AddRange(siteResults);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Source} search failed for query: {Query}", service.SourceName, query);
                }
            }

            return Ok(results);
        }

        var singleResults = await GetService(source).SearchAsync(query, cancellationToken).ConfigureAwait(false);
        return Ok(singleResults);
    }

    /// <summary>
    /// Get series information.
    /// </summary>
    [HttpGet("Series")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SeriesInfo>> GetSeries(
        [Required] string url,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidUrl(url))
        {
            return BadRequest("Invalid URL. Only pages from enabled streaming sites are accepted.");
        }

        var resolvedSource = ResolveSource(source, url);
        var result = await GetService(resolvedSource).GetSeriesInfoAsync(url, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Get episodes for a season.
    /// </summary>
    [HttpGet("Episodes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<EpisodeRef>>> GetEpisodes(
        [Required] string url,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidUrl(url))
        {
            return BadRequest("Invalid URL. Only pages from enabled streaming sites are accepted.");
        }

        var resolvedSource = ResolveSource(source, url);
        var result = await GetService(resolvedSource).GetEpisodesAsync(url, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Get episode details (provider links, English Sub/Dub only).
    /// </summary>
    [HttpGet("Episode")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EpisodeDetails>> GetEpisodeDetails(
        [Required] string url,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidUrl(url))
        {
            return BadRequest("Invalid URL. Only pages from enabled streaming sites are accepted.");
        }

        var resolvedSource = ResolveSource(source, url);
        var result = await GetService(resolvedSource).GetEpisodeDetailsAsync(url, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Get popular series. Use source parameter to select site.
    /// </summary>
    [HttpGet("Popular")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<BrowseItem>>> GetPopular(
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedSource = ResolveSource(source);
        var result = await GetService(resolvedSource).GetPopularAsync(cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Get newly added series. Use source parameter to select site.
    /// </summary>
    [HttpGet("New")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<BrowseItem>>> GetNewReleases(
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedSource = ResolveSource(source);
        var result = await GetService(resolvedSource).GetNewReleasesAsync(cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    // ── Downloads ───────────────────────────────────────────────────

    /// <summary>
    /// Start downloading an episode.
    /// </summary>
    [HttpPost("Download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DownloadTask>> StartDownload(
        [FromBody] DownloadRequest request,
        CancellationToken cancellationToken)
    {
        var maintenanceConfig = Plugin.Instance?.Configuration;
        if (maintenanceConfig?.MaintenanceMode == true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                maintenanceConfig.MaintenanceMessage);
        }

        if (string.IsNullOrEmpty(request.EpisodeUrl))
        {
            return BadRequest("Episode URL is required");
        }

        if (!IsValidUrl(request.EpisodeUrl))
        {
            return BadRequest("Invalid URL. Only pages from enabled streaming sites are accepted.");
        }

        var source = ResolveSource(request.Source, request.EpisodeUrl);
        var config = Plugin.Instance?.Configuration;
        var language = request.LanguageKey ?? config?.GetPreferredLanguage(source) ?? "sub";

        var isMovieRequest = PathHelper.MovieFromUrl.IsMatch(request.EpisodeUrl);
        var basePath = config?.GetDownloadPath(source, language, isMovieRequest) ?? string.Empty;

        if (string.IsNullOrEmpty(basePath))
        {
            return BadRequest("No download path configured. Please set a download path in the plugin settings.");
        }

        var provider = request.Provider ?? config?.GetPreferredProvider(source) ?? string.Empty;
        var seriesTitle = request.SeriesTitle ?? "Unknown";

        var outputPath = PathHelper.BuildOutputPath(basePath, seriesTitle, request.EpisodeUrl);

        // Check if already downloaded (duplicate detection)
        if (!request.Force)
        {
            var (checkSeason, checkEpisode) = PathHelper.ParseSeasonEpisode(request.EpisodeUrl);

            if (_downloadService.IsAlreadyDownloaded(seriesTitle, checkSeason, checkEpisode, language))
            {
                return BadRequest("This episode has already been downloaded in this language.");
            }
        }

        var username = User.FindFirst(ClaimTypes.Name)?.Value ?? User.Identity?.Name;

        var taskId = await _downloadService.StartDownloadAsync(
            request.EpisodeUrl,
            language,
            provider,
            outputPath,
            seriesTitle,
            source,
            cancellationToken,
            username,
            request.Priority).ConfigureAwait(false);

        if (taskId == null)
        {
            return BadRequest("This episode is already queued or downloading.");
        }

        var task = _downloadService.GetDownload(taskId);
        return Ok(task);
    }

    /// <summary>
    /// Start downloading all episodes in a season (batch download).
    /// </summary>
    [HttpPost("DownloadSeason")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<List<DownloadTask>>> DownloadSeason(
        [FromBody] BatchDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var maintenanceConfig = Plugin.Instance?.Configuration;
        if (maintenanceConfig?.MaintenanceMode == true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                maintenanceConfig.MaintenanceMessage);
        }

        if (string.IsNullOrEmpty(request.SeasonUrl))
        {
            return BadRequest("Season URL is required");
        }

        if (!IsValidUrl(request.SeasonUrl))
        {
            return BadRequest("Invalid URL. Only pages from enabled streaming sites are accepted.");
        }

        var source = ResolveSource(request.Source, request.SeasonUrl);
        var config = Plugin.Instance?.Configuration;
        var language = request.LanguageKey ?? config?.GetPreferredLanguage(source) ?? "sub";

        var basePath = config?.GetDownloadPath(source, language) ?? string.Empty;
        var movieBasePath = config?.GetDownloadPath(source, language, true) ?? string.Empty;

        var provider = request.Provider ?? config?.GetPreferredProvider(source) ?? string.Empty;
        var seriesTitle = request.SeriesTitle ?? "Unknown";

        var service = GetService(source);
        var episodes = await service.GetEpisodesAsync(request.SeasonUrl, cancellationToken).ConfigureAwait(false);

        if (episodes.Count == 0)
        {
            return BadRequest("No episodes found for this season.");
        }

        var hasRegularEpisodes = episodes.Any(e => !e.IsMovie);
        var hasMovieEpisodes = episodes.Any(e => e.IsMovie);

        if (hasRegularEpisodes && string.IsNullOrEmpty(basePath))
        {
            return BadRequest("No download path configured. Please set a download path in the plugin settings.");
        }

        if (hasMovieEpisodes && string.IsNullOrEmpty(movieBasePath))
        {
            return BadRequest("No movie download path configured. Please set a path in the plugin settings.");
        }

        var tasks = new List<DownloadTask>();
        var username = User.FindFirst(ClaimTypes.Name)?.Value ?? User.Identity?.Name;

        foreach (var ep in episodes)
        {
            var effectiveBasePath = ep.IsMovie
                ? movieBasePath
                : basePath;

            if (string.IsNullOrEmpty(effectiveBasePath))
            {
                continue;
            }

            var outputPath = PathHelper.BuildOutputPath(effectiveBasePath, seriesTitle, ep.Url);

            var (checkSeason, checkEpisode) = PathHelper.ParseSeasonEpisode(ep.Url);

            if (!request.Force && _downloadService.IsAlreadyDownloaded(seriesTitle, checkSeason, checkEpisode, language))
            {
                continue;
            }

            var taskId = await _downloadService.StartDownloadAsync(
                ep.Url,
                language,
                provider,
                outputPath,
                seriesTitle,
                source,
                cancellationToken,
                username,
                request.Priority).ConfigureAwait(false);

            if (taskId == null) continue;

            var task = _downloadService.GetDownload(taskId);
            if (task != null)
            {
                tasks.Add(task);
            }
        }

        return Ok(tasks);
    }

    /// <summary>
    /// Start downloading all episodes across all seasons of a series.
    /// </summary>
    [HttpPost("DownloadAll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<object>> DownloadAllSeasons(
        [FromBody] FullSeriesDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var maintenanceConfig = Plugin.Instance?.Configuration;
        if (maintenanceConfig?.MaintenanceMode == true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                maintenanceConfig.MaintenanceMessage);
        }

        if (string.IsNullOrEmpty(request.SeriesUrl))
        {
            return BadRequest("Series URL is required");
        }

        if (!IsValidUrl(request.SeriesUrl))
        {
            return BadRequest("Invalid URL. Only pages from enabled streaming sites are accepted.");
        }

        var source = ResolveSource(request.Source, request.SeriesUrl);
        var config = Plugin.Instance?.Configuration;
        var language = request.LanguageKey ?? config?.GetPreferredLanguage(source) ?? "sub";

        var basePath = config?.GetDownloadPath(source, language) ?? string.Empty;
        var movieBasePath = config?.GetDownloadPath(source, language, true) ?? string.Empty;

        if (string.IsNullOrEmpty(basePath))
        {
            return BadRequest("No download path configured. Please set a download path in the plugin settings.");
        }

        var provider = request.Provider ?? config?.GetPreferredProvider(source) ?? string.Empty;

        var service = GetService(source);
        var seriesInfo = await service.GetSeriesInfoAsync(request.SeriesUrl, cancellationToken).ConfigureAwait(false);
        var seriesTitle = request.SeriesTitle ?? seriesInfo.Title ?? "Unknown";

        if (seriesInfo.Seasons == null || seriesInfo.Seasons.Count == 0)
        {
            return BadRequest("No seasons found for this series.");
        }

        var allTasks = new List<DownloadTask>();
        var skippedCount = 0;
        var username = User.FindFirst(ClaimTypes.Name)?.Value ?? User.Identity?.Name;

        foreach (var season in seriesInfo.Seasons)
        {
            var episodes = await service.GetEpisodesAsync(season.Url, cancellationToken).ConfigureAwait(false);

            foreach (var ep in episodes)
            {
                var effectiveBasePath = ep.IsMovie
                    ? movieBasePath
                    : basePath;

                var outputPath = PathHelper.BuildOutputPath(effectiveBasePath, seriesTitle, ep.Url);

                var (checkSeason, checkEpisode) = PathHelper.ParseSeasonEpisode(ep.Url);

                if (!request.Force && _downloadService.IsAlreadyDownloaded(seriesTitle, checkSeason, checkEpisode, language))
                {
                    skippedCount++;
                    continue;
                }

                var taskId = await _downloadService.StartDownloadAsync(
                    ep.Url,
                    language,
                    provider,
                    outputPath,
                    seriesTitle,
                    source,
                    cancellationToken,
                    username,
                    request.Priority).ConfigureAwait(false);

                if (taskId == null) { skippedCount++; continue; }

                var task = _downloadService.GetDownload(taskId);
                if (task != null)
                {
                    allTasks.Add(task);
                }
            }
        }

        // Also handle movies if they exist
        if (seriesInfo.HasMovies)
        {
            var movieUrl = request.SeriesUrl.TrimEnd('/') + "/filme";
            var movies = await service.GetEpisodesAsync(movieUrl, cancellationToken).ConfigureAwait(false);

            foreach (var ep in movies)
            {
                var outputPath = PathHelper.BuildOutputPath(movieBasePath, seriesTitle, ep.Url);

                var (movieSeason, movieEpisode) = PathHelper.ParseSeasonEpisode(ep.Url);

                if (!request.Force && _downloadService.IsAlreadyDownloaded(seriesTitle, movieSeason, movieEpisode, language))
                {
                    skippedCount++;
                    continue;
                }

                var taskId = await _downloadService.StartDownloadAsync(
                    ep.Url,
                    language,
                    provider,
                    outputPath,
                    seriesTitle,
                    source,
                    cancellationToken,
                    username: username,
                    priority: request.Priority).ConfigureAwait(false);

                if (taskId == null) { skippedCount++; continue; }

                var task = _downloadService.GetDownload(taskId);
                if (task != null)
                {
                    allTasks.Add(task);
                }
            }
        }

        return Ok(new
        {
            queued = allTasks.Count,
            skipped = skippedCount,
            seasons = seriesInfo.Seasons.Count,
            tasks = allTasks
        });
    }

    /// <summary>
    /// Get all active/recent downloads (in-memory).
    /// </summary>
    [HttpGet("Downloads")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<DownloadTask>> GetDownloads()
    {
        return Ok(_downloadService.GetActiveDownloads());
    }

    /// <summary>
    /// Get a specific download task.
    /// </summary>
    [HttpGet("Downloads/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<DownloadTask> GetDownload(string id)
    {
        var task = _downloadService.GetDownload(id);
        if (task == null)
        {
            return NotFound();
        }

        return Ok(task);
    }

    /// <summary>
    /// Cancel a download.
    /// </summary>
    [HttpDelete("Downloads/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult CancelDownload(string id)
    {
        if (_downloadService.CancelDownload(id))
        {
            return Ok(new { success = true });
        }

        return NotFound();
    }

    /// <summary>
    /// Clear completed/failed downloads from the active list.
    /// </summary>
    [HttpPost("Downloads/Clear")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ClearCompleted()
    {
        var cleared = _downloadService.ClearCompleted();
        return Ok(new { cleared });
    }

    /// <summary>
    /// Retry a failed download.
    /// </summary>
    [HttpPost("Downloads/{id}/Retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RetryDownload(string id)
    {
        if (_downloadService.RetryDownload(id))
        {
            return Ok(new { success = true });
        }

        return NotFound(new { error = "Download not found or not in failed state" });
    }

    // ── History & Stats ─────────────────────────────────────────────

    /// <summary>
    /// Get persistent download history from the database.
    /// </summary>
    [HttpGet("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<DownloadHistoryRecord>> GetHistory(
        int limit = 50,
        int offset = 0,
        string? status = null,
        string? series = null)
    {
        var records = _historyService.GetHistory(limit, offset, status, series);
        return Ok(records);
    }

    /// <summary>
    /// Get download statistics.
    /// </summary>
    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DownloadStats> GetStats()
    {
        var stats = _historyService.GetStats();
        return Ok(stats);
    }

    /// <summary>
    /// Get the list of unique series that have been downloaded.
    /// </summary>
    [HttpGet("Series/Downloaded")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<string>> GetDownloadedSeries()
    {
        var series = _historyService.GetDownloadedSeries();
        return Ok(series);
    }

    /// <summary>
    /// Check if an episode has already been downloaded.
    /// </summary>
    [HttpGet("IsDownloaded")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<object> CheckIsDownloaded([Required] string url, [Required] string title, string? language = null)
    {
        if (!IsValidUrl(url))
        {
            return BadRequest("Invalid URL. Only pages from enabled streaming sites are accepted.");
        }

        var (season, episode) = PathHelper.ParseSeasonEpisode(url);
        var completedLanguages = _historyService.GetCompletedLanguages(title, season, episode);
        return Ok(new { downloaded = completedLanguages.Count > 0, languages = completedLanguages, url });
    }

    /// <summary>
    /// Serves the site logo/badge SVG for a source, if one is embedded for it.
    /// </summary>
    [HttpGet("SiteLogo/{source}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [AllowAnonymous]
    public ActionResult GetSiteLogo(string source)
    {
        var resourceName = $"Jellyfin.Plugin.AniBridge.Web.{source.ToLowerInvariant()}.svg";
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, "image/svg+xml");
    }

    /// <summary>
    /// Delete a specific history record.
    /// </summary>
    [HttpDelete("History/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteHistoryRecord(string id)
    {
        if (_historyService.DeleteRecord(id))
        {
            return Ok(new { success = true });
        }

        return NotFound();
    }

    /// <summary>
    /// Clean up old history records.
    /// </summary>
    [HttpPost("History/Cleanup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult CleanupHistory(int days = 90)
    {
        var removed = _historyService.CleanupOld(days);
        return Ok(new { removed });
    }
}

/// <summary>
/// Download request model.
/// </summary>
public class DownloadRequest
{
    /// <summary>Gets or sets the episode URL.</summary>
    public string EpisodeUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the language key ("sub" or "dub").</summary>
    public string? LanguageKey { get; set; }

    /// <summary>Gets or sets the provider.</summary>
    public string? Provider { get; set; }

    /// <summary>Gets or sets the series title for file naming.</summary>
    public string? SeriesTitle { get; set; }

    /// <summary>Gets or sets whether to force re-download even if already downloaded.</summary>
    public bool Force { get; set; }

    /// <summary>Gets or sets the source site identifier.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets whether this is a priority download (added to front of queue).</summary>
    public bool Priority { get; set; }
}

/// <summary>
/// Batch download request for an entire season.
/// </summary>
public class BatchDownloadRequest
{
    /// <summary>Gets or sets the season URL.</summary>
    public string SeasonUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the language key ("sub" or "dub").</summary>
    public string? LanguageKey { get; set; }

    /// <summary>Gets or sets the provider.</summary>
    public string? Provider { get; set; }

    /// <summary>Gets or sets the series title for file naming.</summary>
    public string? SeriesTitle { get; set; }

    /// <summary>Gets or sets the source site identifier.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets whether this is a priority download (added to front of queue).</summary>
    public bool Priority { get; set; }

    /// <summary>Gets or sets whether to force re-download of episodes already marked as downloaded.</summary>
    public bool Force { get; set; }
}

/// <summary>
/// Full series download request — downloads all seasons.
/// </summary>
public class FullSeriesDownloadRequest
{
    /// <summary>Gets or sets the series URL.</summary>
    public string SeriesUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the language key ("sub" or "dub").</summary>
    public string? LanguageKey { get; set; }

    /// <summary>Gets or sets the provider.</summary>
    public string? Provider { get; set; }

    /// <summary>Gets or sets the series title for file naming.</summary>
    public string? SeriesTitle { get; set; }

    /// <summary>Gets or sets the source site identifier.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets whether this is a priority download (added to front of queue).</summary>
    public bool Priority { get; set; }

    /// <summary>Gets or sets whether to force re-download of episodes already marked as downloaded.</summary>
    public bool Force { get; set; }
}
