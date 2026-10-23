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
    UseCookies = false
});

// Services
builder.Services.AddSingleton<GeminiWebCookieManager>();
builder.Services.AddSingleton<GeminiWebSessionService>();
builder.Services.AddSingleton<GeminiWebModelService>();

// Background Worker: 自动拦截 Set-Cookie、自动滚动刷新并持久化保活
builder.Services.AddHostedService<CookieKeepAliveBackgroundService>();

var app = builder.Build();

// Middlewares
app.UseMiddleware<ApiKeyAuthMiddleware>();

// Endpoints
app.MapChatCompletionEndpoints();
app.MapModelEndpoints();
app.MapHealthEndpoints();

app.Run();
