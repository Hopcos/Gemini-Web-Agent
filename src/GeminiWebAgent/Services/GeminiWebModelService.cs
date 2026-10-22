using GeminiWebAgent.Models.GeminiWeb;
using GeminiWebAgent.Models.OpenAI;

namespace GeminiWebAgent.Services;

/// <summary>
/// Provides a list of available Gemini models in OpenAI format.
/// </summary>
public class GeminiWebModelService
{
    public OpenAIModelResponse ListModels()
    {
        var models = GeminiWebModelMap.GetAllModels().Select(modelId => new OpenAIModel
        {
            Id = modelId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            OwnedBy = "google"
        }).ToList();

        return new OpenAIModelResponse { Data = models };
    }
}
