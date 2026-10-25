using GeminiWebAgent.Models.OpenAI;
using GeminiWebAgent.Services;

namespace GeminiWebAgent.Endpoints;

public static class ChatCompletionEndpoint
{
    public static void MapChatCompletionEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/chat/completions", async (HttpContext context, OpenAIChatCompletionRequest request, GeminiWebChatService chatService, ILogger<GeminiWebChatService> logger) =>
        {
            var cookies = context.Items["Cookies"]?.ToString() ?? string.Empty;

            if (request.Stream)
            {
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers["Cache-Control"] = "no-cache";
                context.Response.Headers["Connection"] = "keep-alive";

                // Errors are reported by the service itself (HTTP error before the first byte, error chunk after)
                await chatService.StreamChatCompletionAsync(cookies, request, context.Response);
                return Results.Empty;
            }

            try
            {
                var result = await chatService.ChatCompletionAsync(cookies, request);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during chat completion");
                var (statusCode, message, code) = GeminiWebUpstreamException.Describe(ex);
                return Results.Json(
                    new { error = new { message, type = "upstream_error", code } },
                    statusCode: statusCode);
            }
        });
    }
}
