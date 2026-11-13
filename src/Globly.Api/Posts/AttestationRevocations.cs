using System.Text.Json;

namespace Globly.Api.Posts;

/// <summary>Google's revoked attestation certificates, cached for an hour.</summary>
public class AttestationRevocations(IHttpClientFactory httpFactory, ILogger<AttestationRevocations> logger)
{
    public const string Url = "https://android.googleapis.com/attestation/status";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private HashSet<string> _revoked = [];
    private DateTime _nextRefresh = DateTime.MinValue;

    /// <summary>Revoked serials. Falls back to the last list if fetching fails.</summary>
    public async Task<IReadOnlySet<string>> GetAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < _nextRefresh) return _revoked;
        await _lock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < _nextRefresh) return _revoked;
            try
            {
                using var http = httpFactory.CreateClient(nameof(AttestationRevocations));
                await using var body = await http.GetStreamAsync(Url, ct);
                using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
                var revoked = new HashSet<string>();
                foreach (var entry in json.RootElement.GetProperty("entries").EnumerateObject())
                {
                    var serial = entry.Name.ToLowerInvariant().TrimStart('0');
                    revoked.Add(serial.Length == 0 ? "0" : serial);
                }
                _revoked = revoked;
                _nextRefresh = DateTime.UtcNow + MaxAge;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException
                                          or KeyNotFoundException or InvalidOperationException)
            {
                ct.ThrowIfCancellationRequested();
                logger.LogWarning(e, "Could not fetch the attestation revocation list; using the previous one");
                _nextRefresh = DateTime.UtcNow + RetryAfterFailure;
            }
            return _revoked;
        }
        finally
        {
            _lock.Release();
        }
    }
}
