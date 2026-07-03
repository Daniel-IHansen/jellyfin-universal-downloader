# Jellyfin AniBridge Downloader

> **Note:** This is a renamed/reworked fork of the original *Jellyfin AniWorld Downloader* by SiroxCW, extended to support multiple streaming sites. The badge/release URLs below still point at the upstream repo layout as placeholders — update them once you push this to your own repository.

A Jellyfin plugin for searching and downloading anime and series from multiple streaming sites, directly inside Jellyfin's web interface. **English Sub/Dub only — no German.**

Series View| Search View
:---:|:---:
![Series View](screenshots/preview_anime.png) | ![Search View](screenshots/preview_search.png)

## Features

- **Search and browse** anime and series with cover art, popular titles, and new releases
- **Download** individual episodes, full seasons, or entire series
- **Four sites supported**, each a self-contained adapter — adding another site is a single new class, no controller/config changes needed:
  | Site | Content | Languages | Status |
  |------|---------|-----------|--------|
  | [aniworld.to](https://aniworld.to) | Anime | English Sub | Stable |
  | [s.to](https://s.to) | Series | English Dub | Stable |
  | [anikoto.net](https://anikoto.net) | Anime | English Sub + Dub | Best-effort (built against a community-documented third-party API — anikoto.net itself blocks automated access) |
  | [anime.nexus](https://anime.nexus) | Anime | English Sub + Dub | **Experimental, disabled by default** — no public API exists and the site blocks automated research tools, so this integration is an unverified guess at its REST conventions |
- **Multiple providers**: VOE, Filemoon, Vidoza and Vidmoly (for AniWorld/s.to); Anikoto/Anime Nexus resolve directly to a playable stream URL via their own APIs
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
| Download Path (Sub / Dub) | Where to save files for that track (should point to a Jellyfin library folder) |
| Preferred Language | Default track for downloads from that site |
| Preferred Provider | Default streaming provider (AniWorld/s.to only) |
| Fallback Provider | Backup provider if the primary one fails after all retries (AniWorld/s.to only) |
| Custom Base URL | Alternate mirror domain (s.to only, e.g. `serienstream.to`) |

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

The plugin's site-adapter architecture (`StreamingSiteService`) supports two integration styles:

### HTML-scraped sites (aniworld.to / s.to)

1. Searches use each site's AJAX search endpoint
2. Series, season, and episode pages are scraped to find provider links
3. Provider redirect URLs are resolved to embed pages
4. Each provider has a dedicated extractor that pulls out the direct stream URL
5. ffmpeg downloads the stream and saves it as MKV

### API-driven sites (Anikoto / Anime Nexus)

1. Search, series, and episode data come from a JSON REST API instead of HTML scraping
2. The API resolves directly to a playable stream URL — no separate embed-page extractor is needed
3. ffmpeg downloads the stream and saves it as MKV

### Adding a new site

Implement `StreamingSiteService` (for API-driven sites) or `HtmlScrapingSiteService` (for scraped HTML sites), declare which of its native language identifiers map to the plugin's canonical `"sub"`/`"dub"` keys, and register it in `PluginServiceRegistrator`. The controller, config UI, and download pipeline pick it up automatically.

### Supported providers (AniWorld / s.to)

| Provider | Method |
|----------|--------|
| **VOE** | Decodes obfuscated JSON (ROT13, base64, char shift) to extract HLS URLs |
| **Filemoon** | Handles both modern Byse API (AES-256-GCM) and legacy packed JS |
| **Vidmoly** | Extracts HLS URLs from JavaScript sources |
| **Vidoza** | Extracts MP4 URLs from source tags |

## Known Issues

### s.to downloads fail with "No JSON script blocks found in VOE page"

s.to has put their provider redirector (`/r?t=...`) behind a Cloudflare Turnstile gate that activates **per-IP** for "flagged" egress addresses. Many datacenter ranges (Hetzner, OVH, etc.) get the gate; most residential IPs don't. From a flagged IP, every download retry produces logs like:

```
Resolved to embed URL: "https://s.to/r?t=..."
No JSON script blocks found in VOE page
Failed to extract VOE source from page
```

Because the gate requires a real browser to solve an interactive Turnstile widget, the plugin can't bypass it on its own. Your options:

1. **Route the plugin through a clean proxy.** Set **Proxy Server** in plugin settings to a SOCKS5 or HTTP proxy on a residential / unflagged IP (e.g. `socks5://user:pass@proxy:1080`). Restart Jellyfin. This is the recommended fix.
2. **Use a VPN on the Jellyfin host** so all outbound traffic exits a clean IP.
3. **aniworld.to is currently unaffected** — if you only need anime, downloads still work normally on flagged IPs.

To check whether your server's IP is flagged, the repo includes [`scripts/check-sto-flag.sh`](scripts/check-sto-flag.sh): copy it to the server and run it.

### Anikoto / Anime Nexus may not work out of the box

Both integrations were built without the ability to inspect the sites' real network traffic (Anikoto's own site and anime.nexus both block automated fetch tools). Anikoto is built against a documented community API wrapper and should be reasonably solid; Anime Nexus is a best-effort guess at conventional REST endpoints and is disabled by default. If either fails, check the plugin logs (Dashboard > Logs) for the actual HTTP responses — that will show which endpoint path or JSON property name needs adjusting in `AnikotoService.cs` / `AnimeNexusService.cs`.

## Legal Disclaimer

Jellyfin AniBridge Downloader is a **client-side** tool that enables access to content hosted on third-party websites. It **does not host, upload, store, or distribute any media itself**.

This software is **not intended to promote piracy or copyright infringement**. You are solely responsible for how you use Jellyfin AniBridge Downloader and for ensuring that your use **complies with applicable laws** and the **terms of service of the websites you access**.

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
