using System.Text.Json;

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
}
