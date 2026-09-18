# Kea as a web app

`src/Kea.Web` is an ASP.NET Core service that puts Kea on a server: you queue comics from a
browser, the server does the downloading, and the results are browsable and downloadable from the
same page.

It is a third front-end over the same `Kea.Core` as the desktop app and the CLI. No download logic
is duplicated.

## Contents

- [Quick start](#quick-start)
- [Docker](#docker)
- [Access control](#access-control) &mdash; read this before exposing it
- [Behind a reverse proxy](#behind-a-reverse-proxy)
- [Running as a systemd service](#running-as-a-systemd-service)
- [Configuration](#configuration)
- [HTTP API](#http-api)
- [How it differs from the desktop app](#how-it-differs-from-the-desktop-app)

## Quick start

```bash
dotnet run --project src/Kea.Web
```

Then open <http://localhost:5000>. Downloads land in `src/Kea.Web/library` unless you say otherwise.

With no access token set, the service answers **only requests from the machine it runs on**. That
is deliberate: it works immediately on your own machine and cannot be put on a network by accident.

## Docker

```bash
# generate a token first
export KEA_ACCESSTOKEN=$(openssl rand -base64 32)
echo "KEA_ACCESSTOKEN=$KEA_ACCESSTOKEN" > .env

docker compose up -d
```

The compose file publishes to `127.0.0.1:8080` and mounts `./library` for the downloads. The
container runs unprivileged, with a read-only root filesystem and `no-new-privileges`.

To reach it from elsewhere on your network, change the port mapping to `"8080:8080"` — but set a
token first, or every remote request is refused.

## Access control

The desktop builds needed none of this: they ran as the person at the keyboard. A hosted service is
reachable by anyone who can route to it, and this one starts downloads and serves files off disk.
So it is closed by default, with three modes:

| `Kea:AccessToken` | `Kea:AllowAnonymous` | Result |
| --- | --- | --- |
| empty | `false` (default) | **Local only.** Requests from anywhere but loopback get 401. |
| set | either | **Token required.** Every request must present it, local ones included. |
| empty | `true` | **Open to everyone.** Only sensible behind a proxy that authenticates first. |

Callers present the token as an `X-Kea-Token` header, or as a cookie after signing in through the
web UI. The cookie is `HttpOnly`, `SameSite=Strict`, and `Secure` whenever the request arrived over
HTTPS.

A few details worth knowing:

- Tokens are compared **in constant time**, after hashing, so neither the value nor its length
  leaks through timing.
- `X-Forwarded-For` is **ignored** when deciding whether a request is local. It is caller-supplied,
  so trusting it would let any remote request claim to be loopback.
- Setting a token makes it required for local requests too, so other software on the same host
  cannot quietly drive the service.

Generate a token with `openssl rand -base64 32`. Pass it as the `KEA_ACCESSTOKEN` environment
variable rather than putting it in `appsettings.json`, so it stays out of the repository.

### What the service will and will not touch

- It only ever reads, writes and deletes inside the configured library directory. Every path from a
  caller goes through one containment check that rejects traversal, absolute paths, embedded NULs,
  and any path crossing a symbolic link. Symlinks are also hidden from listings.
- It only fetches from `webtoons.com`. Submitted links are parsed and validated before any request
  is made, so the service cannot be pointed at your internal network.
- Absolute server paths are never returned to callers; job results report library-relative paths.

That said: **anyone who can reach the service and authenticate can delete anything in the library.**
Point the library at a directory you are willing to give it, not at your home folder.

## Behind a reverse proxy

TLS is the proxy's job. An nginx server block:

```nginx
server {
    listen 443 ssl;
    server_name kea.example.com;

    ssl_certificate     /etc/letsencrypt/live/kea.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/kea.example.com/privkey.pem;

    # Chapters can be large, and a job runs for as long as it runs.
    client_max_body_size 1m;
    proxy_read_timeout 3600s;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host              $host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # Progress arrives as server-sent events, which must not be buffered.
        proxy_buffering off;
        proxy_cache off;
    }
}
```

Caddy needs less:

```caddyfile
kea.example.com {
    reverse_proxy 127.0.0.1:8080 {
        flush_interval -1
    }
}
```

Keep the access token on even behind a proxy, unless the proxy itself authenticates — in which case
set `Kea:AllowAnonymous` to `true` deliberately.

## Running as a systemd service

```ini
# /etc/systemd/system/kea.service
[Unit]
Description=Kea web app
After=network.target

[Service]
Type=notify
User=kea
Group=kea
WorkingDirectory=/opt/kea
ExecStart=/usr/bin/dotnet /opt/kea/Kea.Web.dll
Restart=on-failure
RestartSec=5

Environment=ASPNETCORE_URLS=http://127.0.0.1:8080
Environment=KEA_LIBRARYPATH=/var/lib/kea/library
# Put the token in an 0600 file owned by the kea user, not in this unit.
EnvironmentFile=/etc/kea/kea.env

NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/kea/library

[Install]
WantedBy=multi-user.target
```

Publish to `/opt/kea` with:

```bash
dotnet publish src/Kea.Web -c Release -o /opt/kea
```

## Configuration

Every setting can be given as `Kea__Name` or `KEA_NAME` in the environment, or under a `Kea`
section in `appsettings.json`.

| Setting | Default | What it does |
| --- | --- | --- |
| `LibraryPath` | `library` | Where downloads are written and served from |
| `AccessToken` | *(empty)* | Shared secret; empty means local-only |
| `AllowAnonymous` | `false` | Serve everyone with no token — deliberate opt-out |
| `MaxConcurrentJobs` | `1` | Jobs running at once |
| `MaxQueuedJobs` | `50` | Backlog limit; submissions past it are refused |
| `MaxUrlsPerJob` | `25` | Comics per submission |
| `MaxParallelDownloads` | `3` | Images fetched at once within a chapter |
| `JobHistoryLimit` | `100` | Finished jobs kept in the list |
| `MaxStitchedHeight` | `30000` | Height cap for single-image output |

Job state lives in memory: a restart clears the job list but never the downloads, which are already
on disk. That keeps a single-user tool free of a database.

## HTTP API

The UI is built on this, and it is just as usable from `curl` or a script. Everything under `/api`
needs access, except `/api/config` and `/api/auth/login`.

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/config` | Whether a token is needed, and the available formats |
| `POST` | `/api/auth/login` | `{"token":"..."}` — sets the session cookie |
| `POST` | `/api/auth/logout` | Clears it |
| `POST` | `/api/jobs` | Queue a download; returns the job |
| `GET` | `/api/jobs` | All jobs, newest first |
| `GET` | `/api/jobs/{id}` | One job |
| `POST` | `/api/jobs/{id}/cancel` | Stop a queued or running job |
| `DELETE` | `/api/jobs/{id}` | Drop a finished job from the list |
| `GET` | `/api/events` | Server-sent events: `created`, `progress`, `updated`, `removed` |
| `GET` | `/api/library?path=` | List a folder |
| `GET` | `/api/library/file?path=` | Download a file (supports range requests) |
| `DELETE` | `/api/library?path=` | Delete a file or folder |
| `GET` | `/api/health` | Liveness probe |

Queueing a download:

```bash
curl -X POST http://localhost:8080/api/jobs \
  -H "X-Kea-Token: $KEA_ACCESSTOKEN" \
  -H "Content-Type: application/json" \
  -d '{
        "urls": ["https://www.webtoons.com/en/action/a-comic/list?title_no=1234"],
        "format": "cbz",
        "start": 1,
        "end": null,
        "comicFolders": true,
        "chapterFolders": true
      }'
```

Watching progress:

```bash
curl -N http://localhost:8080/api/events -H "X-Kea-Token: $KEA_ACCESSTOKEN"
```

`format` takes `pdf`, `cbz`, `images` or `one-image`. `end` may be `null` for "to the end".

## How it differs from the desktop app

- **Downloads are jobs, not a blocking operation.** The browser posts a job, gets an id straight
  back, and watches progress over the event stream. Closing the tab does not stop the download;
  reopening it picks the job back up.
- **Progress is throttled** to a few updates a second before it reaches the browser. The desktop
  app could repaint on every image; a network hop cannot.
- **The library is browsable.** The desktop app saved to a folder and left you to open it. Here the
  files are listed, downloadable and deletable in the browser, since the disk is on the server.
- **Jobs survive a page reload** but not a server restart.

## Known limitations

- **The scrapers are unverified against the live site**, exactly as noted in
  [CROSS_PLATFORM.md](CROSS_PLATFORM.md). Nothing about the web front-end changes that.
- **No multi-user support.** One token, shared. There are no accounts and no per-user libraries.
- **No resume.** A cancelled or failed job restarts from the beginning; already-downloaded chapters
  are kept, and reruns do not overwrite them.
- **Job history is in memory**, so it is lost on restart.
- **The Docker image is built by CI, not by hand here.** The service itself was run and exercised
  directly on Linux — endpoints, access control, path containment and the browser UI — but the
  image build and container start are covered by the `docker` job in
  `.github/workflows/crossplatform.yml` rather than by a local build.
