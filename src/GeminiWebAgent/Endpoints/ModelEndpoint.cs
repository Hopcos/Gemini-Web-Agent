using GeminiWebAgent.Services;

namespace GeminiWebAgent.Endpoints;

public static class ModelEndpoint
{
    public static void MapModelEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/models", (GeminiWebModelService modelService) =>
        {
            var result = modelService.ListModels();
            return Results.Ok(result);
        });
    }
}
