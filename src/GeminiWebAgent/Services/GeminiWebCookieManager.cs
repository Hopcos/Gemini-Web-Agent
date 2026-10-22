using System.Text.Json;
using GeminiWebAgent.Configurations;
using Microsoft.Extensions.Options;

namespace GeminiWebAgent.Services;

/// <summary>
/// 管理 Cookies 的获取、热重载、自动拦截与滚动持久化。
/// 避免在 Linux / Docker 等无头环境下人工频繁按 F12 获取更新。
/// </summary>
public class GeminiWebCookieManager
{
    private readonly GeminiWebOptions _options;
    private readonly ILogger<GeminiWebCookieManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private readonly Dictionary<string, string> _cookieStore = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastFileReadTime = DateTime.MinValue;

    public GeminiWebCookieManager(IOptions<GeminiWebOptions> options, ILogger<GeminiWebCookieManager> logger)
    {
        _options = options.Value;
        _logger = logger;

        InitializeInitialCookies();
    }

    /// <summary>
    /// 初始化加载初始 Cookies
    /// </summary>
    private void InitializeInitialCookies()
    {
        if (string.Equals(_options.CookieSource, "File", StringComparison.OrdinalIgnoreCase))
        {
            TryLoadFromFile();
        }
        else if (!string.IsNullOrWhiteSpace(_options.Cookies))
        {
            ParseAndMergeCookieString(_options.Cookies);
            _logger.LogInformation("Loaded initial cookies from configuration (length: {Length})", _options.Cookies.Length);
        }
    }

    /// <summary>
    /// 获取当前应使用的 Cookie 字符串
    /// </summary>
    public async Task<string> GetCookiesAsync(string? requestBearer = null)
    {
        if (string.Equals(_options.CookieSource, "File", StringComparison.OrdinalIgnoreCase))
        {
            await _lock.WaitAsync();
            try
            {
                TryLoadFromFile();
                if (_cookieStore.Count > 0)
                {
                    return BuildCookieString();
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        // 如果内存中有维护的自动滚动 Cookie，优先使用
        if (_cookieStore.Count > 0)
        {
            return BuildCookieString();
        }

        // Manual 模式降级：使用配置中静态 Cookie，或客户端传入的 Bearer Token
        return !string.IsNullOrWhiteSpace(_options.Cookies)
            ? _options.Cookies
            : requestBearer ?? string.Empty;
    }

    /// <summary>
    /// 从上游 HTTP 响应的 Set-Cookie 标头中提取滚动更新的 Cookies 并持久化
    /// </summary>
    public async Task UpdateFromResponseAsync(HttpResponseMessage response)
    {
        if (!_options.EnableAutoRenewal) return;

        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies)) return;

        var changed = false;

        await _lock.WaitAsync();
        try
        {
            foreach (var header in setCookies)
            {
                // Set-Cookie 格式: name=value; Path=/; Domain=.google.com; Secure; HttpOnly
                var semiIdx = header.IndexOf(';');
                var cookiePart = semiIdx >= 0 ? header[..semiIdx] : header;
                var eqIdx = cookiePart.IndexOf('=');

                if (eqIdx <= 0) continue;

                var name = cookiePart[..eqIdx].Trim();
                var value = cookiePart[(eqIdx + 1)..].Trim();

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value)) continue;

                // 重点关注与捕获 Google 的关键滚动鉴权 Cookie
                if (IsTargetCookie(name))
                {
                    if (!_cookieStore.TryGetValue(name, out var oldVal) || oldVal != value)
                    {
                        _cookieStore[name] = value;
                        changed = true;
                        _logger.LogInformation("[Auto-Renewal] Rolling cookie updated: {Name} (Length: {Len})", name, value.Length);
                    }
                }
            }

            if (changed)
            {
                await PersistCookiesToFileAsync();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 判断是否是 Google 网页端的核心鉴权/安全 Cookie
    /// </summary>
    private static bool IsTargetCookie(string name)
    {
        return name.StartsWith("__Secure-", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "SID", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "HSID", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "SSID", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "APISID", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "SAPISID", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 尝试从本地或挂载文件读取 Cookies
    /// </summary>
    private void TryLoadFromFile()
    {
        if (string.IsNullOrWhiteSpace(_options.CookieFilePath)) return;

        try
        {
            if (!File.Exists(_options.CookieFilePath)) return;

            var lastWrite = File.GetLastWriteTimeUtc(_options.CookieFilePath);
            if (lastWrite <= _lastFileReadTime && _cookieStore.Count > 0) return;

            var content = File.ReadAllText(_options.CookieFilePath).Trim();
            if (string.IsNullOrWhiteSpace(content)) return;

            if (content.StartsWith('{'))
            {
                // JSON 格式: {"__Secure-1PSID": "...", "__Secure-1PSIDTS": "..."}
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(content);
                if (dict != null)
                {
                    foreach (var (k, v) in dict)
                    {
                        if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
                        {
                            _cookieStore[k] = v;
                        }
                    }
                }
            }
            else
            {
                // Raw cookie 格式: "name1=val1; name2=val2"
                ParseAndMergeCookieString(content);
            }

            _lastFileReadTime = lastWrite;
            _logger.LogInformation("Successfully reloaded cookies from file: {Path} (Count: {Count})", _options.CookieFilePath, _cookieStore.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read cookies from file: {Path}", _options.CookieFilePath);
        }
    }

    /// <summary>
    /// 将最新滚动 Cookies 持久化回写到文件中，确保下次启动直接复用
    /// </summary>
    private async Task PersistCookiesToFileAsync()
    {
        if (string.IsNullOrWhiteSpace(_options.CookieFilePath)) return;

        try
        {
            var json = JsonSerializer.Serialize(_cookieStore, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_options.CookieFilePath, json);
            _lastFileReadTime = DateTime.UtcNow;
            _logger.LogInformation("[Auto-Renewal] Persisted updated cookies to: {Path}", _options.CookieFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist cookies to file: {Path}", _options.CookieFilePath);
        }
    }

    /// <summary>
    /// 解析标准 Cookie 字符串并合并入存储
    /// </summary>
    public void ParseAndMergeCookieString(string rawCookies)
    {
        var parts = rawCookies.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var eqIdx = part.IndexOf('=');
            if (eqIdx <= 0) continue;

            var name = part[..eqIdx].Trim();
            var value = part[(eqIdx + 1)..].Trim();

            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value))
            {
                _cookieStore[name] = value;
            }
        }
    }

    /// <summary>
    /// 生成标准 Cookie 请求头字符串
    /// </summary>
    public string BuildCookieString()
    {
        return string.Join("; ", _cookieStore.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
