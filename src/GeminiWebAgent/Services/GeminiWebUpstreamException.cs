namespace GeminiWebAgent.Services;

/// <summary>
/// A failure talking to Gemini Web that should be reported to the client as an HTTP error.
/// </summary>
public class GeminiWebUpstreamException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>
    /// Google answers abusive-looking traffic with 429 or a redirect to https://www.google.com/sorry/...
    /// (a reCAPTCHA interstitial). This also covers a request that was auto-redirected there.
    /// </summary>
    public static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) return true;

        var location = response.Headers.Location?.ToString();
        if (location != null && location.Contains("/sorry/", StringComparison.OrdinalIgnoreCase)) return true;

        return response.RequestMessage?.RequestUri?.AbsolutePath.StartsWith("/sorry/", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static GeminiWebUpstreamException RateLimited() => new(429,
        "Google is rate-limiting this IP address / account (anti-abuse page google.com/sorry). " +
        "Stop sending requests for a while; if it persists, open https://gemini.google.com in a browser on the same network and pass the verification.");

    /// <summary>
    /// Maps any exception raised while serving a chat completion to (HTTP status, message, error code).
    /// </summary>
    public static (int StatusCode, string Message, string Code) Describe(Exception ex) => ex switch
    {
        GeminiWebUpstreamException upstream => (upstream.StatusCode, upstream.Message, upstream.StatusCode switch { 401 => "invalid_cookies", 429 => "rate_limited", _ => "upstream_error" }),
        InvalidOperationException { Message: var m } when m.Contains("AT token", StringComparison.Ordinal) => (401, m, "invalid_cookies"),
        HttpRequestException or TaskCanceledException or IOException => (502, $"Gemini Web request failed: {ex.Message}", "upstream_error"),
        _ => (500, ex.Message, "internal_error")
    };
}
