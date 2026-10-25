using System.Text.Json;
using GeminiWebAgent.Models.GeminiWeb;
using GeminiWebAgent.Models.OpenAI;
using GeminiWebAgent.Services;

namespace GeminiWebAgent.Tests.UnitTests;

public class ToolCallingTests
{
    private static List<OpenAITool> CreateSampleTools()
    {
        return
        [
            new OpenAITool
            {
                Type = "function",
                Function = new OpenAIToolFunction
                {
                    Name = "read_file",
                    Description = "Reads the content of a file at the given path",
                    Parameters = JsonDocument.Parse("""
                    {
                        "type": "object",
                        "properties": {
                            "path": { "type": "string", "description": "Absolute file path" }
                        },
                        "required": ["path"]
                    }
                    """).RootElement
                }
            },
            new OpenAITool
            {
                Type = "function",
                Function = new OpenAIToolFunction
                {
                    Name = "write_to_file",
                    Description = "Writes content to a file at the given path",
                    Parameters = JsonDocument.Parse("""
                    {
                        "type": "object",
                        "properties": {
                            "path": { "type": "string" },
                            "content": { "type": "string" }
                        },
                        "required": ["path", "content"]
                    }
                    """).RootElement
                }
            }
        ];
    }

    [Fact]
    public void ParseToolCalls_ExtractsStandardToolCallBlock()
    {
        var tools = CreateSampleTools();
        var modelOutput = """
        I will read the requested file now.
        ```tool_call
        {"name": "read_file", "arguments": {"path": "E:\\Personal\\LiveConversation\\test.txt"}}
        ```
        Please wait a moment.
        """;

        var (cleanText, toolCalls) = GeminiWebChatService.ParseToolCalls(modelOutput, tools);

        Assert.NotNull(toolCalls);
        Assert.Single(toolCalls);
        Assert.Equal("read_file", toolCalls[0].Function.Name);
        Assert.Equal(0, toolCalls[0].Index);
        Assert.Contains("LiveConversation", toolCalls[0].Function.Arguments);
        Assert.StartsWith("call_", toolCalls[0].Id);
        Assert.NotNull(cleanText);
        Assert.Contains("I will read the requested file now.", cleanText);
        Assert.DoesNotContain("```tool_call", cleanText);
    }

    [Fact]
    public void ParseToolCalls_ExtractsMultipleToolCalls()
    {
        var tools = CreateSampleTools();
        var modelOutput = """
        ```tool_call
        {"name": "read_file", "arguments": {"path": "file1.txt"}}
        ```
        ```tool_call
        {"name": "write_to_file", "arguments": {"path": "file2.txt", "content": "hello"}}
        ```
        """;

        var (cleanText, toolCalls) = GeminiWebChatService.ParseToolCalls(modelOutput, tools);

        Assert.NotNull(toolCalls);
        Assert.Equal(2, toolCalls.Count);
        Assert.Equal("read_file", toolCalls[0].Function.Name);
        Assert.Equal(0, toolCalls[0].Index);
        Assert.Equal("write_to_file", toolCalls[1].Function.Name);
        Assert.Equal(1, toolCalls[1].Index);
        Assert.Null(cleanText); // Only tool calls, cleanText is empty
    }

    [Fact]
    public void ParseToolCalls_ReturnsNull_WhenNoToolsProvided()
    {
        var modelOutput = "Just a normal chat response without any tool calling.";

        var (cleanText, toolCalls) = GeminiWebChatService.ParseToolCalls(modelOutput, null);

        Assert.Null(toolCalls);
        Assert.Equal(modelOutput, cleanText);
    }

    [Fact]
    public void BuildPromptFromMessages_InjectsToolsAndInstructions()
    {
        var tools = CreateSampleTools();
        var modelConfig = GeminiWebModelMap.GetModelConfig("gemini-4-argon");

        var messages = new List<OpenAIMessage>
        {
            new()
            {
                Role = "user",
                Content = JsonDocument.Parse("\"Please inspect the file E:\\\\Personal\\\\LiveConversation\\\\test.cs\"").RootElement
            }
        };

        var prompt = GeminiWebChatService.BuildPromptFromMessages(messages, tools, modelConfig);

        Assert.Contains("You have access to the following tools:", prompt);
        Assert.Contains("read_file", prompt);
        Assert.Contains("write_to_file", prompt);
        Assert.Contains("INSTRUCTIONS FOR TOOL CALLING:", prompt);
        Assert.Contains("The client runtime environment automatically executes your tool calls", prompt);
        Assert.Contains("Please inspect the file", prompt);
    }

    [Fact]
    public void BuildPromptFromMessages_HandlesToolResultsAndAssistantToolCalls()
    {
        var tools = CreateSampleTools();
        var modelConfig = GeminiWebModelMap.GetModelConfig("gemini-4-argon");

        var messages = new List<OpenAIMessage>
        {
            new()
            {
                Role = "user",
                Content = JsonDocument.Parse("\"Read file.txt\"").RootElement
            },
            new()
            {
                Role = "assistant",
                Content = JsonDocument.Parse("\"Reading now\"").RootElement,
                ToolCalls =
                [
                    new OpenAIToolCall
                    {
                        Id = "call_12345678",
                        Type = "function",
                        Function = new OpenAIToolCallFunction
                        {
                            Name = "read_file",
                            Arguments = "{\"path\":\"file.txt\"}"
                        }
                    }
                ]
            },
            new()
            {
                Role = "tool",
                ToolCallId = "call_12345678",
                Name = "read_file",
                Content = JsonDocument.Parse("\"File content: hello world\"").RootElement
            }
        };

        var prompt = GeminiWebChatService.BuildPromptFromMessages(messages, tools, modelConfig);

        Assert.Contains("[Assistant]", prompt);
        Assert.Contains("```tool_call", prompt);
        Assert.Contains("read_file", prompt);
        Assert.Contains("[Tool Result for read_file]:", prompt);
        Assert.Contains("File content: hello world", prompt);
    }
}
