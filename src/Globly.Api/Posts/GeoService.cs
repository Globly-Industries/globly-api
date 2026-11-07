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

    /// <summary>At or above this zoom posts are never clustered.</summary>
    private const double NoClusterZoom = 17;

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
        var cell = zoom >= NoClusterZoom ? 1e-7 : degreesPerPixel * ClusterCellPixels;

        var types = (f.Types ?? []).Select(t => (int)t).ToArray();
        var since = f.Since?.ToUniversalTime() ?? DateTime.UnixEpoch;
        var minScore = f.MinScore ?? int.MinValue;

        var rows = await db.Database.SqlQuery<MapRow>($"""
            SELECT ST_Y(ST_Centroid(ST_Collect(p."Location"))) AS "Latitude",
                   ST_X(ST_Centroid(ST_Collect(p."Location"))) AS "Longitude",
                   COUNT(*)::int AS "Count",
                   (array_agg(p."Id"))[1] AS "PostId",
                   (array_agg(p."MediaType"))[1] AS "MediaType",
                   (array_agg(p."Title"))[1] AS "Title"
            FROM "Posts" p
            WHERE p."Location" && ST_MakeEnvelope({minLng}, {minLat}, {maxLng}, {maxLat}, 4326)
              AND NOT p."IsHidden"
              AND (cardinality({types}) = 0 OR p."MediaType" = ANY({types}))
              AND p."CreatedAt" >= {since}
              AND (p."Upvotes" - p."Downvotes") >= {minScore}
            GROUP BY ST_SnapToGrid(p."Location", {cell})
            LIMIT {MaxRows}
            """).ToListAsync(ct);

        return rows.Select(r => r.Count == 1
                ? new MapPointDto(r.Latitude, r.Longitude, 1, r.PostId, (MediaType)r.MediaType, r.Title)
                : new MapPointDto(r.Latitude, r.Longitude, r.Count, null, null, null))
            .ToList();
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
    }
}
