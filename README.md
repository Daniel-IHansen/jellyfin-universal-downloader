# Jellyfin AniBridge Downloader

> **Note:** This is a renamed/reworked fork of the original *Jellyfin AniWorld Downloader* by SiroxCW, extended to support multiple streaming sites. The badge/release URLs below still point at the upstream repo layout as placeholders.

A Jellyfin plugin for searching and downloading anime and series from multiple streaming sites, directly inside Jellyfin's web interface. **English Sub/Dub only — no German.**

Series View| Search View
:---:|:---:
![Series View](screenshots/preview_anime.png) | ![Search View](screenshots/preview_search.png)

## Features

- **Search and browse** anime and series with cover art, popular titles, and new releases
- **Download** individual episodes, full seasons, or entire series
- **Two sites supported**, each a self-contained adapter — adding another site is a single new class, no controller/config changes needed:
  | Site | Content | Languages | Status |
  |------|---------|-----------|--------|
  | [anikoto.net](https://anikoto.net) | Anime | English Sub + Dub | Best-effort (built against a community-documented third-party API — anikoto.net itself blocks automated access) |
  | [aniwatch.one](https://aniwatch.one) | Anime | English Sub + Dub | Verified against live HTML. MegaCloud and DoodStream servers both have working extractors (MegaCloud preferred by default — DoodStream's anti-bot check can block a server's IP under sustained batch-download traffic); the site also offers a third, custom-player server per episode that isn't surfaced yet since no extractor decodes it |
- Anikoto resolves directly to a playable stream URL via its own API (its Megaplay-backed server needs a dedicated extractor to decode its embed page); AniWatch scrapes the embed URL straight out of the episode page HTML and hands it to the matching extractor (MegaCloud or DoodStream)
- **Download manager** with real-time progress, cancel, retry, and batch operations
- **Automatic retries** with exponential backoff, provider fallback, and optional Sub↔Dub language fallback
- **Auto library scan** so new episodes appear in Jellyfin immediately
- **Jellyfin-compatible naming**: `Series Name/Season 01/Series Name - S01E01 - Episode Title.mkv`
- Icons throughout the UI are rendered with [lucide.dev](https://lucide.dev) — no emoji

## Looking for more?

This plugin is a lightweight downloader built into Jellyfin for convenience. If you need a standalone tool with its own web UI, more configuration options, and additional features for aniworld.to specifically, check out [AniWorld-Downloader](https://github.com/phoenixthrush/AniWorld-Downloader) by phoenixthrush.

## Requirements

- Jellyfin **10.11.0** or newer
- **ffmpeg** (bundled with Jellyfin)
- **[File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)** plugin (optional, required for non-admin access)

## Installation

### Plugin Repository (recommended)

1. In Jellyfin, go to **Dashboard > Plugins > Repositories**
2. Add a new repository with this URL (replace with your own fork's raw manifest.json once published):
   ```
   https://raw.githubusercontent.com/<your-fork>/Jellyfin-AniWorld-Downloader/main/manifest.json
   ```
4. Go to **Catalog**, find **AniBridge Downloader**, and click **Install**
5. Restart Jellyfin

*If the plugin does not show up in the Catalog, restarting Jellyfin made it appear.*

Updates will show up automatically in the plugin catalog.

### Manual Install

1. Build or download the `.zip` (see [Build from Source](#build-from-source) below)
2. Extract it to your Jellyfin plugins directory:
   ```
   /var/lib/jellyfin/plugins/AniBridgeDownloader/
   ```
   The folder should contain `Jellyfin.Plugin.AniBridge.dll` and `meta.json`.
3. Restart Jellyfin

### Build from Source

Requires .NET 9.0 SDK.

```bash
cd Jellyfin.Plugin.AniBridge
dotnet build --configuration Release
```

Then copy the output:

```bash
mkdir -p /var/lib/jellyfin/plugins/AniBridgeDownloader
cp bin/Release/net9.0/Jellyfin.Plugin.AniBridge.dll /var/lib/jellyfin/plugins/AniBridgeDownloader/
cp meta.json /var/lib/jellyfin/plugins/AniBridgeDownloader/
sudo systemctl restart jellyfin
```

> If you're upgrading from the original **AniWorld Downloader** plugin: this release ships under a new plugin GUID (it's a rename, not an in-place update), so uninstall the old plugin first and reconfigure your download paths in the new settings page.

## Configuration

After installing, go to **Dashboard > Plugins > AniBridge Downloader** to configure.

### General

| Setting | Description |
|---------|-------------|
| Max Concurrent Downloads | How many downloads run at the same time (default: 2) |
| Max Retry Attempts | How many times to retry a failed download before giving up (default: 3) |
| Auto-scan Library | Trigger a Jellyfin library scan when a download finishes |
| Enable for non-admin users | Allow non-admin users to access the downloader via the sidebar (see [Non-admin access](#non-admin-access)) |
| Proxy Server | Route all network requests and downloads through a proxy (e.g. `http://proxy:8080` or `socks5://proxy:1080`). Leave empty to connect directly. Requires a server restart after changing. |
| Movie download path | Default save location for movies (should point to a Jellyfin library folder) |
| Language fallback | If the requested track (Sub or Dub) isn't available, try the other one instead of failing |

### Per-site settings

Each site can be enabled or disabled independently and has its own settings. If a per-site download path is left empty, the other language's path is used.

| Setting | Description |
|---------|-------------|
| Enabled | Toggle this site on or off |
| Download Path (Sub / Dub) | Where to save files for that track (should point to a Jellyfin library folder). Defaults to `/Media/Anime/sub` and `/Media/Anime/dub`. |
| Preferred Language | Default track for downloads from that site |

## Non-admin access

By default, the plugin UI is only accessible from the admin dashboard. You can enable it for all users so it appears as a sidebar entry.

### Setup

1. Install the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin
2. Restart Jellyfin
3. Go to **Dashboard > Plugins > AniBridge Downloader** and enable **Enable for non-admin users**
4. Restart Jellyfin again

Non-admin users will see an **AniBridge Downloader** entry in the sidebar that opens the full UI in a modal overlay. The settings button is hidden in this view. Configuration is only available through the admin dashboard.

> **Note:** The File Transformation plugin injects a script tag into Jellyfin's `index.html` at runtime (no files are modified on disk). Disabling the setting and restarting will remove the sidebar entry.

## Usage

1. Open **AniBridge Downloader** from the admin dashboard sidebar (or the sidebar entry if non-admin access is enabled)
2. Use **Search** to find a title across every enabled site, or browse **Popular** / **New Releases**
3. Click a title to see its seasons and episodes
4. Hit **Download** on an episode, or use **Download Season** / **Download All Seasons** for batch downloads
5. Switch to the **Downloads** tab to monitor progress
6. Check **History** for past downloads and stats

## How It Works

The plugin's site-adapter architecture (`StreamingSiteService`) is API-driven:

1. Search, series, and episode data come from a JSON REST API
2. The API resolves directly to a playable stream URL, or (Anikoto's Megaplay-backed server) to an embed page that a dedicated extractor decodes into the real HLS URL
3. ffmpeg downloads the stream and saves it as MKV

### Adding a new site

Implement `StreamingSiteService`, declare which of its native language identifiers map to the plugin's canonical `"sub"`/`"dub"` keys, and register it in `PluginServiceRegistrator`. The controller, config UI, and download pipeline pick it up automatically. A site that only exposes HTML (no JSON API) can still work the same way — just scrape/parse HTML inside that adapter's own methods instead of calling a REST endpoint.

## Known Issues

### Anikoto may not work out of the box

Anikoto was built without the ability to inspect the site's real network traffic (anikoto.net blocks automated fetch tools). It's built against a documented community API wrapper and should be reasonably solid, but if it fails, check the plugin logs (Dashboard > Logs) for the actual HTTP responses — that will show which endpoint path or JSON property name needs adjusting in `AnikotoService.cs`.

### AniWatch's DoodStream server may get blocked under heavy use

DoodStream fronts its embeds with a Cloudflare Turnstile/FingerprintJS bot check that can blanket-403 your server's IP if it sees too many requests in a short window (e.g. a large batch/season download). `DoodStreamExtractor` already rate-limits its own requests to stay under that threshold, but if your IP still gets flagged, switch AniWatch's preferred provider to MegaCloud (the default) or wait it out — the block is per-IP and temporary.

## Legal Disclaimer

Jellyfin AniBridge Downloader is a **client-side** tool that enables access to content hosted on third-party websites. It **does not host, upload, store, or distribute any media itself**.

This software is **not intended to promote piracy or copyright infringement**. You are solely responsible for how you use Jellyfin AniBridge Downloader and for ensuring that your use **complies with applicable laws** and the **terms of service of the websites you access**.

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
