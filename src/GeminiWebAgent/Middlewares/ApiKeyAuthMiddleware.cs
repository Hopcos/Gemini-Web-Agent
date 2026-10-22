using GeminiWebAgent.Configurations;
using GeminiWebAgent.Services;
using Microsoft.Extensions.Options;

namespace GeminiWebAgent.Middlewares;

/// <summary>
/// 鉴权与 Cookie 解析中间件。
/// 
/// 支持以下鉴权场景：
/// 1. 网关私有密钥校验：若配置了 GeminiWeb:ApiKey，客户端必须传入 Authorization: Bearer <ApiKey>。
/// 2. 后端托管 Cookie 模式：若已配置 GeminiWeb:Cookies 或 CookieSource 为 File，无需客户端传 Cookie；
///    客户端可传入任意 API Key（如 sk-xxx），若客户端未传鉴权头，亦允许直接访问。
/// 3. 客户端透传 Cookie 模式：若后端未配置任何 Cookie，客户端需在 Authorization: Bearer <cookies> 中传入完整 Gemini Cookies。
/// </summary>
public class ApiKeyAuthMiddleware(
    RequestDelegate next,
    IOptions<GeminiWebOptions> options,
    GeminiWebCookieManager cookieManager)
{
    private readonly GeminiWebOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        // 1. 健康检查端点完全免鉴权
        if (context.Request.Path.StartsWithSegments("/v1/health"))
        {
            await next(context);
            return;
        }

        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
        string? bearerToken = null;

        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            bearerToken = authHeader["Bearer ".Length..].Trim();
        }

        // 2. 如果配置了网关私有 ApiKey，进行强制鉴权校验
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            if (string.IsNullOrEmpty(bearerToken) || !string.Equals(bearerToken, _options.ApiKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        message = "Invalid or missing gateway API key. Please provide the configured API key in the Authorization header (Bearer <key>).",
                        type = "invalid_request_error",
                        code = "invalid_api_key"
                    }
                });
                return;
            }
        }

        // 3. 解析当前请求生效的 Cookies
        var cookies = await cookieManager.GetCookiesAsync(bearerToken);

        // 4. 若未配置网关 ApiKey，且后端未能获取到任何 Cookies，说明当前处于透传模式但客户端未提供 Cookies
        if (string.IsNullOrWhiteSpace(cookies))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    message = "Invalid API key / cookies. No Gemini cookies found. Please either: " +
                              "1) Configure 'GeminiWeb:Cookies' in appsettings.json, or " +
                              "2) Provide your Gemini cookies in the client request header: Authorization: Bearer __Secure-1PSID=...; __Secure-1PSIDTS=...",
                    type = "invalid_request_error",
                    code = "invalid_api_key"
                }
            });
            return;
        }

        context.Items["Cookies"] = cookies;
        await next(context);
    }
}
