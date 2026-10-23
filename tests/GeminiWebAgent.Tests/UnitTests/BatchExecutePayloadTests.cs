using System.Text.Json;
using GeminiWebAgent.Models.GeminiWeb;
using GeminiWebAgent.Services;

namespace GeminiWebAgent.Tests.UnitTests;

public class BatchExecutePayloadTests
{
    [Fact]
    public void BatchExecute_AntiXsrfPrefix_CanBeStripped()
    {
        var rawResponse = ")]}'\n[[[\"wrb.fr\",\"otAQ7b\",\"[\\\"test response\\\"]\",null,null,null,1]]]";

        Assert.StartsWith(")]}'", rawResponse);
        var newlineIdx = rawResponse.IndexOf('\n');
        Assert.True(newlineIdx >= 0);

        var cleaned = rawResponse[(newlineIdx + 1)..];
        var doc = JsonDocument.Parse(cleaned);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

        var firstOuter = doc.RootElement[0][0];
        Assert.Equal("wrb.fr", firstOuter[0].GetString());
        Assert.Equal("otAQ7b", firstOuter[1].GetString());
    }

    [Fact]
    public void BatchExecute_FReqWrapper_HasValidArrayStructure()
    {
        var serializedInner = JsonSerializer.Serialize(new object?[]
        {
            new object?[] { "Hello from Gemini" },
            null,
            null
        });

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

        var doc = JsonDocument.Parse(fReq);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var innerWrapper = doc.RootElement[0][0];
        Assert.Equal("otAQ7b", innerWrapper[0].GetString());
        Assert.Equal("generic", innerWrapper[3].GetString());
    }

    [Fact]
    public void BuildStreamGenerateInnerPayload_SetsModelCategoryAndStructure()
    {
        var modelConfig = GeminiWebModelMap.GetModelConfig("gemini-4-argon");
        var inner = GeminiWebChatService.BuildStreamGenerateInnerPayload("Test prompt", modelConfig);

        Assert.Equal(80, inner.Length);
        Assert.Equal(3, inner[79]); // ModeCategory 3 for PRO/Argon
        Assert.Equal(2, (inner[41] as object[])?[0]); // Persistence flag [2]

        var outer = new object?[] { null, JsonSerializer.Serialize(inner) };
        var fReq = JsonSerializer.Serialize(outer);

        var doc = JsonDocument.Parse(fReq);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, doc.RootElement[0].ValueKind);
        Assert.Equal(JsonValueKind.String, doc.RootElement[1].ValueKind);

        var innerDoc = JsonDocument.Parse(doc.RootElement[1].GetString()!);
        Assert.Equal(JsonValueKind.Array, innerDoc.RootElement.ValueKind);
        Assert.Equal(80, innerDoc.RootElement.GetArrayLength());
        Assert.Equal(3, innerDoc.RootElement[79].GetInt32());
    }
}
