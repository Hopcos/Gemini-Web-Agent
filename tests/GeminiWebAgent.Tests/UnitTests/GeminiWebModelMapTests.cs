using GeminiWebAgent.Models.GeminiWeb;
using GeminiWebAgent.Services;

namespace GeminiWebAgent.Tests.UnitTests;

public class GeminiWebModelMapTests
{
    [Theory]
    [InlineData("gemini-4-argon", "gemini-4-argon", 3, 4)]
    [InlineData("4 Argon", "gemini-4-argon", 3, 4)]
    [InlineData("4-argon", "gemini-4-argon", 3, 4)]
    [InlineData("argon", "gemini-4-argon", 3, 4)]
    [InlineData("gemini-3.8-flash", "gemini-3.8-flash", 1, 4)]
    [InlineData("3.8 Flash", "gemini-3.8-flash", 1, 4)]
    [InlineData("3.8-flash", "gemini-3.8-flash", 1, 4)]
    [InlineData("gemini-3.5-flash-lite", "gemini-3.5-flash-lite", 6, 4)]
    [InlineData("3.5 Flash-Lite", "gemini-3.5-flash-lite", 6, 4)]
    [InlineData("3.5-flash-lite", "gemini-3.5-flash-lite", 6, 4)]
    [InlineData("gemini-3.1-pro", "gemini-3.1-pro", 3, 4)]
    [InlineData("3.1 Pro", "gemini-3.1-pro", 3, 4)]
    [InlineData("3.1-pro", "gemini-3.1-pro", 3, 4)]
    [InlineData("gemini-2.5-pro", "gemini-2.5-pro", 3, 4)]
    [InlineData("gemini-2.5-flash", "gemini-2.5-flash", 1, 4)]
    [InlineData("gemini-2.5-flash-thinking", "gemini-2.5-flash-thinking", 2, 0)]
    [InlineData("gemini-2.0-flash", "gemini-2.0-flash", 1, 4)]
    [InlineData("gemini-1.5-pro", "gemini-1.5-pro", 3, 4)]
    [InlineData("GEMINI-2.5-FLASH", "gemini-2.5-flash", 1, 4)]
    public void GetModelConfig_KnownModel_ReturnsExpectedConfig(string input, string expectedCanonical, int expectedMode, int expectedThink)
    {
        var config = GeminiWebModelMap.GetModelConfig(input);
        Assert.Equal(expectedCanonical, config.CanonicalName);
        Assert.Equal(expectedMode, config.ModeCategory);
        Assert.Equal(expectedThink, config.ThinkMode);
    }

    [Fact]
    public void GetModelConfig_UnknownModel_FallsBackToDefault()
    {
        var config = GeminiWebModelMap.GetModelConfig("custom-gemini-model");
        Assert.Equal("gemini-3.8-flash", config.CanonicalName);
        Assert.Equal(1, config.ModeCategory);
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
