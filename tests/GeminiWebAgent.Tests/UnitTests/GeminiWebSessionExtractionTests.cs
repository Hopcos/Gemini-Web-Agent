using System.Text.RegularExpressions;

namespace GeminiWebAgent.Tests.UnitTests;

public partial class GeminiWebSessionExtractionTests
{
    [Theory]
    [InlineData(@"""cfb2h"":""boq_gemini-web-uiserver_20261021.15_p0""", "boq_gemini-web-uiserver_20261021.15_p0")]
    [InlineData(@"""cfb2h"": ""boq_gemini-web-uiserver_20261022.00_p0""", "boq_gemini-web-uiserver_20261022.00_p0")]
    [InlineData(@"""cfb2h"":""boq_assistant-bard-web-server_20240901.08_p0""", "boq_assistant-bard-web-server_20240901.08_p0")]
    public void BuildLabelRegex_ExtractsExpectedLabel(string snippet, string expected)
    {
        var match = BuildLabelRegex().Match(snippet);
        Assert.True(match.Success);
        Assert.Equal(expected, match.Groups[1].Value);
    }

    [Fact]
    public void AtTokenRegex_ExtractsExpectedToken()
    {
        var sampleHtml = """
        <script>
        "SNlM0e":"AIaPT3NwJaU_px89M3pXwjlPP6sf:1792715943717"
        </script>
        """;

        var match = AtTokenRegex().Match(sampleHtml);
        Assert.True(match.Success);
        Assert.Equal("AIaPT3NwJaU_px89M3pXwjlPP6sf:1792715943717", match.Groups[1].Value);
    }

    [Fact]
    public void SessionIdRegex_ExtractsExpectedSessionId()
    {
        var sampleHtml = """
        <script>
        "FdrFJe":"-8883572114637741986"
        </script>
        """;

        var match = SessionIdRegex().Match(sampleHtml);
        Assert.True(match.Success);
        Assert.Equal("-8883572114637741986", match.Groups[1].Value);
    }

    [GeneratedRegex(@"""cfb2h""\s*:\s*""([^""]+)""")]
    private static partial Regex BuildLabelRegex();

    [GeneratedRegex(@"""SNlM0e""\s*:\s*""([^""]+)""")]
    private static partial Regex AtTokenRegex();

    [GeneratedRegex(@"""FdrFJe""\s*:\s*""([^""]+)""")]
    private static partial Regex SessionIdRegex();
}
