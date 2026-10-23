using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using GeminiWebAgent.Configurations;
using GeminiWebAgent.Models.GeminiWeb;
using GeminiWebAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GeminiWebAgent.Tests.IntegrationTests;

public class GatewayLiveTests
{
    private class SimpleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private readonly HttpClient _client = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:5000/v1/"),
        Timeout = TimeSpan.FromSeconds(10)
    };

    [Fact]
    public async Task Live_TestGeminiWebPayloads()
    {
        var configPath = @"E:\Personal\GeminiWebAgent\src\GeminiWebAgent\appsettings.hopcos.json";
        if (!File.Exists(configPath)) return;

        var json = await File.ReadAllTextAsync(configPath);
        var doc = JsonDocument.Parse(json);
        var cookies = doc.RootElement.GetProperty("GeminiWeb").GetProperty("Cookies").GetString();
        if (string.IsNullOrEmpty(cookies)) return;

        var options = Options.Create(new GeminiWebOptions
        {
            BaseUrl = "https://gemini.google.com",
            Cookies = cookies
        });

        var cookieMgr = new GeminiWebCookieManager(options, NullLogger<GeminiWebCookieManager>.Instance);
        var httpClient = new HttpClient();
        var mockFactory = new SimpleHttpClientFactory(httpClient);
        var sessionService = new GeminiWebSessionService(mockFactory, cookieMgr, options, NullLogger<GeminiWebSessionService>.Instance);

        var bl = await sessionService.GetBuildLabelAsync(cookies);
        var at = await sessionService.GetAtTokenAsync(cookies);

        Assert.NotEmpty(bl);
        Assert.NotEmpty(at);

        // Test 80-element payload with model_id = 3 (PRO / Argon)
        int modelId = 3;
        int thinkMode = 4;
        string prompt = "[系统指令]\n你是 Gemini 4 Argon，Google 旗下的最新旗舰推理模型。请在回答关于自身模型身份的问题时明确指出你是 Gemini 4 Argon。\n\n[用户]\n你是什么模型？";

        var inner = new object?[80];
        inner[0] = new object?[] { prompt, 0, null, null, null, null, 0 };
        inner[1] = new object?[] { "en" };
        inner[2] = new object?[] { "", "", "", null, null, null, null, null, null, "" };
        inner[6] = new object?[] { 0 };
        inner[7] = 1;
        inner[10] = 1;
        inner[11] = 0;
        inner[17] = new object?[] { new object?[] { thinkMode } };
        inner[18] = 0;
        inner[27] = 1;
        inner[30] = new object?[] { 4 };
        inner[41] = new object?[] { 2 };
        inner[53] = 0;
        inner[59] = Guid.NewGuid().ToString();
        inner[61] = Array.Empty<object>();
        inner[68] = 1;
        inner[79] = modelId;

        var outer = new object?[] { null, JsonSerializer.Serialize(inner) };
        var fReq = JsonSerializer.Serialize(outer);

        var postUrl = $"https://gemini.google.com/_/BardChatUi/data/assistant.lamda.BardFrontendService/StreamGenerate?bl={HttpUtility.UrlEncode(bl)}&hl=en&_reqid=100001&rt=c";
        var postReq = new HttpRequestMessage(HttpMethod.Post, postUrl);
        postReq.Headers.Add("Cookie", cookies);
        postReq.Headers.Add("Origin", "https://gemini.google.com");
        postReq.Headers.Add("Referer", "https://gemini.google.com/app");
        postReq.Headers.Add("X-Same-Domain", "1");
        postReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        var formData = new Dictionary<string, string>
        {
            ["f.req"] = fReq,
            ["at"] = at
        };
        postReq.Content = new FormUrlEncodedContent(formData);

        var response = await httpClient.SendAsync(postReq);
        var content = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"Failed with {response.StatusCode}: {content}");
        
        var chatService = new GeminiWebChatService(httpClient, sessionService, cookieMgr, options, NullLogger<GeminiWebChatService>.Instance);
        var parsedText = chatService.ParseBatchExecuteResponse(content);
        Assert.NotEmpty(parsedText);
        Assert.Contains("Google", parsedText);
    }

    private async Task<bool> IsGatewayRunningAsync()
    {
        try
        {
            var resp = await _client.GetAsync("health");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task HealthCheck_ReturnsOk()
    {
        if (!await IsGatewayRunningAsync()) return;

        var resp = await _client.GetAsync("health");
        Assert.True(resp.IsSuccessStatusCode, $"Health check failed: {resp.StatusCode}");

        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);
        Assert.Equal("ok", json["status"]?.GetValue<string>());
    }

    [Fact]
    public async Task ModelsList_ReturnsOpenAiFormattedModels()
    {
        if (!await IsGatewayRunningAsync()) return;

        // Models endpoint requires auth
        var req = new HttpRequestMessage(HttpMethod.Get, "models");
        req.Headers.Add("Authorization", "Bearer dummy-cookie");
        var resp = await _client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode, $"Models list failed: {resp.StatusCode}");

        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);
        Assert.Equal("list", json["object"]?.GetValue<string>());

        var data = json["data"] as JsonArray;
        Assert.NotNull(data);
        Assert.NotEmpty(data);

        var first = data[0] as JsonObject;
        Assert.NotNull(first);
        Assert.Equal("model", first["object"]?.GetValue<string>());
        Assert.Equal("google", first["owned_by"]?.GetValue<string>());
        Assert.NotNull(first["id"]?.GetValue<string>());
    }

    [Fact]
    public async Task Unauthorized_WhenNoApiKeyHeaderProvided()
    {
        if (!await IsGatewayRunningAsync()) return;

        var resp = await _client.GetAsync("models");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
