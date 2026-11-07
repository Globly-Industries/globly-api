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
| `Ratings/`    | Publication rating module (votes, comments, emoji, moderation reports) |
| `Admin/`      | Administration module                               |
| `Storage/`    | Cloudflare R2 storage (+ local disk for development) |
| `Moderation/` | Automatic content checks before publishing          |
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

### Web demo (Flutter web on Cloudflare Pages)
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
| POST | `/api/auth/register`, `/api/auth/login` | – |
| GET | `/api/auth/me` | user |
| GET | `/api/posts/map?minLat&minLng&maxLat&maxLng&zoom[&types&since&minScore]` | – |
| GET | `/api/posts/nearby?lat&lng[&radiusKm&limit]` | – |
| GET | `/api/posts/feed?page&pageSize` | – |
| GET | `/api/posts/{id}` | – |
| GET | `/api/posts/mine` | user |
| POST | `/api/posts` (multipart: title, description, mediaType, latitude, longitude, file) | user |
| PUT / DELETE | `/api/posts/{id}` | author or admin |
| PUT | `/api/posts/{id}/vote` `{ value: 1 / -1 / 0 }` | user |
| GET / POST | `/api/posts/{id}/comments` (`?kind=All/Text/Emoji`) | GET: – / POST: user |
| GET | `/api/posts/{id}/emoji` | – |
| DELETE | `/api/comments/{id}` | author or admin |
| POST | `/api/posts/{id}/report` | user |
| GET | `/api/admin/posts?flaggedOnly`, `/api/admin/posts/{id}/reports`, `/api/admin/users` | admin |
| POST | `/api/admin/posts/{id}/dismiss-reports` | admin |
| DELETE | `/api/admin/posts/{id}`, `/api/admin/users/{id}` | admin |
| GET | `/health` | – |
