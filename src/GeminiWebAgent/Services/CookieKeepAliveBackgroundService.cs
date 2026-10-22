using GeminiWebAgent.Configurations;
using Microsoft.Extensions.Options;

namespace GeminiWebAgent.Services;

/// <summary>
/// 后台守护服务：定期轻量访问上游 gemini.google.com，
/// 自动触发并捕获 Google 下发的滚动 Set-Cookie (__Secure-1PSIDTS)，
/// 实现 Linux / Docker 无头部署环境下的永续保活。
/// </summary>
public class CookieKeepAliveBackgroundService(
    IHttpClientFactory httpClientFactory,
    GeminiWebCookieManager cookieManager,
    GeminiWebSessionService sessionService,
    IOptions<GeminiWebOptions> options,
    ILogger<CookieKeepAliveBackgroundService> logger) : BackgroundService
{
    private readonly GeminiWebOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableAutoRenewal)
        {
            logger.LogInformation("Cookie auto-renewal is disabled. Background keep-alive worker will not run.");
            return;
        }

        logger.LogInformation("Cookie keep-alive background worker started. Check interval: {Minutes} minutes",
            _options.KeepAliveIntervalMinutes);

        // 首次启动先稍等 10 秒，让应用完全就绪
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cookies = await cookieManager.GetCookiesAsync();

                if (!string.IsNullOrWhiteSpace(cookies))
                {
                    logger.LogInformation("[KeepAlive] Proactively pinging Gemini Web to refresh rolling cookies & session...");

                    var client = httpClientFactory.CreateClient("GeminiWebSession");
                    var req = new HttpRequestMessage(HttpMethod.Get, _options.BaseUrl);
                    req.Headers.Add("Cookie", cookies);
                    req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

                    var resp = await client.SendAsync(req, stoppingToken);

                    // 拦截 Set-Cookie 并回写最新滚动 Token
                    await cookieManager.UpdateFromResponseAsync(resp);

                    // 刷新 build label 和 anti-XSRF token 状态
                    if (resp.IsSuccessStatusCode)
                    {
                        await sessionService.GetBuildLabelAsync(cookies);
                        logger.LogInformation("[KeepAlive] Session & cookies refreshed successfully.");
                    }
                    else
                    {
                        logger.LogWarning("[KeepAlive] Ping returned status code: {Status}", resp.StatusCode);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[KeepAlive] Error during periodic keep-alive inspection");
            }

            var delay = TimeSpan.FromMinutes(Math.Max(5, _options.KeepAliveIntervalMinutes));
            await Task.Delay(delay, stoppingToken);
        }

        logger.LogInformation("Cookie keep-alive background worker stopped.");
    }
}
