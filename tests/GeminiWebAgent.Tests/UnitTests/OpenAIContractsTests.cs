using System.Text.Json;
using GeminiWebAgent.Models.OpenAI;

namespace GeminiWebAgent.Tests.UnitTests;

public class OpenAIContractsTests
{
    [Fact]
    public void ChatCompletionRequest_CanDeserializeStandardOpenAIPayload()
    {
        var json = """
        {
            "model": "gemini-2.5-flash",
            "messages": [
                { "role": "system", "content": "You are a helpful assistant." },
                { "role": "user", "content": "Hello!" }
            ],
            "temperature": 0.7,
            "stream": true,
            "max_tokens": 1000
        }
        """;

        var request = JsonSerializer.Deserialize<OpenAIChatCompletionRequest>(json);

        Assert.NotNull(request);
        Assert.Equal("gemini-2.5-flash", request.Model);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("system", request.Messages[0].Role);
        Assert.Equal("You are a helpful assistant.", request.Messages[0].Content?.GetString());
        Assert.Equal("user", request.Messages[1].Role);
        Assert.Equal("Hello!", request.Messages[1].Content?.GetString());
        Assert.Equal(0.7, request.Temperature);
        Assert.True(request.Stream);
        Assert.Equal(1000, request.MaxTokens);
    }

    [Fact]
    public void ChatCompletionResponse_SerializesToOpenAIFormat()
    {
        var response = new OpenAIChatCompletionResponse
        {
            Id = "chatcmpl-test-123",
            Created = 1700000000,
            Model = "gemini-2.5-flash",
            Choices =
            [
                new OpenAIChatCompletionChoice
                {
                    Index = 0,
                    Message = new OpenAIChoiceMessage
                    {
                        Role = "assistant",
                        Content = "Hello from Gemini Web!"
                    },
                    FinishReason = "stop"
                }
            ],
            Usage = new OpenAIUsage
            {
                PromptTokens = 10,
                CompletionTokens = 20,
                TotalTokens = 30
            }
        };

        var json = JsonSerializer.Serialize(response);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("chatcmpl-test-123", root.GetProperty("id").GetString());
        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.Equal("gemini-2.5-flash", root.GetProperty("model").GetString());

        var choice = root.GetProperty("choices")[0];
        Assert.Equal(0, choice.GetProperty("index").GetInt32());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("Hello from Gemini Web!", choice.GetProperty("message").GetProperty("content").GetString());

        var usage = root.GetProperty("usage");
        Assert.Equal(10, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(20, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(30, usage.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public void ChatCompletionChunk_SerializesDeltaCorrectly()
    {
        var chunk = new OpenAIChatCompletionResponse
        {
            Id = "chatcmpl-test-456",
            Object = "chat.completion.chunk",
            Created = 1700000000,
            Model = "gemini-2.5-flash",
            Choices =
            [
                new OpenAIChatCompletionChoice
                {
                    Index = 0,
                    Delta = new OpenAIChoiceDelta
                    {
                        Role = "assistant",
                        Content = "chunk 1"
                    },
                    FinishReason = null
                }
            ]
        };

        var json = JsonSerializer.Serialize(chunk);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("chat.completion.chunk", root.GetProperty("object").GetString());
        var choice = root.GetProperty("choices")[0];
        Assert.True(choice.TryGetProperty("delta", out var delta));
        Assert.Equal("assistant", delta.GetProperty("role").GetString());
        Assert.Equal("chunk 1", delta.GetProperty("content").GetString());
        // Message should be omitted when null
        Assert.False(choice.TryGetProperty("message", out _));
    }
}
