using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Globly.Api.Auth;
using Globly.Api.Data;
using Globly.Api.Data.Entities;
using Globly.Api.Moderation;
using Globly.Api.Posts;
using Globly.Api.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------- Cloudflare Tunnel ----------
// cloudflared terminates TLS and forwards plain HTTP to Kestrel, so Kestrel only listens on
// localhost (see "Urls" in appsettings) and we trust the forwarded headers from the tunnel.
const long maxUploadBytes = 100 * 1024 * 1024; // Cloudflare's proxied body limit on Free/Pro plans.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = maxUploadBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUploadBytes);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Real client IP as reported by Cloudflare.
    o.ForwardedForHeaderName = "CF-Connecting-IP";
    // Loopback is trusted by default (cloudflared on the same host). If cloudflared runs in a
    // container, add its network via ReverseProxy:KnownNetworks, e.g. "172.16.0.0/12".
    foreach (var cidr in config.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [])
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
});

// ---------- Database (PostgreSQL + PostGIS) ----------
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(config.GetConnectionString("Default"), npgsql => npgsql.UseNetTopologySuite()));

// ---------- Auth (ASP.NET Identity + JWT bearer) ----------
builder.Services.AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = false;
        o.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-";
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Lockout.MaxFailedAccessAttempts = 10;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>();

var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be set and at least 32 characters (use user-secrets or env var Jwt__Key).");
builder.Services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
builder.Services.AddSingleton<TokenService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = jwt.SigningKey,
            NameClaimType = "unique_name",
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        };
        o.Events = new JwtBearerEvents
        {
            // Reject tokens of deleted users or users whose security stamp changed (e.g. password change).
            OnTokenValidated = async ctx =>
            {
                var userManager = ctx.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
                var user = await userManager.FindByIdAsync(ctx.Principal!.GetUserId() ?? "");
                if (user is null || user.SecurityStamp != ctx.Principal!.FindFirst("security_stamp")?.Value)
                    ctx.Fail("User no longer valid.");
            },
        };
    });
builder.Services.AddAuthorization();

// ---------- Rate limiting (per real client IP) ----------
var authPerMinute = config.GetValue("RateLimit:AuthPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

// ---------- Modules ----------
builder.Services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
builder.Services.Configure<ModerationOptions>(config.GetSection(ModerationOptions.Section));
builder.Services.Configure<CaptureOptions>(config.GetSection(CaptureOptions.Section));
builder.Services.AddHttpClient(nameof(AttestationRevocations), c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<AttestationRevocations>();
var storageProvider = config[$"{StorageOptions.Section}:Provider"] ?? "Local";
if (storageProvider.Equals("R2", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IMediaStorage, R2MediaStorage>();
else
    builder.Services.AddSingleton<IMediaStorage, LocalMediaStorage>();

builder.Services.Configure<MediaOptions>(config.GetSection(MediaOptions.Section));
builder.Services.AddSingleton<IMediaSanitizer, MediaSanitizer>();
builder.Services.AddSingleton<IContentModerator, RuleBasedContentModerator>();
builder.Services.AddScoped<CommunityModeration>();
builder.Services.Configure<NsfwOptions>(config.GetSection(NsfwOptions.Section));
builder.Services.AddSingleton(NsfwClassifier.Create);
builder.Services.AddSingleton<NsfwScanSignal>();
builder.Services.AddHostedService<NsfwScanner>();
builder.Services.Configure<TurnstileOptions>(config.GetSection(TurnstileOptions.Section));
builder.Services.AddHttpClient<TurnstileVerifier>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<PostService>();
builder.Services.AddScoped<GeoService>();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

// ---------- CORS for the Flutter web build ----------
// The Android app doesn't need this; browsers do. Auth uses the Authorization header, not cookies,
// so no credentials are allowed.
var corsOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (builder.Environment.IsDevelopment())
        // `flutter run -d chrome` picks a random localhost port.
        p.SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.IsLoopback);
    else
        p.WithOrigins(corsOrigins);
    p.AllowAnyHeader().AllowAnyMethod().SetPreflightMaxAge(TimeSpan.FromHours(1));
}));

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(); // API explorer at /scalar
}

if (app.Services.GetRequiredService<IMediaStorage>() is LocalMediaStorage local)
{
    Directory.CreateDirectory(local.RootPath);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(local.RootPath),
        RequestPath = "/media",
    });
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

await DbInitializer.InitializeAsync(app.Services, config);

app.Run();
