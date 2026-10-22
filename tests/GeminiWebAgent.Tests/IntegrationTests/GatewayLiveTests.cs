using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace GeminiWebAgent.Tests.IntegrationTests;

public class GatewayLiveTests
{
    private readonly HttpClient _client = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:5000/v1/"),
        Timeout = TimeSpan.FromSeconds(10)
    };

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
