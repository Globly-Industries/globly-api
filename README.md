# Globly API

ASP.NET Core (.NET 10) backend for **Globly**, a geospatial social network.
PostgreSQL + PostGIS stores users, posts and locations; Cloudflare R2 stores media;
the server is exposed to the internet through a Cloudflare Tunnel.

```
Flutter app ──HTTPS──▶ Cloudflare ──Tunnel──▶ cloudflared ──HTTP──▶ Kestrel 127.0.0.1:5021
     │                                                                  │
     └──────── direct media streaming (public/presigned URL) ◀── R2 ◀──┘ (server-only writes)
```

## Project layout (`src/Globly.Api`)

| Folder        | Module                                              |
|---------------|-----------------------------------------------------|
| `Auth/`       | User authentication module (Identity + JWT)         |
| `Posts/`      | Content processing + geospatial module (map clustering, nearby, feed) |
| `Ratings/`    | Publication rating module (votes, comments, moderation reports) |
| `Admin/`      | Administration module                               |
| `Storage/`    | Cloudflare R2 storage (+ local disk for development) |
| `Moderation/` | Automatic content checks and the NSFW filter        |
| `Data/`       | EF Core DbContext, entities, migrations             |

## Running locally

```powershell
docker compose up -d db                # PostGIS on localhost:5432
dotnet run --project src/Globly.Api    # http://localhost:5021, API explorer at /scalar
```

Development settings (`appsettings.Development.json`) use local disk instead of R2 and seed an
admin user `admin` / `admin12345`. Migrations are applied automatically on startup.

Android emulator reaches the API at `http://10.0.2.2:5021`.

### Migrations

```powershell
dotnet tool restore
dotnet ef migrations add <Name> --project src/Globly.Api --output-dir Data/Migrations
```

### Tests

```powershell
dotnet test tests/Globly.Api.Tests
```

The video and audio tests need `ffmpeg` and `ffprobe` on PATH, and show as skipped without them.

## Media metadata

Uploads are cleaned before they are stored (`Storage/MediaSanitizer.cs`), because camera files can
contain the exact GPS position and media URLs are public. Images go through Magick.NET (metadata
removed, pixels turned upright, colour profile kept) and video/audio through ffmpeg (metadata removed,
no re-encoding). If a file can't be cleaned it is rejected, never stored as it is.

ffmpeg is installed in the Docker image. **To upload video or audio when running locally, `ffmpeg` must
be on PATH** (or set `Media__FfmpegPath`), otherwise those uploads fail with a 500. Files uploaded before
this was added were not cleaned.

## Deploying to the server (Windows + Docker Desktop)

Everything runs in Docker: PostGIS, the API and `cloudflared`. The tunnel's public hostname
`api.dagnis.my.id` must point to **`http://api:8080`** (the API container), not localhost.

One-time setup on the server:
1. Docker Desktop -> Settings -> General -> enable **Start Docker Desktop when you sign in**.
   Docker Desktop only runs while a user is logged in, so enable automatic sign-in on the server
   and turn off sleep in Windows power settings.
2. Install Git, then `git clone https://github.com/Globly-Industries/globly-api.git`.
3. `copy .env.example .env` and fill it in (R2 keys, tunnel token, random passwords).
4. `docker compose --profile prod up -d --build`

Check: `http://localhost:5021/health` on the server, then `https://api.dagnis.my.id/health`.

Update to a new version:

```powershell
git pull
docker compose --profile prod up -d --build
```

Logs: `docker compose logs -f api`. Data lives in the `pgdata` Docker volume and survives updates
(`docker compose down -v` deletes it!).

### Cloudflare notes
- **Upload limit:** Cloudflare rejects proxied request bodies over 100 MB (Free/Pro). Video uploads are
  capped at 95 MB by `Moderation:MaxVideoBytes`. Larger files would need direct-to-R2 presigned uploads.
- **Client IP:** `CF-Connecting-IP` is used as the client address (rate limiting). If cloudflared is
  not on the same host/loopback, add its network to `ReverseProxy:KnownNetworks`.
- **Media reads** go straight from the app to R2, never through the tunnel.

### Human check at registration (Cloudflare Turnstile)
Off until both keys are set, so it never blocks anything by accident.
1. Cloudflare dashboard -> **Turnstile -> Add widget**: name `Globly`, hostname `globly.dagnis.my.id`,
   widget mode **Managed**.
2. Put the keys in the server's `.env`: `TURNSTILE_SITE_KEY=...` and `TURNSTILE_SECRET_KEY=...`,
   then `docker compose --profile prod up -d`.

The app asks `GET /api/auth/config` for the site key, so no app rebuild is needed. Development uses
Cloudflare's always-pass test keys (`appsettings.Development.json`).

## Community moderation
Posts are hidden automatically (gone from map, feed and nearby; the author and admins still see them,
with the reason) when either rule is met:
- `Moderation:ReportThreshold` (default 5) different users report the post, or
- it has at least `DownvoteHideMinVotes` (10) votes and `DownvoteHidePercent` (80%) or more are downvotes.

Hidden posts wait in the admin queue (`GET /api/admin/posts`). **Restore** makes the post visible again,
clears its reports and stops the downvote rule from hiding it again (new reports still count).
**Delete** removes it with its media. Admins can also hide a post manually.

## NSFW filter
Photos and videos are scored by an image classifier after they are published
(`Moderation/NsfwScanner.cs`), so uploads never wait for it. The model is an ONNX export of
[Falconsai/nsfw_image_detection](https://huggingface.co/Falconsai/nsfw_image_detection) (Apache-2.0),
run on the CPU with ONNX Runtime. Videos are sampled at one frame every 3 seconds and score as their
worst frame; animated images up to 8 frames. Audio is not scanned.
- Score ≥ `Moderation:Nsfw:HideThreshold` (0.9): hidden with reason `Automatic`, waits in the admin queue.
- Score ≥ `ReviewThreshold` (0.6): stays visible but goes to the admin queue as "flagged".
- **Restore** in the queue approves the post; the filter never hides an approved post.

Scores are stored on the post (`NsfwScore`, shown to admins only), so the thresholds can be tuned
from real data. Posts from before the filter existed are scanned in the background, newest first.

The Docker image downloads the model (87 MB, pinned and checksum-verified). **To scan when running
locally**, download it once (without it the API runs normally and logs that scanning is off):

```powershell
mkdir src/Globly.Api/models
curl.exe -L -o src/Globly.Api/models/nsfw.onnx https://huggingface.co/onnx-community/nsfw_image_detection-ONNX/resolve/1ceb3c7fe1e9f3f2507e6df577437f23a9149fd5/onnx/model_quantized.onnx
```

The model tests show as skipped until it is there.

Errors carry a machine-readable `code` (e.g. `username_taken`, `captcha_failed`) next to the English
`detail`, so the app can show them in the user's language.

### Web demo (Flutter web on Cloudflare, globly.dagnis.my.id)
- **API CORS:** browsers may call the API only from origins listed in `Cors:AllowedOrigins`
  (default `https://globly.dagnis.my.id`). In Development any `localhost` origin is allowed.
- **R2 CORS:** Flutter web loads images with fetch requests, so the bucket must allow the site too.
  In the bucket: Settings -> CORS Policy:

```json
[
  {
    "AllowedOrigins": ["https://globly.dagnis.my.id"],
    "AllowedMethods": ["GET", "HEAD"],
    "AllowedHeaders": ["*"],
    "MaxAgeSeconds": 3600
  }
]
```

## API overview

| Method | Route | Auth |
|---|---|---|
| GET | `/api/auth/config` (Turnstile site key or null) | – |
| POST | `/api/auth/register` (+ `captchaToken` when Turnstile is on), `/api/auth/login` | – |
| GET | `/api/auth/me` | user |
| GET | `/api/posts/map?minLat&minLng&maxLat&maxLng&zoom[&types&since&minScore]` | – |
| GET | `/api/posts/nearby?lat&lng[&radiusKm&limit]` | – |
| GET | `/api/posts/feed?page&pageSize` | – |
| GET | `/api/posts/{id}` | – |
| GET | `/api/posts/mine` | user |
| POST | `/api/posts` (multipart: title, description, mediaType, latitude, longitude, file) | user |
| PUT / DELETE | `/api/posts/{id}` | author or admin |
| PUT | `/api/posts/{id}/vote` `{ value: 1 / -1 / 0 }` | user |
| GET / POST | `/api/posts/{id}/comments` | GET: – / POST: user |
| DELETE | `/api/comments/{id}` | author or admin |
| POST | `/api/posts/{id}/report` | user |
| GET | `/api/admin/posts?hiddenOnly`, `/api/admin/posts/{id}/reports`, `/api/admin/users` | admin |
| POST | `/api/admin/posts/{id}/restore`, `/api/admin/posts/{id}/hide` | admin |
| DELETE | `/api/admin/posts/{id}`, `/api/admin/users/{id}` | admin |
| GET | `/health` | – |
