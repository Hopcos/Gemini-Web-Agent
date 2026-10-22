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
        var prompt = BuildPromptFromMessages(request.Messages);
        var model = request.Model;

        var responseText = await SendBatchExecuteAsync(cookies, prompt, model, stream: false);
        var parsedText = ParseBatchExecuteResponse(responseText);

        return BuildOpenAIResponse(parsedText, model);
    }

    /// <summary>
    /// Streaming chat completion via SSE.
    /// </summary>
    public async Task StreamChatCompletionAsync(string cookies, OpenAIChatCompletionRequest request, HttpResponse httpResponse)
    {
        var prompt = BuildPromptFromMessages(request.Messages);
        var model = request.Model;

        var completionId = $"chatcmpl-{Guid.NewGuid():N}";
        var isFirstChunk = true;

        try
        {
            var httpRequestMessage = await BuildBatchExecuteRequestAsync(cookies, prompt, model, stream: true);

            using var response = await httpClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead);
            await cookieManager.UpdateFromResponseAsync(response);

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

            var buffer = new StringBuilder();

            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                buffer.AppendLine(line);

                // Try to parse accumulated buffer for text content
                var currentText = ExtractStreamingText(buffer.ToString());
                if (!string.IsNullOrEmpty(currentText))
                {
                    var chunk = BuildStreamChunk(currentText, model, completionId, isFirstChunk);
                    isFirstChunk = false;

                    await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
                    await httpResponse.Body.FlushAsync();

                    buffer.Clear();
                }
            }

            // Send any remaining content
            var remainingText = ExtractStreamingText(buffer.ToString());
            if (!string.IsNullOrEmpty(remainingText))
            {
                var chunk = BuildStreamChunk(remainingText, model, completionId, isFirstChunk);
                await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
                await httpResponse.Body.FlushAsync();
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
    /// Builds a single text prompt from OpenAI messages.
    /// Handles system, user, and assistant messages.
    /// </summary>
    private static string BuildPromptFromMessages(List<OpenAIMessage> messages)
    {
        var parts = new List<string>();

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
    private async Task<string> SendBatchExecuteAsync(string cookies, string prompt, string model, bool stream)
    {
        var request = await BuildBatchExecuteRequestAsync(cookies, prompt, model, stream);

        var response = await httpClient.SendAsync(request);
        await cookieManager.UpdateFromResponseAsync(response);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Gemini Web API error: {StatusCode} - {Body}", response.StatusCode, responseBody);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                sessionService.InvalidateSession();
                throw new HttpRequestException("Authentication failed. Cookies may be invalid or expired.");
            }

            throw new HttpRequestException($"Gemini Web API error: {response.StatusCode} - {responseBody}");
        }

        return responseBody;
    }

    /// <summary>
    /// Builds the batchexecute HTTP request.
    /// </summary>
    private async Task<HttpRequestMessage> BuildBatchExecuteRequestAsync(string cookies, string prompt, string model, bool stream)
    {
        var bl = await sessionService.GetBuildLabelAsync(cookies);
        var at = await sessionService.GetAtTokenAsync(cookies);
        var sid = await sessionService.GetSessionIdAsync(cookies);
        var reqId = sessionService.GetNextRequestId();

        var geminiModelId = GeminiWebModelMap.GetGeminiModelId(model);

        // Build the inner request payload
        // The batchexecute format wraps the prompt in a deeply nested JSON array
        var innerPayload = BuildInnerPayload(prompt, geminiModelId);
        var serializedInner = JsonSerializer.Serialize(innerPayload);

        // Build the f.req outer wrapper
        var fReq = JsonSerializer.Serialize(new object?[]
        {
            new object?[]
            {
                new object?[]
                {
                    "otAQ7b",
                    serializedInner,
                    null,
                    "generic"
                }
            }
        });

        // Build URL with query parameters
        var baseUrl = $"{_options.BaseUrl}/_/BardChatUi/data/assistant.lamda.BardFrontendService/StreamGenerate";
        var queryParams = new Dictionary<string, string>
        {
            ["bl"] = bl,
            ["_reqid"] = reqId.ToString(),
            ["rt"] = stream ? "c" : "b"
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
        request.Headers.Add("Referer", $"{_options.BaseUrl}/");
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        request.Headers.Add("X-Same-Domain", "1");

        return request;
    }

    /// <summary>
    /// Builds the inner payload structure for the batchexecute request.
    /// </summary>
    private static object?[] BuildInnerPayload(string prompt, string geminiModelId)
    {
        // The payload format is a deeply nested array structure
        // Position [0] = prompt parts array
        // Position [1] = conversation context (null for new)
        // Position [2] = model configuration
        return
        [
            new object?[]
            {
                new object?[] { prompt }  // Prompt text
            },
            null,  // Conversation ID (null = new conversation)
            null,  // Response ID
            null,
            null,
            null,
            new object?[]
            {
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                geminiModelId  // Position [79] — model identifier
            }
        ];
    }

    /// <summary>
    /// Parses the batchexecute response format.
    /// Response starts with ")]}'\\n" prefix and contains nested JSON arrays.
    /// </summary>
    private string ParseBatchExecuteResponse(string responseBody)
    {
        try
        {
            // Strip the anti-XSRF prefix
            var cleanedBody = responseBody;
            if (cleanedBody.StartsWith(")]}'"))
            {
                var newlineIndex = cleanedBody.IndexOf('\n');
                if (newlineIndex >= 0)
                {
                    cleanedBody = cleanedBody[(newlineIndex + 1)..];
                }
            }

            // Parse the outer array structure
            // Response format: [[["wrb.fr","otAQ7b","[...actual response...]",null,null,null,sequence]]]
            var outerArray = JsonSerializer.Deserialize<JsonElement>(cleanedBody);

            if (outerArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outerArray.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Array) continue;

                    foreach (var innerItem in item.EnumerateArray())
                    {
                        if (innerItem.ValueKind != JsonValueKind.Array) continue;

                        // Look for the response with rpcid "otAQ7b"
                        if (innerItem.GetArrayLength() >= 3)
                        {
                            var rpcId = innerItem[1].GetString();
                            if (rpcId == "otAQ7b")
                            {
                                var responseData = innerItem[2].GetString();
                                if (!string.IsNullOrEmpty(responseData))
                                {
                                    return ExtractTextFromResponseData(responseData);
                                }
                            }
                        }
                    }
                }
            }

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

                                foreach (var innerItem in item.EnumerateArray())
                                {
                                    if (innerItem.ValueKind != JsonValueKind.Array) continue;
                                    if (innerItem.GetArrayLength() >= 3)
                                    {
                                        var rpcId = innerItem[1].ValueKind == JsonValueKind.String
                                            ? innerItem[1].GetString()
                                            : null;

                                        if (rpcId == "otAQ7b")
                                        {
                                            var responseData = innerItem[2].GetString();
                                            if (!string.IsNullOrEmpty(responseData))
                                            {
                                                return ExtractTextFromResponseData(responseData);
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
