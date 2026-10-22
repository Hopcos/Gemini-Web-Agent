using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiWebAgent.Models.OpenAI;

public class OpenAIChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "gemini-2.5-flash";

    [JsonPropertyName("messages")]
    public List<OpenAIMessage> Messages { get; set; } = [];

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("stop")]
    public List<string>? Stop { get; set; }
}

public class OpenAIMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public JsonElement? Content { get; set; }
}

public class OpenAIContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("image_url")]
    public OpenAIImageUrl? ImageUrl { get; set; }
}

public class OpenAIImageUrl
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
