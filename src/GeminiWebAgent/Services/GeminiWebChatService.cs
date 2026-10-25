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
        var effectiveTools = GetEffectiveTools(request);
        var prompt = BuildPromptFromMessages(request.Messages, effectiveTools, modelConfig);
        var model = modelConfig.CanonicalName;

        var responseText = await SendBatchExecuteAsync(cookies, prompt, modelConfig, stream: false);
        var parsedText = ParseBatchExecuteResponse(responseText);

        var (cleanText, toolCalls) = ParseToolCalls(parsedText, effectiveTools);
        var finishReason = toolCalls != null && toolCalls.Count > 0 ? "tool_calls" : "stop";

        var promptTokens = EstimateTokens(prompt);
        var completionTokens = EstimateTokens(parsedText);

        return BuildOpenAIResponse(cleanText, toolCalls, finishReason, model, promptTokens, completionTokens);
    }

    /// <summary>
    /// Streaming chat completion via SSE.
    /// </summary>
    public async Task StreamChatCompletionAsync(string cookies, OpenAIChatCompletionRequest request, HttpResponse httpResponse)
    {
        var modelConfig = GeminiWebModelMap.GetModelConfig(request.Model);
        var effectiveTools = GetEffectiveTools(request);
        var prompt = BuildPromptFromMessages(request.Messages, effectiveTools, modelConfig);
        var model = modelConfig.CanonicalName;

        var completionId = $"chatcmpl-{Guid.NewGuid():N}";
        var isFirstChunk = true;

        try
        {
            // If tools are provided: accumulate full response to parse tool_calls properly
            if (effectiveTools != null && effectiveTools.Count > 0)
            {
                string lastText = string.Empty;
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    using var httpRequestMessage = await BuildBatchExecuteRequestAsync(cookies, prompt, modelConfig, stream: true);
                    using var response = await httpClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead);
                    await cookieManager.UpdateFromResponseAsync(response);

                    if ((response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                         response.StatusCode == System.Net.HttpStatusCode.Forbidden) && attempt == 1)
                    {
                        logger.LogInformation("Refreshing session and retrying streaming request after 401/403...");
                        sessionService.InvalidateSession();
                        continue;
                    }

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

                    lastText = string.Empty;
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(")]}'") || int.TryParse(trimmed, out _))
                            continue;

                        var currentText = ExtractStreamingText(trimmed);
                        if (!string.IsNullOrEmpty(currentText))
                        {
                            lastText = currentText;
                        }
                    }

                    if (IsGeminiGlitchResponse(lastText) && attempt < 3)
                    {
                        logger.LogWarning("Gemini Web returned temporary glitch response on attempt {Attempt} during tool streaming. Retrying in 500ms...", attempt);
                        await Task.Delay(500);
                        continue;
                    }

                    break;
                }

                var (cleanText, toolCalls) = ParseToolCalls(lastText, effectiveTools);
                var finishReason = toolCalls != null && toolCalls.Count > 0 ? "tool_calls" : "stop";

                // 1. If there is explanatory text, emit text chunk first
                if (!string.IsNullOrEmpty(cleanText))
                {
                    var textChunk = new OpenAIChatCompletionResponse
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
                                    Role = "assistant",
                                    Content = cleanText
                                },
                                FinishReason = null
                            }
                        ]
                    };
                    await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(textChunk)}\n\n");
                    await httpResponse.Body.FlushAsync();
                }

                // 2. If tools were called, emit tool_calls delta chunk (with Index: 0, 1...)
                if (toolCalls != null && toolCalls.Count > 0)
                {
                    for (var i = 0; i < toolCalls.Count; i++)
                    {
                        toolCalls[i].Index = i;
                    }

                    var toolCallChunk = new OpenAIChatCompletionResponse
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
                                    Role = string.IsNullOrEmpty(cleanText) ? "assistant" : null,
                                    ToolCalls = toolCalls
                                },
                                FinishReason = null
                            }
                        ]
                    };
                    await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(toolCallChunk)}\n\n");
                    await httpResponse.Body.FlushAsync();
                }

                // 3. Emit final transition chunk with finish_reason and usage
                var promptTokens = EstimateTokens(prompt);
                var completionTokens = EstimateTokens(lastText);
                var finalToolChunk = new OpenAIChatCompletionResponse
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
                            FinishReason = finishReason
                        }
                    ],
                    Usage = new OpenAIUsage
                    {
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                        TotalTokens = promptTokens + completionTokens
                    }
                };

                await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(finalToolChunk)}\n\n");
                await httpResponse.WriteAsync("data: [DONE]\n\n");
                await httpResponse.Body.FlushAsync();
                return;
            }

            // Non-tool streaming path with glitch detection and automatic retry
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                using var httpRequestMessage = await BuildBatchExecuteRequestAsync(cookies, prompt, modelConfig, stream: true);
                using var response = await httpClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead);
                await cookieManager.UpdateFromResponseAsync(response);

                if ((response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                     response.StatusCode == System.Net.HttpStatusCode.Forbidden) && attempt == 1)
                {
                    logger.LogInformation("Refreshing session and retrying streaming request after 401/403...");
                    sessionService.InvalidateSession();
                    continue;
                }

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
                string fullCompletionText = string.Empty;
                bool detectedGlitch = false;

                while (await reader.ReadLineAsync() is { } line)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(")]}'") || int.TryParse(trimmed, out _))
                        continue;

                    var currentText = ExtractStreamingText(trimmed);
                    if (string.IsNullOrEmpty(currentText)) continue;

                    // If the initial stream chunk is a known Gemini Web glitch, intercept before streaming to client
                    if (isFirstChunk && IsGeminiGlitchResponse(currentText))
                    {
                        detectedGlitch = true;
                        break;
                    }

                    if (currentText.Length > previousLength)
                    {
                        var deltaText = currentText[previousLength..];
                        previousLength = currentText.Length;
                        fullCompletionText = currentText;

                        var chunk = BuildStreamChunk(deltaText, model, completionId, isFirstChunk);
                        isFirstChunk = false;

                        await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
                        await httpResponse.Body.FlushAsync();
                    }
                }

                if (detectedGlitch && attempt < 3)
                {
                    logger.LogWarning("Gemini Web returned temporary glitch response on attempt {Attempt} during text streaming. Retrying in 500ms...", attempt);
                    await Task.Delay(500);
                    continue;
                }

                // Send final chunk with finish_reason and usage
                var promptTok = EstimateTokens(prompt);
                var compTok = EstimateTokens(fullCompletionText);
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
                    ],
                    Usage = new OpenAIUsage
                    {
                        PromptTokens = promptTok,
                        CompletionTokens = compTok,
                        TotalTokens = promptTok + compTok
                    }
                };
                await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(finalChunk)}\n\n");
                await httpResponse.WriteAsync("data: [DONE]\n\n");
                await httpResponse.Body.FlushAsync();
                return;
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
    /// Resolves tools from either Tools or legacy Functions property.
    /// </summary>
    public static List<OpenAITool>? GetEffectiveTools(OpenAIChatCompletionRequest request)
    {
        if (request.Tools != null && request.Tools.Count > 0)
        {
            return request.Tools;
        }

        if (request.Functions != null && request.Functions.Count > 0)
        {
            return request.Functions.Select(f => new OpenAITool
            {
                Type = "function",
                Function = f
            }).ToList();
        }

        return null;
    }

    /// <summary>
    /// Builds a single text prompt from OpenAI messages, including tool definitions and model persona grounding.
    /// Handles system, user, assistant, and tool messages.
    /// </summary>
    public static string BuildPromptFromMessages(List<OpenAIMessage> messages, List<OpenAITool>? tools, GeminiWebModelConfig modelConfig)
    {
        var parts = new List<string>();

        if (tools != null && tools.Count > 0)
        {
            var toolDefs = tools.Select(t => new
            {
                name = t.Function.Name,
                description = t.Function.Description,
                parameters = t.Function.Parameters
            });
            var toolsJson = JsonSerializer.Serialize(toolDefs, new JsonSerializerOptions { WriteIndented = true });

            parts.Add(
                "[System Instructions]\n" +
                $"You are {modelConfig.DisplayName}, an autonomous AI software engineer embedded in the developer's workspace.\n" +
                "You have access to the following tools:\n" +
                toolsJson + "\n\n" +
                "INSTRUCTIONS FOR TOOL CALLING:\n" +
                "1. Whenever the user's request requires viewing, inspecting, searching, reading, creating, editing, or modifying files, executing commands, or exploring the project, you MUST invoke the appropriate tool.\n" +
                "2. The client runtime environment automatically executes your tool calls on your behalf and returns the results to you.\n" +
                "3. To call a tool, respond with a code block formatted exactly as:\n" +
                "```tool_call\n" +
                "{\"name\": \"tool_name\", \"arguments\": {\"arg1\": \"value1\"}}\n" +
                "```\n" +
                "4. AUTONOMOUS WORKSPACE EXPLORATION: When asked to add, build, modify, or integrate features, themes, or code, NEVER ask the user what frontend framework, libraries, or configuration files are used. You MUST first use available tools (such as list_files or read_file) to inspect the project structure, package.json, or workspace files autonomously.\n" +
                "5. DIRECT ACTION: Do NOT output greetings, self-introductions, or announcements of your model identity at the beginning of your responses. Proceed directly to calling the appropriate tools or answering the request."
            );
        }
        else
        {
            // Autonomous agent environment grounding for text-based tool callers (DeepSeek Harness, Roo Code, Trae, etc.)
            parts.Add(
                "[System Instructions]\n" +
                $"You are {modelConfig.DisplayName}, Google's {modelConfig.Description}, an autonomous AI software engineer embedded in the developer's workspace and client agent runtime.\n" +
                "All workspace tools, commands, and environment features (such as pwsh, glob, web_search, file reading, and editing) described in this session are fully available, active, and executed on your behalf by the client runtime.\n" +
                "CRITICAL OPERATIONAL RULES:\n" +
                "1. NEVER claim that tools, commands, or local workspace access are restricted, disabled, or unavailable.\n" +
                "2. When asked to add, build, modify, or integrate features, themes, or code in the workspace: NEVER ask the user for the project structure, framework, or files. Autonomously inspect and modify the workspace using the available tools and commands.\n" +
                "3. When asked about your model name or identity, clearly and directly identify yourself as " + modelConfig.DisplayName + ".\n" +
                "4. DIRECT ACTION: Do NOT output greetings, self-introductions, or conversational filler at the start of your responses. Proceed directly to answering the request or executing the necessary actions."
            );
        }

        foreach (var message in messages)
        {
            var text = message.Content == null
                ? string.Empty
                : message.Content.Value.ValueKind == JsonValueKind.String
                    ? message.Content.Value.GetString() ?? string.Empty
                    : ExtractTextFromContentParts(message.Content.Value);

            switch (message.Role)
            {
                case "system":
                    if (!string.IsNullOrEmpty(text))
                        parts.Add($"[System Instructions]\n{text}");
                    break;

                case "user":
                    if (!string.IsNullOrEmpty(text))
                        parts.Add($"[User]\n{text}");
                    break;

                case "assistant":
                    var assistantParts = new List<string>();
                    if (!string.IsNullOrEmpty(text))
                        assistantParts.Add(text);

                    if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                    {
                        foreach (var tc in message.ToolCalls)
                        {
                            assistantParts.Add($"```tool_call\n{{\"name\": \"{tc.Function.Name}\", \"arguments\": {tc.Function.Arguments}}}\n```");
                        }
                    }

                    if (assistantParts.Count > 0)
                    {
                        parts.Add($"[Assistant]\n{string.Join("\n", assistantParts)}");
                    }
                    break;

                case "tool":
                    var toolName = !string.IsNullOrEmpty(message.Name)
                        ? message.Name
                        : (!string.IsNullOrEmpty(message.ToolCallId) ? message.ToolCallId : "tool");
                    parts.Add($"[Tool Result for {toolName}]:\n{text}");
                    break;

                default:
                    if (!string.IsNullOrEmpty(text))
                        parts.Add(text);
                    break;
            }
        }

        if (messages.Count > 0 && messages[^1].Role == "tool")
        {
            parts.Add("Based on the tool results above, proceed with the next step to fulfill the user's request by calling the appropriate tool or completing the task.");
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
    /// Detects whether the response from Gemini Web is an internal glitch / canned error message.
    /// </summary>
    public static bool IsGeminiGlitchResponse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("I encountered an error doing what you asked", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("I seem to be encountering an error", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Sorry, something went wrong", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("I'm having a hard time fulfilling your request", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("An error occurred. Please try again", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sends a request to the Gemini Web batchexecute endpoint.
    /// </summary>
    private async Task<string> SendBatchExecuteAsync(string cookies, string prompt, GeminiWebModelConfig modelConfig, bool stream)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var request = await BuildBatchExecuteRequestAsync(cookies, prompt, modelConfig, stream);

            var response = await httpClient.SendAsync(request);
            await cookieManager.UpdateFromResponseAsync(response);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                if (attempt < 3 && IsGeminiGlitchResponse(responseBody))
                {
                    logger.LogWarning("Gemini Web returned temporary glitch response on attempt {Attempt}. Retrying in 500ms...", attempt);
                    await Task.Delay(500);
                    continue;
                }

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

            if (attempt == 3)
            {
                throw new HttpRequestException($"Gemini Web API error: {response.StatusCode} - {responseBody}");
            }
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
    /// Estimates token count for text (supports English, CJK, and code).
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int cjkCount = 0;
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) cjkCount++;
        }
        int nonCjk = text.Length - cjkCount;
        return Math.Max(1, cjkCount + (nonCjk / 4));
    }

    /// <summary>
    /// Builds the final OpenAI-format response from parsed text and optional tool calls.
    /// </summary>
    private static OpenAIChatCompletionResponse BuildOpenAIResponse(
        string? content,
        List<OpenAIToolCall>? toolCalls,
        string finishReason,
        string model,
        int promptTokens = 0,
        int completionTokens = 0)
    {
        if (completionTokens == 0)
        {
            completionTokens = Math.Max(1, (content?.Length ?? 0) / 4);
        }

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
                        Content = content,
                        ToolCalls = toolCalls
                    },
                    FinishReason = finishReason
                }
            ],
            Usage = new OpenAIUsage
            {
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = promptTokens + completionTokens
            }
        };
    }

    /// <summary>
    /// Parses tool_call codeblocks from model text output.
    /// </summary>
    public static (string? CleanText, List<OpenAIToolCall>? ToolCalls) ParseToolCalls(string text, List<OpenAITool>? tools)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (text, null);
        }

        var knownTools = tools != null && tools.Count > 0
            ? new HashSet<string>(tools.Select(t => t.Function.Name), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var toolCalls = new List<OpenAIToolCall>();
        var cleanText = text;

        // 1. Try ```tool_call ... ``` or ```json ... ``` blocks
        var matches = CodeBlockRegex().Matches(text);
        foreach (Match match in matches)
        {
            var blockContent = match.Groups[1].Value.Trim();
            if (TryParseToolCallJson(blockContent, knownTools, out var call))
            {
                toolCalls.Add(call);
                cleanText = cleanText.Replace(match.Value, string.Empty);
            }
        }

        // 2. Try XML-style <tool_call>...</tool_call>
        var xmlMatches = XmlToolCallRegex().Matches(cleanText);
        foreach (Match match in xmlMatches)
        {
            var blockContent = match.Groups[1].Value.Trim();
            if (TryParseToolCallJson(blockContent, knownTools, out var call))
            {
                toolCalls.Add(call);
                cleanText = cleanText.Replace(match.Value, string.Empty);
            }
        }

        // 3. Fallback: if no blocks matched, check if entire response is a JSON tool call
        if (toolCalls.Count == 0 && cleanText.TrimStart().StartsWith('{'))
        {
            if (TryParseToolCallJson(cleanText.Trim(), knownTools, out var call))
            {
                toolCalls.Add(call);
                cleanText = null;
            }
        }

        if (toolCalls.Count > 0)
        {
            for (var i = 0; i < toolCalls.Count; i++)
            {
                toolCalls[i].Index = i;
            }
        }

        var trimmed = cleanText?.Trim();
        var finalContent = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        return (finalContent, toolCalls.Count > 0 ? toolCalls : null);
    }

    private static bool TryParseToolCallJson(string jsonStr, HashSet<string> knownTools, out OpenAIToolCall call)
    {
        call = new OpenAIToolCall();
        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            string? name = null;
            if (root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
            {
                name = nameProp.GetString();
            }
            else if (root.TryGetProperty("function", out var fnProp) && fnProp.ValueKind == JsonValueKind.Object &&
                     fnProp.TryGetProperty("name", out var fnNameProp) && fnNameProp.ValueKind == JsonValueKind.String)
            {
                name = fnNameProp.GetString();
            }

            if (string.IsNullOrEmpty(name)) return false;

            if (knownTools.Count > 0 && !knownTools.Contains(name))
            {
                return false;
            }

            string argumentsStr = "{}";
            if (root.TryGetProperty("arguments", out var argsProp))
            {
                argumentsStr = argsProp.ValueKind == JsonValueKind.String
                    ? argsProp.GetString() ?? "{}"
                    : argsProp.GetRawText();
            }
            else if (root.TryGetProperty("parameters", out var paramsProp))
            {
                argumentsStr = paramsProp.ValueKind == JsonValueKind.String
                    ? paramsProp.GetString() ?? "{}"
                    : paramsProp.GetRawText();
            }

            call.Id = $"call_{Guid.NewGuid():N}"[..12];
            call.Type = "function";
            call.Function = new OpenAIToolCallFunction
            {
                Name = name,
                Arguments = argumentsStr
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    [GeneratedRegex(@"```(?:tool_call|json)?\s*\n?(.*?)\n?```", RegexOptions.Singleline)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"<tool_call>\s*(.*?)\s*</tool_call>", RegexOptions.Singleline)]
    private static partial Regex XmlToolCallRegex();

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
