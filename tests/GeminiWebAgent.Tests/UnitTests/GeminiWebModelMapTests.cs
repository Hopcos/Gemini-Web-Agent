using GeminiWebAgent.Models.GeminiWeb;
using GeminiWebAgent.Services;

namespace GeminiWebAgent.Tests.UnitTests;

public class GeminiWebModelMapTests
{
    [Theory]
    [InlineData("gemini-4-argon", "models/gemini-4-argon")]
    [InlineData("4 Argon", "models/gemini-4-argon")]
    [InlineData("4-argon", "models/gemini-4-argon")]
    [InlineData("argon", "models/gemini-4-argon")]
    [InlineData("gemini-3.8-flash", "models/gemini-3.8-flash")]
    [InlineData("3.8 Flash", "models/gemini-3.8-flash")]
    [InlineData("3.8-flash", "models/gemini-3.8-flash")]
    [InlineData("gemini-3.5-flash-lite", "models/gemini-3.5-flash-lite")]
    [InlineData("3.5 Flash-Lite", "models/gemini-3.5-flash-lite")]
    [InlineData("3.5-flash-lite", "models/gemini-3.5-flash-lite")]
    [InlineData("gemini-3.1-pro", "models/gemini-3.1-pro")]
    [InlineData("3.1 Pro", "models/gemini-3.1-pro")]
    [InlineData("3.1-pro", "models/gemini-3.1-pro")]
    [InlineData("gemini-2.5-pro", "models/gemini-2.5-pro")]
    [InlineData("gemini-2.5-flash", "models/gemini-2.5-flash")]
    [InlineData("gemini-2.5-flash-thinking", "models/gemini-2.5-flash-thinking")]
    [InlineData("gemini-2.0-flash", "models/gemini-2.0-flash")]
    [InlineData("gemini-1.5-pro", "models/gemini-1.5-pro")]
    [InlineData("GEMINI-2.5-FLASH", "models/gemini-2.5-flash")] // Case-insensitive
    public void GetGeminiModelId_KnownModel_ReturnsExpectedMapping(string input, string expected)
    {
        var result = GeminiWebModelMap.GetGeminiModelId(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetGeminiModelId_UnknownModel_PrependsModelsPrefix()
    {
        var result = GeminiWebModelMap.GetGeminiModelId("custom-gemini-model");
        Assert.Equal("models/custom-gemini-model", result);
    }

    [Fact]
    public void GetAllModels_ReturnsNonEmptyList()
    {
        var models = GeminiWebModelMap.GetAllModels();
        Assert.NotEmpty(models);
        Assert.Contains("gemini-4-argon", models);
        Assert.Contains("gemini-3.8-flash", models);
        Assert.Contains("gemini-3.5-flash-lite", models);
        Assert.Contains("gemini-3.1-pro", models);
        Assert.Contains("gemini-2.5-flash", models);
        Assert.Contains("gemini-2.5-pro", models);
    }

    [Fact]
    public void GeminiWebModelService_ListModels_ReturnsOpenAIModelList()
    {
        var service = new GeminiWebModelService();
        var response = service.ListModels();

        Assert.Equal("list", response.Object);
        Assert.NotEmpty(response.Data);
        Assert.All(response.Data, model =>
        {
            Assert.Equal("model", model.Object);
            Assert.Equal("google", model.OwnedBy);
            Assert.True(model.Created > 0);
            Assert.False(string.IsNullOrEmpty(model.Id));
        });
    }
}
