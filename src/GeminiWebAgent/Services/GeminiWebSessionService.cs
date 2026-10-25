using System.Text.RegularExpressions;
using GeminiWebAgent.Configurations;
using Microsoft.Extensions.Options;

namespace GeminiWebAgent.Services;

/// <summary>
/// Manages session state for the Gemini Web interface.
/// Extracts required parameters (build label, AT token, session ID) from the web page.
/// </summary>
public partial class GeminiWebSessionService(
    IHttpClientFactory httpClientFactory,
    GeminiWebCookieManager cookieManager,
    IOptions<GeminiWebOptions> options,
    ILogger<GeminiWebSessionService> logger)
{
    private readonly GeminiWebOptions _options = options.Value;
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient("GeminiWebSession");
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private string _buildLabel = string.Empty;
    private string _atToken = string.Empty;
    private string _sessionId = string.Empty;
    private string _lastCookies = string.Empty;
    private int _requestId = 100000;
    private DateTime _lastRefresh = DateTime.MinValue;

    /// <summary>
    /// Gets the current build label, refreshing if needed.
    /// </summary>
    public async Task<string> GetBuildLabelAsync(string cookies)
    {
        await EnsureSessionAsync(cookies);
        return _buildLabel;
    }

    /// <summary>
    /// Gets the current AT token, refreshing if needed.
    /// </summary>
    public async Task<string> GetAtTokenAsync(string cookies)
    {
        await EnsureSessionAsync(cookies);
        return _atToken;
    }

    /// <summary>
    /// Gets the current session ID.
    /// </summary>
    public async Task<string> GetSessionIdAsync(string cookies)
    {
        await EnsureSessionAsync(cookies);
        return _sessionId;
    }

    /// <summary>
    /// Gets and increments the request ID.
    /// </summary>
    public int GetNextRequestId()
    {
        return Interlocked.Add(ref _requestId, 100000);
    }

    /// <summary>
    /// Whether the session is currently initialized and valid.
    /// </summary>
    public bool IsInitialized => !string.IsNullOrEmpty(_buildLabel) && !string.IsNullOrEmpty(_atToken);

    /// <summary>
    /// Forces a session refresh on next call.
    /// </summary>
    public void InvalidateSession()
    {
        _lastRefresh = DateTime.MinValue;
        _lastCookies = string.Empty;
    }

    private async Task EnsureSessionAsync(string cookies)
    {
        // Refresh every 30 minutes or if not initialized
        if (IsInitialized && (DateTime.UtcNow - _lastRefresh).TotalMinutes < 30)
            return;

        await _semaphore.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (IsInitialized && (DateTime.UtcNow - _lastRefresh).TotalMinutes < 30)
                return;

            await RefreshSessionAsync(cookies);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task RefreshSessionAsync(string cookies)
    {
        logger.LogInformation("Refreshing Gemini Web session parameters...");

        var targetUrl = $"{_options.BaseUrl.TrimEnd('/')}/app";
        var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
        request.Headers.Add("Cookie", cookies);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

        var response = await _httpClient.SendAsync(request);
        await cookieManager.UpdateFromResponseAsync(response);
        var html = await response.Content.ReadAsStringAsync();

        if (GeminiWebUpstreamException.IsRateLimited(response))
        {
            throw GeminiWebUpstreamException.RateLimited();
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Fetch from /app failed with {StatusCode}, falling back to BaseUrl...", response.StatusCode);
            var fallbackReq = new HttpRequestMessage(HttpMethod.Get, _options.BaseUrl);
            fallbackReq.Headers.Add("Cookie", cookies);
            fallbackReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            response = await _httpClient.SendAsync(fallbackReq);
            html = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Failed to fetch Gemini Web page: {StatusCode}", response.StatusCode);
                throw new HttpRequestException($"Failed to fetch Gemini Web page: {response.StatusCode}");
            }
        }

        // Extract build label (bl parameter / cfb2h)
        var blMatch = BuildLabelRegex().Match(html);
        if (blMatch.Success)
        {
            _buildLabel = blMatch.Groups[1].Value;
            logger.LogInformation("Extracted build label: {BuildLabel}", _buildLabel);
        }
        else
        {
            // Fallback: search for any boq_ server build label
            var fallbackBoq = FallbackBuildLabelRegex().Match(html);
            if (fallbackBoq.Success)
            {
                _buildLabel = fallbackBoq.Value;
                logger.LogInformation("Extracted fallback build label: {BuildLabel}", _buildLabel);
            }
            else
            {
                _buildLabel = "boq_gemini-web-uiserver_20261022.00_p0";
                logger.LogWarning("Could not extract build label, using default fallback: {BuildLabel}", _buildLabel);
            }
        }

        // Extract AT token (SNlM0e)
        var atMatch = AtTokenRegex().Match(html);
        if (atMatch.Success)
        {
            _atToken = atMatch.Groups[1].Value;
            logger.LogInformation("Extracted AT token successfully");
        }
        else if (!string.IsNullOrEmpty(_atToken))
        {
            logger.LogWarning("Could not extract new AT token from Gemini Web page, retaining existing token");
        }
        else
        {
            logger.LogWarning("Could not extract AT token from Gemini Web page");
            throw new InvalidOperationException("Could not extract AT token (SNlM0e) from Gemini Web page. Cookies may be invalid or expired.");
        }

        // Extract session ID (FdrFJe)
        var sidMatch = SessionIdRegex().Match(html);
        if (sidMatch.Success)
        {
            _sessionId = sidMatch.Groups[1].Value;
            logger.LogInformation("Extracted session ID successfully");
        }
        else
        {
            // Session ID is optional; generate a random one
            _sessionId = Random.Shared.NextInt64(1000000000, 9999999999).ToString();
            logger.LogInformation("Generated random session ID: {SessionId}", _sessionId);
        }

        _requestId = 100000;
        _lastRefresh = DateTime.UtcNow;

        logger.LogInformation("Gemini Web session refreshed successfully");
    }

    [GeneratedRegex(@"""cfb2h""\s*:\s*""([^""]+)""")]
    private static partial Regex BuildLabelRegex();

    [GeneratedRegex(@"""SNlM0e""\s*:\s*""([^""]+)""")]
    private static partial Regex AtTokenRegex();

    [GeneratedRegex(@"""FdrFJe""\s*:\s*""([^""]+)""")]
    private static partial Regex SessionIdRegex();

    [GeneratedRegex(@"boq_[a-zA-Z0-9_\-\.]+")]
    private static partial Regex FallbackBuildLabelRegex();
}
