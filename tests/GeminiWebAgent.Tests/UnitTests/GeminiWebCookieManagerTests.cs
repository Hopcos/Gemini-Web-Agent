using GeminiWebAgent.Configurations;
using GeminiWebAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GeminiWebAgent.Tests.UnitTests;

public class GeminiWebCookieManagerTests
{
    [Fact]
    public void ParseAndMergeCookieString_ParsesKeyValuesCorrectly()
    {
        var options = Options.Create(new GeminiWebOptions());
        var manager = new GeminiWebCookieManager(options, NullLogger<GeminiWebCookieManager>.Instance);

        manager.ParseAndMergeCookieString("__Secure-1PSID=psid_val; __Secure-1PSIDTS=ts_val; SID=sid_val");

        var cookieString = manager.BuildCookieString();
        Assert.Contains("__Secure-1PSID=psid_val", cookieString);
        Assert.Contains("__Secure-1PSIDTS=ts_val", cookieString);
        Assert.Contains("SID=sid_val", cookieString);
    }

    [Fact]
    public async Task UpdateFromResponseAsync_UpdatesRollingCookieFromSetCookie()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_cookies_{Guid.NewGuid():N}.json");
        try
        {
            var options = Options.Create(new GeminiWebOptions
            {
                CookieSource = "File",
                CookieFilePath = tempFile,
                EnableAutoRenewal = true
            });
            var manager = new GeminiWebCookieManager(options, NullLogger<GeminiWebCookieManager>.Instance);

            manager.ParseAndMergeCookieString("__Secure-1PSID=initial_psid; __Secure-1PSIDTS=old_ts");

            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Headers.Add("Set-Cookie", "__Secure-1PSIDTS=new_refreshed_ts_value; Domain=.google.com; Path=/; Secure; HttpOnly");

            await manager.UpdateFromResponseAsync(response);

            var cookies = await manager.GetCookiesAsync();
            Assert.Contains("__Secure-1PSIDTS=new_refreshed_ts_value", cookies);
            Assert.Contains("__Secure-1PSID=initial_psid", cookies);

            // Verify persistence to file
            Assert.True(File.Exists(tempFile));
            var persistedContent = await File.ReadAllTextAsync(tempFile);
            Assert.Contains("new_refreshed_ts_value", persistedContent);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task GetCookiesAsync_ManualMode_ReturnsRequestBearer_WhenNoConfiguredCookies()
    {
        var options = Options.Create(new GeminiWebOptions
        {
            CookieSource = "Manual",
            Cookies = ""
        });
        var manager = new GeminiWebCookieManager(options, NullLogger<GeminiWebCookieManager>.Instance);

        var result = await manager.GetCookiesAsync("custom-bearer-cookie");
        Assert.Equal("custom-bearer-cookie", result);
    }
}
