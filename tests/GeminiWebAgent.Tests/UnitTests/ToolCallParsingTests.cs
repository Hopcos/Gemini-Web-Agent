using System.Text.Json;
using GeminiWebAgent.Models.OpenAI;
using GeminiWebAgent.Services;

namespace GeminiWebAgent.Tests.UnitTests;

/// <summary>
/// Real model output is messy. A tool call that fails to parse is silently shown as plain text,
/// finish_reason becomes "stop" and the agent loop ends -- so the parser has to be tolerant.
/// </summary>
public class ToolCallParsingTests
{
    private static List<OpenAITool> Tools(params string[] names) =>
        names.Select(n => new OpenAITool
        {
            Function = new OpenAIToolFunction
            {
                Name = n,
                Parameters = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement
            }
        }).ToList();

    private static string Arg(OpenAIToolCall call, string name) =>
        JsonDocument.Parse(call.Function.Arguments).RootElement.GetProperty(name).GetString()!;

    [Fact]
    public void FileContentContainingMarkdownFences_IsStillOneToolCall()
    {
        var text = "Writing the docs.\n```tool_call\n" +
                   "{\"name\": \"write_to_file\", \"arguments\": {\"path\": \"README.md\", \"content\": \"# Title\\n```js\\nlet a = 1;\\n```\\nDone\\n\"}}\n" +
                   "```\nAnd then continue.";

        var (clean, calls) = GeminiWebChatService.ParseToolCalls(text, Tools("write_to_file"));

        Assert.NotNull(calls);
        var call = Assert.Single(calls);
        Assert.Equal("write_to_file", call.Function.Name);
        Assert.Equal("# Title\n```js\nlet a = 1;\n```\nDone\n", Arg(call, "content"));
        Assert.Equal("Writing the docs.\n\nAnd then continue.", clean);
    }

    [Fact]
    public void RawNewlinesAndTabsInsideJsonStrings_AreRepaired()
    {
        var text = "```tool_call\n{\"name\": \"write_to_file\", \"arguments\": {\"path\": \"a.css\", \"content\": \"body {\n\tcolor: red;\n}\n\"}}\n```";

        var (_, calls) = GeminiWebChatService.ParseToolCalls(text, Tools("write_to_file"));

        Assert.NotNull(calls);
        Assert.Equal("body {\n\tcolor: red;\n}\n", Arg(calls[0], "content"));
    }

    [Fact]
    public void MissingClosingFence_StillParses()
    {
        var text = "```tool_call\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.txt\"}}";

        var (clean, calls) = GeminiWebChatService.ParseToolCalls(text, Tools("read_file"));

        Assert.NotNull(calls);
        Assert.Null(clean);
    }

    [Theory]
    [InlineData("functions.read_file")]
    [InlineData("default_api:read_file")]
    [InlineData("READ_FILE")]
    [InlineData("read-file")]
    public void ToolNameVariants_ResolveToTheDeclaredName(string emitted)
    {
        var text = $"```tool_call\n{{\"name\": \"{emitted}\", \"arguments\": {{\"path\": \"a.txt\"}}}}\n```";

        var (_, calls) = GeminiWebChatService.ParseToolCalls(text, Tools("read_file", "write_to_file"));

        Assert.NotNull(calls);
        Assert.Equal("read_file", calls[0].Function.Name);
    }

    [Theory]
    [InlineData("""{"tool": "read_file", "args": {"path": "a.txt"}}""")]
    [InlineData("""{"tool_name": "read_file", "input": {"path": "a.txt"}}""")]
    [InlineData("""{"function": "read_file", "parameters": {"path": "a.txt"}}""")]
    [InlineData("""{"function_call": {"name": "read_file", "arguments": "{\"path\": \"a.txt\"}"}}""")]
    public void AlternativeKeyNames_AreAccepted(string json)
    {
        var (_, calls) = GeminiWebChatService.ParseToolCalls($"```json\n{json}\n```", Tools("read_file"));

        Assert.NotNull(calls);
        Assert.Equal("a.txt", Arg(calls[0], "path"));
    }

    [Fact]
    public void SeveralCallsInOneReply_KeepTheirOrderAndIndexes()
    {
        var text = "```tool_call\n{\"name\": \"write_to_file\", \"arguments\": {\"path\": \"a\", \"content\": \"x\"}}\n```\n" +
                   "```tool_call\n{\"name\": \"write_to_file\", \"arguments\": {\"path\": \"b\", \"content\": \"y\"}}\n```\n" +
                   "<tool_call>{\"name\": \"run_command\", \"arguments\": {\"command\": \"npm test\"}}</tool_call>";

        var (clean, calls) = GeminiWebChatService.ParseToolCalls(text, Tools("write_to_file", "run_command"));

        Assert.NotNull(calls);
        Assert.Equal(["a", "b"], calls.Take(2).Select(c => Arg(c, "path")));
        Assert.Equal("run_command", calls[2].Function.Name);
        Assert.Equal([0, 1, 2], calls.Select(c => c.Index!.Value));
        Assert.Null(clean);
    }

    [Fact]
    public void UnrelatedCodeBlocksAndUnknownTools_StayText()
    {
        var text = "Use this:\n```json\n{\"name\": \"demo\", \"version\": \"1.0\"}\n```\nand\n```tool_call\n{\"name\": \"delete_everything\", \"arguments\": {}}\n```";

        var (clean, calls) = GeminiWebChatService.ParseToolCalls(text, Tools("read_file"));

        Assert.Null(calls);
        Assert.Equal(text, clean);
    }

    [Fact]
    public void BareJsonReply_IsATooCall()
    {
        var (clean, calls) = GeminiWebChatService.ParseToolCalls(
            "{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.txt\"}}", Tools("read_file"));

        Assert.NotNull(calls);
        Assert.Null(clean);
    }
}
