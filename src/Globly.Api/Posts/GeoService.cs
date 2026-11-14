using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Globly.Api.Posts;

/// <summary>Geospatial module: viewport queries with server-side grid clustering, and nearest-post search.</summary>
public class GeoService(AppDbContext db)
{
    public static readonly GeometryFactory Geometry =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    public static Point ToPoint(double lat, double lng) => Geometry.CreatePoint(new Coordinate(lng, lat));

    private const int MaxRows = 2000;

    /// <summary>Approximate on-screen size of a cluster cell, in pixels.</summary>
    private const double ClusterCellPixels = 60;


    public async Task<List<MapPointDto>> GetMapPointsAsync(MapQuery q, PostFilter f, CancellationToken ct)
    {
        // Viewport crossing the antimeridian is split into two boxes.
        if (q.MinLng > q.MaxLng)
        {
            var west = await QueryBox(q.MinLat, q.MinLng, q.MaxLat, 180, q.Zoom, f, ct);
            var east = await QueryBox(q.MinLat, -180, q.MaxLat, q.MaxLng, q.Zoom, f, ct);
            return [.. west, .. east];
        }
        return await QueryBox(q.MinLat, q.MinLng, q.MaxLat, q.MaxLng, q.Zoom, f, ct);
    }

    private async Task<List<MapPointDto>> QueryBox(
        double minLat, double minLng, double maxLat, double maxLng, double zoom, PostFilter f, CancellationToken ct)
    {
        // Degrees per pixel at this zoom for 256px web-mercator tiles (longitude axis).
        var degreesPerPixel = 360.0 / (256 * Math.Pow(2, zoom));
        // Clusters even at the closest zoom, so posts on the same spot become one tappable stack.
        var cell = degreesPerPixel * ClusterCellPixels;

        var types = (f.Types ?? []).Select(t => (int)t).ToArray();
        var since = f.Since?.ToUniversalTime() ?? DateTime.UnixEpoch;
        var minScore = f.MinScore ?? int.MinValue;
        var w = FeedRanking.Recent;

        // A cluster sits on its best post, as ranked in the Recent feed (without awards, which
        // would cost a lookup per post), so zooming in on it always finds that post there.
        var rows = await db.Database.SqlQuery<MapRow>($"""
            WITH scored AS (
                SELECT p.*,
                       ST_SnapToGrid(p."Location", {cell}) AS cell,
                       {w.Likes} * sign((p."Upvotes" - p."Downvotes")::float8)
                           * least(1, ln(1 + abs(p."Upvotes" - p."Downvotes")::float8) / ln(1 + {FeedRanking.LikesFloor}::float8))
                       + {w.Views} * least(1, ln(1 + greatest(p."Views", 0)::float8) / ln(1 + {FeedRanking.ViewsFloor}::float8))
                       + {w.Tab} * power(0.5, greatest(0, extract(epoch FROM ({DateTime.UtcNow} - p."CreatedAt"))::float8 / 3600)
                           / {FeedRanking.RecencyHalfLifeHours}) AS score
                FROM "Posts" p
                WHERE p."Location" && ST_MakeEnvelope({minLng}, {minLat}, {maxLng}, {maxLat}, 4326)
                  AND NOT p."IsHidden"
                  AND (cardinality({types}) = 0 OR p."MediaType" = ANY({types}))
                  AND p."CreatedAt" >= {since}
                  AND (p."Upvotes" - p."Downvotes") >= {minScore}
            )
            SELECT ST_Y((array_agg(s."Location" ORDER BY s.score DESC, s."CreatedAt" DESC))[1]) AS "Latitude",
                   ST_X((array_agg(s."Location" ORDER BY s.score DESC, s."CreatedAt" DESC))[1]) AS "Longitude",
                   COUNT(*)::int AS "Count",
                   (array_agg(s."Id" ORDER BY s.score DESC, s."CreatedAt" DESC))[1] AS "PostId",
                   (array_agg(s."MediaType" ORDER BY s.score DESC, s."CreatedAt" DESC))[1] AS "MediaType",
                   (array_agg(s."Title" ORDER BY s.score DESC, s."CreatedAt" DESC))[1] AS "Title",
                   (array_agg(s."LocationRadiusMeters" ORDER BY s.score DESC, s."CreatedAt" DESC))[1] AS "LocationRadiusMeters",
                   max(s.score) AS "Score",
                   ST_YMin(ST_Extent(s."Location")) AS "MinLat",
                   ST_XMin(ST_Extent(s."Location")) AS "MinLng",
                   ST_YMax(ST_Extent(s."Location")) AS "MaxLat",
                   ST_XMax(ST_Extent(s."Location")) AS "MaxLng"
            FROM scored s
            GROUP BY s.cell
            LIMIT {MaxRows}
            """).ToListAsync(ct);

        return rows.Select(r => r.Count == 1
                ? new MapPointDto(r.Latitude, r.Longitude, 1, r.PostId, (MediaType)r.MediaType, r.Title, r.LocationRadiusMeters, r.Score)
                : new MapPointDto(r.Latitude, r.Longitude, r.Count, null, (MediaType)r.MediaType, null, null, r.Score,
                    new MapBoundsDto(r.MinLat, r.MinLng, r.MaxLat, r.MaxLng)))
            .ToList();
    }

    /// <summary>Visible posts inside a box (e.g. a map cluster's bounds).</summary>
    public IQueryable<Post> InArea(double minLat, double minLng, double maxLat, double maxLng)
    {
        // A cluster of posts on one spot has a zero-size box; the margin also absorbs rounding.
        const double margin = 1e-7;
        return db.Posts.FromSql($"""
            SELECT * FROM "Posts"
            WHERE NOT "IsHidden"
              AND "Location" && ST_MakeEnvelope({minLng - margin}, {minLat - margin}, {maxLng + margin}, {maxLat + margin}, 4326)
            """);
    }

    /// <summary>Posts within <paramref name="radiusKm"/> of a point (true geodesic distance).</summary>
    public IQueryable<Post> Nearby(double lat, double lng, double radiusKm)
    {
        var meters = radiusKm * 1000;
        var origin = ToPoint(lat, lng);
        return db.Posts
            .FromSql($"""
                SELECT * FROM "Posts"
                WHERE NOT "IsHidden"
                  AND ST_DWithin("Location"::geography, ST_SetSRID(ST_MakePoint({lng}, {lat}), 4326)::geography, {meters})
                """)
            .OrderBy(p => p.Location.Distance(origin));
    }

    public sealed class MapRow
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public int Count { get; set; }
        public Guid PostId { get; set; }
        public int MediaType { get; set; }
        public string Title { get; set; } = "";
        public int? LocationRadiusMeters { get; set; }
        public double Score { get; set; }
        public double MinLat { get; set; }
        public double MinLng { get; set; }
        public double MaxLat { get; set; }
        public double MaxLng { get; set; }
    }
}
