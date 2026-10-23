using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using GeminiWebAgent.Configurations;
using GeminiWebAgent.Models.GeminiWeb;
using GeminiWebAgent.Models.OpenAI;
using Microsoft.Extensions.Options;

namespace GeminiWebAgent.Services;

/// <summary>
/// Core chat service that translates OpenAI chat completion requests
/// to Gemini Web batchexecute protocol and converts responses back.
/// </summary>
public partial class GeminiWebChatService(
    HttpClient httpClient,
    GeminiWebSessionService sessionService,
    GeminiWebCookieManager cookieManager,
    IOptions<GeminiWebOptions> options,
    ILogger<GeminiWebChatService> logger)
{
    private readonly GeminiWebOptions _options = options.Value;

    /// <summary>
    /// Non-streaming chat completion.
    /// </summary>
    public async Task<OpenAIChatCompletionResponse> ChatCompletionAsync(string cookies, OpenAIChatCompletionRequest request)
    {
        var modelConfig = GeminiWebModelMap.GetModelConfig(request.Model);
        var prompt = BuildPromptFromMessages(request.Messages, modelConfig);
        var model = modelConfig.CanonicalName;

        var responseText = await SendBatchExecuteAsync(cookies, prompt, modelConfig, stream: false);
        var parsedText = ParseBatchExecuteResponse(responseText);

        return BuildOpenAIResponse(parsedText, model);
    }

    /// <summary>
    /// Streaming chat completion via SSE.
    /// </summary>
    public async Task StreamChatCompletionAsync(string cookies, OpenAIChatCompletionRequest request, HttpResponse httpResponse)
    {
        var modelConfig = GeminiWebModelMap.GetModelConfig(request.Model);
        var prompt = BuildPromptFromMessages(request.Messages, modelConfig);
        var model = modelConfig.CanonicalName;

        var completionId = $"chatcmpl-{Guid.NewGuid():N}";
        var isFirstChunk = true;

        try
        {
            var httpRequestMessage = await BuildBatchExecuteRequestAsync(cookies, prompt, modelConfig, stream: true);

            var response = await httpClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead);
            await cookieManager.UpdateFromResponseAsync(response);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                logger.LogInformation("Refreshing session and retrying streaming request after 401/403...");
                sessionService.InvalidateSession();
                response.Dispose();

                httpRequestMessage = await BuildBatchExecuteRequestAsync(cookies, prompt, modelConfig, stream: true);
                response = await httpClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead);
                await cookieManager.UpdateFromResponseAsync(response);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    logger.LogError("Gemini Web streaming error: {StatusCode} - {Body}", response.StatusCode, errorBody);

                    var errorChunk = CreateStreamErrorChunk(model, completionId, $"Gemini Web error: {response.StatusCode}");
                    await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(errorChunk)}\n\n");
                    await httpResponse.WriteAsync("data: [DONE]\n\n");
                    return;
                }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var previousLength = 0;

            while (await reader.ReadLineAsync() is { } line)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(")]}'") || int.TryParse(trimmed, out _))
                    continue;

                var currentText = ExtractStreamingText(trimmed);
                if (!string.IsNullOrEmpty(currentText) && currentText.Length > previousLength)
                {
                    var deltaText = currentText[previousLength..];
                    previousLength = currentText.Length;

                    var chunk = BuildStreamChunk(deltaText, model, completionId, isFirstChunk);
                    isFirstChunk = false;

                    await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
                    await httpResponse.Body.FlushAsync();
                }
            }

            // Send final chunk with finish_reason
            var finalChunk = new OpenAIChatCompletionResponse
            {
                Id = completionId,
                Object = "chat.completion.chunk",
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = model,
                Choices =
                [
                    new OpenAIChatCompletionChoice
                    {
                        Index = 0,
                        Delta = new OpenAIChoiceDelta(),
                        FinishReason = "stop"
                    }
                ]
            };
            await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(finalChunk)}\n\n");
            await httpResponse.WriteAsync("data: [DONE]\n\n");
            await httpResponse.Body.FlushAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during streaming chat completion");

            var errorChunk = CreateStreamErrorChunk(model, completionId, ex.Message);
            await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(errorChunk)}\n\n");
            await httpResponse.WriteAsync("data: [DONE]\n\n");
            await httpResponse.Body.FlushAsync();
        }
    }

    /// <summary>
    /// Builds a single text prompt from OpenAI messages, including model persona grounding.
    /// Handles system, user, and assistant messages.
    /// </summary>
    private static string BuildPromptFromMessages(List<OpenAIMessage> messages, GeminiWebModelConfig modelConfig)
    {
        var parts = new List<string>();

        // Model persona grounding
        parts.Add($"[System Instructions]\nYou are {modelConfig.DisplayName}, Google's {modelConfig.Description}. When asked about your model name or identity, clearly and directly identify yourself as {modelConfig.DisplayName}.");

        foreach (var message in messages)
        {
            if (message.Content == null) continue;

            var text = message.Content.Value.ValueKind == JsonValueKind.String
                ? message.Content.Value.GetString() ?? string.Empty
                : ExtractTextFromContentParts(message.Content.Value);

            if (string.IsNullOrEmpty(text)) continue;

            switch (message.Role)
            {
                case "system":
                    parts.Add($"[System Instructions]\n{text}");
                    break;
                case "user":
                    parts.Add(text);
                    break;
                case "assistant":
                    parts.Add($"[Previous Assistant Response]\n{text}");
                    break;
            }
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Extracts text from multimodal content parts.
    /// </summary>
    private static string ExtractTextFromContentParts(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;

        var texts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            var contentPart = JsonSerializer.Deserialize<OpenAIContentPart>(part.GetRawText());
            if (contentPart?.Type == "text" && contentPart.Text != null)
            {
                texts.Add(contentPart.Text);
            }
        }

        return string.Join("\n", texts);
    }

    /// <summary>
    /// Sends a request to the Gemini Web batchexecute endpoint.
    /// </summary>
    private async Task<string> SendBatchExecuteAsync(string cookies, string prompt, GeminiWebModelConfig modelConfig, bool stream)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var request = await BuildBatchExecuteRequestAsync(cookies, prompt, modelConfig, stream);

            var response = await httpClient.SendAsync(request);
            await cookieManager.UpdateFromResponseAsync(response);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                return responseBody;
            }

            logger.LogWarning("Gemini Web API error on attempt {Attempt}: {StatusCode} - {Body}", attempt, response.StatusCode, responseBody);

            if ((response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                 response.StatusCode == System.Net.HttpStatusCode.Forbidden) && attempt == 1)
            {
                logger.LogInformation("Refreshing session and retrying after 401/403...");
                sessionService.InvalidateSession();
                continue;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                sessionService.InvalidateSession();
                throw new HttpRequestException("Authentication failed. Cookies may be invalid or expired.");
            }

            throw new HttpRequestException($"Gemini Web API error: {response.StatusCode} - {responseBody}");
        }

        throw new HttpRequestException("Failed to send request to Gemini Web after retry.");
    }

    /// <summary>
    /// Builds the batchexecute HTTP request.
    /// </summary>
    private async Task<HttpRequestMessage> BuildBatchExecuteRequestAsync(string cookies, string prompt, GeminiWebModelConfig modelConfig, bool stream)
    {
        var bl = await sessionService.GetBuildLabelAsync(cookies);
        var at = await sessionService.GetAtTokenAsync(cookies);
        var sid = await sessionService.GetSessionIdAsync(cookies);
        var reqId = sessionService.GetNextRequestId();

        // Construct 80-element internal payload for StreamGenerate
        var inner = BuildStreamGenerateInnerPayload(prompt, modelConfig);

        var fReq = JsonSerializer.Serialize(new object?[]
        {
            null,
            JsonSerializer.Serialize(inner)
        });

        // Build URL with query parameters
        var baseUrl = $"{_options.BaseUrl.TrimEnd('/')}/_/BardChatUi/data/assistant.lamda.BardFrontendService/StreamGenerate";
        var queryParams = new Dictionary<string, string>
        {
            ["bl"] = bl,
            ["hl"] = "en",
            ["_reqid"] = reqId.ToString(),
            ["rt"] = "c"
        };

        var queryString = string.Join("&", queryParams.Select(kv => $"{kv.Key}={HttpUtility.UrlEncode(kv.Value)}"));
        var url = $"{baseUrl}?{queryString}";

        // Build form-encoded body
        var formData = new Dictionary<string, string>
        {
            ["f.req"] = fReq,
            ["at"] = at
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(formData)
        };

        // Set browser-like headers
        request.Headers.Add("Cookie", cookies);
        request.Headers.Add("Origin", _options.BaseUrl);
        request.Headers.Add("Referer", $"{_options.BaseUrl.TrimEnd('/')}/app");
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        request.Headers.Add("X-Same-Domain", "1");

        return request;
    }

    /// <summary>
    /// Builds the 80-element internal payload structure for StreamGenerate.
    /// Index [79] corresponds to the MODE_CATEGORY enum for model routing.
    /// Index [17] corresponds to the thinking mode configuration.
    /// </summary>
    public static object?[] BuildStreamGenerateInnerPayload(string prompt, GeminiWebModelConfig modelConfig)
    {
        var inner = new object?[80];
        inner[0] = new object?[] { prompt, 0, null, null, null, null, 0 };
        inner[1] = new object?[] { "en" };
        inner[2] = new object?[] { "", "", "", null, null, null, null, null, null, "" };
        inner[6] = new object?[] { 0 };
        inner[7] = 1;
        inner[10] = 1;
        inner[11] = 0;
        inner[17] = new object?[] { new object?[] { modelConfig.ThinkMode } };
        inner[18] = 0;
        inner[27] = 1;
        inner[30] = new object?[] { 4 };
        inner[41] = new object?[] { 2 };
        inner[53] = 0;
        inner[59] = Guid.NewGuid().ToString();
        inner[61] = Array.Empty<object>();
        inner[68] = 1;
        inner[79] = modelConfig.ModeCategory;

        return inner;
    }

    /// <summary>
    /// Parses the batchexecute response format.
    /// Response starts with ")]}'\\n" prefix and contains nested JSON arrays.
    /// </summary>
    public string ParseBatchExecuteResponse(string responseBody)
    {
        try
        {
            var lines = responseBody.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string lastText = string.Empty;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(")]}'") || int.TryParse(trimmed, out _))
                    continue;

                var text = ExtractStreamingText(trimmed);
                if (!string.IsNullOrEmpty(text))
                {
                    lastText = text;
                }
            }

            if (!string.IsNullOrEmpty(lastText))
                return lastText;

            logger.LogWarning("Could not parse batchexecute response structure");
            return string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error parsing batchexecute response");
            return string.Empty;
        }
    }

    /// <summary>
    /// Extracts the actual text content from the inner response data.
    /// The response data is a JSON string containing nested arrays.
    /// </summary>
    private static string ExtractTextFromResponseData(string responseData)
    {
        try
        {
            var data = JsonSerializer.Deserialize<JsonElement>(responseData);

            // Navigate the nested structure to find the text response
            // Typical path: [4][0][1][0] contains the response text
            // or: [0][0] contains the response text
            if (data.ValueKind == JsonValueKind.Array)
            {
                // Try path [4][0][1][0] — modern format
                if (TryGetNestedString(data, [4, 0, 1, 0], out var text1))
                    return text1;

                // Try path [0][0] — legacy format
                if (TryGetNestedString(data, [0, 0], out var text2))
                    return text2;

                // Try path [4][0][0] 
                if (TryGetNestedString(data, [4, 0, 0], out var text3))
                    return text3;

                // Fallback: search recursively for the first long string
                return FindFirstLongString(data);
            }

            return responseData;
        }
        catch
        {
            return responseData;
        }
    }

    /// <summary>
    /// Tries to navigate a nested JSON array by indices and get a string value.
    /// </summary>
    private static bool TryGetNestedString(JsonElement element, int[] indices, out string result)
    {
        result = string.Empty;
        var current = element;

        foreach (var index in indices)
        {
            if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                return false;

            current = current[index];
        }

        if (current.ValueKind == JsonValueKind.String)
        {
            result = current.GetString() ?? string.Empty;
            return !string.IsNullOrEmpty(result);
        }

        return false;
    }

    /// <summary>
    /// Recursively searches for the first string longer than 20 characters in a nested JSON structure.
    /// Used as a fallback when the expected path doesn't work.
    /// </summary>
    private static string FindFirstLongString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (str != null && str.Length > 20)
                return str;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var result = FindFirstLongString(item);
                if (!string.IsNullOrEmpty(result))
                    return result;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Extracts text from streaming response chunks.
    /// </summary>
    private static string? ExtractStreamingText(string chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk)) return null;

        try
        {
            // Clean up the chunk
            var cleaned = chunk.Trim();

            // Strip anti-XSRF prefix if present
            if (cleaned.StartsWith(")]}'"))
            {
                var newlineIndex = cleaned.IndexOf('\n');
                if (newlineIndex >= 0)
                    cleaned = cleaned[(newlineIndex + 1)..];
            }

            // Try to parse as a number + JSON structure (streaming format)
            // Streaming responses are typically: "number\n[[...json...]]\n"
            var lines = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // Skip pure number lines (byte counts)
                if (int.TryParse(trimmedLine, out _)) continue;

                // Try to parse as JSON array
                if (trimmedLine.StartsWith('['))
                {
                    try
                    {
                        var jsonElement = JsonSerializer.Deserialize<JsonElement>(trimmedLine);
                        if (jsonElement.ValueKind == JsonValueKind.Array)
                        {
                            // Look for text content in the response
                            foreach (var item in jsonElement.EnumerateArray())
                            {
                                if (item.ValueKind != JsonValueKind.Array) continue;

                                // Case 1: 2-level array [[ "wrb.fr", null, "..." ]]
                                if (item.GetArrayLength() >= 3)
                                {
                                    var tag = item[0].ValueKind == JsonValueKind.String ? item[0].GetString() : null;
                                    var rpcId = item[1].ValueKind == JsonValueKind.String ? item[1].GetString() : null;

                                    if (tag == "wrb.fr" || rpcId == "otAQ7b")
                                    {
                                        var responseData = item[2].ValueKind == JsonValueKind.String ? item[2].GetString() : null;
                                        if (!string.IsNullOrEmpty(responseData))
                                        {
                                            var extracted = ExtractTextFromResponseData(responseData);
                                            if (!string.IsNullOrEmpty(extracted))
                                                return extracted;
                                        }
                                    }
                                }

                                // Case 2: 3-level array [[[ "wrb.fr", null, "..." ]]]
                                foreach (var innerItem in item.EnumerateArray())
                                {
                                    if (innerItem.ValueKind != JsonValueKind.Array) continue;
                                    if (innerItem.GetArrayLength() >= 3)
                                    {
                                        var tag = innerItem[0].ValueKind == JsonValueKind.String ? innerItem[0].GetString() : null;
                                        var rpcId = innerItem[1].ValueKind == JsonValueKind.String ? innerItem[1].GetString() : null;

                                        if (tag == "wrb.fr" || rpcId == "otAQ7b")
                                        {
                                            var responseData = innerItem[2].ValueKind == JsonValueKind.String ? innerItem[2].GetString() : null;
                                            if (!string.IsNullOrEmpty(responseData))
                                            {
                                                var extracted = ExtractTextFromResponseData(responseData);
                                                if (!string.IsNullOrEmpty(extracted))
                                                    return extracted;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Not valid JSON, skip
                    }
                }
            }
        }
        catch
        {
            // Ignore parse errors in streaming
        }

        return null;
    }

    /// <summary>
    /// Builds the final OpenAI-format response from parsed text.
    /// </summary>
    private static OpenAIChatCompletionResponse BuildOpenAIResponse(string content, string model)
    {
        var estimatedTokens = Math.Max(1, content.Length / 4);

        return new OpenAIChatCompletionResponse
        {
            Id = $"chatcmpl-{Guid.NewGuid():N}",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model,
            Choices =
            [
                new OpenAIChatCompletionChoice
                {
                    Index = 0,
                    Message = new OpenAIChoiceMessage
                    {
                        Role = "assistant",
                        Content = content
                    },
                    FinishReason = "stop"
                }
            ],
            Usage = new OpenAIUsage
            {
                PromptTokens = 0,  // Not available from web interface
                CompletionTokens = estimatedTokens,
                TotalTokens = estimatedTokens
            }
        };
    }

    /// <summary>
    /// Builds a streaming chunk response.
    /// </summary>
    private static OpenAIChatCompletionResponse BuildStreamChunk(string content, string model, string completionId, bool isFirstChunk)
    {
        return new OpenAIChatCompletionResponse
        {
            Id = completionId,
            Object = "chat.completion.chunk",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model,
            Choices =
            [
                new OpenAIChatCompletionChoice
                {
                    Index = 0,
                    Delta = new OpenAIChoiceDelta
                    {
                        Role = isFirstChunk ? "assistant" : null,
                        Content = content
                    },
                    FinishReason = null
                }
            ]
        };
    }

    /// <summary>
    /// Creates an error chunk for streaming responses.
    /// </summary>
    private static OpenAIChatCompletionResponse CreateStreamErrorChunk(string model, string completionId, string errorMessage)
    {
        return new OpenAIChatCompletionResponse
        {
            Id = completionId,
            Object = "chat.completion.chunk",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model,
            Choices =
            [
                new OpenAIChatCompletionChoice
                {
                    Index = 0,
                    Delta = new OpenAIChoiceDelta
                    {
                        Content = $"Error: {errorMessage}"
                    },
                    FinishReason = "stop"
                }
            ]
        };
    }
}
