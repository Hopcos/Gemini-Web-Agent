using System.Text.RegularExpressions;

namespace GeminiWebAgent.Tests.UnitTests;

public partial class GeminiWebSessionExtractionTests
{
    [Fact]
    public void BuildLabelRegex_ExtractsExpectedLabel()
    {
        var sampleHtml = """
        <html>
        <head>
        <script>
        window.WIZ_global_data = {"cfb2h":"boq_assistant-bard-web-server_20240901.08_p0","SNlM0e":"AIzaSyD-sample-at-token-value","FdrFJe":"1234567890123456789"};
        </script>
        </head>
        </html>
        """;

        var match = BuildLabelRegex().Match(sampleHtml);
        Assert.True(match.Success);
        Assert.Equal("boq_assistant-bard-web-server_20240901.08_p0", match.Groups[1].Value);
    }

    [Fact]
    public void AtTokenRegex_ExtractsExpectedToken()
    {
        var sampleHtml = """
        <script>
        "SNlM0e":"AIzaSyD_SampleToken_12345ABCDE"
        </script>
        """;

        var match = AtTokenRegex().Match(sampleHtml);
        Assert.True(match.Success);
        Assert.Equal("AIzaSyD_SampleToken_12345ABCDE", match.Groups[1].Value);
    }

    [Fact]
    public void SessionIdRegex_ExtractsExpectedSessionId()
    {
        var sampleHtml = """
        <script>
        "FdrFJe":"9876543210987654321"
        </script>
        """;

        var match = SessionIdRegex().Match(sampleHtml);
        Assert.True(match.Success);
        Assert.Equal("9876543210987654321", match.Groups[1].Value);
    }

    [GeneratedRegex(@"""cfb2h"":""(boq_assistant[^""]+)""")]
    private static partial Regex BuildLabelRegex();

    [GeneratedRegex(@"""SNlM0e"":""([^""]+)""")]
    private static partial Regex AtTokenRegex();

    [GeneratedRegex(@"""FdrFJe"":""([^""]+)""")]
    private static partial Regex SessionIdRegex();
}
