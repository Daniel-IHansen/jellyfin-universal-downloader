using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AniBridge.Configuration;

/// <summary>
/// Plugin configuration for AniBridge Downloader. Per-site settings live in <see cref="Sites"/>
/// (a flat list, not a dictionary, so it round-trips through Jellyfin's XML serializer) —
/// adding a new site adapter only requires registering it in <c>PluginServiceRegistrator</c>;
/// a default config entry is created for it automatically on first access.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ── General settings ─────────────────────────────────────────

    /// <summary>
    /// Gets or sets the maximum concurrent downloads.
    /// </summary>
    public int MaxConcurrentDownloads { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum retry attempts for failed downloads.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Gets or sets whether to automatically scan the Jellyfin library
    /// when a download completes.
    /// </summary>
    public bool AutoScanLibrary { get; set; } = true;

    /// <summary>
    /// Gets or sets whether non-admin users can access the plugin UI.
    /// Requires the File Transformation plugin and a server restart.
    /// </summary>
    public bool EnableNonAdminAccess { get; set; } = false;

    /// <summary>
    /// Gets or sets whether maintenance mode is enabled.
    /// When enabled, new downloads are blocked and a message is displayed to users.
    /// Existing queued/active downloads continue to completion.
    /// </summary>
    public bool MaintenanceMode { get; set; } = false;

    /// <summary>
    /// Gets or sets the message displayed when maintenance mode is active.
    /// </summary>
    public string MaintenanceMessage { get; set; } = "The downloader is currently under maintenance and does not accept new downloads at this time.";

    /// <summary>
    /// Gets or sets the HTTP proxy URL for all network requests (e.g. "http://proxy:8080" or "socks5://proxy:1080").
    /// Leave empty to not use a proxy.
    /// </summary>
    public string ProxyUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the dedicated download path for movies across all sources.
    /// Leave empty to use the normal per-site/per-language download paths.
    /// </summary>
    public string MovieDownloadPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether to fall back to the other English track (Sub &lt;-&gt; Dub) when the
    /// requested one isn't available for a given episode/provider.
    /// </summary>
    public bool EnableLanguageFallback { get; set; } = true;

    // ── Per-site configs ─────────────────────────────────────────

    /// <summary>
    /// Gets or sets the per-site downloader configuration entries. Kept as a list (rather than
    /// a dictionary) so it serializes cleanly via Jellyfin's XML config persistence.
    /// </summary>
    public List<SiteConfigEntry> Sites { get; set; } = new()
    {
        new SiteConfigEntry
        {
            Source = "aniworld",
            Config = new SiteDownloaderConfig { Enabled = true, PreferredProvider = "Vidmoly", FallbackProvider = "VOE" },
        },
        new SiteConfigEntry
        {
            Source = "sto",
            Config = new SiteDownloaderConfig { Enabled = true, PreferredProvider = "VOE" },
        },
        new SiteConfigEntry
        {
            Source = "anikoto",
            Config = new SiteDownloaderConfig { Enabled = true, PreferredProvider = "Anikoto" },
        },
        new SiteConfigEntry
        {
            Source = "animenexus",
            Config = new SiteDownloaderConfig { Enabled = false, PreferredProvider = "AnimeNexus" },
        },
    };

    /// <summary>
    /// Resolves the effective download path for a given source and language ("sub" or "dub").
    /// Checks the per-language path first, then falls back to the site's general path.
    /// </summary>
    public string GetDownloadPath(string source, string? language = null, bool isMovie = false)
    {
        if (isMovie && !string.IsNullOrEmpty(MovieDownloadPath))
        {
            return MovieDownloadPath;
        }

        var siteConfig = GetSiteConfig(source);

        if (!string.IsNullOrEmpty(language))
        {
            var langPath = siteConfig.GetLanguagePath(language);
            if (!string.IsNullOrEmpty(langPath))
            {
                return langPath;
            }
        }

        return siteConfig.DownloadPath;
    }

    /// <summary>
    /// Resolves the effective preferred language ("sub" or "dub") for a given source.
    /// </summary>
    public string GetPreferredLanguage(string source)
    {
        var siteConfig = GetSiteConfig(source);
        return string.IsNullOrEmpty(siteConfig.PreferredLanguage) ? "sub" : siteConfig.PreferredLanguage;
    }

    /// <summary>
    /// Resolves the effective preferred provider for a given source.
    /// </summary>
    public string GetPreferredProvider(string source)
    {
        return GetSiteConfig(source).PreferredProvider;
    }

    /// <summary>
    /// Resolves the effective fallback provider for a given source.
    /// </summary>
    public string GetFallbackProvider(string source)
    {
        return GetSiteConfig(source).FallbackProvider;
    }

    /// <summary>
    /// Gets the site-specific config for a source, creating (and persisting) a default entry
    /// the first time an unknown source is requested. This is what lets a newly-registered
    /// site adapter "just work" without any config-file migration.
    /// </summary>
    public SiteDownloaderConfig GetSiteConfig(string source)
    {
        var entry = Sites.FirstOrDefault(s => string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            return entry.Config;
        }

        entry = new SiteConfigEntry { Source = source ?? string.Empty, Config = new SiteDownloaderConfig() };
        Sites.Add(entry);
        return entry.Config;
    }
}

/// <summary>
/// Associates a source identifier (e.g. "aniworld") with its <see cref="SiteDownloaderConfig"/>.
/// </summary>
public class SiteConfigEntry
{
    /// <summary>Gets or sets the source identifier, matching <c>StreamingSiteService.SourceName</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the configuration for this site.</summary>
    public SiteDownloaderConfig Config { get; set; } = new();
}

/// <summary>
/// Per-site downloader configuration.
/// </summary>
public class SiteDownloaderConfig
{
    /// <summary>Gets or sets whether this downloader is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a custom base URL override for this site (e.g. an alternate mirror domain). Leave empty to use the site's default.</summary>
    public string CustomBaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the general download path. Empty = use per-language paths only.</summary>
    public string DownloadPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the download path for English Sub.</summary>
    public string DownloadPathSub { get; set; } = string.Empty;

    /// <summary>Gets or sets the download path for English Dub.</summary>
    public string DownloadPathDub { get; set; } = string.Empty;

    /// <summary>Looks up the per-language download paths by canonical key ("sub"/"dub"). Only non-empty entries are included.</summary>
    [XmlIgnore]
    public Dictionary<string, string> DownloadPaths
    {
        get
        {
            var dict = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(DownloadPathSub))
            {
                dict["sub"] = DownloadPathSub;
            }

            if (!string.IsNullOrEmpty(DownloadPathDub))
            {
                dict["dub"] = DownloadPathDub;
            }

            return dict;
        }
    }

    /// <summary>Gets the per-language download path for a specific canonical language key ("sub"/"dub").</summary>
    public string GetLanguagePath(string langKey)
    {
        return langKey switch
        {
            "sub" => DownloadPathSub,
            "dub" => DownloadPathDub,
            _ => string.Empty,
        };
    }

    /// <summary>Gets or sets the preferred language ("sub" or "dub"). Empty = defaults to "sub".</summary>
    public string PreferredLanguage { get; set; } = string.Empty;

    /// <summary>Gets or sets the preferred provider.</summary>
    public string PreferredProvider { get; set; } = string.Empty;

    /// <summary>Gets or sets the fallback provider.</summary>
    public string FallbackProvider { get; set; } = string.Empty;
}
