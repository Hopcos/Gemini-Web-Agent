namespace GeminiWebAgent.Endpoints;

public static class HealthEndpoint
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/health", () => Results.Ok(new { status = "ok" }));
    }
}
