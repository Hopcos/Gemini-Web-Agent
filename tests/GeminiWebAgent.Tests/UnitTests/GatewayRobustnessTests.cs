using System.Net;
using System.Text.Json;
using GeminiWebAgent.Models.GeminiWeb;
using GeminiWebAgent.Models.OpenAI;
using GeminiWebAgent.Services;

namespace GeminiWebAgent.Tests.UnitTests;

public class GatewayRobustnessTests
{
    private const string GlitchText = "I encountered an error doing what you asked. Could you try again?";

    private static OpenAIMessage Msg(string role, string content) => new()
    {
        Role = role,
        Content = JsonDocument.Parse(JsonSerializer.Serialize(content)).RootElement
    };

    [Fact]
    public void SanitizeMessages_DropsGlitchRepliesAndCollapsesResentUserMessages()
    {
        var messages = new List<OpenAIMessage>
        {
            Msg("user", "add a theme"),
            Msg("assistant", GlitchText),
            Msg("user", "add a theme"),
            Msg("assistant", GlitchText),
            Msg("user", "who are you?"),
            Msg("assistant", "I am Gemini."),
            Msg("user", "add a theme")
        };

        var result = GeminiWebChatService.SanitizeMessages(messages);

        Assert.Equal(["user", "user", "assistant", "user"], result.Select(m => m.Role));
        Assert.DoesNotContain(result, m => m.Content?.GetString() == GlitchText);
    }

    [Fact]
    public void SanitizeMessages_KeepsAssistantToolCallsEvenWithoutText()
    {
        var messages = new List<OpenAIMessage>
        {
            Msg("user", "read it"),
            new()
            {
                Role = "assistant",
                ToolCalls = [new OpenAIToolCall { Id = "c1", Function = new OpenAIToolCallFunction { Name = "read_file" } }]
            }
        };

        Assert.Equal(2, GeminiWebChatService.SanitizeMessages(messages).Count);
    }

    [Fact]
    public void IsGeminiGlitchResponse_OnlyMatchesShortCannedReplies()
    {
        Assert.True(GeminiWebChatService.IsGeminiGlitchResponse(GlitchText));

        var legitimate = "export function ErrorPage() { return <p>Sorry, something went wrong</p>; }\n" + new string('x', 400);
        Assert.False(GeminiWebChatService.IsGeminiGlitchResponse(legitimate));
        Assert.False(GeminiWebChatService.IsGeminiGlitchResponse(null));
    }

    [Fact]
    public void ParseToolCalls_WithoutTools_KeepsJsonSnippetAsText()
    {
        var text = "Here is your package.json:\n```json\n{\"name\": \"demo\", \"version\": \"1.0.0\"}\n```";

        var (cleanText, toolCalls) = GeminiWebChatService.ParseToolCalls(text, null);

        Assert.Null(toolCalls);
        Assert.Equal(text, cleanText);
    }

    [Fact]
    public void Request_AcceptsStopAsStringOrArray_AndArgumentsAsObject()
    {
        var withString = JsonSerializer.Deserialize<OpenAIChatCompletionRequest>(
            """{"model":"m","messages":[],"stop":"\n\nHuman:"}""")!;
        Assert.Equal(["\n\nHuman:"], withString.Stop);

        var withArray = JsonSerializer.Deserialize<OpenAIChatCompletionRequest>(
            """{"model":"m","messages":[],"stop":["a","b"]}""")!;
        Assert.Equal(["a", "b"], withArray.Stop);

        var withObjectArgs = JsonSerializer.Deserialize<OpenAIChatCompletionRequest>(
            """
            {"model":"m","messages":[{"role":"assistant","content":null,
              "tool_calls":[{"id":"c1","type":"function","function":{"name":"read_file","arguments":{"path":"a.txt"}}}]}]}
            """)!;
        Assert.Equal("""{"path":"a.txt"}""", withObjectArgs.Messages[0].ToolCalls![0].Function.Arguments);
    }

    [Fact]
    public void GetModelConfig_NullModel_FallsBackToDefault()
    {
        Assert.Equal("gemini-3.8-flash", GeminiWebModelMap.GetModelConfig(null).CanonicalName);
    }

    [Fact]
    public void BuildPrompt_TextMode_DoesNotAssertCapabilitiesTheModelDoesNotHave()
    {
        var config = GeminiWebModelMap.GetModelConfig("gemini-4-argon");

        var withClientPrompt = GeminiWebChatService.BuildPromptFromMessages(
            [Msg("system", "Call tools with ```pwsh blocks."), Msg("user", "add a theme")], null, config);
        Assert.Contains("Call tools with ```pwsh blocks.", withClientPrompt);
        Assert.Contains("Follow the client's instructions exactly", withClientPrompt);
        Assert.DoesNotContain("fully available", withClientPrompt);
        Assert.DoesNotContain("NEVER ask the user", withClientPrompt);

        var plain = GeminiWebChatService.BuildPromptFromMessages([Msg("user", "hi")], null, config);
        Assert.Contains("never claim to have done so", plain);
        Assert.EndsWith("[User]\nhi", plain);
    }

    [Fact]
    public void BuildPrompt_NeverReplaysGlitchRepliesToTheModel()
    {
        var config = GeminiWebModelMap.GetModelConfig("gemini-4-argon");

        var prompt = GeminiWebChatService.BuildPromptFromMessages(
            [Msg("user", "do it"), Msg("assistant", GlitchText), Msg("user", "do it")], null, config);

        Assert.DoesNotContain("I encountered an error", prompt);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(prompt, @"\[User\]"));
    }

    [Fact]
    public void IsRateLimited_DetectsSorryRedirectAnd429()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://www.google.com/sorry/index?continue=https://gemini.google.com/");
        Assert.True(GeminiWebUpstreamException.IsRateLimited(redirect));

        Assert.True(GeminiWebUpstreamException.IsRateLimited(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        Assert.False(GeminiWebUpstreamException.IsRateLimited(new HttpResponseMessage(HttpStatusCode.BadRequest)));
    }

    [Fact]
    public void Describe_MapsExceptionsToHttpStatusAndCode()
    {
        Assert.Equal((429, "rate_limited"), Pick(GeminiWebUpstreamException.RateLimited()));
        Assert.Equal((401, "invalid_cookies"), Pick(new GeminiWebUpstreamException(401, "auth")));
        Assert.Equal((401, "invalid_cookies"), Pick(new InvalidOperationException("Could not extract AT token (SNlM0e)")));
        Assert.Equal((502, "upstream_error"), Pick(new HttpRequestException("boom")));
        Assert.Equal((500, "internal_error"), Pick(new ArgumentException("bug")));

        static (int, string) Pick(Exception ex)
        {
            var (status, _, code) = GeminiWebUpstreamException.Describe(ex);
            return (status, code);
        }
    }
}
