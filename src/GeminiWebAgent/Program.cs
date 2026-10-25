using GeminiWebAgent.Configurations;
using GeminiWebAgent.Endpoints;
using GeminiWebAgent.Middlewares;
using GeminiWebAgent.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<GeminiWebOptions>(builder.Configuration.GetSection("GeminiWeb"));

// HttpClients
builder.Services.AddHttpClient("GeminiWebSession", client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    UseCookies = false
});

builder.Services.AddHttpClient<GeminiWebChatService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    UseCookies = false,
    // A redirect from StreamGenerate means Google's anti-abuse page (/sorry/), never a real answer
    AllowAutoRedirect = false
});

// Services
builder.Services.AddSingleton<GeminiWebCookieManager>();
builder.Services.AddSingleton<GeminiWebSessionService>();
builder.Services.AddSingleton<GeminiWebModelService>();

// Background Worker: 自动拦截 Set-Cookie、自动滚动刷新并持久化保活
builder.Services.AddHostedService<CookieKeepAliveBackgroundService>();

var app = builder.Build();

// Bodyless 4xx/5xx (e.g. a request body that cannot be bound) would reach agent clients as a blank error:
// log it and answer in the OpenAI error shape instead.
app.UseStatusCodePages(async statusContext =>
{
    var httpContext = statusContext.HttpContext;
    var statusCode = httpContext.Response.StatusCode;

    app.Logger.LogWarning("{Method} {Path} -> {StatusCode} without a body (unreadable or invalid request?)",
        httpContext.Request.Method, httpContext.Request.Path, statusCode);

    httpContext.Response.ContentType = "application/json";
    await httpContext.Response.WriteAsJsonAsync(new
    {
        error = new
        {
            message = statusCode == 400
                ? "Invalid request body: it could not be parsed as an OpenAI chat completion request."
                : $"Request failed with HTTP {statusCode}.",
            type = "invalid_request_error",
            code = "http_" + statusCode
        }
    });
});

// Middlewares
app.UseMiddleware<ApiKeyAuthMiddleware>();

// Endpoints
app.MapChatCompletionEndpoints();
app.MapModelEndpoints();
app.MapHealthEndpoints();

app.Run();
