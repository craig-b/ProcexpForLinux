using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Procexp.Model;

namespace Procexp.Provenance;

/// <summary>
/// Fronts the VirusTotal v3 file-reputation API, with an on-disk cache and a
/// rate limiter.
/// </summary>
/// <remarks>
/// Ports the macOS client directly; only the key storage and cache location
/// change. The public API allows four requests a minute, which is low enough that
/// caching is not an optimisation but a requirement — without it, opening the
/// Properties window twice on the same binary would burn half the budget.
/// </remarks>
public sealed class VirusTotalClient : IDisposable
{
    private const string Endpoint = "https://www.virustotal.com/api/v3/files/";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTimeOffset> _requestTimes = new();
    private readonly int _maxRequestsPerWindow;
    private readonly Func<string?> _apiKeyProvider;
    private readonly string? _cachePath;

    private Dictionary<string, VirusTotalResult>? _cache;

    public VirusTotalClient(int maxRequestsPerMinute = 4, Func<string?>? apiKeyProvider = null)
    {
        _maxRequestsPerWindow = Math.Max(1, maxRequestsPerMinute);
        _apiKeyProvider = apiKeyProvider ?? DefaultApiKey;
        _cachePath = ResolveCachePath();
    }

    /// <summary>
    /// The process-wide client. The public API allows four requests a minute per
    /// key; separate instances would each enforce that limit independently and
    /// overrun it together.
    /// </summary>
    public static VirusTotalClient Shared { get; } = new();

    /// <summary>
    /// Whether an API key is configured. Checking is opt-in — no key, no network
    /// traffic — and callers use this to say "not configured" rather than
    /// rendering the same blank as "not yet checked".
    /// </summary>
    public bool HasApiKey => !string.IsNullOrEmpty(_apiKeyProvider());

    /// <summary>
    /// Reputation for a hash, from cache when possible.
    /// </summary>
    /// <returns>
    /// Null when no API key is configured, or when VirusTotal has never seen the
    /// file. Both are ordinary outcomes rather than errors.
    /// </returns>
    public async ValueTask<VirusTotalResult?> ResultAsync(
        string sha256,
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LoadCache();

            if (_cache!.TryGetValue(sha256, out var cached))
            {
                return cached;
            }

            var apiKey = _apiKeyProvider();
            if (string.IsNullOrEmpty(apiKey))
            {
                return null;
            }

            await AwaitRateLimitSlotAsync(cancellationToken).ConfigureAwait(false);

            var result = await FetchAsync(sha256, apiKey, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                _cache[sha256] = result;
                PersistCache();
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<VirusTotalResult?> FetchAsync(
        string sha256,
        string apiKey,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + sha256);
        request.Headers.Add("x-apikey", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new ProviderException(
                ProviderErrorKind.Underlying,
                $"VirusTotal request failed: {e.Message}"
            );
        }

        using (response)
        {
            // An unknown file is a legitimate answer, not a failure.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderException(
                    ProviderErrorKind.Underlying,
                    $"VirusTotal returned {(int)response.StatusCode} {response.ReasonPhrase}"
                );
            }

            var body = await response
                .Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            VirusTotalResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(
                    body,
                    VirusTotalJsonContext.Default.VirusTotalResponse
                );
            }
            catch (JsonException e)
            {
                throw new ProviderException(
                    ProviderErrorKind.Underlying,
                    $"VirusTotal response was unreadable: {e.Message}"
                );
            }

            var stats = parsed?.Data?.Attributes?.LastAnalysisStats;
            if (stats is null)
            {
                return null;
            }

            // "Positives" counts detections; the denominator is every engine that
            // returned a verdict, which excludes those that timed out or could not
            // process the file.
            var positives = stats.Malicious + stats.Suspicious;
            var total = positives + stats.Undetected + stats.Harmless;

            return new VirusTotalResult
            {
                Positives = positives,
                Total = total,
                Permalink = $"https://www.virustotal.com/gui/file/{sha256}",
                CheckedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    /// <summary>Block until a request slot frees up in the sliding window.</summary>
    private async Task AwaitRateLimitSlotAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = DateTimeOffset.UtcNow;

            while (_requestTimes.Count > 0 && now - _requestTimes.Peek() > Window)
            {
                _requestTimes.Dequeue();
            }

            if (_requestTimes.Count < _maxRequestsPerWindow)
            {
                _requestTimes.Enqueue(now);
                return;
            }

            var wait = Window - (now - _requestTimes.Peek());
            await Task.Delay(
                    wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(50),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    // ---- Key storage --------------------------------------------------------

    /// <summary>
    /// Read the API key.
    /// </summary>
    /// <remarks>
    /// The macOS build keeps this in the Keychain. Linux has no single equivalent
    /// — the Secret Service API exists but binds the app to a running desktop
    /// keyring daemon, which a tool that must work over SSH cannot assume. An
    /// environment variable and a mode-600 config file cover both cases; wiring
    /// libsecret in as a preferred source later would not change this interface.
    /// </remarks>
    private static string? DefaultApiKey()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("VIRUSTOTAL_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        var path = Path.Combine(ConfigDirectory(), "virustotal.key");
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string ConfigDirectory()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = string.IsNullOrEmpty(xdg)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config"
            )
            : xdg;

        return Path.Combine(root, "procexp");
    }

    private static string? ResolveCachePath()
    {
        try
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            var root = string.IsNullOrEmpty(xdg)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".cache"
                )
                : xdg;

            var directory = Path.Combine(root, "procexp");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "virustotal-cache.json");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void LoadCache()
    {
        if (_cache is not null)
        {
            return;
        }

        _cache = [];

        if (_cachePath is null || !File.Exists(_cachePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_cachePath);
            var loaded = JsonSerializer.Deserialize(
                json,
                VirusTotalJsonContext.Default.DictionaryStringVirusTotalResult
            );
            if (loaded is not null)
            {
                _cache = loaded;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt cache is not worth failing over; start fresh.
        }
    }

    private void PersistCache()
    {
        if (_cachePath is null || _cache is null)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(
                _cache,
                VirusTotalJsonContext.Default.DictionaryStringVirusTotalResult
            );
            File.WriteAllText(_cachePath, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Caching is best-effort.
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}

// ---- Response shapes --------------------------------------------------------

internal sealed record VirusTotalResponse
{
    [JsonPropertyName("data")]
    public VirusTotalData? Data { get; init; }
}

internal sealed record VirusTotalData
{
    [JsonPropertyName("attributes")]
    public VirusTotalAttributes? Attributes { get; init; }
}

internal sealed record VirusTotalAttributes
{
    [JsonPropertyName("last_analysis_stats")]
    public VirusTotalStats? LastAnalysisStats { get; init; }
}

internal sealed record VirusTotalStats
{
    [JsonPropertyName("malicious")]
    public int Malicious { get; init; }

    [JsonPropertyName("suspicious")]
    public int Suspicious { get; init; }

    [JsonPropertyName("undetected")]
    public int Undetected { get; init; }

    [JsonPropertyName("harmless")]
    public int Harmless { get; init; }
}

[JsonSerializable(typeof(VirusTotalResponse))]
[JsonSerializable(typeof(Dictionary<string, VirusTotalResult>))]
internal sealed partial class VirusTotalJsonContext : JsonSerializerContext;
