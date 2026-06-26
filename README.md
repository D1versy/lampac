# Lampac Next Generation

[![Build](https://github.com/lampac-nextgen/lampac/actions/workflows/build.yml/badge.svg)](https://github.com/lampac-nextgen/lampac/actions/workflows/build.yml)
[![Test — build all projects](https://github.com/lampac-nextgen/lampac/actions/workflows/test-build.yml/badge.svg)](https://github.com/lampac-nextgen/lampac/actions/workflows/test-build.yml)
[![Release](https://github.com/lampac-nextgen/lampac/actions/workflows/release.yml/badge.svg)](https://github.com/lampac-nextgen/lampac/actions/workflows/release.yml)
[![Format code](https://github.com/lampac-nextgen/lampac/actions/workflows/format-code.yml/badge.svg)](https://github.com/lampac-nextgen/lampac/actions/workflows/format-code.yml)

[![GitHub release (latest SemVer)](https://img.shields.io/github/v/release/lampac-nextgen/lampac?label=version)](https://github.com/lampac-nextgen/lampac/releases)
[![GitHub tag (latest SemVer pre-release)](https://img.shields.io/github/v/tag/lampac-nextgen/lampac?include_prereleases&label=pre-release)](https://github.com/lampac-nextgen/lampac/tags)
[![License: MIT](https://img.shields.io/github/license/lampac-nextgen/lampac)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)

> Self-hosted backend server for [Lampa](https://github.com/yumata/lampa). It aggregates links to publicly available content from 70+ sources and serves them to Lampa as plugins. Built on ASP.NET Core (.NET 10).

---

[Lampa](https://github.com/yumata/lampa) is a free app for browsing movie information. **Lampac NextGen** extends it: it collects links from dozens of Russian, Ukrainian, anime and Western sources, serves them as a JSON API, and additionally provides TorrServer, DLNA, transcoding, bookmark synchronization and much more. The default port is **9118**.

<details>
<summary><strong>Features</strong></summary>

- **70+ VOD, anime and 18+ sources** — providers in `Modules/OnlineRUS`, `OnlinePaid`, `OnlineAnime`, `OnlineENG`, `OnlineUKR`, `OnlineGEO`, `Adult/`
- **TorrServer** — built-in torrent server running as a subprocess
- **DLNA/UPnP** — media server for local files
- **JacRed** — torrent indexer aggregator (Jackett-compatible)
- **Transcoding** — FFmpeg transcoding (up to 5 streams)
- **Tracks** — subtitle and audio track management (FFprobe)
- **Sync** — cross-device sync of bookmarks and history (SQLite)
- **TimeCode** — playback position saving
- **TmdbProxy** — local TMDB API cache
- **LampaWeb** — built-in Lampa UI hosting (auto-updates from GitHub)
- **WebLog** — real-time debugging of HTTP and Playwright traffic
- **Playwright** — Chromium/Firefox automation to bypass JS protections
- **RCH** — WebSocket relay for clients behind NAT
- **WAF** — firewall with geo-blocking, rate limits and brute-force protection
- **GeoIP** — MaxMind GeoLite2 (databases bundled)
- **Hot config reload** — `init.conf` is applied without a restart
- **Multi-platform** — `linux/amd64`, `linux/arm64`

</details>

---

## Contents

- [Lampac Next Generation](#lampac-next-generation)
  - [Contents](#contents)
  - [Quick start](#quick-start)
    - [Docker](#docker)
    - [Native installation (Linux)](#native-installation-linux)
    - [Native installation (Windows)](#native-installation-windows)
    - [Manual build](#manual-build)
  - [Configuration](#configuration)
  - [Modules](#modules)
  - [Content providers](#content-providers)
  - [API](#api)
  - [Architecture](#architecture)
  - [Dependencies](#dependencies)
  - [Project structure](#project-structure)
  - [Additional documentation](#additional-documentation)

---

## Quick start

### Docker

**Main scenario** — `docker-compose.yaml`, port **9118**.

```bash
git clone https://github.com/lampac-nextgen/lampac.git
cd lampac

mkdir -p lampac-docker/config lampac-docker/plugins
cp config/example.init.conf lampac-docker/config/init.conf
printf '%s' 'your_root_password' > lampac-docker/config/passwd

# Uncomment the volumes block in docker-compose.yaml
docker compose up -d
```

By default all volumes are commented out — the container starts with the `init.conf` and `passwd` from the image. The working directory inside the container is `/lampac`; files are read from its root, not from the `config/` subdirectory.

<details>
<summary><strong>Volumes and network</strong></summary>

| Host path | Container path | Purpose |
| --- | --- | --- |
| `./lampac-docker/config/passwd` | `/lampac/passwd` | root password (WebLog, service functions) |
| `./lampac-docker/config/init.conf` | `/lampac/init.conf` | Configuration |
| `./lampac-docker/plugins/lampainit.js` | `/lampac/plugins/override/lampainit.js` | Client plugin override |
| `./lampac-docker/cache` | `/lampac/cache` | Cache |
| `./lampac-docker/database` | `/lampac/database` | Databases (Sync, TimeCode, SISI) |
| `./lampac-docker/mods/<Name>` | `/lampac/mods/<Name>` | Custom modules |

The default network is bridge with IP `10.10.10.10`. For `host` mode, uncomment `network_mode: host` in the compose file and reconcile the `ports` / `networks` blocks.

Minimal service example:

```yaml
services:
  lampac:
    image: ghcr.io/lampac-nextgen/lampac
    ports:
      - "9118:9118"
    shm_size: 1024mb
    restart: unless-stopped
    volumes:
      - ./lampac-docker/config/passwd:/lampac/passwd
      - ./lampac-docker/config/init.conf:/lampac/init.conf
      - ./lampac-docker/plugins/lampainit.js:/lampac/plugins/override/lampainit.js
```

</details>

<details>
<summary><strong>Dev mode (port 29118)</strong></summary>

`docker-compose.dev.yaml` — a separate instance on port **29118** for development. Volumes are enabled by default.

```bash
mkdir -p lampac-docker/config lampac-docker/plugins
cp config/example.init.conf lampac-docker/config/development.init.conf
# In development.init.conf set: "listen"."port": 29118

printf '%s' 'your_root_password' > lampac-docker/config/passwd
cp Modules/LampaWeb/plugins/lampainit.js lampac-docker/plugins/lampainit.js

docker compose -f docker-compose.dev.yaml up -d
```

> Both compose files use `container_name: lampac` — running them at the same time isn't possible without edits.

</details>

<details>
<summary><strong>Managing modules in Docker</strong></summary>

Which modules are loaded is controlled by two mechanisms:

1. **`BaseModule.SkipModules`** in `init.conf` — names of modules that won't be loaded even if their code is in the image.
2. **`manifest.json`** in the module directory — the `"enable": true|false` key. Some modules ([AdminPanel](Modules/AdminPanel/manifest.json), [ExternalBind](Modules/ExternalBind/manifest.json)) ship with `"enable": false`.

To enable a disabled module without rebuilding the image: copy its directory, edit `manifest.json` and mount it into `/lampac/module/<Name>/` (built-in) or `/lampac/mods/<Name>/` (custom).

</details>

---

### Native installation (Linux)

Debian/Ubuntu, amd64 and arm64 are supported. The script installs the .NET 10 runtime, creates a system user `lampac` and registers a systemd service.

```bash
# Install
curl -fsSL https://raw.githubusercontent.com/lampac-nextgen/lampac/main/install.sh | sudo bash

# Install a specific version
curl -fsSL https://raw.githubusercontent.com/lampac-nextgen/lampac/main/install.sh | sudo bash -s -- --tag v1.2.3

# Update
curl -fsSL https://raw.githubusercontent.com/lampac-nextgen/lampac/main/install.sh | sudo bash -s -- --update

# Update / downgrade to a specific tag
curl -fsSL https://raw.githubusercontent.com/lampac-nextgen/lampac/main/install.sh | sudo bash -s -- --update --tag v1.2.3

# Reinstall the same version (no interactive confirmation)
curl -fsSL https://raw.githubusercontent.com/lampac-nextgen/lampac/main/install.sh | sudo bash -s -- --update --force

# Check for an update without making changes
curl -fsSL https://raw.githubusercontent.com/lampac-nextgen/lampac/main/install.sh | sudo bash -s -- --update --dry-run

# Pre-release
curl -fsSL https://raw.githubusercontent.com/lampac-nextgen/lampac/main/install.sh | sudo bash -s -- --pre-release

# Remove
curl -fsSL https://raw.githubusercontent.com/lampac-nextgen/lampac/main/install.sh | sudo bash -s -- --remove

# Verbose log during install (for error diagnostics)
curl -fsSL https://raw.githubusercontent.com/lampac-nextgen/lampac/main/install.sh | sudo bash -s -- --verbose

# Verbose log during update (for error diagnostics)
curl -fsSL https://raw.githubusercontent.com/lampac-nextgen/lampac/main/install.sh | sudo bash -s -- --update --verbose

# Current installed version (may show N/A before the first update)
curl -fsSL https://raw.githubusercontent.com/lampac-nextgen/lampac/main/install.sh | sudo bash -s -- --version
```

```bash
# Service management
systemctl status lampac
systemctl restart lampac
journalctl -u lampac -f
```

<details>
<summary><strong>Environment variables</strong></summary>

| Variable | Default | Description |
| --- | --- | --- |
| `LAMPAC_INSTALL_ROOT` | `/opt/lampac` | Install directory |
| `LAMPAC_USER` | `lampac` | System user |
| `LAMPAC_UID` | `1000` | UID (if taken, a free one is chosen) |
| `LAMPAC_GID` | `1000` | GID (if taken, a free one is chosen) |
| `LAMPAC_PORT` | `9118` | Port (for the post-install hint) |
| `LAMPAC_GITHUB_REPO` | `lampac-nextgen/lampac` | GitHub releases repository |
| `LAMPAC_DOTNET_ROOT` | `/usr/share/dotnet` | .NET install path |
| `LAMPAC_DOTNET_CHANNEL` | `10.0` | .NET runtime version |

</details>

<details>
<summary><strong>What's preserved on update (rsync excludes)</strong></summary>

`--update` uses `rsync --delete` — it removes files not present in the release, but the following paths are **protected**:

| Path | Description |
| --- | --- |
| `install.sh` | The script itself |
| `init.conf`, `init.yaml` | Configuration |
| `mods/` | Custom modules |
| `data/kinoukr.json`, `data/PizdatoeDb.json` | Local databases |
| `*.db`, `*.db-shm`, `*.db-wal` | SQLite (Sync, SISI, TimeCode) |
| `logs/`, `cache/` | Logs and cache |
| `TorrServer`, `torrserver/`, `data/ts/` | TorrServer and its data |
| `.local/`, `.aspnet/`, `.claude/`, `.config/`, `.playwright/` | User home directories |
| `users.json`, `passwd`, `current.conf`, `database/` | User data |
| `wwwroot/` | User statics and Lampa UI cache |
| `plugins/override/` | Plugin overrides |
| `notifications_date.txt` | Notifications state |
| `excludes.conf` | Additional excludes file |
| `version.txt` | Installed version marker |

To protect your own files, create `excludes.conf` next to `Core.dll`:

```bash
# /opt/lampac/excludes.conf — one exclude per line, # — comment
my_custom_folder/
config/local.conf
*.custom
```

Paths are relative to `LAMPAC_INSTALL_ROOT`; use a trailing slash for folders; glob patterns are supported.

</details>

---

### Native installation (Windows)

1. **Install the .NET 10 Runtime**
   Download and install the **.NET 10.0 Runtime** from the [official site](https://dotnet.microsoft.com/download/dotnet/10.0) (choose the `ASP.NET Core Runtime` for Windows).

2. **Download a release**
   Go to the [releases page](https://github.com/lampac-nextgen/lampac/releases) and download the `lampac-nextgen.zip` archive. Extract it anywhere, for example `C:\lampacNG`.

3. **Set up the configuration**
   Rename `example.init.conf` to `init.conf` and edit it to your needs.

4. **Start the server**
   Open a command prompt (cmd or PowerShell) in the extracted folder and run: `dotnet Core.dll`

The server will start on port 9118 (or another one specified in init.conf). Press `Ctrl+C` to stop it.

> **NOTE**
> To run it in the background you can use NSSM (create a Windows service):
>
> - To create the service, download the [NSSM](https://nssm.cc/download) tool and extract it, for example, into `C:\nssm`
>
> - Create the service from an **administrator CMD**:
>
> ```cmd
> "C:\nssm\win64\nssm.exe" install Lampac "C:\Program Files\dotnet\dotnet.exe" "C:\lampacNG\Core.dll"
> "C:\nssm\win64\nssm.exe" set Lampac AppDirectory "C:\lampacNG"
> "C:\nssm\win64\nssm.exe" set Lampac Start SERVICE_AUTO_START
> "C:\nssm\win64\nssm.exe" start Lampac
> ```
>
> - Remove the service:
>
> ```cmd
> "C:\nssm\win64\nssm.exe" stop Lampac
> "C:\nssm\win64\nssm.exe" remove Lampac
> ```
>
> Remember that to update the service you must first stop it, then replace the files in `C:\lampacNG` with the new ones from the archive, and then start the service again.
---

### Manual build

**Requirements:** .NET SDK 10.0+

```bash
./build.sh                          # build into publish/
RUNTIME_ID=linux-arm64 ./build.sh   # cross-compilation

dotnet publish Core/Core.csproj -c Release -o publish   # directly
dotnet build NextGen.slnx                               # verify the whole solution compiles

cd publish && dotnet Core.dll
```

<details>
<summary><strong>build.sh options</strong></summary>

| Flag | Description |
| --- | --- |
| `--clean` | Remove bin/ and obj/ from all projects |
| `--format` | Format code (`dotnet format`) |
| `-o /path` | Custom output directory |
| `-c Debug` | Debug configuration |

</details>

---

## Configuration

Configuration is stored in `init.conf` (JSON) or `init.yaml` next to `Core.dll`. It's checked every second and **reloaded without a restart**. Backups go to `database/backup/init/`.

Examples: [`config/example.init.conf`](config/example.init.conf), [`config/example.init.yaml`](config/example.init.yaml).

<details>
<summary><strong>Main parameters</strong></summary>

```jsonc
{
  // Low-memory mode (~−140 MB RSS in a typical scenario, see the section below)
  "lowMemoryMode": false,

  // Network settings
  "listen": {
    "ip": "0.0.0.0",
    "port": 9118,
    "scheme": "http",
    "version": true,
    "ResponseCancelAfter": 15    // response timeout, seconds
  },

  // Modules
  "BaseModule": {
    "SkipModules": [],           // module names to disable
    "LoadModules": [".*"],       // whitelist: name, group (OnlineUKR), mask (LME.*)
    "ValidateRequest": true,
    "BlockedBots": true
  },

  // Cache
  "cache": {
    "extend": 180                // TTL extension, minutes
  },

  // Playwright
  "chromium": { "enable": false, "count": 1, "restart": 3600 },
  "firefox":  { "enable": false, "count": 1 },

  // Remote Client Hub (WebSocket relay for clients behind NAT)
  "rch": { "enable": false, "requiredConnected": 1 },

  // File logging (logs/, 14 days)
  "serilog": false,

  // GC memory management
  "GC": {
    "Concurrent": true,
    "ConserveMemory": 0,
    "HighMemoryPercent": 90,
    "RetainVM": false
  },

  // Stream encryption
  "kit": { "aesgcmkeyName": "" }
}
```

</details>

<details>
<summary><strong>Low-memory mode (lowMemoryMode)</strong></summary>

In the root of `init.conf` or `init.yaml` set:

```json
"lowMemoryMode": true
```

The default is `false`. In a typical install the process working memory ends up **roughly 140 MB lower** than without this mode (an estimate; the actual saving depends on the OS, Docker limits and the nature of the load).

**What changes internally:** buffer pool sizes and auxiliary JSON/string allocations are reduced; GeoIP databases are opened via a memory-mapped file instead of being fully loaded into RAM; an aggressive `ThreadPool` minimum isn't raised; the NetVips image proxy works without an in-memory cache; heap compaction (including the LOH) runs more often while idle; some modules disable secondary caches.

**Trade-off:** under very high concurrent load, peak throughput may be slightly lower than in the default mode.

</details>

<details>
<summary><strong>WAF and security</strong></summary>

```jsonc
{
  "WAF": {
    "enable": true,
    "countryAllow": ["RU", "UA", "BY"],   // geo-blocking (empty — all countries)
    "whiteIps": ["192.168.1.0/24"],        // IP/CIDR whitelist
    "bruteForceProtection": true,
    "limit_map": {
      "/lite/": 10,
      "/externalids": 10
    }
  }
}
```

</details>

<details>
<summary><strong>Authentication (accsdb)</strong></summary>

```jsonc
{
  "accsdb": {
    "enable": true,
    "accounts": "user1:2026-12-31,user2:2027-06-01",
    // or the detailed format:
    "users": [
      { "id": "user1", "expires": "2026-12-31" },
      { "id": "user2", "expires": "2027-06-01" }
    ]
  }
}
```

</details>

<details>
<summary><strong>VOD, SISI and Lampa UI plugins</strong></summary>

```jsonc
{
  // VOD plugin
  "online": {
    "name": "Lampac NextGen",
    "version": true,
    "btn_priority_forced": true
  },

  // SISI (18+)
  "sisi": {
    "lgbt": false,
    "NextHUB": true,
    "history": { "enable": false }
  },

  // Statistics (/stats/*)
  "openstat": { "enable": false },

  // Lampa UI plugins
  "LampaWeb": {
    "initPlugins": {
      "online": true, "sisi": true, "torrserver": true,
      "timecode": true, "jacred": true, "tmdbProxy": true,
      "cubProxy": true, "pirate_store": true
    }
  }
}
```

</details>

<details>
<summary><strong>Provider configuration (example)</strong></summary>

Each provider is configured in its own section of `init.conf`:

```jsonc
{
  "Rezka":  { "enable": true, "host": "https://rezka.ag", "priority": 1 },
  "Filmix": { "enable": true, "host": "https://filmix.biz", "token": "TOKEN", "priority": 2 },
  "KinoPub":{ "enable": true, "token": "TOKEN" },
  "Kodik":  { "enable": true, "token": "TOKEN" }
}
```

</details>

---

## Modules

Disabled by default in `SkipModules` ([`config/base.conf`](config/base.conf)): **Catalog**, **DLNA**, **Tracks**, **Transcoding**, **WebLog**, **CacheMedia**, **ProxyLimiter**, **ForkPlayerXML**, **MsxNative**, **TelegramAuth**, **TelegramAuthBot**. WAF and accsdb are also disabled by default.

> The service modules **Sync**, **SyncEvents**, **Storage** and **TimeCode** are **not** in `SkipModules` — they load together with the core until you add them to `SkipModules` manually.

> [!WARNING]
> The **DLNA**, **Tracks**, **Transcoding** and **Catalog** modules don't sanitize incoming requests. Don't enable them on a publicly accessible VPS without restricting access via a firewall or reverse proxy.

| Module | Default | Description |
| --- | :---: | --- |
| **Online** | ✅ | VOD core: `/online.js` plugin, `/lite/*` aggregator. Providers in `Modules/Online*/`. WAF: 10 req/s. [README](Online/README.md) |
| **SISI** | ✅ | 18+: `/sisi.js` plugin, SQLite (history, bookmarks). Platforms in `Modules/Adult/*`. [README](SISI/README.md) |
| **LampaWeb** | ✅ | Lampa UI hosting. Auto-updates from GitHub every 90 min. |
| **TorrServer** | ✅ | TorrServer process management, `/ts/` proxy. Random per-session password. |
| **JacRed** | ✅ | Torrent indexer aggregator (Rutor, Kinozal, RuTracker, NNMClub, Toloka, Bitru, etc.). |
| **NextHUB** | ✅ | 18+ showcase from YAML (`Modules/NextHUB/sites/`). Route `/nexthub`. WAF: 5 req/s. [README](Modules/NextHUB/README.md) |
| **TmdbProxy** | ✅ | Local TMDB API cache (`cache/tmdb/`). |
| **CubProxy** | ✅ | HTTP/HTTPS proxy with a file cache (`cache/cub/`). |
| **TimeCode** | ✅ | Saving and restoring playback position. SQLite. |
| **Kit** | ✅ | Stream encryption (CryptoKit), `kit` config in `init.conf`. |
| **PidTor** | ✅ | PidTor source, route `/lite/pidtor`. |
| **Catalog** | ⛔ | Catalog browser from YAML (`sites/`). Route `/catalog/`. Trusted network only. |
| **DLNA** | ⛔ | DLNA/UPnP media server. Formats: mp4, mkv, ts, webm, avi, flac, etc. Trusted network only. |
| **Sync** | ✅ | Bookmark and history sync. Endpoints `/storage/`, `/bookmark/`. SQLite. Disable: add `Sync` to `SkipModules`. |
| **SyncEvents** | ✅ | Broadcasts sync events over WebSocket (NwsEvents). Disable: `SyncEvents` in `SkipModules`. |
| **Storage** | ✅ | Data storage for Sync, NWS (`onlyreg`). Disable: `Storage` in `SkipModules`. |
| **Tracks** | ⛔ | Subtitles and audio tracks (`database/tracks/`), FFprobe integration (`/ffprobe`). Trusted network only. |
| **Transcoding** | ⛔ | FFmpeg HLS/DASH transcoding. Up to 5 streams, 5-min timeout. `cache/transcoding/`. Trusted network only. |
| **WebLog** | ⛔ | `/weblog` page: a stream of HTTP and Playwright events over WebSocket. Requires the root password. Don't enable it publicly. |
| **CacheMedia** | ⛔ | Caching of SISI streams (`ProxyApiCacheStream` events for specific platforms). |
| **ProxyLimiter** | ⛔ | Concurrency limits for SISI media-proxy requests. `ProxyLimiter` config. [README](Modules/Proxy/ProxyLimiter/README.md) |
| **ForkPlayerXML** | ⛔ | ForkPlayer: `/fxml` playlists, `/` redirect for the ForkPlayer client. [README](Modules/ForkPlayerXML/README.md) |
| **MsxNative** | ⛔ | MSX/MS X: Sisi adaptation and access under `accsdb`. [README](Modules/MsxNative/README.md) |
| **WatchTogether** | ⛔ | Synchronized watching (WebSocket rooms). |
| **AdminPanel** | ⛔ (manifest) | Web admin panel and JSON API (`/adminpanel/`). `"enable": false` in [manifest.json](Modules/AdminPanel/manifest.json). |
| **ExternalBind** | ⛔ (manifest) | Lite/Online binding for remote URLs (FilmixPro, Rezka, KinoPub). [README](Modules/ExternalBind/README.md) |
| **TelegramAuth** | ⛔ | HTTP API `/tg/auth/…`, accsdb integration. [README](Modules/Community/TelegramAuth/README.md) |
| **TelegramAuthBot** | ⛔ | Telegram bot for device linking (long polling). [README](Modules/Community/TelegramAuthBot/README.md) |

<details>
<summary><strong>Custom modules</strong></summary>

Create a subdirectory in `mods/` with a `manifest.json` and `.cs` files — Roslyn will compile it at startup:

```json
{
  "name": "MyModule",
  "description": "Module description",
  "version": "1.0",
  "enable": true,
  "dynamic": true
}
```

`dynamic: true` — a hot rebuild when `.cs` files change, without restarting the server. Use the examples in `Modules/*/manifest.json` as a reference.

</details>

---

## Content providers

<details>
<summary><strong>VOD — online cinema</strong></summary>

| Provider | Group | Notes |
| --- | --- | --- |
| `Alloha` | OnlinePaid | |
| `CDNvideohub` | OnlineRUS | |
| `Collaps` | OnlineRUS | Including a DASH variant |
| `FanCDN` | OnlineRUS | |
| `Filmix` | OnlinePaid | FilmixPartner, FilmixTV variants |
| `FlixCDN` | OnlineRUS | |
| `GetsTV` | OnlinePaid | |
| `HDVB` | OnlineRUS | |
| `IptvOnline` | OnlinePaid | |
| `iRemux` | OnlinePaid | |
| `Kinobase` | OnlineRUS | |
| `Kinogo` | OnlineRUS | |
| `Kinotochka` | OnlineRUS | |
| `Kinoflix` / `AsiaGe` / `Geosaitebi` | OnlineGEO | |
| `KinoPub` | OnlinePaid | Requires a token |
| `LeProduction` | OnlineRUS | |
| `Mirage` | OnlineRUS | |
| `Phantom` | OnlineRUS | |
| `PiTor` | Online | Streaming via torrent |
| `PizdatoeHD` | OnlineRUS | |
| `Rezka` / `RezkaPremium` | OnlinePaid | |
| `RutubeMovie` | OnlineRUS | |
| `SakhTV` | OnlinePaid | |
| `Spectre` | OnlineRUS | |
| `VeoVeo` | OnlineRUS | Offline DB `data/veoveo.json` |
| `Vibix` | OnlineRUS | |
| `VideoDB` / `Videoseed` | OnlineRUS | Routes `/lite/videodb`, `/lite/videoseed` |
| `VkMovie` | OnlineRUS | |
| `VoKino` | OnlinePaid | |
| `Zetflix` / `ZetflixDB` | OnlineRUS | |

</details>

<details>
<summary><strong>Anime (12 sources)</strong></summary>

| Provider | Service |
| --- | --- |
| `AniLiberty` | AniLiberty |
| `AniLibria` | AniLibria |
| `AniMedia` | AniMedia |
| `AnimeGo` | AnimeGo |
| `AnimeLib` | AnimeLib |
| `Animebesst` | AnimeBesst |
| `Animevost` | Animevost |
| `Dreamerscast` | Dreamerscast |
| `Kodik` | Kodik (universal, VOD + anime) |
| `Mikai` | Mikai |
| `MoonAnime` | MoonAnime |
| `AnimeON` | AnimeON |

</details>

<details>
<summary><strong>English-language content (10 sources)</strong></summary>

| Provider | Service |
| --- | --- |
| `AutoEmbed` | AutoEmbed |
| `HydraFlix` | HydraFlix |
| `MovPI` | MovPI |
| `PlayEmbed` | PlayEmbed |
| `RgShows` | RgShows |
| `SmashyStream` | SmashyStream |
| `TwoEmbed` | TwoEmbed |
| `VidLink` | VidLink |
| `VidSrc` | VidSrc |
| `Videasy` | Videasy |

</details>

<details>
<summary><strong>Ukrainian CDNs (8 sources)</strong></summary>

| Provider | Service |
| --- | --- |
| `Ashdi` | Ashdi |
| `BamBoo` | BamBoo |
| `Eneyida` | Eneyida |
| `HdvbUA` | HDVB (UA) |
| `Kinoukr` | KinoUkr (offline DB `data/kinoukr.json`, ~130k records) |
| `Tortuga` | Tortuga |
| `UAFilm` | UAFilm |
| `UaKino` | UaKino |

</details>

<details>
<summary><strong>SISI — 18+ content (15 platforms)</strong></summary>

| Platform | Routes |
| --- | --- |
| BongaCams | `/bgs` |
| Chaturbate | `/chu` |
| Ebalovo | `/elo` |
| Eporner | `/epr` |
| HQporner | `/hqr` |
| PornHub | `/phub`, `/phubgay`, `/phubsml` |
| PornHubPremium | `/phubprem` |
| Porntrex | `/ptx` |
| Runetki | `/runetki` |
| Spankbang | `/sbg` |
| Tizam | `/tizam` |
| Xhamster | `/xmr`, `/xmrgay`, `/xmrsml` |
| Xnxx | `/xnx` |
| Xvideos | `/xds`, `/xdsgay`, `/xdssml` |
| XvideosRED | `/xdsred` |

</details>

<details>
<summary><strong>NextHUB — 18+ showcase from YAML</strong></summary>

The **NextHUB** module is a showcase of 18+ sites driven by YAML descriptions in `Modules/NextHUB/sites/` (the file name without extension = the value of the `plugin` URL parameter).

- **Route:** `GET /nexthub?plugin=<name>` — parameters: `plugin` (required), optionally `search`, `sort`, `cat`, `model`, `pg`
- **Config:** `NextHUB.sites_enabled` — if set, only allows plugins whose name is contained in the string (e.g. `pornhub,beeg`)
- **Overrides:** `Modules/NextHUB/override/{plugin}.yaml` or `_.yaml` — merged on top of the base YAML
- **WAF:** a 5 req/s limit on `/nexthub`

[More — README](Modules/NextHUB/README.md)

</details>

---

## API

<details>
<summary><strong>Core</strong></summary>

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/version` | Server version |
| `GET` | `/api/headers` | Current request headers |
| `GET` | `/api/geo[?ip=]` | GeoIP location of an IP address |
| `GET` | `/api/myip` | Client IP address |
| `GET` | `/api/chromium/ping` | Playwright ping (`pong`) |
| `POST` | `/rch/result?id=` | RCH relay: write result (max 10 MB) |
| `POST` | `/rch/gzresult?id=` | RCH relay: write gzip result (max 10 MB) |
| `WS` | `/ws` | NativeWebSocket for RCH push |
| `GET` | `/stats/gc` | Memory: heap, WorkingSet, PrivateMemory |
| `GET` | `/stats/request` | Request counters, active connections, top slow paths |
| `GET` | `/stats/tempdb` | Caches and buffer pools |
| `GET` | `/stats/threadpool` | ThreadPool diagnostics |
| `GET` | `/stats/browser/context` | Playwright state (contexts, counters) |

> `/stats/*` (except `/stats/gc`) are only available when `openstat.enable: true`.

</details>

<details>
<summary><strong>Online / SISI / Modules</strong></summary>

**Online (VOD)**

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/online.js` | Lampa VOD plugin |
| `GET` | `/online/js/{token}` | Plugin with token authorization |
| `GET` | `/lite/{provider}` | List of sources from a provider |
| `GET` | `/externalids` | ID mapping (TMDB ↔ KinoPoisk, etc.) |
| `GET` | `/lifeevents` | SSE stream of provider health events |

**SISI (18+)**

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/sisi.js` | Lampa SISI plugin |
| `GET` | `/sisi/js/{token}` | Plugin with token authorization |
| `GET` | `/{provider}` | Platform content (e.g. `/phub`, `/xnx`) |
| `GET` | `/sisi/bookmark` | Bookmark management |
| `GET` | `/sisi/history` | Watch history |

**Modules**

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/catalog/{site}/…` | Site catalog |
| `GET` | `/dlna/…` | DLNA media server |
| `GET` | `/storage/…` | Sync storage |
| `GET` | `/bookmark/…` | Sync bookmarks |
| `GET` | `/timecode/…` | Playback positions |
| `GET` | `/tmdb/…` | TMDB proxy/cache |
| `GET` | `/transcoding/…` | HLS/DASH transcoding |
| `GET` | `/ffprobe` | Track metadata (FFprobe) |
| `GET` | `/nexthub` | NextHUB: 18+ browser from YAML |
| `GET` | `/nexthub/vidosik` | NextHUB: view an item (`uri`, `related`) |
| `GET` | `/ts/…` | TorrServer |
| `GET` | `/weblog` | Real-time HTTP/Playwright debugging |
| `GET` | `/fxml` … | ForkPlayer: JSON/XML playlists (**ForkPlayerXML**, see module [`Modules/ForkPlayerXML/README.md`](Modules/ForkPlayerXML/README.md)) |

</details>

---

## Architecture

```text
┌─────────────────────────────────────────────────────────────────┐
│  Core  (ASP.NET Core web host, port 9118)                       │
│  Program.cs → Startup.cs → Middleware Pipeline                  │
├────────────────────┬────────────────────────────────────────────┤
│  Shared (lib)      │  BaseController, CoreInit (config),        │
│                    │  models, services, Playwright, HTTP pools  │
├────────────────────┴────────────────────────────────────────────┤
│  Dynamically loaded modules                                     │
│  ┌─────────┐ ┌─────────┐ ┌──────────┐ ┌───────────────────┐     │
│  │ Online  │ │  SISI   │ │ Catalog  │ │    LampaWeb       │     │
│  │(VOD API)│ │ + Adult │ │(catalog) │ │(Lampa UI)         │     │
│  └─────────┘ └─────────┘ └──────────┘ └───────────────────┘     │
│  ┌─────────┐ ┌─────────┐ ┌──────────┐ ┌───────────────────┐     │
│  │TorrServr│ │  DLNA   │ │  JacRed  │ │   Transcoding     │     │
│  └─────────┘ └─────────┘ └──────────┘ └───────────────────┘     │
│  ┌─────────┐ ┌─────────┐ ┌──────────┐ ┌───────────────────┐     │
│  │TmdbProxy│ │  Sync   │ │ TimeCode │ │     Tracks        │     │
│  │CubProxy │ │ WebLog  │ │ NextHUB  │ │  AdminPanel, Kit  │     │
│  └─────────┘ └─────────┘ └──────────┘ └───────────────────┘     │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │  Modules/OnlineRUS · OnlinePaid · OnlineAnime · OnlineENG │  │
│  │  OnlineUKR · OnlineGEO  — one project per provider        │  │
│  │  Modules/Adult/* — 18+ platforms                          │  │
│  │  Modules/Community/* — TelegramAuth, TelegramAuthBot      │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

| Layer | Description |
| --- | --- |
| **Core** | Entry point, Middleware Pipeline, `ApiController`. [README](Core/README.md) |
| **Shared** | Models, controllers, configuration, HTTP pools, Roslyn. [README](Shared/README.md) |
| **Online** | VOD core: `/online.js`, `/lite/*`, providers in `Modules/Online*/`. [README](Online/README.md) |
| **SISI** | 18+ core: `/sisi.js`, SQLite. Platforms in `Modules/Adult/`. [README](SISI/README.md) |
| **Modules/** | Functional modules, proxies, Community, Sync, etc. |

<details>
<summary><strong>Module loading, Roslyn and middleware</strong></summary>

**Module loading:**

Compiled assemblies are loaded from `runtimes/references/`. Module sources from `module/` and `mods/` are compiled by **Roslyn** (`CSharpEval`) at startup — this enables hot loading and custom overlays.

Load order:

1. First `mods/` (custom), then `module/` (built-in)
2. Filtering: `SkipModules`, `LoadModules` (regex/name/group), the `enable` flag in manifest.json
3. `dynamic: true` → a hot rebuild when `.cs` files change
4. `IModuleConfigure.Configure` → registration in DI
5. `IModuleLoaded.Loaded` → called after the application starts

**Middleware Pipeline:**

```
ForwardedHeaders → BaseMod → ModHeaders → RequestInfo
  → [/nws WebSocket] → Routing → Compression
  → ProxyImg → StaticFiles → WAF → Authorization
  → Accsdb → Controllers
```

**Configuration:**

- `init.conf` / `init.yaml` — the main config
- `base.conf` — defaults (fallback)
- Hot reload: watcher every ~1 sec, backups in `database/backup/init/`

</details>

---

## Dependencies

<details>
<summary><strong>NuGet packages (.NET 10.0)</strong></summary>

| Package | Version | Purpose |
| --- | --- | --- |
| `Microsoft.CodeAnalysis.CSharp` + `.Scripting` | 5.0.0 | Roslyn: on-the-fly module compilation |
| `Microsoft.Playwright` | 1.50.0 | Chromium/Firefox automation |
| `HtmlAgilityPack` | 1.12.4 | HTML parsing |
| `HtmlKit` | 1.2.0 | HTML parsing |
| `MaxMind.GeoIP2` | 5.4.1 | GeoIP (the `GeoLite2-*.mmdb` databases are bundled) |
| `Newtonsoft.Json` | 13.0.4 | JSON serialization |
| `Microsoft.EntityFrameworkCore` (+ Sqlite, Design) | 10.0.2 | ORM for SQLite (Sync, TimeCode, SISI, ExternalIds) |
| `Microsoft.Extensions.DependencyModel` | 10.0.2 | Dependency loading during dynamic compilation |
| `Microsoft.IO.RecyclableMemoryStream` | 3.0.1 | Memory pool for streams |
| `NetVips` / `NetVips.Native` | 3.2.0 / 8.18.0 | Image processing (libvips) |
| `YamlDotNet` | 16.3.0 | YAML configuration parsing |
| `Serilog.AspNetCore` + `.Sinks.File` | 9.0.0 / 7.0.0 | Structured logging |
| `System.Management` | 10.0.2 | OS and hardware info |

</details>

---

## Project structure

<details>
<summary><strong>Directory tree</strong></summary>

```text
lampac/
├── Core/                       # Entry point, middleware, module loading
│   ├── Program.cs              # Startup, initialization
│   ├── Startup.cs              # DI, HTTP clients, module loading
│   ├── Controllers/            # ApiController, RchApiEndpoints
│   ├── Middlewares/            # WAF, Accsdb, BaseMod, ProxyImg and others
│   ├── Services/               # NativeWebSocket, CronCacheWatcher
│   ├── data/                   # GeoIP databases, static JSON databases
│   ├── plugins/                # JS plugins (RCH, NWS)
│   └── wwwroot/                # Static files (SISI UI, stats, etc.)
├── Shared/                     # Shared library
│   ├── CoreInit.cs             # Configuration loading and hot-reload
│   ├── BaseController.cs       # Base controller
│   ├── Models/                 # Shared data models
│   └── Services/               # HTTP, cache, Playwright, GeoIP, Roslyn
├── Online/                     # VOD core (/online.js, /lite/*, externalids)
├── SISI/                       # 18+ core (/sisi.js, SQLite, bookmarks)
├── Modules/
│   ├── AdminPanel/             # Web admin panel (manifest: enable: false)
│   ├── Adult/                  # 18+ platforms (15 sources)
│   ├── Catalog/                # Site catalog (YAML)
│   ├── Community/              # TelegramAuth, TelegramAuthBot
│   ├── DLNA/                   # DLNA/UPnP media server
│   ├── ForkPlayerXML/          # ForkPlayer: /fxml
│   ├── ExternalBind/           # URL binding (manifest: enable: false)
│   ├── MsxNative/              # MSX player, Sisi
│   ├── JacRed/                 # Torrent indexer aggregator
│   ├── Kit/                    # Cryptography
│   ├── LampacApk/              # Android APK generator for the current server address
│   ├── LampaWeb/               # Lampa UI hosting
│   ├── NextHUB/                # 18+ showcase from YAML, sites/*.yaml
│   ├── OnlineAnime/            # 12 anime sources
│   ├── OnlineENG/              # 10 English-language sources
│   ├── OnlineGEO/              # 3 Georgian sources
│   ├── OnlinePaid/             # 9 paid VOD sources
│   ├── OnlineRUS/              # 21 Russian CDNs
│   ├── OnlineUKR/              # 8 Ukrainian sources
│   ├── PidTor/                 # PidTor source
│   ├── Proxy/                  # CubProxy, TmdbProxy, CacheMedia, CorsMedia, Corseu, ProxyLimiter
│   ├── Sync/                   # Sync, SyncEvents, Storage, TimeCode
│   ├── TorrServer/             # TorrServer management
│   ├── Tracks/                 # Subtitles and tracks (FFprobe)
│   ├── Transcoding/            # FFmpeg transcoding
│   ├── WatchTogether/          # Synchronized watching
│   └── WebLog/                 # HTTP/Playwright debug log
├── TestModules/                # Example modules → mods/ on publish
├── config/
│   ├── base.conf               # Default values
│   ├── example.init.conf       # Example config (JSON)
│   └── example.init.yaml       # Example config (YAML)
├── docker-compose.yaml         # Production (port 9118)
├── docker-compose.dev.yaml     # Dev (port 29118)
├── charts/lampac/              # Helm chart for Kubernetes
├── Dockerfile                  # Multi-arch image (amd64, arm64)
├── build.sh                    # dotnet publish Core/Core.csproj → publish/
├── install.sh                  # Native Linux installation
└── NextGen.slnx                # Solution (128+ projects)
```

After `dotnet publish`: module sources are in `module/` (Online, SISI, Modules), TestModules in `mods/`, and DLL dependencies in `runtimes/references/`.

</details>

---

## Additional documentation

| Document | About |
| --- | --- |
| [Core/README.md](Core/README.md) | `Program`/`Startup`, middleware, loading `module/` and `mods/` |
| [Shared/README.md](Shared/README.md) | `CoreInit`, controllers, `CSharpEval`, cache, HTTP, Playwright |
| [Online/README.md](Online/README.md) | VOD core, `/online.js`, `/lite/`, PiTor, Externalids |
| [SISI/README.md](SISI/README.md) | 18+ core, `Modules/Adult/*` platforms, route table |
| [Modules/NextHUB/README.md](Modules/NextHUB/README.md) | YAML sites, `/nexthub`, config, WAF |
| [Modules/Community/README.md](Modules/Community/README.md) | Telegram authorization, Lampa client, API |
| [Modules/Community/TelegramAuth/README.md](Modules/Community/TelegramAuth/README.md) | HTTP API `/tg/auth/…`, accsdb, storage |
| [Modules/Community/TelegramAuthBot/README.md](Modules/Community/TelegramAuthBot/README.md) | Long-polling bot, commands, config |
| [Modules/ExternalBind/README.md](Modules/ExternalBind/README.md) | Lite/Online binding, local IP flag |
| [charts/lampac/README.md](charts/lampac/README.md) | Helm chart for Kubernetes (`ghcr.io/lampac-nextgen/lampac`) |

---

[![Star History Chart](https://api.star-history.com/svg?repos=lampac-nextgen/lampac&type=Date)](https://star-history.com/#lampac-nextgen/lampac&Date)
