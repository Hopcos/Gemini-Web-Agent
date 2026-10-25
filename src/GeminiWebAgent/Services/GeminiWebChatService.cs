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

    private const int MaxUpstreamAttempts = 3;

    /// <summary>
    /// Non-streaming chat completion.
    /// </summary>
    public async Task<OpenAIChatCompletionResponse> ChatCompletionAsync(string cookies, OpenAIChatCompletionRequest request)
    {
        var modelConfig = GeminiWebModelMap.GetModelConfig(request.Model);
        var effectiveTools = GetEffectiveTools(request);
        var prompt = BuildPromptFromMessages(request.Messages, effectiveTools, modelConfig);
        var model = modelConfig.CanonicalName;
        LogRequest(request, effectiveTools, prompt, model);

        var parsedText = await GenerateWithRetryAsync(cookies, prompt, modelConfig);

        var (cleanText, toolCalls) = ParseToolCalls(parsedText, effectiveTools);
        var finishReason = toolCalls != null && toolCalls.Count > 0 ? "tool_calls" : "stop";
        LogReply(prompt, parsedText, effectiveTools, toolCalls);

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
        LogRequest(request, effectiveTools, prompt, model);

        var completionId = $"chatcmpl-{Guid.NewGuid():N}";

        try
        {
            // If tools are provided: accumulate the full response so tool_calls can be parsed properly
            if (effectiveTools != null && effectiveTools.Count > 0)
            {
                var fullText = await GenerateWithRetryAsync(cookies, prompt, modelConfig);

                var (cleanText, toolCalls) = ParseToolCalls(fullText, effectiveTools);
                var finishReason = toolCalls != null && toolCalls.Count > 0 ? "tool_calls" : "stop";
                LogReply(prompt, fullText, effectiveTools, toolCalls);

                // 1. If there is explanatory text, emit text chunk first
                if (!string.IsNullOrEmpty(cleanText))
                {
                    await WriteSseAsync(httpResponse, BuildStreamChunk(cleanText, model, completionId, isFirstChunk: true));
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
                    await WriteSseAsync(httpResponse, toolCallChunk);
                }

                // 3. Emit final transition chunk with finish_reason and usage
                await WriteSseAsync(httpResponse, BuildFinalChunk(model, completionId, finishReason, prompt, fullText));
                await WriteDoneAsync(httpResponse);
                return;
            }

            // Plain text streaming: deltas are forwarded as soon as upstream produces them.
            // Glitch / empty upstream replies are retried before the first delta is written.
            var previousLength = 0;
            var isFirstChunk = true;

            var text = await GenerateWithRetryAsync(cookies, prompt, modelConfig, async snapshot =>
            {
                if (snapshot.Length <= previousLength) return;

                var deltaText = snapshot[previousLength..];
                previousLength = snapshot.Length;

                await WriteSseAsync(httpResponse, BuildStreamChunk(deltaText, model, completionId, isFirstChunk));
                isFirstChunk = false;
            });

            await WriteSseAsync(httpResponse, BuildFinalChunk(model, completionId, "stop", prompt, text));
            await WriteDoneAsync(httpResponse);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during streaming chat completion");
            var (statusCode, message, code) = GeminiWebUpstreamException.Describe(ex);

            if (!httpResponse.HasStarted)
            {
                // Nothing has been sent yet: answer with a real HTTP error so the client can retry / surface it.
                httpResponse.StatusCode = statusCode;
                httpResponse.ContentType = "application/json";
                await httpResponse.WriteAsJsonAsync(new { error = new { message, type = "upstream_error", code } });
                return;
            }

            await WriteSseAsync(httpResponse, CreateStreamErrorChunk(model, completionId, message));
            await WriteDoneAsync(httpResponse);
        }
    }

    private void LogRequest(OpenAIChatCompletionRequest request, List<OpenAITool>? tools, string prompt, string model)
    {
        logger.LogInformation(
            "Chat request: model={Model} stream={Stream} messages={Messages} tools={Tools} promptChars={PromptChars} lastRole={LastRole}",
            model, request.Stream, request.Messages.Count, tools?.Count ?? 0, prompt.Length, request.Messages.LastOrDefault()?.Role);
    }

    /// <summary>
    /// A reply that mentions a tool call but yielded none ends the agent loop silently, so say so loudly.
    /// With GeminiWeb:LogPayloads the prompt tail and the full reply are logged as well.
    /// </summary>
    private void LogReply(string prompt, string reply, List<OpenAITool>? tools, List<OpenAIToolCall>? toolCalls)
    {
        var parsedCount = toolCalls?.Count ?? 0;
        logger.LogInformation("Reply: chars={Chars} toolCalls={ToolCalls}", reply.Length, parsedCount);

        if (tools is { Count: > 0 } && parsedCount == 0 &&
            (reply.Contains("tool_call", StringComparison.OrdinalIgnoreCase) || reply.Contains("\"arguments\"", StringComparison.Ordinal)))
        {
            logger.LogWarning("Reply looks like a tool call but none could be parsed; it is returned as plain text and ends the agent loop. Reply: {Reply}",
                reply.Length > 3000 ? reply[..3000] : reply);
        }

        if (_options.LogPayloads)
        {
            logger.LogInformation("Payload prompt (tail): {Prompt}", prompt.Length > 4000 ? prompt[^4000..] : prompt);
            logger.LogInformation("Payload reply: {Reply}", reply.Length > 6000 ? reply[..6000] : reply);
        }
    }

    private static async Task WriteSseAsync(HttpResponse httpResponse, OpenAIChatCompletionResponse chunk)
    {
        await httpResponse.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
        await httpResponse.Body.FlushAsync();
    }

    private static async Task WriteDoneAsync(HttpResponse httpResponse)
    {
        await httpResponse.WriteAsync("data: [DONE]\n\n");
        await httpResponse.Body.FlushAsync();
    }

    private static OpenAIChatCompletionResponse BuildFinalChunk(string model, string completionId, string finishReason, string prompt, string completionText)
    {
        var promptTokens = EstimateTokens(prompt);
        var completionTokens = EstimateTokens(completionText);

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
        var conversation = SanitizeMessages(messages);

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
                "5. DIRECT ACTION: Do NOT output greetings, self-introductions, or announcements of your model identity at the beginning of your responses. Proceed directly to calling the appropriate tools or answering the request.\n" +
                "6. Always provide every required argument of the tool you call. The block must contain valid JSON: write newlines inside string values as \\n and escape double quotes.\n" +
                "7. A file is only created, modified or a command only run once a [Tool Result ...] in this conversation confirms it. Never state or imply otherwise.\n" +
                "8. Reply to the latest message in the conversation; earlier turns are context only.\n" +
                "9. COMPLETE THE WHOLE REQUEST: a single edit is rarely the whole job. After every [Tool Result] decide what is still missing (other files to create or update, wiring, defaults, a build or test run) and call the next tool right away. Reply without a tool call only when every part of the request is implemented, and then give a short summary.\n" +
                "10. When several steps are independent (for example creating several files) you may output several ```tool_call blocks in one reply; they are executed in order."
            );
        }
        else if (conversation.Any(m => m.Role is "system" or "developer" && GetMessageText(m).Length > 0))
        {
            // The client's own system prompt defines the (text based) tool protocol. Stay out of its way:
            // only point at it and keep the model honest, instead of asserting capabilities of our own.
            parts.Add(
                "[System Instructions]\n" +
                $"You are {modelConfig.DisplayName}, Google's {modelConfig.Description}, acting as the assistant of the client application described in the instructions that follow.\n" +
                "Those instructions define the tools, commands and output format available in this session. The client application executes them for you and sends the results back as later messages.\n" +
                "RULES:\n" +
                "1. Follow the client's instructions exactly, including the exact syntax they require for calling tools.\n" +
                "2. Inspect the workspace with the client's tools instead of asking the user for information you can discover yourself.\n" +
                "3. Only report that a file was changed or a command was run once a tool result in this conversation confirms it.\n" +
                "4. When asked about your model name or identity, answer that you are " + modelConfig.DisplayName + ".\n" +
                "5. Reply to the latest message in the conversation without greetings or self-introduction; earlier turns are context only.\n" +
                "6. Keep working step by step until the whole request is implemented; do not stop or summarize after the first edit while parts are still missing."
            );
        }
        else
        {
            parts.Add(
                "[System Instructions]\n" +
                $"You are {modelConfig.DisplayName}, Google's {modelConfig.Description}. " +
                "Reply to the latest message in the conversation directly; earlier turns are context only. " +
                "You cannot access the user's files or run commands in this conversation, so never claim to have done so."
            );
        }

        foreach (var message in conversation)
        {
            var text = GetMessageText(message);

            switch (message.Role)
            {
                case "system":
                case "developer":
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
                            assistantParts.Add($"```tool_call\n{{\"name\": \"{tc.Function.Name}\", \"arguments\": {tc.Function.Arguments ?? "{}"}}}\n```");
                        }
                    }

                    if (assistantParts.Count > 0)
                    {
                        parts.Add($"[Assistant]\n{string.Join("\n", assistantParts)}");
                    }
                    break;

                case "tool":
                case "function":
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

        if (conversation.Count > 0 && conversation[^1].Role is "tool" or "function")
        {
            parts.Add("Based on the tool results above, continue with the next step of the user's request: if anything is still missing or unverified, call the next tool now. Only when the whole request is fully done, reply with a short summary and no tool call.");
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Cleans the replayed history before it is flattened into a prompt:
    /// drops canned Gemini error replies (they make the model repeat the error) and
    /// collapses identical consecutive user messages (agent clients resend the request after an error).
    /// </summary>
    public static List<OpenAIMessage> SanitizeMessages(List<OpenAIMessage> messages)
    {
        var result = new List<OpenAIMessage>(messages.Count);

        foreach (var message in messages)
        {
            if (message == null) continue;

            if (message.Role == "assistant" &&
                message.ToolCalls is not { Count: > 0 } &&
                IsGeminiGlitchResponse(GetMessageText(message)))
            {
                continue;
            }

            if (message.Role == "user" &&
                result.Count > 0 &&
                result[^1].Role == "user" &&
                GetMessageText(result[^1]) == GetMessageText(message))
            {
                result[^1] = message;
                continue;
            }

            result.Add(message);
        }

        return result;
    }

    /// <summary>
    /// Returns the plain text of a message whose content is either a string or an array of content parts.
    /// </summary>
    private static string GetMessageText(OpenAIMessage message)
    {
        if (message.Content is not { } content) return string.Empty;

        return content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? string.Empty
            : ExtractTextFromContentParts(content);
    }

    /// <summary>
    /// Extracts text from multimodal content parts (non-text parts such as images are skipped).
    /// </summary>
    private static string ExtractTextFromContentParts(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;

        var texts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                if (part.GetString() is { } plain) texts.Add(plain);
            }
            else if (part.ValueKind == JsonValueKind.Object &&
                     part.TryGetProperty("text", out var textProp) &&
                     textProp.ValueKind == JsonValueKind.String &&
                     textProp.GetString() is { } partText)
            {
                texts.Add(partText);
            }
        }

        return string.Join("\n", texts);
    }

    /// <summary>
    /// Detects whether the response from Gemini Web is an internal glitch / canned error message.
    /// Only short replies qualify: a long answer that merely mentions such a phrase (e.g. generated
    /// error-page code) is legitimate content.
    /// </summary>
    public static bool IsGeminiGlitchResponse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Trim().Length > 200) return false;

        return text.Contains("I encountered an error doing what you asked", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("I seem to be encountering an error", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Sorry, something went wrong", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("I'm having a hard time fulfilling your request", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("An error occurred. Please try again", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Calls StreamGenerate and returns the final text, retrying transient failures:
    /// 401/403 (session refresh), 4xx/5xx upstream errors, Gemini's canned glitch replies and empty replies.
    /// <paramref name="onProgress"/> receives each accumulated text snapshot as soon as it arrives;
    /// retries only happen while nothing has been handed to it yet.
    /// Throws <see cref="GeminiWebUpstreamException"/> when every attempt failed.
    /// </summary>
    private async Task<string> GenerateWithRetryAsync(
        string cookies,
        string prompt,
        GeminiWebModelConfig modelConfig,
        Func<string, Task>? onProgress = null)
    {
        var failure = "Gemini Web returned no response";

        for (var attempt = 1; attempt <= MaxUpstreamAttempts; attempt++)
        {
            using var httpRequestMessage = await BuildBatchExecuteRequestAsync(cookies, prompt, modelConfig, stream: true);
            using var response = await httpClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead);
            await cookieManager.UpdateFromResponseAsync(response);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                logger.LogWarning("Gemini Web API error on attempt {Attempt}/{Max}: {StatusCode} - {Body}",
                    attempt, MaxUpstreamAttempts, response.StatusCode, errorBody.Length > 500 ? errorBody[..500] : errorBody);

                // Retrying (and refreshing the session) only makes Google's throttling worse
                if (GeminiWebUpstreamException.IsRateLimited(response))
                {
                    throw GeminiWebUpstreamException.RateLimited();
                }

                // Stale bl/at parameters or expired cookies: rebuild the session before trying again
                sessionService.InvalidateSession();

                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    if (attempt == 1)
                    {
                        logger.LogInformation("Refreshing session and retrying after 401/403...");
                        continue;
                    }

                    throw new GeminiWebUpstreamException(401, "Authentication failed. Cookies may be invalid or expired.");
                }

                failure = $"Gemini Web API error: {(int)response.StatusCode} {response.StatusCode}";
                if (attempt < MaxUpstreamAttempts && (int)response.StatusCode >= 500)
                {
                    await Task.Delay(500 * attempt);
                    continue;
                }

                throw new GeminiWebUpstreamException(502, failure);
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var lastText = string.Empty;
            var emitted = false;
            var glitch = false;

            while (await reader.ReadLineAsync() is { } line)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(")]}'") || int.TryParse(trimmed, out _))
                    continue;

                var currentText = ExtractStreamingText(trimmed);
                if (string.IsNullOrEmpty(currentText)) continue;

                if (IsGeminiGlitchResponse(currentText))
                {
                    glitch = true;
                    if (!emitted && string.IsNullOrEmpty(lastText)) break;
                    continue; // real content already arrived; ignore the trailing error marker
                }

                lastText = currentText;

                if (onProgress != null)
                {
                    await onProgress(currentText);
                    emitted = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(lastText))
                return lastText;

            failure = glitch
                ? "Gemini Web returned a temporary error (\"I encountered an error doing what you asked\")"
                : "Gemini Web returned an empty response";
            logger.LogWarning("{Failure} on attempt {Attempt}/{Max}", failure, attempt, MaxUpstreamAttempts);

            if (attempt < MaxUpstreamAttempts)
            {
                // From the second failure on, rebuild bl/at in case the cached session went stale
                if (attempt >= 2) sessionService.InvalidateSession();
                await Task.Delay(500 * attempt);
            }
        }

        throw new GeminiWebUpstreamException(502, $"{failure} on all {MaxUpstreamAttempts} attempts. Please retry.");
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
    /// Extracts tool calls from the model's text output.
    /// A call is a JSON object following a ```tool_call / ```json / ``` fence, a &lt;tool_call&gt; tag, or making up the whole reply.
    /// The object is cut out by brace matching (not by the closing fence) so file contents that themselves
    /// contain ``` survive, and raw newlines inside JSON strings are repaired. Anything that is not a call to a
    /// declared tool stays in the text.
    /// </summary>
    public static (string? CleanText, List<OpenAIToolCall>? ToolCalls) ParseToolCalls(string text, List<OpenAITool>? tools)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (text, null);
        }

        // Without declared tools nothing can be a tool call: a ```json snippet in a normal answer must stay text.
        if (tools == null || tools.Count == 0)
        {
            return (text, null);
        }

        var toolNames = new Dictionary<string, string>();
        foreach (var tool in tools)
        {
            toolNames.TryAdd(NormalizeToolName(tool.Function.Name), tool.Function.Name);
        }

        var toolCalls = new List<OpenAIToolCall>();
        var cleanText = new StringBuilder();
        var consumedUntil = 0;

        foreach (var candidate in FindToolCallCandidates(text))
        {
            if (candidate.JsonStart < consumedUntil) continue; // inside an already extracted call (e.g. file content)

            var jsonEnd = FindJsonObjectEnd(text, candidate.JsonStart);
            if (jsonEnd < 0) continue;

            var json = text[candidate.JsonStart..jsonEnd];
            if (!TryParseToolCallJson(json, toolNames, out var call)) continue;

            var end = SkipClosingMarker(text, jsonEnd);
            cleanText.Append(text, consumedUntil, candidate.MarkerStart - consumedUntil);
            consumedUntil = end;
            toolCalls.Add(call);
        }

        cleanText.Append(text, consumedUntil, text.Length - consumedUntil);

        for (var i = 0; i < toolCalls.Count; i++)
        {
            toolCalls[i].Index = i;
        }

        if (toolCalls.Count == 0)
        {
            return (text.Trim() is { Length: > 0 } unchanged ? unchanged : null, null);
        }

        var trimmed = cleanText.ToString().Trim();
        return (string.IsNullOrEmpty(trimmed) ? null : trimmed, toolCalls);
    }

    private readonly record struct ToolCallCandidate(int MarkerStart, int JsonStart);

    /// <summary>
    /// Positions where a tool call object may start: after an opening fence / tag, or at the start of the reply.
    /// </summary>
    private static IEnumerable<ToolCallCandidate> FindToolCallCandidates(string text)
    {
        var firstNonSpace = 0;
        while (firstNonSpace < text.Length && char.IsWhiteSpace(text[firstNonSpace])) firstNonSpace++;
        if (firstNonSpace < text.Length && text[firstNonSpace] == '{')
        {
            yield return new ToolCallCandidate(0, firstNonSpace);
        }

        foreach (Match opener in ToolCallOpenerRegex().Matches(text))
        {
            var jsonStart = opener.Index + opener.Length;
            while (jsonStart < text.Length && char.IsWhiteSpace(text[jsonStart])) jsonStart++;

            if (jsonStart < text.Length && text[jsonStart] == '{' && jsonStart != firstNonSpace)
            {
                yield return new ToolCallCandidate(opener.Index, jsonStart);
            }
        }
    }

    /// <summary>
    /// Returns the index just past the JSON object starting at <paramref name="start"/>, or -1 when it never closes.
    /// String-aware, so braces / fences inside string values do not matter.
    /// </summary>
    private static int FindJsonObjectEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return i + 1;
                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// Skips the closing ``` or &lt;/tool_call&gt; that follows a call (it may be missing when the reply was cut short).
    /// </summary>
    private static int SkipClosingMarker(string text, int position)
    {
        var i = position;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

        if (string.CompareOrdinal(text, i, "```", 0, 3) == 0) return i + 3;
        if (string.Compare(text, i, "</tool_call>", 0, 12, StringComparison.OrdinalIgnoreCase) == 0) return i + 12;

        return position;
    }

    private static string NormalizeToolName(string name)
    {
        // "functions.read_file" / "default_api:read_file" -> "read_file"
        var cut = name.LastIndexOfAny(['.', ':', '/']);
        if (cut >= 0 && cut < name.Length - 1) name = name[(cut + 1)..];

        return new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static readonly string[] ToolNameKeys = ["name", "tool", "tool_name", "toolName", "function", "function_name", "action"];
    private static readonly string[] ToolArgumentKeys = ["arguments", "parameters", "args", "input", "params"];

    private static bool TryParseToolCallJson(string jsonStr, Dictionary<string, string> toolNames, out OpenAIToolCall call)
    {
        call = new OpenAIToolCall();

        if (!TryParseJsonObject(jsonStr, out var doc)) return false;

        using (doc)
        {
            var root = doc.RootElement;

            // {"function_call": {...}} / {"function": {"name": ..., "arguments": ...}} wrap the real call one level down
            foreach (var wrapper in new[] { "function_call", "tool_call", "function" })
            {
                if (root.TryGetProperty(wrapper, out var inner) && inner.ValueKind == JsonValueKind.Object)
                {
                    root = inner;
                    break;
                }
            }

            string? name = null;
            foreach (var key in ToolNameKeys)
            {
                if (root.TryGetProperty(key, out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                {
                    name = nameProp.GetString();
                    break;
                }
            }

            if (string.IsNullOrEmpty(name)) return false;
            if (!toolNames.TryGetValue(NormalizeToolName(name), out var declaredName)) return false;

            var argumentsStr = "{}";
            foreach (var key in ToolArgumentKeys)
            {
                if (!root.TryGetProperty(key, out var argsProp)) continue;

                argumentsStr = argsProp.ValueKind == JsonValueKind.String
                    ? argsProp.GetString() ?? "{}"
                    : argsProp.GetRawText();
                break;
            }

            call.Id = $"call_{Guid.NewGuid():N}"[..12];
            call.Type = "function";
            call.Function = new OpenAIToolCallFunction
            {
                Name = declaredName,
                Arguments = argumentsStr
            };
            return true;
        }
    }

    /// <summary>
    /// Parses a JSON object, repairing the usual model slip: raw newlines / tabs inside string values.
    /// </summary>
    private static bool TryParseJsonObject(string json, out JsonDocument doc)
    {
        foreach (var candidate in new[] { json, EscapeControlCharsInStrings(json) })
        {
            try
            {
                doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Object) return true;
                doc.Dispose();
            }
            catch (JsonException)
            {
                // try the repaired variant
            }
        }

        doc = null!;
        return false;
    }

    private static string EscapeControlCharsInStrings(string json)
    {
        var sb = new StringBuilder(json.Length + 16);
        var inString = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];

            if (!inString)
            {
                if (c == '"') inString = true;
                sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '\\' when i + 1 < json.Length:
                    sb.Append(c).Append(json[++i]);
                    break;
                case '"':
                    inString = false;
                    sb.Append(c);
                    break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    // ```tool_call / ```json / ```js / bare ``` followed by the object, or an XML-style tag
    [GeneratedRegex(@"```[A-Za-z_]*[ \t]*\r?\n?|<tool_call>", RegexOptions.IgnoreCase)]
    private static partial Regex ToolCallOpenerRegex();

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
