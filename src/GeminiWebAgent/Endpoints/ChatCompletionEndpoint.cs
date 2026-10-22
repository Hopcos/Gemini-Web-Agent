using GeminiWebAgent.Models.OpenAI;
using GeminiWebAgent.Services;

namespace GeminiWebAgent.Endpoints;

public static class ChatCompletionEndpoint
{
    public static void MapChatCompletionEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/chat/completions", async (HttpContext context, OpenAIChatCompletionRequest request, GeminiWebChatService chatService) =>
        {
            var cookies = context.Items["Cookies"]?.ToString() ?? string.Empty;

            if (request.Stream)
            {
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers["Cache-Control"] = "no-cache";
                context.Response.Headers["Connection"] = "keep-alive";

                await chatService.StreamChatCompletionAsync(cookies, request, context.Response);
            }
            else
            {
                var result = await chatService.ChatCompletionAsync(cookies, request);
                return Results.Ok(result);
            }

            return Results.Empty;
        });
    }
}
